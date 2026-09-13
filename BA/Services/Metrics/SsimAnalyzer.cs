using System.Globalization;
using System.Text.RegularExpressions;
using FFMpegCore;

namespace BA.Services.Metrics;

// Berechnet SSIM (0..1) zwischen Referenz und verzerrter Datei via ffmpeg-ssim-Filter
public static partial class SsimAnalyzer
{
    public static async Task<double?> ComputeSsimAsync(string referencePath, string distortedPath)
    {
        if (!File.Exists(referencePath) || !File.Exists(distortedPath))
            return null;

        double? ssim = null;

        try
        {
            await FFMpegArguments
                .FromFileInput(distortedPath)
                .AddFileInput(referencePath)
                .OutputToFile("NUL", overwrite: true, options => options
                    .ForceFormat("null")
                    .WithCustomArgument("-lavfi [0:v][1:v]ssim"))
                .NotifyOnError(line =>
                {
                    var m = AllRx().Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        ssim = v;
                })
                .ProcessAsynchronously();
        }
        catch
        {
            return null;
        }

        return ssim;
    }

    [GeneratedRegex(@"All:\s*([\d.]+)")]
    private static partial Regex AllRx();
}
