using System.Diagnostics;
using System.Text.RegularExpressions;
using FFMpegCore;

namespace BA.Services.Camera;

/// <summary>
/// Ermittelt verfügbare Kameras – auf Windows über die DirectShow-Geräteliste von ffmpeg
/// </summary>
public static partial class CameraService
{
    public static async Task<IReadOnlyList<string>> ListCamerasAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        try
        {
            var psi = new ProcessStartInfo(ResolveFFmpegPath(), "-hide_banner -list_devices true -f dshow -i dummy")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return Array.Empty<string>();

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return ParseDshowVideoDevices(stderr);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Kamera] Auflisten fehlgeschlagen: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static string ResolveFFmpegPath()
    {
        var folder = GlobalFFOptions.Current.BinaryFolder;
        return string.IsNullOrWhiteSpace(folder) ? "ffmpeg" : Path.Combine(folder, "ffmpeg.exe");
    }

    private static IReadOnlyList<string> ParseDshowVideoDevices(string ffmpegStderr)
    {
        var devices = new List<string>();
        foreach (Match match in DshowVideoLine().Matches(ffmpegStderr))
        {
            var name = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(name) && !devices.Contains(name))
                devices.Add(name);
        }
        return devices;
    }

    // Zeilen wie:  [dshow @ ...] "Integrated Camera" (video)
    [GeneratedRegex("\"([^\"]+)\"\\s*\\(video\\)")]
    private static partial Regex DshowVideoLine();
}
