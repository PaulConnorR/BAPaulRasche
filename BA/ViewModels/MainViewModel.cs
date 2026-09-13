using BA.Models;
using BA.Services;
using BA.Services.Camera;
using BA.Services.Compression;
using BA.Services.Metrics;
using BA.Services.Transmission;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;

namespace BA.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    public VideoInfo LeftInfo { get; } = new();

    public VideoInfo RightInfo { get; } = new();

    public IReadOnlyList<VideoCompressor> CompressionMethods { get; } = new VideoCompressor[]
    {
        new H264Compressor(),
        new H265Compressor(),
        new Av1Compressor(),
        new Vp9Compressor(),
    };

    public IReadOnlyList<VideoTransmitter> TransmissionProtocols { get; } = new VideoTransmitter[]
    {
        new SrtTransmitter(),
        new RtpUdpTransmitter(),
        new RistTransmitter(),
        new WebRtcTransmitter(),
    };

    [ObservableProperty]
    public partial MediaSource? LeftSource { get; set; }

    [ObservableProperty]
    public partial MediaSource? RightSource { get; set; }

    [ObservableProperty]
    public partial VideoCompressor? SelectedCompression { get; set; }

    [ObservableProperty]
    public partial VideoTransmitter? SelectedProtocol { get; set; }

    [ObservableProperty]
    public partial string TargetHost { get; set; } = "192.168.2.174";

    [ObservableProperty]
    public partial string TargetPortText { get; set; } = "9000";

    [ObservableProperty]
    public partial string NetworkConditionText { get; set; } = "Baseline";

    public IReadOnlyList<ClumsyPreset> ClumsyPresets { get; } = ClumsyPreset.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClumsyCommand))]
    public partial ClumsyPreset? SelectedClumsyPreset { get; set; }

    // Fertiger clumsy-Aufruf zum Kopieren (clumsy wird manuell gestartet).
    public string ClumsyCommand => SelectedClumsyPreset?.BuildCommand(BasePort) ?? "— Preset wählen —";

    private int BasePort => int.TryParse(TargetPortText, out var p) ? p : 9000;

    partial void OnSelectedClumsyPresetChanged(ClumsyPreset? value)
    {
        if (value is not null)
            NetworkConditionText = value.Name;
    }

    partial void OnTargetPortTextChanged(string value) => OnPropertyChanged(nameof(ClumsyCommand));

    [RelayCommand]
    private Task CopyClumsyCommandAsync() => Clipboard.SetTextAsync(ClumsyCommand);

    public IReadOnlyList<string> WlanBands { get; } = new[] { "—", "2,4 GHz", "5 GHz" };

    [ObservableProperty]
    public partial string WlanBandText { get; set; } = "5 GHz";

    // Loopback: Sender schickt an 127.0.0.1, Anzeige über die Empfangs-Seite dieser App. Rückkanal entfällt
    // (beide Pfade nutzen dieselbe MetricsService-Instanz); Messreihe als "Loopback" markiert.
    [ObservableProperty]
    public partial bool OnePcTest { get; set; }

    // Sender hält den Rückkanal-Server offen und steuert einen Empfänger fern (Empfang startet vor dem
    // Senden = Frame-0-Abgleich)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HarnessButtonText))]
    public partial bool HarnessMode { get; set; }

    public string HarnessButtonText => HarnessMode ? "Harness-Server aktiv" : "Harness-Server (Empfänger fernsteuern)";

    // Rückkanal-Server früh starten/stoppen, damit sich der Empfänger schon vor dem ersten Lauf verbinden kann.
    partial void OnHarnessModeChanged(bool value)
    {
        if (value)
            _collector.Start();
        else
            _ = _collector.StopAsync();
    }

    // Automatische Messreihe (Harness): Achsen der Matrix.
    [ObservableProperty] public partial string HarnessGopList { get; set; } = "1,15,30";
    [ObservableProperty] public partial string HarnessRepetitionsText { get; set; } = "1";
    [ObservableProperty] public partial string HarnessWarmupText { get; set; } = "3";
    [ObservableProperty] public partial bool HarnessUseCrf { get; set; } = true;
    [ObservableProperty] public partial bool HarnessUseCbr { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HarnessRunButtonText))]
    public partial bool HarnessRunning { get; set; }

    [ObservableProperty] public partial string HarnessProgress { get; set; } = "—";

    public string HarnessRunButtonText => HarnessRunning ? "Auto-Messreihe stoppen" : "Auto-Messreihe starten";

    private CancellationTokenSource? _harnessCts;

    /// <summary>
    /// Fährt die Matrix Codec × Protokoll × GOP × Ratenmodus × Messmodus × Wiederholung ab (feste Quelle,
    /// endliche Sequenz je Lauf, Empfänger ferngesteuert) und schreibt eine aggregierte CSV. Die Netzbedingung
    /// bleibt fest; für die Netz-Achse die Reihe je clumsy-Preset erneut fahren.
    /// </summary>
    [RelayCommand]
    private async Task RunAutoMeasurementAsync()
    {
        if (HarnessRunning)
        {
            _harnessCts?.Cancel();
            return;
        }

        if (LeftInfo.FilePath is not { } inputPath || !File.Exists(inputPath))
        {
            HarnessProgress = "Kein Video geladen.";
            return;
        }
        if (!HarnessMode)
        {
            HarnessProgress = "Harness-Server aus – bitte einschalten (Empfänger fernsteuern).";
            return;
        }
        if (!MetricsService.Current.RemoteReceiverConnected)
        {
            HarnessProgress = "Kein Empfänger verbunden (Empfangsseite: Harness-Modus starten).";
            return;
        }
        if (IsFileStreaming || IsCameraStreaming)
        {
            HarnessProgress = "Bitte laufende Übertragung zuerst stoppen.";
            return;
        }

        _ = int.TryParse(TargetPortText, out var port);
        var gops = ParseGops(HarnessGopList);
        _ = int.TryParse(HarnessRepetitionsText, out var reps);
        reps = Math.Max(1, reps);
        _ = int.TryParse(HarnessWarmupText, out var warmup);
        warmup = Math.Max(0, warmup);
        var duration = PipelineSettings.Current.MeasureDuration;

        var rateModes = new List<bool>();
        if (HarnessUseCrf) rateModes.Add(false); // false = CRF
        if (HarnessUseCbr) rateModes.Add(true);
        if (rateModes.Count == 0) rateModes.Add(false);

        // Messmodus-Achse: jede aktive Messart = eigener Sub-Lauf.
        var modes = new List<string>();
        if (PipelineSettings.Current.MeasureLatencyEnabled) modes.Add("Latenz");
        if (PipelineSettings.Current.QualityMeasurementEnabled) modes.Add("Qualität");
        if (modes.Count == 0) modes.Add("Durchsatz");

        var protocols = TransmissionProtocols.Where(p => p.SupportsLiveCamera).ToList();
        var codecs = CompressionMethods.ToList();

        int total = rateModes.Count * codecs.Count * protocols.Count * gops.Count * modes.Count * reps;
        if (total == 0)
        {
            HarnessProgress = "Leere Matrix (GOP-Liste prüfen).";
            return;
        }

        var s = PipelineSettings.Current;
        var (origGop, origCbr, origMeas, origQual, origFinite) =
            (s.GopSize, s.CbrEnabled, s.MeasureLatencyEnabled, s.QualityMeasurementEnabled, s.FiniteMeasurement);
        s.FiniteMeasurement = true; // Harness misst immer endliche Sequenzen

        _harnessCts = new CancellationTokenSource();
        var ct = _harnessCts.Token;
        HarnessRunning = true;
        var summaries = new List<RunSummary>();
        int done = 0;

        try
        {
            foreach (var cbr in rateModes)
            foreach (var codec in codecs)
            foreach (var transmitter in protocols)
            foreach (var gop in gops)
            foreach (var mode in modes)
            for (int r = 0; r < reps; r++)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                HarnessProgress =
                    $"Lauf {done}/{total}: {codec.CodecId} · {transmitter.ProtocolId} · GOP {gop} · " +
                    $"{(cbr ? "CBR" : "CRF")} · {mode} (Wdh {r + 1}/{reps})";

                try
                {
                    var summary = await RunSingleHarnessRunAsync(
                        inputPath, codec, transmitter, gop, cbr, mode, warmup, duration, port, ct);
                    summaries.Add(summary);

                    if (summary.ReceivedSsim is not null || summary.ReceivedPsnr is not null || summary.ReceivedVmaf is not null)
                        HarnessProgress += $" · Qualität: SSIM {summary.ReceivedSsim?.ToString("0.000") ?? "—"}" +
                            $" / PSNR {(summary.ReceivedPsnr is { } ps ? ps.ToString("0.0") + " dB" : "—")}" +
                            $" / VMAF {summary.ReceivedVmaf?.ToString("0.0") ?? "—"}";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Einzelnen Lauf überspringen, aufräumen und mit der nächsten Kombination weiter.
                    Debug.WriteLine($"[Harness] Lauf {done}/{total} fehlgeschlagen: {ex.Message}");
                    try { await _fileStream.StopAsync(); } catch { }
                    try { await StopRemoteReceiverAsync(); } catch { }
                    IsFileStreaming = false;
                }
            }

            HarnessProgress = $"Fertig: {summaries.Count} Läufe.";
        }
        catch (OperationCanceledException)
        {
            HarnessProgress = $"Abgebrochen nach {summaries.Count} Läufen.";
        }
        catch (Exception ex)
        {
            HarnessProgress = $"Fehler: {ex.Message}";
        }
        finally
        {
            try { await _fileStream.StopAsync(); } catch { }
            try { await StopRemoteReceiverAsync(); } catch { }
            IsFileStreaming = false;

            // Einstellungen wiederherstellen und Harness-Überschreibungen aufheben (wieder UI-Schalter maßgeblich).
            s.HarnessMeasureLatencyOverride = null;
            s.HarnessQualityMeasurementOverride = null;
            s.GopSize = origGop; s.CbrEnabled = origCbr;
            s.MeasureLatencyEnabled = origMeas; s.QualityMeasurementEnabled = origQual;
            s.FiniteMeasurement = origFinite;

            if (summaries.Count > 0)
            {
                try
                {
                    var path = HarnessReport.WriteSummaryCsv(summaries);
                    HarnessProgress += $" · CSV: {path}";
                }
                catch (Exception ex)
                {
                    HarnessProgress += $" · CSV-Export fehlgeschlagen: {ex.Message}";
                }
            }

            HarnessRunning = false;
            _harnessCts?.Dispose();
            _harnessCts = null;
        }
    }

    private async Task<RunSummary> RunSingleHarnessRunAsync(
        string inputPath, VideoCompressor codec, VideoTransmitter transmitter, int gop, bool cbr,
        string mode, int warmup, int duration, int port, CancellationToken ct)
    {
        var s = PipelineSettings.Current;
        s.GopSize = gop;
        s.CbrEnabled = cbr;
        // Messmodus NUR über die Überschreibung setzen, nicht die UI-Schalter umlegen (sonst springt der
        // Toggle im Betrieb). Pipeline/Steuerkanal lesen Effective*.
        s.HarnessMeasureLatencyOverride = mode == "Latenz";
        s.HarnessQualityMeasurementOverride = mode == "Qualität";

        var run = RunContext.New(codec.CodecId, transmitter.ProtocolId, gop,
            LeftInfo.Width, LeftInfo.Height, RunNetworkCondition, LeftInfo.FileName,
            s.RateControlLabel, s.RunTargetBitrateKbps, WlanBandText);

        StartLiveAuxiliaries(run);
        var mediaPort = MediaPorts.For(port, transmitter.ProtocolId);
        await PrepareRemoteReceiverAsync(run, mediaPort);

        _fileStream.Start(inputPath, codec, transmitter, EffectiveHost, mediaPort);
        IsFileStreaming = true;

        // WebRTC startet den Encoder erst nach dem Datenkanal-Aufbau – erst danach das Messfenster starten,
        // sonst fiele die Messdauer in den Verbindungsaufbau. Andere Protokolle: no-op.
        await _fileStream.WaitUntilReadyAsync(TimeSpan.FromSeconds(15));

        // Endliche Sequenz: der Sender endet nach der Messdauer selbst (-t); mit etwas Reserve stoppen.
        try { await Task.Delay(TimeSpan.FromSeconds(duration + 1), ct); }
        finally
        {
            await _fileStream.StopAsync();
            IsFileStreaming = false;
            await StopRemoteReceiverAsync();
        }

        if (mode == "Qualität")
            await WaitForReceivedQualityAsync(ct);
        else
            await Task.Delay(1500, ct);

        var samples = MetricsService.Current.SnapshotSamples();
        var latencySamples = MetricsService.Current.SnapshotLatencySamples();
        return HarnessAggregator.Summarize(
            run, samples, latencySamples, warmup, mode, duration,
            MetricsService.Current.ReceivedSsimValue,
            MetricsService.Current.ReceivedPsnrValue,
            MetricsService.Current.ReceivedVmafValue);
    }

    // Großzügiger Timeout, weil die VMAF-Berechnung bei hoher Auflösung bis ~1 min dauern kann. (Eigentlich nicht mehr nötig aber sicher ist sicher)
    private async Task WaitForReceivedQualityAsync(CancellationToken ct)
    {
        var runId = MetricsService.Current.CurrentRun.RunId;
        for (int i = 0; i < 180; i++) // 90 s
        {
            if (MetricsService.Current.ReceivedQualityRunId == runId)
                return;
            await Task.Delay(500, ct);
        }
    }

    private static List<int> ParseGops(string list) => list
        .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => int.TryParse(t, out var g) ? g : -1)
        .Where(g => g >= 1)
        .Distinct()
        .ToList();

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    private const string LoopbackHost = "127.0.0.1";

    private readonly CameraStreamService _cameraStream = new();
    private readonly FileStreamService _fileStream = new();

    // Rückkanal-Server: nimmt die Messwerte des Empfänger-PCs entgegen (nur Zwei-PC).
    private readonly MetricsCollector _collector = new();

    private string EffectiveHost => OnePcTest ? LoopbackHost : TargetHost;
    private string RunNetworkCondition => OnePcTest ? "Loopback (Ein-PC-Test)" : NetworkConditionText;

    [ObservableProperty]
    public partial IReadOnlyList<string> Cameras { get; set; } = Array.Empty<string>();

    [ObservableProperty]
    public partial string? SelectedCamera { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraStreamButtonText))]
    public partial bool IsCameraStreaming { get; set; }

    public string CameraStreamButtonText => IsCameraStreaming ? "Live-Übertragung stoppen" : "Kamera live übertragen";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileStreamButtonText))]
    public partial bool IsFileStreaming { get; set; }

    public string FileStreamButtonText => IsFileStreaming ? "Datei-Live stoppen" : "Datei live übertragen (einstufig)";

    // Vorlauf jedes Sende-Vorgangs: Messlauf eröffnen und – nur im Zwei-PC-Betrieb – den Rückkanal-Server starten.
    private void StartLiveAuxiliaries(RunContext run)
    {
        MetricsService.Current.BeginRun(run, resetSamples: true);
        if (!OnePcTest)
        {
            _collector.Start();
            WebRtcSignaling.Send = sig => _ = _collector.SendWebRtcAsync(sig);
        }
    }

    // Rückkanal-Server stoppen, aber NICHT im Harness-Modus (dort bleibt er über einzelne Läufe bestehen).
    private async Task StopLiveAuxiliariesAsync()
    {
        if (!HarnessMode)
            await _collector.StopAsync();
    }

    // Teilt dem Empfänger Protokoll/Port/Quelle mit und wartet auf "ready"; ohne Empfänger wird nach Timeout trotzdem gesendet.
    private async Task PrepareRemoteReceiverAsync(RunContext run, int port)
    {
        if (!HarnessMode || OnePcTest)
            return;

        var cmd = new ControlCommand(
            "prepare", run.Protocol, port, run.Codec, run.Gop,
            run.RateControl, run.TargetBitrateKbps, run.SourceName,
            run.NetworkCondition, PipelineSettings.Current.EffectiveMeasureLatency, run.RunId,
            RecordQuality: PipelineSettings.Current.EffectiveQualityMeasurement);

        var ready = await _collector.SendControlAsync(cmd, TimeSpan.FromSeconds(8));
        if (ready)
        {
            await Task.Delay(500); // Nachlaufzeit, damit der ffmpeg-Listener des Empfängers wirklich lauscht
            Debug.WriteLine("[Harness] Empfänger bereit – sende.");
        }
        else
        {
            Debug.WriteLine("[Harness] Kein 'ready' (Timeout/kein Empfänger) – sende trotzdem.");
        }
    }

    private async Task StopRemoteReceiverAsync()
    {
        if (!HarnessMode || OnePcTest)
            return;
        try { await _collector.SendControlAsync(new ControlCommand("stop"), TimeSpan.Zero); }
        catch { }
    }

    public MainViewModel()
    {
        Title = "Senden";
        SelectedCompression = CompressionMethods[0];
        SelectedProtocol = TransmissionProtocols[0];
        SelectedClumsyPreset = ClumsyPresets[0];

        _ = LoadCamerasAsync();
    }

    [RelayCommand]
    private async Task LoadCamerasAsync()
    {
        Cameras = await CameraService.ListCamerasAsync();
        SelectedCamera ??= Cameras.Count > 0 ? Cameras[0] : null;
        Debug.WriteLine($"[Kamera] {Cameras.Count} Kamera(s) gefunden.");
    }

    [RelayCommand]
    private async Task ToggleCameraStreamAsync()
    {
        if (IsCameraStreaming)
        {
            await _cameraStream.StopAsync();
            await StopRemoteReceiverAsync();
            await StopLiveAuxiliariesAsync();
            IsCameraStreaming = false;
            Debug.WriteLine("[Kamera-Live] Gestoppt.");
            return;
        }

        if (IsFileStreaming)
        {
            Debug.WriteLine("[Kamera-Live] Datei-Live läuft bereits – bitte zuerst stoppen.");
            return;
        }

        if (SelectedCamera is not { } camera)
        {
            Debug.WriteLine("[Kamera-Live] Keine Kamera ausgewählt.");
            return;
        }
        if (SelectedCompression is not { } compressor)
        {
            Debug.WriteLine("[Kamera-Live] Kein Kompressionsverfahren ausgewählt.");
            return;
        }
        if (SelectedProtocol is not { } transmitter)
        {
            Debug.WriteLine("[Kamera-Live] Kein Protokoll ausgewählt.");
            return;
        }
        _ = int.TryParse(TargetPortText, out var port);

        try
        {
            var run = RunContext.New(compressor.CodecId, transmitter.ProtocolId, PipelineSettings.Current.Gop,
                0, 0, RunNetworkCondition, camera,
                PipelineSettings.Current.RateControlLabel, PipelineSettings.Current.RunTargetBitrateKbps, WlanBandText);
            StartLiveAuxiliaries(run);

            var mediaPort = MediaPorts.For(port, transmitter.ProtocolId);
            await PrepareRemoteReceiverAsync(run, mediaPort);

            var target = _cameraStream.Start(camera, compressor, transmitter, EffectiveHost, mediaPort);
            IsCameraStreaming = true;
            Debug.WriteLine(
                $"[Kamera-Live] Start ({(OnePcTest ? "Ein-PC-Test" : "Netzwerk")}) – Kamera={camera}; " +
                $"Verfahren={compressor.Name}; Protokoll={transmitter.Name}; Ziel={target}");
        }
        catch (Exception ex)
        {
            await _cameraStream.StopAsync();
            await StopRemoteReceiverAsync();
            await StopLiveAuxiliariesAsync();
            IsCameraStreaming = false;
            Debug.WriteLine($"[Kamera-Live] Fehlgeschlagen: {ex.Message}");
        }
    }

    // Live encodierte Übertragung der Originaldatei (LeftInfo), nicht des vorab komprimierten Ergebnisses.
    [RelayCommand]
    private async Task ToggleFileStreamAsync()
    {
        if (IsFileStreaming)
        {
            await _fileStream.StopAsync();
            await StopRemoteReceiverAsync();
            await StopLiveAuxiliariesAsync();
            IsFileStreaming = false;
            Debug.WriteLine("[Datei-Live] Gestoppt.");
            return;
        }

        if (IsCameraStreaming)
        {
            Debug.WriteLine("[Datei-Live] Kamera-Live läuft bereits – bitte zuerst stoppen.");
            return;
        }

        if (LeftInfo.FilePath is not { } inputPath)
        {
            Debug.WriteLine("[Datei-Live] Kein Video geladen (bitte zuerst laden).");
            return;
        }
        if (SelectedCompression is not { } compressor)
        {
            Debug.WriteLine("[Datei-Live] Kein Kompressionsverfahren ausgewählt.");
            return;
        }
        if (SelectedProtocol is not { } transmitter)
        {
            Debug.WriteLine("[Datei-Live] Kein Protokoll ausgewählt.");
            return;
        }
        _ = int.TryParse(TargetPortText, out var port);

        try
        {
            var run = RunContext.New(compressor.CodecId, transmitter.ProtocolId, PipelineSettings.Current.Gop,
                LeftInfo.Width, LeftInfo.Height, RunNetworkCondition, LeftInfo.FileName,
                PipelineSettings.Current.RateControlLabel, PipelineSettings.Current.RunTargetBitrateKbps, WlanBandText);
            StartLiveAuxiliaries(run);

            var mediaPort = MediaPorts.For(port, transmitter.ProtocolId);
            await PrepareRemoteReceiverAsync(run, mediaPort);

            var target = _fileStream.Start(inputPath, compressor, transmitter, EffectiveHost, mediaPort);
            IsFileStreaming = true;
            Debug.WriteLine(
                $"[Datei-Live] Start ({(OnePcTest ? "Ein-PC-Test" : "Netzwerk")}) – Datei={LeftInfo.FileName}; " +
                $"Verfahren={compressor.Name}; Protokoll={transmitter.Name}; Ziel={target}");
        }
        catch (Exception ex)
        {
            await _fileStream.StopAsync();
            await StopRemoteReceiverAsync();
            await StopLiveAuxiliariesAsync();
            IsFileStreaming = false;
            Debug.WriteLine($"[Datei-Live] Fehlgeschlagen: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PickVideoAsync()
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v" },
            [DevicePlatform.macOS] = new[] { "mp4", "mov", "mkv", "avi", "webm", "m4v" },
            [DevicePlatform.iOS] = new[] { "public.movie" },
            [DevicePlatform.Android] = new[] { "video/*" },
        });

        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Video auswählen",
            FileTypes = fileTypes,
        });

        if (result is null)
            return;

        var file = new FileInfo(result.FullPath);

        LeftInfo.FilePath = result.FullPath;
        LeftInfo.FileName = result.FileName;
        LeftInfo.FileSizeBytes = file.Exists ? file.Length : 0;
        LeftInfo.Width = 0;
        LeftInfo.Height = 0;
        LeftInfo.Duration = TimeSpan.Zero;

        LeftSource = MediaSource.FromFile(result.FullPath);
    }

    public void UpdateMediaMetadata(VideoInfo target, int width, int height, TimeSpan duration)
    {
        target.Width = width;
        target.Height = height;
        target.Duration = duration;
    }

    [RelayCommand]
    private async Task CompressAsync(string side)
    {
        var source = side == "right" ? RightInfo : LeftInfo;

        if (SelectedCompression is not { } compressor)
        {
            Debug.WriteLine("[Komprimierung] Kein Verfahren ausgewählt.");
            return;
        }

        if (source.FilePath is not { } inputPath)
        {
            Debug.WriteLine($"[Komprimierung] Seite={side}: kein Video geladen.");
            return;
        }

        var outputPath = BuildOutputPath(inputPath, compressor);
        Debug.WriteLine(
            $"[Komprimierung] Start – Seite={side}; Datei={source.FileName}; " +
            $"Verfahren={compressor.Name} ({compressor.CodecId}); Ziel={outputPath}");

        IsBusy = true;
        BusyText = "Kompression läuft …";
        try
        {
            var result = await compressor.CompressAsync(inputPath, outputPath);

            RightInfo.ProcessingTime = result.ProcessingTime;

            if (!result.Success)
            {
                Debug.WriteLine($"[Komprimierung] Fehlgeschlagen: {result.ErrorMessage}");
                return;
            }

            Debug.WriteLine(
                $"[Komprimierung] Fertig in {result.ProcessingTime.TotalMilliseconds:F0} ms; " +
                $"Ausgabegröße={result.OutputSizeBytes} B");

            RightInfo.FilePath = result.OutputPath;
            RightInfo.FileName = Path.GetFileName(result.OutputPath!);
            RightInfo.FileSizeBytes = result.OutputSizeBytes;
            RightInfo.Width = 0;
            RightInfo.Height = 0;
            RightInfo.Duration = TimeSpan.Zero;

            RightSource = MediaSource.FromFile(result.OutputPath!);

            BusyText = "SSIM wird berechnet …";
            var ssim = await SsimAnalyzer.ComputeSsimAsync(inputPath, result.OutputPath!);
            MetricsService.Current.SetSsim(ssim);
            Debug.WriteLine($"[Komprimierung] SSIM={ssim?.ToString("0.0000") ?? "n/v"}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartTransmissionAsync()
    {
        if (SelectedProtocol is not { } transmitter)
        {
            Debug.WriteLine("[Übertragung] Kein Protokoll ausgewählt.");
            return;
        }

        var source = RightInfo.FilePath is not null ? RightInfo : LeftInfo; // komprimiertes Ergebnis bevorzugt
        if (source.FilePath is not { } inputPath)
        {
            Debug.WriteLine("[Übertragung] Kein Video vorhanden (bitte laden bzw. komprimieren).");
            return;
        }

        _ = int.TryParse(TargetPortText, out var port);
        var mediaPort = MediaPorts.For(port, transmitter.ProtocolId);

        Debug.WriteLine(
            $"[Übertragung] Start ({(OnePcTest ? "Ein-PC-Test" : "Netzwerk")}) – " +
            $"Protokoll={transmitter.Name} ({transmitter.ProtocolId}); " +
            $"Ziel={transmitter.UrlScheme}{EffectiveHost}:{mediaPort}; Quelle={source.FileName}");

        var codecId = SelectedCompression?.CodecId ?? "—";
        StartLiveAuxiliaries(
            RunContext.New(codecId, transmitter.ProtocolId, PipelineSettings.Current.Gop,
                source.Width, source.Height, RunNetworkCondition, source.FileName, wlanBand: WlanBandText));

        IsBusy = true;
        BusyText = "Übertragung läuft …";
        try
        {
            var result = await transmitter.TransmitAsync(inputPath, EffectiveHost, mediaPort);

            if (!result.Success)
            {
                Debug.WriteLine($"[Übertragung] Fehlgeschlagen: {result.ErrorMessage}");
                return;
            }

            Debug.WriteLine(
                $"[Übertragung] Fertig in {result.TransmissionTime.TotalMilliseconds:F0} ms; " +
                $"gesendet={result.BytesSent} B; Endpunkt={result.Endpoint}");
        }
        finally
        {
            IsBusy = false;
            await StopLiveAuxiliariesAsync();
        }
    }

    private static string BuildOutputPath(string inputPath, VideoCompressor compressor)
    {
        var name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(
            FileSystem.CacheDirectory,
            $"{name}_{compressor.CodecId}{compressor.FileExtension}");
    }
}
