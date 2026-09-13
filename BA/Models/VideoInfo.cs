using CommunityToolkit.Mvvm.ComponentModel;

namespace BA.Models;

// Metadaten eines geladenen Videos: Dateiname/-größe aus dem Dateisystem, Auflösung/Dauer per Best-effort
// vom MediaElement nach MediaOpened.
public partial class VideoInfo : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideo))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeDisplay))]
    public partial long FileSizeBytes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionDisplay))]
    public partial int Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionDisplay))]
    public partial int Height { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessingTimeDisplay))]
    public partial TimeSpan? ProcessingTime { get; set; }

    public bool HasVideo => !string.IsNullOrEmpty(FilePath);

    public string FileSizeDisplay => FileSizeBytes > 0 ? FormatBytes(FileSizeBytes) : "—";

    public string ResolutionDisplay => Width > 0 && Height > 0 ? $"{Width} × {Height} px" : "—";

    public string DurationDisplay => Duration > TimeSpan.Zero ? Duration.ToString(@"hh\:mm\:ss") : "—";

    public string ProcessingTimeDisplay => ProcessingTime.HasValue ? $"{ProcessingTime.Value.TotalMilliseconds:F0} ms" : "—";

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
