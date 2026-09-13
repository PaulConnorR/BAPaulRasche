namespace BA.Services.Srt;

/// <summary>
/// Unveränderlicher Schnappschuss der SRT-Transportstatistik (aus <c>srt_bstats</c>/<c>CBytePerfMon</c>), ohne
/// SrtSharp-Typen, damit die Metrik-Schicht von der nativen Bibliothek entkoppelt bleibt. <see cref="OneWayLatencyMs"/>
/// ≈ RTT/2 liefert die Übertragungslatenz OHNE Uhr-Synchronisation zwischen den beiden Testgeräten.
/// </summary>
/// <param name="RcvAvgBelatedMs">Rohwert pktRcvAvgBelatedTime. ACHTUNG: nur gültig, wenn im Intervall belated-Pakete
///   auftraten – sonst ein nicht zurückgesetzter Müllwert. NICHT als Jitter verwenden (der kommt aus der RTT-Streuung).</param>
public sealed record SrtStats(
    double RttMs,
    double SendRateMbps,
    double RecvRateMbps,
    int PktSndLoss,
    int PktRcvLoss,
    int PktRetrans,
    int RcvBufMs,
    double RcvAvgBelatedMs,
    long PktSent,
    long PktRecv)
{
    public double OneWayLatencyMs => RttMs / 2.0;
}
