namespace BA.Services.Metrics;

/// <summary>Ein Zeitreihen-Datenpunkt der Auswertung (eine Zeile in der CSV).</summary>
public sealed record MetricsSample(
    DateTime Timestamp,
    RunContext Run,
    double SenderBitrateKbps,
    double SenderFps,
    long SenderFrames,
    long SenderDrops,
    double ReceiverBitrateKbps,
    double ReceiverFps,
    long ReceiverFrames,
    double CpuPercent,
    double RamMb,
    double? Ssim,
    double ClockOffsetMs,          // Sender−Empfänger (ms); 0 = nicht synchronisiert
    double EncodeDecodeLatencyMs,  // 0 = nicht gemessen
    double FrameDeliveryPercent,
    int TsContinuityErrors,
    double? ReceivedSsim = null,
    double? ReceivedPsnr = null,
    double? ReceivedVmaf = null,
    // Transportmetriken protokoll-übergreifend in denselben Feldern (Jitter: SRT = RTT-Streuung, RTP/RIST = Interarrival).
    long TransportPacketsReceived = 0,
    long TransportPacketsLost = 0,
    double TransportLossPercent = 0,
    double TransportJitterMs = 0,
    double TransportLatencyMs = 0,     // SRT = RTT/2, WebRTC = Echo-Ping, sonst 0
    long TransportRetransmitted = 0);  // ARQ (SRT/RIST), 0 für RTP/WebRTC
