namespace BA.Services.Transmission;

public sealed class WebRtcTransmitter : VideoTransmitter
{
    public override string Name => "WebRTC";

    public override string ProtocolId => "webrtc";

    // Kein URL-Schema, sondern SDP/ICE-Signaling über den Rückkanal.
    public override string UrlScheme => string.Empty;

    public override string ContainerFormat => "mpegts";

    // AV1/VP9 über die WebRTC-Bytepumpe als Matroska (wie SRT/RIST).
    public override string ContainerFormatFor(string codecId) =>
        codecId is "av1" or "vp9" ? "matroska" : ContainerFormat;

    public override string Description =>
        "WebRTC (SIPSorcery): Datenkanal-Transport mit ICE/DTLS, Signaling über den Rückkanal.";

    protected override Task<long> ExecuteTransmissionAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "WebRTC überträgt nur über den Live-/Harness-Pfad (Signaling über den Mess-Rückkanal), " +
            "nicht über den zweistufigen Einzel-Sendepfad.");
}
