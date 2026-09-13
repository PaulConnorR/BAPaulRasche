namespace BA.Services.Srt;

// Fehler aus der libsrt-Schicht, plattform-neutral (referenziert SrtSharp nicht), damit er auch außerhalb
// der Windows-gebundenen SRT-Klassen gefangen werden kann.
public sealed class SrtException : Exception
{
    public SrtException(string message) : base(message) { }
}
