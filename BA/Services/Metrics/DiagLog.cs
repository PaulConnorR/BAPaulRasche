using System.Text;

namespace BA.Services.Metrics;

//Diagnose-Log: hängt zeitgestempelte Zeilen an Dokumente/BAMetrics/ba_debug.log an.
public static class DiagLog
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string Path_()
    {
        if (_path is not null)
            return _path;
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BAMetrics");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "ba_debug.log");
        return _path;
    }

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(Path_(), line, Encoding.UTF8);
        }
        catch { } // Diagnose darf nie den Ablauf stören
    }
}
