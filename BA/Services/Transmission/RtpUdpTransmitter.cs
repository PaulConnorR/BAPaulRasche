namespace BA.Services.Transmission;

public sealed class RtpUdpTransmitter : VideoTransmitter
{
    public override string Name => "RTP/UDP";

    public override string ProtocolId => "rtp";

    public override string UrlScheme => "rtp://";

    // rtp_mpegts multiplext Audio+Video als MPEG-TS über RTP/UDP in einen einzigen Port –
    // einfacher als reines RTP (das pro Stream einen eigenen Port + SDP bräuchte).
    // Gilt für den zweistufigen -c-copy-Pfad (ExecuteTransmissionAsync).
    public override string ContainerFormat => "rtp_mpegts";

    // Live-Pfad: die native RtpSender-Pumpe setzt den RTP-Header selbst (für echte Verlust-/Jitter-Metriken),
    // ffmpeg liefert also reines MPEG-TS an den lokalen UDP-Port der Pumpe.
    public override string ContainerFormatFor(string codecId) => "mpegts";

    public override string Description =>
        "RTP über UDP: minimale Latenz ohne Wiederholungen, dafür anfällig für Paketverluste.";

    protected override Task<long> ExecuteTransmissionAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken)
        => SendViaFFMpegAsync(
            inputPath,
            BuildOutputUrl(host, port),
            ContainerFormat,
            options => options.WithCopyCodec(),
            cancellationToken);
}
