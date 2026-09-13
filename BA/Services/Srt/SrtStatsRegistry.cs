namespace BA.Services.Srt;

// Prozessweites, thread-sicheres Register der laufenden SRT-Pumpen; der Metrik-Timer liest hier die aktiven
// Quellen aus, ohne die Sende-/Empfangsdienste direkt zu kennen (vgl. TransportStatsRegistry).
public static class SrtStatsRegistry
{
    private static readonly object Gate = new();
    private static readonly List<ISrtStatsSource> Sources = new();

    public static void Register(ISrtStatsSource source)
    {
        lock (Gate) { if (!Sources.Contains(source)) Sources.Add(source); }
    }

    public static void Unregister(ISrtStatsSource source)
    {
        lock (Gate) { Sources.Remove(source); }
    }

    // Kopie, damit der Aufrufer gefahrlos iterieren kann.
    public static IReadOnlyList<ISrtStatsSource> Snapshot()
    {
        lock (Gate) { return Sources.ToArray(); }
    }
}
