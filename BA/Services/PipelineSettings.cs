using CommunityToolkit.Mvvm.ComponentModel;

namespace BA.Services;

/// <summary>
/// Zentrale, per UI (Slider) einstellbare Latenz-Hebel, beim Start des jeweiligen ffmpeg-/VLC-Vorgangs gelesen
/// (Änderungen wirken ab dem nächsten Start). Singleton, damit Sende- und Empfangsseite dieselben Werte nutzen.
/// </summary>
public partial class PipelineSettings : ObservableObject
{
    public static PipelineSettings Current { get; } = new();

    // Default 400; VLC-Standard wäre 1000.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VlcCachingDisplay))]
    public partial double VlcCachingMs { get; set; } = 400;

    // Sender und Empfänger. Default 20; SRT-Standard wäre 120.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SrtLatencyDisplay))]
    public partial double SrtLatencyMs { get; set; } = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GopDisplay))]
    public partial double GopSize { get; set; } = 15;

    // AUS = CRF (konstante Qualität), AN = capped VBR ("CBR") für faire Vergleiche bei gleicher Bitrate.
    // Die konkreten Flags baut jeder Codec in ApplyLiveEncoding.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateControlLabel))]
    [NotifyPropertyChangedFor(nameof(RateControlDisplay))]
    public partial bool CbrEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetBitrateDisplay))]
    [NotifyPropertyChangedFor(nameof(RateControlDisplay))]
    public partial double TargetBitrateKbps { get; set; } = 4000;

    // Default 23 (x264-Referenz).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrfDisplay))]
    [NotifyPropertyChangedFor(nameof(RateControlDisplay))]
    public partial double CrfValue { get; set; } = 23;

    // bufsize = maxrate × Faktor. Default 1.0 (straff, latenzfreundlich); größere Werte glätten die Bitrate,
    // erhöhen aber die Pufferlatenz.
    public double BufsizeFactor { get; set; } = 1.0;

    public bool UseSrtSharp { get; set; } = true;

    // Sender und Empfänger geben pro Frame eine showinfo-Zeile aus; über den Frame-Index und den Uhr-Offset
    // zusammengeführt ergibt das Encode+Transport+Decode. Empfang dabei ohne Bild.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureLatencyDisplay))]
    public partial bool MeasureLatencyEnabled { get; set; }

    public string MeasureLatencyDisplay => MeasureLatencyEnabled ? "ein (Empfang ohne Bild)" : "aus";

    // Sender sendet die Quelle einmal für MeasureDurationSeconds statt in Endlosschleife. Voraussetzung für
    // die Full-Reference-Qualität (Frame-0-Abgleich mit dem Original).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiniteMeasurementDisplay))]
    public partial bool FiniteMeasurement { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureDurationDisplay))]
    public partial double MeasureDurationSeconds { get; set; } = 10;

    // Empfänger schneidet den Stream verlustfrei mit und berechnet danach offline SSIM/PSNR/VMAF gegen das
    // lokale Original. Ohne Bild; sinnvoll mit FiniteMeasurement.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityMeasurementDisplay))]
    public partial bool QualityMeasurementEnabled { get; set; }

    // Leer = automatisch aus dem mitgelieferten TestVideos-Ordner bzw. manuell wählen.
    public string SourceFolder { get; set; } = string.Empty;

    public string FiniteMeasurementDisplay => FiniteMeasurement ? $"ein ({MeasureDurationSeconds:0} s)" : "aus (Endlosschleife)";
    public string MeasureDurationDisplay => $"{MeasureDurationSeconds:0} s";
    public string QualityMeasurementDisplay => QualityMeasurementEnabled ? "ein (Empfang ohne Bild)" : "aus";
    public int MeasureDuration => (int)MeasureDurationSeconds;

    // Der Harness setzt den Messmodus pro Sub-Lauf hierüber, ohne die UI-Schalter umzulegen (null = kein Harness).
    public bool? HarnessMeasureLatencyOverride { get; set; }
    public bool? HarnessQualityMeasurementOverride { get; set; }

    public bool EffectiveMeasureLatency => HarnessMeasureLatencyOverride ?? MeasureLatencyEnabled;
    public bool EffectiveQualityMeasurement => HarnessQualityMeasurementOverride ?? QualityMeasurementEnabled;

    // Ganzzahlige Werte für die ffmpeg-/VLC-Argumente.
    public int VlcCaching => (int)VlcCachingMs;
    public int SrtLatency => (int)SrtLatencyMs;
    public int Gop => (int)GopSize;
    public int TargetBitrate => (int)TargetBitrateKbps;
    public int Crf => (int)CrfValue;
    public int Bufsize => (int)Math.Max(1, TargetBitrateKbps * BufsizeFactor);

    public string RateControlLabel => CbrEnabled ? "CBR" : "CRF";

    // Für die CSV; nur im CBR-Modus sinnvoll, sonst 0.
    public int RunTargetBitrateKbps => CbrEnabled ? TargetBitrate : 0;

    public string VlcCachingDisplay => $"{VlcCaching} ms";
    public string SrtLatencyDisplay => $"{SrtLatency} ms";
    public string GopDisplay => $"{Gop} Frames";
    public string TargetBitrateDisplay => $"{TargetBitrate} kbps";
    public string CrfDisplay => $"CRF {Crf}";
    public string RateControlDisplay => CbrEnabled ? $"CBR · {TargetBitrate} kbps" : $"CRF {Crf}";
}
