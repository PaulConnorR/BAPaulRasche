using FFMpegCore;

namespace BA.Services.Transmission;

public sealed class RistTransmitter : VideoTransmitter
{
    public override string Name => "RIST";

    public override string ProtocolId => "rist";

    public override string UrlScheme => "rist://";

    public override string ContainerFormat => "mpegts";

    // AV1/VP9 über die RIST-Bytepumpe als Matroska (wie SRT).
    public override string ContainerFormatFor(string codecId) =>
        codecId is "av1" or "vp9" ? "matroska" : ContainerFormat;

    public override string Description =>
        "Reliable Internet Stream Transport: latenzarm mit Fehlerkorrektur (ARQ), offener Standard – ähnlich SRT.";

    public override string BuildOutputUrl(string host, int port) => $"rist://{host}:{port}";

    // Live-Pfad läuft über die native Pumpe (localhost-UDP)
    protected override Task<long> ExecuteTransmissionAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken)
        => SendViaFFMpegAsync(
            inputPath,
            BuildOutputUrl(host, port),
            ContainerFormat,
            options => options.WithCopyCodec()
                .WithCustomArgument($"-rist_profile main -buffer_size {BA.Services.PipelineSettings.Current.SrtLatency}"),
            cancellationToken);
}
