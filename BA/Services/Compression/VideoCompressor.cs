using System.Diagnostics;
using FFMpegCore;

namespace BA.Services.Compression;

/// <summary>
/// Basisklasse aller Kompressionsverfahren. Eingabepruefung, Zeitmessung, Ausgabegroesse und Fehlerbehandlung
/// passieren hier einheitlich, damit alle Verfahren vergleichbare Messwerte liefern; abgeleitete Klassen
/// implementieren nur <see cref="ExecuteCompressionAsync"/>.
/// </summary>
public abstract class VideoCompressor
{
    public abstract string Name { get; }
    public abstract string CodecId { get; }
    public abstract string FileExtension { get; }
    public virtual string Description => string.Empty;

    // Wirft nicht: Fehler werden als CompressionResult.Failed zurueckgegeben.
    public async Task<CompressionResult> CompressAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return CompressionResult.Failed("Kein Eingabepfad angegeben.");

        if (string.IsNullOrWhiteSpace(outputPath))
            return CompressionResult.Failed("Kein Ausgabepfad angegeben.");

        if (!File.Exists(inputPath))
            return CompressionResult.Failed($"Eingabedatei nicht gefunden: {inputPath}");

        Debug.WriteLine("Kompression gestartet: " + Name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await ExecuteCompressionAsync(inputPath, outputPath, cancellationToken);
            stopwatch.Stop();

            var outputFile = new FileInfo(outputPath);
            return CompressionResult.Succeeded(
                outputPath,
                outputFile.Exists ? outputFile.Length : 0,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return CompressionResult.Failed("Kompression abgebrochen.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CompressionResult.Failed(ex.Message, stopwatch.Elapsed);
        }
    }

    protected abstract Task ExecuteCompressionAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken);

    // Encoder-Einstellungen für die Live-Übertragung (niedrige Latenz statt maximaler Qualität).
    public abstract void ApplyLiveEncoding(FFMpegArgumentOptions options);

    // Ratenkontroll-Flags des Live-Encoders. Weil sie je Encoder verschieden heißen, liefert jeder Codec
    // cbrArgs (capped VBR) und crfArgs selbst; die Auswahl kommt zentral aus PipelineSettings.Current.
    protected static string LiveRateControlArgs(string cbrArgs, string crfArgs)
        => PipelineSettings.Current.CbrEnabled ? cbrArgs : crfArgs;

    // Codec-spezifische Optionen liefert die jeweilige Klasse über configure.
    // Voraussetzung: ffmpeg im PATH oder über GlobalFFOptions.Configure(...) gesetzt.
    protected static async Task RunFFMpegAsync(
        string inputPath,
        string outputPath,
        Action<FFMpegArgumentOptions> configure,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(outputPath, overwrite: true, configure)
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously();
    }

    public override string ToString() => Name;
}
