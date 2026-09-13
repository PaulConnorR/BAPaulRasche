using BA.Services.Compression;
using BA.Services.Transmission;
using FFMpegCore;
using FFMpegCore.Enums;

namespace BA.Services.Camera;

/// <summary>
/// Live-Kameraübertragung in einer ffmpeg-Pipeline: DirectShow-Kamera → encodieren (gewählter Codec, Low-Latency)
/// → senden (gewähltes Protokoll). Encoder-Optionen vom <see cref="VideoCompressor"/>, Ausgabeziel vom
/// <see cref="VideoTransmitter"/>. Windows/DirectShow. Vgl. <see cref="FileStreamService"/> (Datei statt Kamera).
/// </summary>
public sealed class CameraStreamService : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _ffmpegTask;
#if WINDOWS
    // Aktiv, falls SRT und SrtSharp (sonst null → alter srt://-Pfad).
    private BA.Services.Srt.SrtSender? _srtSender;
#endif
    private BA.Services.Rtp.RtpSender? _rtpSender;
    private BA.Services.Rist.RistSender? _ristSender;

    public bool IsRunning => _cts is not null;

    public string Start(
        string cameraName,
        VideoCompressor compressor,
        VideoTransmitter transmitter,
        string host,
        int port)
    {
        if (IsRunning)
            throw new InvalidOperationException("Live-Übertragung läuft bereits.");

        if (!transmitter.SupportsLiveCamera)
            throw new NotSupportedException(
                $"Live-Kameraübertragung via {transmitter.Name} wird derzeit nicht unterstützt.");

        var outputUrl = transmitter.BuildOutputUrl(host, port);
        // Die nativen Pumpen sind Byte-Pipes; ffmpeg schreibt auf einen lokalen UDP-Port, die Pumpe überträgt.
#if WINDOWS
        if (transmitter is SrtTransmitter && BA.Services.PipelineSettings.Current.UseSrtSharp)
        {
            _srtSender = new BA.Services.Srt.SrtSender();
            outputUrl = _srtSender.Start(host, port, BA.Services.PipelineSettings.Current.SrtLatency);
        }
#endif
        if (transmitter is RtpUdpTransmitter)
        {
            _rtpSender = new BA.Services.Rtp.RtpSender();
            outputUrl = _rtpSender.Start(host, port);
        }
        if (transmitter is RistTransmitter)
        {
            _ristSender = new BA.Services.Rist.RistSender();
            outputUrl = _ristSender.Start(host, port);
        }
        _cts = new CancellationTokenSource();

        // FFMpegCore quotet den Gerätenamen nicht; Kameranamen enthalten oft Leerzeichen → explizit quoten.
        var device = $"video=\"{cameraName}\"";

        var measure = BA.Services.PipelineSettings.Current.EffectiveMeasureLatency;
        var vf = BuildVideoFilter(measure);
        if (measure)
            Metrics.LatencyTracker.Reset();

        var args = FFMpegArguments
            .FromDeviceInput(device, inputOptions =>
            {
                inputOptions.ForceFormat("dshow").WithCustomArgument("-rtbufsize 100M");
            })
            .OutputToUrl(outputUrl, outputOptions =>
            {
                compressor.ApplyLiveEncoding(outputOptions);
                outputOptions
                    .ForceFormat(transmitter.ContainerFormatFor(compressor.CodecId)) // AV1/VP9 → Matroska, sonst mpegts
                    .WithCustomArgument("-muxdelay 0 -muxpreload 0 -flush_packets 1"); // Mux entpuffern, sofort flushen
                transmitter.ConfigureOutput(outputOptions);
                if (vf is not null)
                    outputOptions.WithCustomArgument(vf);
            })
            .NotifyOnError(line =>
            {
                Metrics.MetricsService.Current.RecordSenderLine(line);
                if (measure && Metrics.LatencyTracker.ParseFrameIndex(line) is { } n)
                    Metrics.LatencyTracker.RecordEncode(n);
            })
            .CancellableThrough(_cts.Token);

        // FFMpegCore-Default -loglevel error würde showinfo unterdrücken; im Mess-Modus anheben.
        if (measure)
            args = args.WithLogLevel(FFMpegLogLevel.Info);

        var task = args.ProcessAsynchronously();

        // ffmpeg-Fehler des Hintergrundprozesses nicht verschlucken.
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                System.Diagnostics.Debug.WriteLine(
                    $"[Kamera-Live] ffmpeg beendet mit Fehler: {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);

        _ffmpegTask = task;
        return outputUrl;
    }

    // showinfo gibt pro Frame den Frame-Index aus, den die Latenzmessung als Schlüssel nutzt.
    private static string? BuildVideoFilter(bool measure) => measure ? "-vf showinfo" : null;

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        try { _cts.Cancel(); } catch { }
        if (_ffmpegTask is not null)
        {
            try { await _ffmpegTask; } catch { }
        }

#if WINDOWS
        if (_srtSender is not null)
        {
            await _srtSender.StopAsync();
            _srtSender = null;
        }
#endif
        if (_rtpSender is not null)
        {
            await _rtpSender.StopAsync();
            _rtpSender = null;
        }
        if (_ristSender is not null)
        {
            await _ristSender.StopAsync();
            _ristSender = null;
        }

        _ffmpegTask = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
