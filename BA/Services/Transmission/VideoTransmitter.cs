using System.Diagnostics;
using FFMpegCore;

namespace BA.Services.Transmission;

/// <summary>
/// Basisklasse aller Übertragungsprotokolle, Eingabe-/Zielprüfung, Zeitmessung und Fehlerbehandlung einheitlich hier, abgeleitete Klassen implementieren nur <see cref="ExecuteTransmissionAsync"/>.
/// </summary>
public abstract class VideoTransmitter
{
    public abstract string Name { get; }
    public abstract string ProtocolId { get; }
    public abstract string UrlScheme { get; }
    public abstract string ContainerFormat { get; }

    // AV1/VP9 lassen sich nicht über MPEG-TS übertragen → Byte-Pipe-Protokolle nutzen Matroska; RTP bleibt TS-gebunden.
    public virtual string ContainerFormatFor(string codecId) => ContainerFormat;

    public virtual bool SupportsLiveCamera => true;

    public virtual string BuildOutputUrl(string host, int port) => BuildEndpoint(host, port);

    // ffmpeg-Ausgabeoptionen, die sich nicht über die URL setzen lassen.
    public virtual void ConfigureOutput(FFMpegArgumentOptions options) { }

    public virtual string Description => string.Empty;

    // Wirft nicht: Fehler werden als TransmissionResult.Failed zurückgegeben.
    public async Task<TransmissionResult> TransmitAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return TransmissionResult.Failed("Kein Eingabepfad angegeben.");

        if (!File.Exists(inputPath))
            return TransmissionResult.Failed($"Eingabedatei nicht gefunden: {inputPath}");

        if (string.IsNullOrWhiteSpace(host))
            return TransmissionResult.Failed("Kein Zielhost angegeben.");

        if (port is < 1 or > 65535)
            return TransmissionResult.Failed($"Ungültiger Port: {port}");

        var endpoint = BuildEndpoint(host, port);

        Debug.WriteLine("Übertragung gestartet: " + Name);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bytesSent = await ExecuteTransmissionAsync(inputPath, host, port, cancellationToken);
            stopwatch.Stop();
            return TransmissionResult.Succeeded(endpoint, bytesSent, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return TransmissionResult.Failed("Übertragung abgebrochen.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return TransmissionResult.Failed(ex.Message, stopwatch.Elapsed);
        }
    }

    protected abstract Task<long> ExecuteTransmissionAsync(
        string inputPath,
        string host,
        int port,
        CancellationToken cancellationToken);

    // Sendet die Datei mit -re in Echtzeit; Rückgabe: Eingabegröße (nicht neu codiert).
    protected static async Task<long> SendViaFFMpegAsync(
        string inputPath,
        string outputUrl,
        string format,
        Action<FFMpegArgumentOptions> configureOutput,
        CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(inputPath, verifyExists: true, inputOptions => inputOptions
                .WithCustomArgument("-re")) // native Framerate = Echtzeit-Stream
            .OutputToUrl(outputUrl, outputOptions =>
            {
                outputOptions.ForceFormat(format);
                configureOutput(outputOptions);
                if (format is "mpegts" or "rtp_mpegts") // MPEG-TS-Mux entpuffern
                    outputOptions.WithCustomArgument("-muxdelay 0 -muxpreload 0 -flush_packets 1");
            })
            .NotifyOnError(line => Metrics.MetricsService.Current.RecordSenderLine(line))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously();

        return new FileInfo(inputPath).Length;
    }

    protected string BuildEndpoint(string host, int port)
        => string.IsNullOrEmpty(UrlScheme) ? $"{host}:{port}" : $"{UrlScheme}{host}:{port}";

    public override string ToString() => Name;
}
