using System.Globalization;
using System.Text;

namespace BA.Services.Metrics;

// Schreibt die aggregierten Harness-Läufe als Excel-lesbare CSV (eine Zeile pro Lauf).
public static class HarnessReport
{
    // Schreibt nach Dokumente/BAMetrics und liefert den Pfad zurück.
    public static string WriteSummaryCsv(IReadOnlyList<RunSummary> summaries)
    {
        var ci = CultureInfo.CurrentCulture;
        var sep = ci.TextInfo.ListSeparator; // passt zum lokalen Excel
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine(string.Join(sep,
            "RunID", "Codec", "Protokoll", "GOP", "Ratenmodus", "Ziel_kbps", "WLAN_Band", "Netzbedingung", "Quelle",
            "Messmodus", "Dauer_s", "Samples",
            "Latenz_Mittel_ms", "Latenz_P95_ms", "Latenz_P99_ms", "Transport_Latenz_ms_Mittel",
            "Sender_kbps_Mittel", "Empf_kbps_Mittel", "Sender_fps_Mittel", "Empf_fps_Mittel",
            "CPU_%_Mittel", "RAM_MB_Mittel", "Frame_Zustellung_%_Mittel", "TS_ContErr",
            "Transport_PktEmpf", "Transport_PktVerlust", "Transport_Verlust_%_Mittel", "Transport_Jitter_ms_Mittel", "Transport_Retransmit_Pkt",
            "Empf_SSIM", "Empf_PSNR_dB", "Empf_VMAF"));

        foreach (var s in summaries)
        {
            sb.AppendLine(string.Join(sep,
                s.RunId, s.Codec, s.Protocol, s.Gop.ToString(inv), s.RateControl,
                s.TargetBitrateKbps > 0 ? s.TargetBitrateKbps.ToString(inv) : string.Empty,
                s.WlanBand,
                s.NetworkCondition, s.Source, s.MeasureMode, s.DurationSec.ToString(inv), s.SampleCount.ToString(inv),
                s.LatencyMeanMs.ToString("0.##", inv), s.LatencyP95Ms.ToString("0.##", inv), s.LatencyP99Ms.ToString("0.##", inv),
                s.TransportLatencyMeanMs.ToString("0.##", inv),
                s.SenderKbpsMean.ToString("0.#", inv), s.ReceiverKbpsMean.ToString("0.#", inv),
                s.SenderFpsMean.ToString("0.#", inv), s.ReceiverFpsMean.ToString("0.#", inv),
                s.CpuMean.ToString("0.#", inv), s.RamMean.ToString("0.#", inv), s.FrameDeliveryMean.ToString("0.#", inv),
                s.TsContinuityErrors.ToString(inv),
                s.TransportPacketsReceived.ToString(inv), s.TransportPacketsLost.ToString(inv),
                s.TransportLossPercentMean.ToString("0.##", inv), s.TransportJitterMeanMs.ToString("0.##", inv),
                s.TransportRetransmitted.ToString(inv),
                s.ReceivedSsim?.ToString("0.0000", inv) ?? string.Empty,
                s.ReceivedPsnr?.ToString("0.0#", inv) ?? string.Empty,
                s.ReceivedVmaf?.ToString("0.0#", inv) ?? string.Empty));
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BAMetrics");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"BA_Harness_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
