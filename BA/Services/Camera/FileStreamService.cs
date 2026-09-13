using BA.Services.Compression;
using BA.Services.Transmission;
using FFMpegCore;
using FFMpegCore.Enums;

namespace BA.Services.Camera;

/// <summary>
/// Einstufige Live-Übertragung aus einer Videodatei
/// </summary>
public sealed class FileStreamService : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _ffmpegTask;
#if WINDOWS
    private BA.Services.Srt.SrtSender? _srtSender;
#endif
    private BA.Services.Rtp.RtpSender? _rtpSender;
    private BA.Services.Rist.RistSender? _ristSender;
#if WINDOWS
    private BA.Services.WebRtc.WebRtcSender? _webRtcSender;
#endif

    public bool IsRunning => _cts is not null;

    public string Start(
        string inputPath,
        VideoCompressor compressor,
        VideoTransmitter transmitter,
        string host,
        int port)
    {
        if (IsRunning)
            throw new InvalidOperationException("Live-Übertragung läuft bereits.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Quelldatei nicht gefunden.", inputPath);

        if (!transmitter.SupportsLiveCamera)
            throw new NotSupportedException(
                $"Live-Übertragung via {transmitter.Name} wird derzeit nicht unterstützt.");

        var outputUrl = transmitter.BuildOutputUrl(host, port);
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

        bool webRtc = transmitter is WebRtcTransmitter;
#if WINDOWS
        if (webRtc)
        {
            _webRtcSender = new BA.Services.WebRtc.WebRtcSender();
            outputUrl = _webRtcSender.Start(host, port);
        }
#endif
        _cts = new CancellationTokenSource();

        var measure = BA.Services.PipelineSettings.Current.EffectiveMeasureLatency;
        var vf = BuildVideoFilter(measure);
        if (measure)
            Metrics.LatencyTracker.Reset();

        var ps = BA.Services.PipelineSettings.Current;
        Metrics.DiagLog.Write(
            $"[SENDER-START] codec={compressor.CodecId} proto={transmitter.ProtocolId} ziel={outputUrl} " +
            $"finite={ps.FiniteMeasurement} dauer={ps.MeasureDuration}s measure={measure} " +
            $"rate={ps.RateControlLabel} vf='{vf ?? "-"}' loglevel={(measure ? "info" : "error(default)")}");

        var args = FFMpegArguments
            .FromFileInput(inputPath, verifyExists: true, inputOptions =>
            {
                // Endliche Messsequenz (Frame-0-Abgleich) oder Endlosschleife. -re/-stream_loop sind Eingabe-Optionen.
                var finite = BA.Services.PipelineSettings.Current.FiniteMeasurement;
                inputOptions.WithCustomArgument(finite
                    ? $"-re -t {BA.Services.PipelineSettings.Current.MeasureDuration}"
                    : "-stream_loop -1 -re");
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

        // FFMpegCore-Default -loglevel error würde die showinfo-Zeilen unterdrücken; im Mess-Modus anheben.
        if (measure)
            args = args.WithLogLevel(FFMpegLogLevel.Info);

        Task task;
#if WINDOWS
        if (webRtc && _webRtcSender is not null)
        {
            // Encoder erst starten, wenn der Datenkanal offen ist, sonst gehen die ersten Frames ins Leere und
            // die Frame-Zähler liefen versetzt. Bis zu 15 s auf "ready" warten.
            var sender = _webRtcSender;
            task = Task.Run(async () =>
            {
                await Task.WhenAny(sender.Ready, Task.Delay(TimeSpan.FromSeconds(15)));
                Metrics.DiagLog.Write($"[SENDER-START] WebRTC-Datenkanal ready={sender.Ready.IsCompleted} → ffmpeg startet");
                await args.ProcessAsynchronously();
            });
        }
        else
#endif
        {
            task = args.ProcessAsynchronously();
        }

        // ffmpeg-Fehler des Hintergrundprozesses nicht verschlucken.
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                System.Diagnostics.Debug.WriteLine(
                    $"[Datei-Live] ffmpeg beendet mit Fehler: {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);

        _ffmpegTask = task;
        return outputUrl;
    }

    // showinfo gibt pro Frame den Frame-Index aus, den die Latenzmessung als Schlüssel nutzt.
    private static string? BuildVideoFilter(bool measure) => measure ? "-vf showinfo" : null;

    // Nur bei WebRTC relevant (Encoder startet erst mit offenem Datenkanal); sonst sofort fertig.
    public Task WaitUntilReadyAsync(TimeSpan timeout)
    {
#if WINDOWS
        if (_webRtcSender is not null)
            return Task.WhenAny(_webRtcSender.Ready, Task.Delay(timeout));
#endif
        return Task.CompletedTask;
    }

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
#if WINDOWS
        if (_webRtcSender is not null)
        {
            await _webRtcSender.StopAsync();
            _webRtcSender = null;
        }
#endif

        _ffmpegTask = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
