using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BA.Services.Metrics;

/// <summary>
/// Sammelt Live-Metriken aus den ffmpeg-stderr-Zeilen (Sender, Empfänger, via FFMpegCore NotifyOnError),
/// misst CPU/RAM, hält den letzten SSIM-Wert und exportiert die Zeitreihe als CSV. Singleton, damit Sende-,
/// Empfangspfad und UI dieselbe Instanz nutzen.
/// </summary>
public partial class MetricsService : ObservableObject
{
    public static MetricsService Current { get; } = new();

    [ObservableProperty][NotifyPropertyChangedFor(nameof(SenderDisplay))] public partial double SenderBitrateKbps { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SenderDisplay))] public partial double SenderFps { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SenderDisplay))] public partial long SenderFrames { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(SenderDisplay))] public partial long SenderDrops { get; set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ReceiverDisplay))] public partial double ReceiverBitrateKbps { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ReceiverDisplay))] public partial double ReceiverFps { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ReceiverDisplay))] public partial long ReceiverFrames { get; set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ResourceDisplay))] public partial double FfmpegCpuPercent { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ResourceDisplay))] public partial double FfmpegRamMb { get; set; }

    // Frame-Zustellrate = Δ Empfänger-Frames / Δ Sender-Frames pro Sekunde (End-to-End-Frameverlust);
    // TS-Continuity-Fehler = Paketverlust-Indikator aus dem MPEG-TS-Demuxer des Empfängers.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrameDeliveryDisplay))] public partial double FrameDeliveryPercent { get; set; } = 100;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrameDeliveryDisplay))] public partial int TsContinuityErrors { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(FrameDeliveryDisplay))] public partial bool FrameDeliveryValid { get; set; }

    public string FrameDeliveryDisplay => FrameDeliveryValid
        ? $"{FrameDeliveryPercent:0.0} % zugestellt · Verlust {100 - FrameDeliveryPercent:0.0} % · TS-Fehler {TsContinuityErrors}"
        : (TsContinuityErrors > 0 ? $"TS-Fehler {TsContinuityErrors}" : "—");

    // Vereinheitlichte Transportmetriken
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial bool TransportActive { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial long TransportPacketsReceived { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial long TransportPacketsLost { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial double TransportLossPercent { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial double TransportJitterMs { get; set; }
    // ARQ-Neuübertragungen (SRT: bstats pktRetrans; RIST: erneut gesendet; RTP/WebRTC 0).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportDisplay))] public partial long TransportRetransmitted { get; set; }

    public string TransportDisplay => TransportActive
        ? $"Verlust {TransportLossPercent:0.0} % ({TransportPacketsLost} Pkt) · Jitter {TransportJitterMs:0.0} ms · {TransportPacketsReceived} Pkt empf."
            + (TransportRetransmitted > 0 ? $" · Retransmit {TransportRetransmitted} Pkt" : string.Empty)
        : "—";

    // Reine Übertragungslatenz (Einweg ≈ RTT/2), unabhängig von Encoder/Decoder (aktuell WebRTC: Echo-Ping).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(TransportLatencyDisplay))]
    public partial double TransportLatencyMs { get; set; }

    public string TransportLatencyDisplay => TransportLatencyMs > 0
        ? $"{TransportLatencyMs:0.0} ms (nur Transport)"
        : "—";

    [ObservableProperty] public partial string SsimDisplay { get; set; } = "—";

    // Empfangsseitige Qualität gegen das Original (nach dem endlichen Lauf); enthält Codec- und Transportverlust.
    [ObservableProperty] public partial string ReceivedQualityDisplay { get; set; } = "—";

    [ObservableProperty] public partial string ExportStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunDisplay))]
    public partial RunContext CurrentRun { get; set; } = RunContext.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteReceiverDisplay))]
    public partial bool RemoteReceiverConnected { get; set; }

    // Uhr-Offset Sender−Empfänger (ms), per Rückkanal gemessen; Grundlage der Encode→Decode-Latenz.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockSyncDisplay))]
    public partial double ClockOffsetMs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockSyncDisplay))]
    public partial bool ClockSynced { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockSyncDisplay))]
    public partial double ClockSyncDelayMs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    public partial double EncodeDecodeLatencyMs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyDisplay))]
    public partial bool LatencyMeasured { get; set; }

    public string LatencyDisplay => LatencyMeasured
        ? $"{EncodeDecodeLatencyMs:0.0} ms (Encode+Transport+Decode)"
        : "—";

    public string RunDisplay => CurrentRun.RunId == "—"
        ? "—"
        : $"{CurrentRun.Codec} · {CurrentRun.Protocol} · GOP {CurrentRun.Gop} · {CurrentRun.RateControlDisplay} · {CurrentRun.ResolutionDisplay} · {CurrentRun.NetworkCondition}";
    public string RemoteReceiverDisplay => RemoteReceiverConnected ? "verbunden" : "—";
    public string ClockSyncDisplay => ClockSynced
        ? $"{ClockOffsetMs:+0.0;-0.0;0} ms (Sender−Empf.) · ±{ClockSyncDelayMs / 2:0.0} ms"
        : "—";

    public string SenderDisplay => $"{SenderBitrateKbps:0} kbit/s · {SenderFps:0} fps · {SenderFrames} Frames · {SenderDrops} Drops";
    public string ReceiverDisplay => $"{ReceiverBitrateKbps:0} kbit/s · {ReceiverFps:0} fps · {ReceiverFrames} Frames";
    public string ResourceDisplay => $"ffmpeg: {FfmpegCpuPercent:0} % CPU · {FfmpegRamMb:0} MB RAM";

    private readonly List<MetricsSample> _samples = new();
    private readonly Timer _timer;
    private double? _lastSsim;
    // Empfangsseitige Qualität des aktuellen Laufs (einmal nach dem Lauf gesetzt; in jede folgende CSV-Zeile geschrieben).
    private double? _recvSsim;
    private double? _recvPsnr;
    private double? _recvVmaf;
    // RunId zur Korrelation, damit ein spät berechneter Wert nicht dem falschen Lauf zufällt.
    private string _recvQualityRunId = string.Empty;
    // Einzelne Latenzproben mit Zeitstempel (für Mittel/P95/P99, warmup-filterbar); in BeginRun geleert.
    private readonly List<(long Ticks, double Ms)> _latencySamples = new();
    private readonly object _latencyGate = new();
    private TimeSpan _lastCpuTotal;
    private DateTime _lastCpuAt;

    // Vom Rückkanal gemeldete, empfänger-gemessene Transportqualität (im Zwei-PC-Betrieb sieht der Sender
    // Verlust/Empf.-Pakete/Jitter nur so). _remoteAt = Frische.
    private long _remoteTxRecv, _remoteTxLost;
    private double _remoteTxLossPct, _remoteTxJitter;
    private DateTime _remoteAt;

    // Gleitendes Fenster der letzten RTT-Werte für den RTT-Jitter (Standardabweichung).
    private readonly Queue<double> _rttWindow = new();
    private const int RttWindowSize = 10;

    private long _lastSenderFrames, _lastReceiverFrames;

    private static readonly Regex FpsRx = new(@"fps=\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex BitrateRx = new(@"bitrate=\s*([\d.]+)\s*kbits/s", RegexOptions.Compiled);
    private static readonly Regex FrameRx = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex DropRx = new(@"drop=\s*(\d+)", RegexOptions.Compiled);
    // MPEG-TS-Demuxer meldet Paketverlust als "Continuity check failed" (RIST/RTP-Verlust-Indikator).
    private static readonly Regex ContinuityRx = new(@"[Cc]ontinuity check failed", RegexOptions.Compiled);

    private MetricsService()
    {
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void RecordSenderLine(string line)
    {
        if (!TryParse(line, out var bitrate, out var fps, out var frames, out var drops))
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (bitrate is { } b) SenderBitrateKbps = b;
            if (fps is { } f) SenderFps = f;
            if (frames is { } fr) SenderFrames = fr;
            if (drops is { } d) SenderDrops = d;
        });
    }

    public void RecordReceiverLine(string line)
    {
        // Kein "frame=" in der Continuity-Zeile, daher vor dem Early-Return zählen.
        if (ContinuityRx.IsMatch(line))
            MainThread.BeginInvokeOnMainThread(() => TsContinuityErrors++);

        if (!TryParse(line, out var bitrate, out var fps, out var frames, out _))
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (bitrate is { } b) ReceiverBitrateKbps = b;
            if (fps is { } f) ReceiverFps = f;
            if (frames is { } fr) ReceiverFrames = fr;
        });
    }

    public void SetSsim(double? ssim)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _lastSsim = ssim;
            SsimDisplay = ssim is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "n/v";
        });
    }

    public double? ReceivedSsimValue => _recvSsim;
    public double? ReceivedPsnrValue => _recvPsnr;
    public double? ReceivedVmafValue => _recvVmaf;
    public string ReceivedQualityRunId => _recvQualityRunId;

    public void SetReceivedQuality(double? ssim, double? psnr, double? vmaf, string runId = "")
    {
        _recvSsim = ssim;
        _recvPsnr = psnr;
        _recvVmaf = vmaf;
        _recvQualityRunId = runId;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var ci = CultureInfo.InvariantCulture;
            ReceivedQualityDisplay =
                $"SSIM {(ssim is { } s ? s.ToString("0.0000", ci) : "n/v")} · " +
                $"PSNR {(psnr is { } p ? p.ToString("0.0", ci) + " dB" : "n/v")} · " +
                $"VMAF {(vmaf is { } v ? v.ToString("0.0", ci) : "n/v")}";
        });
    }

    /// <summary>
    /// Setzt den Kontext des aktuellen Messlaufs. <paramref name="resetSamples"/> = true (Sender) startet eine
    /// saubere Messreihe; false (Empfänger) erhält die laufende Reihe.
    /// </summary>
    public void BeginRun(RunContext run, bool resetSamples)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentRun = run;
            if (resetSamples)
            {
                _samples.Clear();
                ExportStatus = string.Empty;
                TsContinuityErrors = 0;
                FrameDeliveryPercent = 100;
                FrameDeliveryValid = false;
                _lastSenderFrames = 0;
                _lastReceiverFrames = 0;
                _recvSsim = _recvPsnr = _recvVmaf = null;
                _recvQualityRunId = string.Empty;
                ReceivedQualityDisplay = "—";
                // Latenz verwerfen, sonst erbt ein Lauf ohne frische Messung den alten Wert
                lock (_latencyGate)
                    _latencySamples.Clear();
                EncodeDecodeLatencyMs = 0;
                LatencyMeasured = false;
                TransportActive = false;
                TransportPacketsReceived = TransportPacketsLost = 0;
                TransportLossPercent = TransportJitterMs = 0;
                TransportRetransmitted = 0;
                TransportLatencyMs = 0;
                _remoteTxRecv = _remoteTxLost = 0;
                _remoteTxLossPct = _remoteTxJitter = 0;
            }
        });
    }

    // Transportwerte in Feldern halten und im Tick einfalten; Frame-/fps-/Bitrate direkt in die Anzeige.
    public void ApplyRemoteReceiverMetrics(ReceiverMetricsReport r)
    {
        _remoteTxRecv = r.TransportPacketsReceived;
        _remoteTxLost = r.TransportPacketsLost;
        _remoteTxLossPct = r.TransportLossPercent;
        _remoteTxJitter = r.TransportJitterMs;
        _remoteAt = DateTime.UtcNow;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            RemoteReceiverConnected = true;
            ReceiverBitrateKbps = r.ReceiverBitrateKbps;
            ReceiverFps = r.ReceiverFps;
            ReceiverFrames = r.ReceiverFrames;
            TsContinuityErrors = r.TsContinuityErrors;

            // Qualität nur übernehmen, wenn sie zum AKTUELLEN Lauf gehört, sonst fällt ein später Wert dem falschen Lauf zu.
            if ((r.ReceivedSsim is not null || r.ReceivedPsnr is not null || r.ReceivedVmaf is not null)
                && r.ReceivedQualityRunId == CurrentRun.RunId)
                SetReceivedQuality(r.ReceivedSsim, r.ReceivedPsnr, r.ReceivedVmaf, r.ReceivedQualityRunId);

            if (r.ClockOffsetMs != 0)
            {
                ClockOffsetMs = r.ClockOffsetMs;
                ClockSynced = true;
            }
        });
    }

    // offsetMs = Sender−Empfänger, delayMs = Round-Trip-Verzögerung des besten Samples
    public void SetClockOffset(double offsetMs, double delayMs)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            ClockOffsetMs = offsetMs;
            ClockSyncDelayMs = delayMs;
            ClockSynced = true;
        });

    public void SetEncodeDecodeLatency(double ms)
    {
        lock (_latencyGate)
            _latencySamples.Add((DateTime.UtcNow.Ticks, ms));
        MainThread.BeginInvokeOnMainThread(() =>
        {
            EncodeDecodeLatencyMs = ms;
            LatencyMeasured = true;
        });
    }

    public IReadOnlyList<(long Ticks, double Ms)> SnapshotLatencySamples()
    {
        lock (_latencyGate)
            return _latencySamples.ToArray();
    }

    public void SetRemoteReceiverConnected(bool connected)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            RemoteReceiverConnected = connected;
            if (!connected)
                ClockSynced = false; // ohne Rückkanal keine gültige Synchronisation mehr
        });

    private static bool TryParse(string line, out double? bitrate, out double? fps, out long? frames, out long? drops)
    {
        bitrate = fps = null; frames = drops = null;
        if (string.IsNullOrEmpty(line) || !line.Contains("frame="))
            return false;

        if (BitrateRx.Match(line) is { Success: true } bm && double.TryParse(bm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) bitrate = b;
        if (FpsRx.Match(line) is { Success: true } fm && double.TryParse(fm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) fps = f;
        if (FrameRx.Match(line) is { Success: true } frm && long.TryParse(frm.Groups[1].Value, out var fr)) frames = fr;
        if (DropRx.Match(line) is { Success: true } dm && long.TryParse(dm.Groups[1].Value, out var d)) drops = d;
        return true;
    }

    private void Tick()
    {
        LatencyTracker.LogPeriodicIfActive();

        double ramMb = 0;
        var cpuTotal = TimeSpan.Zero;
        var procs = Process.GetProcessesByName("ffmpeg");
        foreach (var p in procs)
        {
            try
            {
                ramMb += p.WorkingSet64 / 1024.0 / 1024.0;
                cpuTotal += p.TotalProcessorTime;
            }
            catch { }
            finally { p.Dispose(); }
        }

        var now = DateTime.UtcNow;
        double cpuPercent = 0;
        if (_lastCpuAt != default)
        {
            var wallMs = (now - _lastCpuAt).TotalMilliseconds;
            if (wallMs > 0)
                cpuPercent = (cpuTotal - _lastCpuTotal).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0;
        }
        _lastCpuTotal = cpuTotal;
        _lastCpuAt = now;
        if (cpuPercent < 0) cpuPercent = 0;

        // SRT-bstats hier (Timer-Thread) statt auf dem UI-Thread abrufen, weil srt_bstats ein nativer Aufruf ist.
        Srt.SrtStats? snd = null, rcv = null;
        foreach (var source in Srt.SrtStatsRegistry.Snapshot())
        {
            var st = source.TryGetStats();
            if (st is null) continue;
            if (source.Role == "Sender") snd = st; else rcv = st;
        }
        // Rückkanal-Werte gelten als aktuell, wenn die letzte Meldung < 3 s zurückliegt.
        bool remoteFresh = _remoteAt != default && (DateTime.UtcNow - _remoteAt) < TimeSpan.FromSeconds(3);

        bool srtActive = snd is not null || rcv is not null;
        double srtRtt = rcv?.RttMs ?? snd?.RttMs ?? 0;
        // Jitter = RTT-Streuung (Standardabweichung); aussagekräftiger als das bstats-Feld pktRcvAvgBelatedTime.
        double srtJitter = 0;
        if (srtActive && srtRtt > 0)
        {
            _rttWindow.Enqueue(srtRtt);
            while (_rttWindow.Count > RttWindowSize)
                _rttWindow.Dequeue();
            if (_rttWindow.Count >= 2)
            {
                double mean = _rttWindow.Average();
                double variance = _rttWindow.Sum(r => (r - mean) * (r - mean)) / _rttWindow.Count;
                srtJitter = Math.Sqrt(variance);
            }
        }
        else
        {
            _rttWindow.Clear();
        }

        // Native-Pumpen-Quellen (RTP/RIST/WebRTC), lokal
        Transport.TransportStats? txRcv = null;
        long txSent = 0;
        double txLatency = 0;
        long txRetrans = 0;
        foreach (var src in Transport.TransportStatsRegistry.Snapshot())
        {
            var st = src.TryGetStats();
            if (st is null) continue;
            if (src.Role == "Sender") { txSent = st.PacketsSent; txLatency = st.TransportLatencyMs; txRetrans = st.Retransmitted; }
            else txRcv = st;
        }
        bool pumpActive = txSent > 0 || txRcv is not null;
        bool transportActive = srtActive || pumpActive || remoteFresh;

        // Vereinheitlichung: EIN Satz Kennzahlen. Empfänger-gemessen (Loopback oder Rückkanal im Zwei-PC-Betrieb).
        long tRecv; long tLost; double tLossPct; double tJitter;
        if (rcv is not null)          // lokaler SRT-Empfänger (Loopback)
        {
            tRecv = rcv.PktRecv;
            tLost = rcv.PktRcvLoss;
            long tot = rcv.PktRecv + rcv.PktRcvLoss;
            tLossPct = tot > 0 ? 100.0 * rcv.PktRcvLoss / tot : 0;
            tJitter = srtJitter;
        }
        else if (txRcv is not null)   // lokale RTP/RIST/WebRTC-Pumpe (Loopback)
        {
            tRecv = txRcv.PacketsReceived;
            tLost = txRcv.PacketsLost;
            tLossPct = txRcv.LossPercent;
            tJitter = txRcv.JitterMs;
        }
        else if (remoteFresh)         // Zwei-PC: der Empfänger meldet seine gemessenen Werte
        {
            tRecv = _remoteTxRecv;
            tLost = _remoteTxLost;
            tLossPct = _remoteTxLossPct;
            tJitter = srtActive ? srtJitter : _remoteTxJitter; // SRT: RTT-Streuung ist sender-lokal verfügbar
        }
        else { tRecv = tLost = 0; tLossPct = tJitter = 0; }

        // Sender-seitig gemessen: Retransmit + Einweg-Transportlatenz (SRT: RTT/2; WebRTC: Echo-Ping).
        long tRetrans = pumpActive ? txRetrans : (srtActive ? (snd?.PktRetrans ?? 0) : 0);
        double tLatency = pumpActive ? txLatency : (srtActive && srtRtt > 0 ? srtRtt / 2.0 : 0);

        var active = procs.Length > 0 || remoteFresh;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            FfmpegCpuPercent = cpuPercent;
            FfmpegRamMb = ramMb;

            TransportActive = transportActive;
            TransportPacketsReceived = tRecv;
            TransportPacketsLost = tLost;
            TransportLossPercent = tLossPct;
            TransportJitterMs = tJitter;
            TransportRetransmitted = tRetrans;
            TransportLatencyMs = tLatency;

            // Frame-Zustellrate aus den Zuwächsen (nicht kumulativ) – robust gegen Startversatz/Schleifen.
            long dSend = SenderFrames - _lastSenderFrames;
            long dRecv = ReceiverFrames - _lastReceiverFrames;
            _lastSenderFrames = SenderFrames;
            _lastReceiverFrames = ReceiverFrames;
            if (dSend > 0 && dRecv >= 0)
            {
                var ratio = Math.Clamp((double)dRecv / dSend * 100.0, 0, 100);
                FrameDeliveryPercent = ratio;
                FrameDeliveryValid = true;
            }

            if (active)
            {
                _samples.Add(new MetricsSample(
                    DateTime.Now,
                    CurrentRun,
                    SenderBitrateKbps, SenderFps, SenderFrames, SenderDrops,
                    ReceiverBitrateKbps, ReceiverFps, ReceiverFrames,
                    FfmpegCpuPercent, FfmpegRamMb, _lastSsim,
                    ClockSynced ? ClockOffsetMs : 0,
                    LatencyMeasured ? EncodeDecodeLatencyMs : 0,
                    FrameDeliveryValid ? FrameDeliveryPercent : 100,
                    TsContinuityErrors,
                    _recvSsim, _recvPsnr, _recvVmaf,
                    transportActive ? tRecv : 0, transportActive ? tLost : 0,
                    transportActive ? tLossPct : 0, transportActive ? tJitter : 0,
                    tLatency, transportActive ? tRetrans : 0));
            }
        });
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (_samples.Count == 0)
        {
            ExportStatus = "Keine Daten zum Exportieren.";
            return;
        }

        var ci = CultureInfo.CurrentCulture;
        var sep = ci.TextInfo.ListSeparator; // passt zum lokalen Excel (; in DE, , in EN)
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep,
            "RunID", "Codec", "Protokoll", "GOP", "Ratenmodus", "Ziel_kbps", "Aufloesung", "WLAN_Band", "Netzbedingung", "Quelle",
            "Zeit", "Sender_kbps", "Sender_fps", "Sender_Frames", "Sender_Drops",
            "Empf_kbps", "Empf_fps", "Empf_Frames", "CPU_%", "RAM_MB", "SSIM",
            "Uhr_Offset_ms", "Latenz_EncDec_ms", "Transport_Latenz_ms",
            "Transport_PktEmpf", "Transport_PktVerlust", "Transport_Verlust_%", "Transport_Jitter_ms", "Transport_Retransmit_Pkt",
            "Frame_Zustellung_%", "TS_ContErr",
            "Empf_SSIM", "Empf_PSNR_dB", "Empf_VMAF"));

        foreach (var s in _samples)
        {
            sb.AppendLine(string.Join(sep,
                s.Run.RunId,
                s.Run.Codec,
                s.Run.Protocol,
                s.Run.Gop.ToString(ci),
                s.Run.RateControl,
                s.Run.TargetBitrateKbps > 0 ? s.Run.TargetBitrateKbps.ToString(ci) : string.Empty,
                s.Run.Width > 0 && s.Run.Height > 0 ? $"{s.Run.Width}x{s.Run.Height}" : string.Empty,
                s.Run.WlanBand,
                s.Run.NetworkCondition,
                s.Run.SourceName,
                s.Timestamp.ToString("HH:mm:ss", ci),
                s.SenderBitrateKbps.ToString("0.#", ci),
                s.SenderFps.ToString("0.#", ci),
                s.SenderFrames.ToString(ci),
                s.SenderDrops.ToString(ci),
                s.ReceiverBitrateKbps.ToString("0.#", ci),
                s.ReceiverFps.ToString("0.#", ci),
                s.ReceiverFrames.ToString(ci),
                s.CpuPercent.ToString("0.#", ci),
                s.RamMb.ToString("0.#", ci),
                s.Ssim?.ToString("0.0000", ci) ?? string.Empty,
                s.ClockOffsetMs.ToString("0.##", ci),
                s.EncodeDecodeLatencyMs.ToString("0.##", ci),
                s.TransportLatencyMs.ToString("0.##", ci),
                s.TransportPacketsReceived.ToString(ci),
                s.TransportPacketsLost.ToString(ci),
                s.TransportLossPercent.ToString("0.##", ci),
                s.TransportJitterMs.ToString("0.##", ci),
                s.TransportRetransmitted.ToString(ci),
                s.FrameDeliveryPercent.ToString("0.#", ci),
                s.TsContinuityErrors.ToString(ci),
                s.ReceivedSsim?.ToString("0.0000", ci) ?? string.Empty,
                s.ReceivedPsnr?.ToString("0.0#", ci) ?? string.Empty,
                s.ReceivedVmaf?.ToString("0.0#", ci) ?? string.Empty));
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BAMetrics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"BA_Metrics_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            ExportStatus = $"Gespeichert: {path} ({_samples.Count} Zeilen)";
            Debug.WriteLine($"[Metrik] CSV exportiert: {path}");
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export fehlgeschlagen: {ex.Message}";
        }
    }

    public IReadOnlyList<MetricsSample> SnapshotSamples() => _samples.ToArray();

    [RelayCommand]
    private void ResetSamples()
    {
        _samples.Clear();
        ExportStatus = "Messreihe zurückgesetzt.";
    }
}
