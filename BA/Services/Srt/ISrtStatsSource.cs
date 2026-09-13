namespace BA.Services.Srt;

// Plattform-neutrale SRT-Statistikquelle (kein SrtSharp-Typ in der Signatur), damit die Metrik-Schicht die
// Werte abfragen kann, ohne die Windows-gebundenen SRT-Klassen zu kennen.
public interface ISrtStatsSource
{
    string Role { get; }

    // null, wenn (noch) nicht verbunden.
    SrtStats? TryGetStats();
}
