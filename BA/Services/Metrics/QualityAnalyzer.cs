using System.Globalization;
using System.Text.RegularExpressions;
using FFMpegCore;

namespace BA.Services.Metrics;

public sealed record QualityResult(double? Ssim, double? Psnr, double? Vmaf);

/// <summary>
/// Berechnet die Full-Reference-Bildqualität des empfangenen Streams gegen das Original, offline nach dem Lauf, VMAF rausgenommen wegen Berechnungszeit
/// </summary>
public static partial class QualityAnalyzer
{
    public static async Task<QualityResult> ComputeAsync(string referencePath, string distortedPath)
    {
        if (!File.Exists(referencePath) || !File.Exists(distortedPath))
            return new QualityResult(null, null, null);

        var ssim = await RunFilterAsync(referencePath, distortedPath, "[0:v][1:v]ssim", SsimRx());
        var psnr = await RunFilterAsync(referencePath, distortedPath, "[0:v][1:v]psnr", PsnrRx());
        //var vmaf = await RunFilterAsync(referencePath, distortedPath, "[0:v][1:v]libvmaf", VmafRx());
        var vmaf = -1;
        return new QualityResult(ssim, psnr, vmaf);
    }

    // Führt EINE ffmpeg-Metrik aus und parst den Zahlenwert per rx aus stderr; Fehler (z.B. libvmaf fehlt) → null.
    private static async Task<double?> RunFilterAsync(string referencePath, string distortedPath, string lavfi, Regex rx)
    {
        double? value = null;
        try
        {
            await FFMpegArguments
                .FromFileInput(distortedPath)
                .AddFileInput(referencePath)
                .OutputToFile("NUL", overwrite: true, options => options
                    .ForceFormat("null")
                    .WithCustomArgument($"-lavfi {lavfi}"))
                .NotifyOnError(line =>
                {
                    var m = rx.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        value = v;
                })
                .ProcessAsynchronously();
        }
        catch
        {
            return null; // Filter nicht verfügbar (z.B. ffmpeg ohne libvmaf) oder Auswertung fehlgeschlagen.
        }

        return value;
    }

    // ssim-Filter:0.987654
    [GeneratedRegex(@"All:\s*([\d.]+)")]
    private static partial Regex SsimRx();

    // psnr-Filter:38.42 ..." 
    [GeneratedRegex(@"average:\s*([\d.]+)")]
    private static partial Regex PsnrRx();

    //VMAF score: 93.279722"
    [GeneratedRegex(@"VMAF score:\s*([\d.]+)")]
    private static partial Regex VmafRx();
}
