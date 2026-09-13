namespace BA.Services.Metrics;

/// <summary>
/// Aggregiertes Ergebnis eines Messlaufs (eine Zeile im Export): verdichtet die 1-Hz-Zeitreihe zu Kennzahlen
/// (Mittel + P95/P99 für Latenz, Mittel für Durchsatz/Ressourcen, Maxima für kumulative Zähler).
/// </summary>
public sealed record RunSummary(
    string RunId, string Codec, string Protocol, int Gop, string RateControl, int TargetBitrateKbps,
    string WlanBand, string NetworkCondition, string Source, string MeasureMode, int DurationSec, int SampleCount,
    double LatencyMeanMs, double LatencyP95Ms, double LatencyP99Ms,
    double SenderKbpsMean, double ReceiverKbpsMean, double SenderFpsMean, double ReceiverFpsMean,
    double CpuMean, double RamMean, double FrameDeliveryMean, int TsContinuityErrors,
    double? ReceivedSsim, double? ReceivedPsnr, double? ReceivedVmaf,
    // Vereinheitlichte Transportmetriken für ALLE Protokolle: Empf.-Pakete/Verlust/Retransmit als Maximum
    // (kumulativ), Verlust%/Jitter/Latenz als Mittel über die Zeilen mit gemessenem Wert.
    long TransportPacketsReceived, long TransportPacketsLost, double TransportLossPercentMean,
    double TransportJitterMeanMs, long TransportRetransmitted, double TransportLatencyMeanMs);

public static class HarnessAggregator
{
    public static RunSummary Summarize(
        RunContext run, IReadOnlyList<MetricsSample> samples, IReadOnlyList<(long Ticks, double Ms)> latencySamples,
        int warmupSeconds, string measureMode, int durationSec, double? ssim, double? psnr, double? vmaf)
    {
        // Aufwärmphase verwerfen; wenn dadurch nichts übrig bliebe, alle Samples nehmen.
        var window = samples.Count > warmupSeconds
            ? samples.Skip(warmupSeconds).ToList()
            : samples.ToList();

        var latencies = TrimWarmup(latencySamples, warmupSeconds);
        var txRows = window.Where(s => s.TransportPacketsReceived > 0).ToList();
        var jitters = window.Where(s => s.TransportJitterMs > 0).Select(s => s.TransportJitterMs).ToList();

        return new RunSummary(
            run.RunId, run.Codec, run.Protocol, run.Gop, run.RateControl, run.TargetBitrateKbps,
            run.WlanBand, run.NetworkCondition, run.SourceName, measureMode, durationSec, window.Count,
            Mean(latencies), Percentile(latencies, 95), Percentile(latencies, 99),
            Mean(window.Select(s => s.SenderBitrateKbps)), Mean(window.Select(s => s.ReceiverBitrateKbps)),
            Mean(window.Select(s => s.SenderFps)), Mean(window.Select(s => s.ReceiverFps)),
            Mean(window.Select(s => s.CpuPercent)), Mean(window.Select(s => s.RamMb)),
            Mean(window.Select(s => s.FrameDeliveryPercent)), Max(window.Select(s => s.TsContinuityErrors)),
            ssim, psnr, vmaf,
            txRows.Count == 0 ? 0 : txRows.Max(s => s.TransportPacketsReceived),
            txRows.Count == 0 ? 0 : txRows.Max(s => s.TransportPacketsLost),
            Mean(txRows.Select(s => s.TransportLossPercent)), Mean(jitters),
            window.Count == 0 ? 0 : window.Max(s => s.TransportRetransmitted),
            Mean(window.Where(s => s.TransportLatencyMs > 0).Select(s => s.TransportLatencyMs)));
    }

    // Verwirft die Latenzproben der ersten warmupSeconds; bei sehr kurzem Lauf bleiben alle stehen.
    private static List<double> TrimWarmup(IReadOnlyList<(long Ticks, double Ms)> samples, int warmupSeconds)
    {
        if (samples.Count == 0)
            return new List<double>();
        long t0 = samples.Min(s => s.Ticks);
        long cutoff = warmupSeconds * TimeSpan.TicksPerSecond;
        var kept = samples.Where(s => s.Ticks - t0 >= cutoff).Select(s => s.Ms).ToList();
        return kept.Count > 0 ? kept : samples.Select(s => s.Ms).ToList();
    }

    private static double Mean(IEnumerable<double> values)
    {
        var list = values as ICollection<double> ?? values.ToList();
        return list.Count == 0 ? 0 : list.Sum() / list.Count;
    }

    private static int Max(IEnumerable<int> values)
    {
        int max = 0;
        foreach (var v in values)
            if (v > max) max = v;
        return max;
    }

    // Lineares Perzentil (0..100); 0 wenn leer.
    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 1)
            return sorted[0];

        var rank = (p / 100.0) * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
    }
}
