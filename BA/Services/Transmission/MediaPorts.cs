namespace BA.Services.Transmission;

/// <summary>
/// Leitet aus dem eingegebenen Basis-Port je Protokoll einen eigenen Medien-Port ab, damit SRT/RTP/RIST beim
/// schnellen Protokollwechsel im Harness nie denselben UDP-Port kurz nacheinander neu binden (ein gerade
/// geschlossener libsrt-Socket gibt den Port erst verzögert frei). Beide Seiten leiten identisch ab.
/// </summary>
public static class MediaPorts
{
    public static int For(int basePort, string protocolId) => protocolId switch
    {
        "rtp" => basePort + 1,
        "rist" => basePort + 2,
        _ => basePort, // srt, webrtc (webrtc belegt keinen Medien-Port)
    };
}
