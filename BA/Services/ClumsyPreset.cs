using System.Globalization;
using System.Text;

namespace BA.Services;

public sealed record ClumsyPreset(string Name, double LossPercent, int CapKbps)
{
    public string BuildCommand(int basePort)
    {
        if (LossPercent <= 0 && CapKbps <= 0)
            return "— clumsy aus (keine künstliche Netzbedingung) —";

        var filter = $"outbound and udp and udp.DstPort >= {basePort} and udp.DstPort <= {basePort + 2}";
        var sb = new StringBuilder($"clumsy.exe --filter \"{filter}\"");
        if (LossPercent > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $" --drop on --drop-outbound on --drop-chance {LossPercent.ToString(CultureInfo.InvariantCulture)}");
        if (CapKbps > 0)
            // clumsy erwartet KB/s (Bytes/s): kbit/s → /8.
            sb.Append(CultureInfo.InvariantCulture,
                $" --bandwidth on --bandwidth-outbound on --bandwidth-bandwidth {CapKbps / 8}");
        return sb.ToString();
    }

    public override string ToString() => Name;

    public static IReadOnlyList<ClumsyPreset> All { get; } = new ClumsyPreset[]
    {
        new("Baseline", 0, 0),
        new("Verlust 1 %", 1, 0),
        new("Verlust 5 %", 5, 0),
        new("Verlust 10 %", 10, 0),
        new("Deckel 8 Mbit/s", 0, 8000),
        new("Deckel 4 Mbit/s", 0, 4000),
        new("Deckel 2 Mbit/s", 0, 2000),
        new("5 % + 4 Mbit/s", 5, 4000),
    };
}
