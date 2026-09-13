using System.Net;
using System.Net.Sockets;
using FFMpegCore;
using FFMpegCore.Enums;

namespace BA.Services.Reception;

/// <summary>
/// Empfängt einen Stream und reicht ihn latenzarm an einen lokalen UDP-Port weiter, den der VLC-Player abspielt.
/// </summary>
public sealed class StreamReceiver : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _ffmpegTask;
#if WINDOWS
    private BA.Services.Srt.SrtReceiver? _srtReceiver;
#endif
    private BA.Services.Rtp.RtpReceiver? _rtpReceiver;
    // RIST Simple Profile: RTP + NACK-Retransmit + Reorder.
    private BA.Services.Rist.RistReceiver? _ristReceiver;
#if WINDOWS
    private BA.Services.WebRtc.WebRtcReceiver? _webRtcReceiver;
#endif

    public bool IsRunning => _cts is not null;

    // Normalmodus: liefert die lokale UDP-URL für VLC. measureLatency: Decode mit showinfo ohne Anzeige, leere URL.
    public string Start(string protocolId, string bindAddress, int port, bool measureLatency = false, string? recordPath = null, string recordFormat = "mpegts")
    {
        if (IsRunning)
            throw new InvalidOperationException("Empfang läuft bereits.");

        // Vor dem Binden loggen, damit ein Bind-Fehler im Log sichtbar bleibt.
        Metrics.DiagLog.Write($"[EMPFAENGER-VERSUCH] proto={protocolId} port={port} measure={measureLatency} " +
            $"record={(string.IsNullOrEmpty(recordPath) ? "-" : recordPath)}");

        var inputUrl = BuildListenerInput(protocolId, bindAddress, port);
        // Die nativen Pumpen nehmen den Transport entgegen und liefern inputUrl als localhost-UDP an ffmpeg.
#if WINDOWS
        if (protocolId == "webrtc")
        {
            _webRtcReceiver = new BA.Services.WebRtc.WebRtcReceiver();
            inputUrl = _webRtcReceiver.Start(bindAddress, port);
        }
        if (protocolId == "srt" && PipelineSettings.Current.UseSrtSharp)
        {
            _srtReceiver = new BA.Services.Srt.SrtReceiver();
            inputUrl = _srtReceiver.Start(bindAddress, port, PipelineSettings.Current.SrtLatency);
        }
#endif
        if (protocolId == "rtp")
        {
            _rtpReceiver = new BA.Services.Rtp.RtpReceiver();
            inputUrl = _rtpReceiver.Start(bindAddress, port);
        }
        if (protocolId == "rist")
        {
            _ristReceiver = new BA.Services.Rist.RistReceiver();
            inputUrl = _ristReceiver.Start(bindAddress, port);
        }
        _cts = new CancellationTokenSource();

        // Stream-Analyse aus (sonst puffert ffmpeg bis zu 5 s / 5 MB, um den Eingang zu erkennen).
        var inputArgs = "-fflags nobuffer -flags low_delay -analyzeduration 0 -probesize 32k";

        Metrics.DiagLog.Write(
            $"[EMPFAENGER-START] proto={protocolId} port={port} measure={measureLatency} " +
            $"record={(string.IsNullOrEmpty(recordPath) ? "-" : recordPath)} input={inputUrl} " +
            $"modus={(measureLatency ? "Latenz(null+showinfo)" : !string.IsNullOrEmpty(recordPath) ? "Qualitaet(copy→Datei)" : "Anzeige(copy→VLC)")}");

        if (measureLatency)
        {
            Metrics.LatencyTracker.Reset();

            // showinfo erzwingt den Decode; Ausgabe verwerfen (keine Anzeige).
            _ffmpegTask = FFMpegArguments
                .FromUrlInput(new Uri(inputUrl), inputOptions => inputOptions
                    .WithCustomArgument(inputArgs))
                .OutputToFile("NUL", overwrite: true, outputOptions => outputOptions
                    .ForceFormat("null")
                    .WithCustomArgument("-an -vf showinfo"))
                .NotifyOnError(line =>
                {
                    Metrics.MetricsService.Current.RecordReceiverLine(line);
                    if (Metrics.LatencyTracker.ParseFrameIndex(line) is { } n)
                        Metrics.LatencyTracker.RecordDecode(n);
                })
                .CancellableThrough(_cts.Token)
                .WithLogLevel(FFMpegLogLevel.Info) // sonst unterdrückt -loglevel error die showinfo-Zeilen
                .ProcessAsynchronously();

            return string.Empty;
        }

        if (!string.IsNullOrEmpty(recordPath))
        {
            // Verlustfrei (-c copy) mitschneiden, bewahrt das Empfangene inkl. Verlust-Artefakten für die Offline-
            // Qualitätsmessung. Container passend zum Codec (AV1/VP9 → matroska), sonst scheitert der Mux.
            _ffmpegTask = FFMpegArguments
                .FromUrlInput(new Uri(inputUrl), inputOptions => inputOptions
                    .WithCustomArgument(inputArgs))
                .OutputToFile(recordPath, overwrite: true, outputOptions => outputOptions
                    .WithCopyCodec()
                    .ForceFormat(recordFormat))
                .NotifyOnError(line => Metrics.MetricsService.Current.RecordReceiverLine(line))
                .CancellableThrough(_cts.Token)
                .ProcessAsynchronously();

            return string.Empty;
        }

        var localPort = GetFreeUdpPort();
        var localOut = $"udp://127.0.0.1:{localPort}?pkt_size=1316";
        // Eingang → MPEG-TS über lokales UDP, ohne Neucodieren.
        _ffmpegTask = FFMpegArguments
            .FromUrlInput(new Uri(inputUrl), inputOptions => inputOptions
                .WithCustomArgument(inputArgs))
            .OutputToUrl(localOut, outputOptions => outputOptions
                .WithCopyCodec()
                .ForceFormat("mpegts")
                .WithCustomArgument("-muxdelay 0 -muxpreload 0 -flush_packets 1"))
            .NotifyOnError(line => Metrics.MetricsService.Current.RecordReceiverLine(line))
            .CancellableThrough(_cts.Token)
            .ProcessAsynchronously();

        return $"udp://@127.0.0.1:{localPort}";
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
        if (_srtReceiver is not null)
        {
            await _srtReceiver.StopAsync();
            _srtReceiver = null;
        }
#endif
        if (_rtpReceiver is not null)
        {
            await _rtpReceiver.StopAsync();
            _rtpReceiver = null;
        }
        if (_ristReceiver is not null)
        {
            await _ristReceiver.StopAsync();
            _ristReceiver = null;
        }
#if WINDOWS
        if (_webRtcReceiver is not null)
        {
            await _webRtcReceiver.StopAsync();
            _webRtcReceiver = null;
        }
#endif

        _ffmpegTask = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static string BuildListenerInput(string protocolId, string bindAddress, int port) => protocolId switch
    {
        "srt" => $"srt://{bindAddress}:{port}?mode=listener&latency={PipelineSettings.Current.SrtLatency}",
        "rtp" => $"rtp://{bindAddress}:{port}",
        // RIST/WebRTC haben keinen ffmpeg-Listener; die native Pumpe liefert die Eingabe-URL.
        "rist" => string.Empty,
        "webrtc" => string.Empty,
        _ => throw new NotSupportedException($"Unbekanntes Protokoll: {protocolId}"),
    };

    private static int GetFreeUdpPort()
    {
        using var udp = new UdpClient(0, AddressFamily.InterNetwork);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}
