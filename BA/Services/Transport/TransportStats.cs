namespace BA.Services.Transport;

public sealed record TransportStats(
    string Protocol,
    string Role,
    long PacketsSent = 0,
    long PacketsReceived = 0,
    long PacketsLost = 0,
    double LossPercent = 0,
    double JitterMs = 0,
    double RecvRateMbps = 0,
    double TransportLatencyMs = 0,
    long Retransmitted = 0);

public interface ITransportStatsSource
{
    string Protocol { get; }
    string Role { get; }
    TransportStats? TryGetStats();
}

// Prozessweites Register der aktiven Pumpen
public static class TransportStatsRegistry
{
    private static readonly object Gate = new();
    private static readonly List<ITransportStatsSource> Sources = new();

    public static void Register(ITransportStatsSource source)
    {
        lock (Gate) { if (!Sources.Contains(source)) Sources.Add(source); }
    }

    public static void Unregister(ITransportStatsSource source)
    {
        lock (Gate) { Sources.Remove(source); }
    }

    // Kopie, damit der Aufrufer gefahrlos iterieren kann.
    public static IReadOnlyList<ITransportStatsSource> Snapshot()
    {
        lock (Gate) { return Sources.ToArray(); }
    }
}
