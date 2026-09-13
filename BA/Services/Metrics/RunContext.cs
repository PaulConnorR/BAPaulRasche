namespace BA.Services.Metrics;

/// <summary>
/// Beschreibt einen Messlauf (Codec/Protokoll/GOP/Auflösung/Netzbedingung), pro <see cref="MetricsSample"/>
/// mitgeschrieben, damit jede CSV-Zeile eindeutig einem Lauf zugeordnet ist
/// </summary>
public sealed record RunContext(
    string RunId,
    string Codec,
    string Protocol,
    int Gop,
    int Width,
    int Height,
    string NetworkCondition,
    string SourceName,
    string RateControl = "—",
    int TargetBitrateKbps = 0,
    string WlanBand = "—")
{
    public static RunContext Empty { get; } = new("—", "—", "—", 0, 0, 0, "—", "—");

    public string ResolutionDisplay => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";

    public string RateControlDisplay => RateControl switch
    {
        "CBR" => $"CBR {TargetBitrateKbps} kbps",
        "—" => "—",
        _ => RateControl,
    };

    public static RunContext New(
        string codec,
        string protocol,
        int gop,
        int width,
        int height,
        string? networkCondition,
        string? sourceName,
        string rateControl = "—",
        int targetBitrateKbps = 0,
        string? wlanBand = null)
        => new(
            $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}",
            string.IsNullOrWhiteSpace(codec) ? "—" : codec,
            string.IsNullOrWhiteSpace(protocol) ? "—" : protocol,
            gop,
            width,
            height,
            string.IsNullOrWhiteSpace(networkCondition) ? "—" : networkCondition.Trim(),
            string.IsNullOrWhiteSpace(sourceName) ? "—" : sourceName,
            string.IsNullOrWhiteSpace(rateControl) ? "—" : rateControl,
            targetBitrateKbps,
            string.IsNullOrWhiteSpace(wlanBand) ? "—" : wlanBand.Trim());
}
