namespace BA.Services.Transmission;

public sealed class SrtTransmitter : VideoTransmitter
{
    public override string Name => "SRT";

    public override string ProtocolId => "srt";

    public override string UrlScheme => "srt://";

    public override string ContainerFormat => "mpegts";

    // AV1/VP9 sind nicht MPEG-TS-fähig → über die SRT-Bytepumpe stattdessen Matroska (empfängerseitig auto-erkannt).
    public override string ContainerFormatFor(string codecId) =>
        codecId is "av1" or "vp9" ? "matroska" : ContainerFormat;

    // pkt_size=1316 = 7×188 (TS-Pakete pro UDP-Datagramm). latency: SRT-Puffer (per Slider, Default 20 ms).
    public override string BuildOutputUrl(string host, int port) =>
        $"srt://{host}:{port}?pkt_size=1316&latency={BA.Services.PipelineSettings.Current.SrtLatency}";

    public override string Description =>
        "Secure Reliable Transport: latenzarm mit Fehlerkorrektur, gut für unzuverlässige Netze.";

    // Sendet als MPEG-TS über SRT (Caller-Modus). Kein Neucodieren (-c copy): Datei ist schon komprimiert.
    protected override async Task<long> ExecuteTransmissionAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
#if WINDOWS
        // Echter SRT-Transport über SrtSharp: ffmpeg schreibt MPEG-TS auf einen lokalen UDP-Port, die
        // SrtSender-Pumpe leitet es über libsrt an das Empfangsgerät (liefert bstats-Metriken).
        if (BA.Services.PipelineSettings.Current.UseSrtSharp)
        {
            await using var sender = new BA.Services.Srt.SrtSender();
            var localUrl = sender.Start(host, port, BA.Services.PipelineSettings.Current.SrtLatency);
            try
            {
                return await SendViaFFMpegAsync(
                    inputPath, localUrl, ContainerFormat,
                    options => options.WithCopyCodec(), cancellationToken);
            }
            finally
            {
                await sender.StopAsync();
            }
        }
#endif
        // Fallback: ffmpeg spricht SRT direkt (srt://…), keine Transport-Metriken.
        return await SendViaFFMpegAsync(
            inputPath,
            BuildOutputUrl(host, port),
            ContainerFormat,
            options => options.WithCopyCodec(),
            cancellationToken);
    }
}
