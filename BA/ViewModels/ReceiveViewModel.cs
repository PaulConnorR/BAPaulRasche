using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BA.Models;
using BA.Services;
using BA.Services.Metrics;
using BA.Services.Reception;
using BA.Services.Transmission;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Maui.Dispatching;

namespace BA.ViewModels;

public partial class ReceiveViewModel : BaseViewModel
{
    private readonly StreamReceiver _receiver = new();
    private readonly LibVLC _libVLC = new(enableDebugLogs: true);
    private Media? _currentMedia;

    // Rückkanal-Client: meldet die Empfangskennzahlen an den Sender-PC.
    private readonly MetricsReporter _reporter = new();

    public MediaPlayer MediaPlayer { get; }

    public VideoInfo ReceivedInfo { get; } = new();

    public IReadOnlyList<VideoTransmitter> Protocols { get; } = new VideoTransmitter[]
    {
        new SrtTransmitter(),
        new RtpUdpTransmitter(),
        new RistTransmitter(),
        new WebRtcTransmitter(),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenEndpointDisplay))]
    public partial VideoTransmitter? SelectedProtocol { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenEndpointDisplay))]
    public partial string LocalIpAddress { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListenEndpointDisplay))]
    public partial string ListenPortText { get; set; } = "9000";

    // IP des Sender-PCs für den Rückkanal. Leer = kein Zurücksenden der Messwerte.
    [ObservableProperty]
    public partial string ReturnHostText { get; set; } = "192.168.2.188";

    // Referenz-Originaldatei für die Qualitätsmessung; sonst automatisch aus TestVideos bzw. Quellordner.
    [ObservableProperty]
    public partial string ReferencePath { get; set; } = string.Empty;

    private string? _recordPath;
    // RunId, damit die berechnete Qualität dem richtigen Lauf zugeordnet wird.
    private string _currentRunId = string.Empty;

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Getrennt";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveButtonText))]
    public partial bool IsReceiving { get; set; }

    // Ferngesteuert: der Empfänger verbindet den Rückkanal zum Sender, der Protokoll/Port/Quelle signalisiert;
    // Empfang startet automatisch vor dem Senden. Braucht die Sender-IP.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HarnessButtonText))]
    public partial bool HarnessMode { get; set; }

    public string HarnessButtonText => HarnessMode ? "Harness-Modus verlassen" : "Harness-Modus (ferngesteuert)";

    // Zeigt den Basis-Port; der gebundene Medien-Port (RTP=Basis+1, RIST=Basis+2) steht im DiagLog.
    public string ListenEndpointDisplay => $"{SelectedProtocol?.Name ?? "—"} · {LocalIpAddress}:{ListenPortText}";

    public string ReceiveButtonText => IsReceiving ? "Empfang stoppen" : "Empfang starten";

    public ReceiveViewModel()
    {
        Title = "Empfangen";
        MediaPlayer = new MediaPlayer(_libVLC);
        SelectedProtocol = Protocols[0];
        _reporter.OnControl = OnControlCommand;
        RefreshConnectionInfo();
    }

    [RelayCommand]
    private void RefreshConnectionInfo()
    {
        LocalIpAddress = GetLocalIPv4();
        Debug.WriteLine($"[Empfang] Verbindungsinfo aktualisiert: {ListenEndpointDisplay}");
    }

    [RelayCommand]
    private async Task ToggleReceivingAsync()
    {
        if (IsReceiving)
        {
            await StopReceptionAsync();
            ConnectionStatus = "Getrennt";
            Debug.WriteLine("[Empfang] Gestoppt.");
            return;
        }

        if (SelectedProtocol is not { } protocol)
        {
            Debug.WriteLine("[Empfang] Kein Protokoll ausgewählt.");
            return;
        }

        if (!int.TryParse(ListenPortText, out var basePort) || basePort is < 1 or > 65535)
        {
            Debug.WriteLine($"[Empfang] Ungültiger Port: {ListenPortText}");
            return;
        }

        // Denselben Medien-Port aus dem Basis-Port ableiten wie der Sender.
        var port = MediaPorts.For(basePort, protocol.ProtocolId);
        var measure = PipelineSettings.Current.MeasureLatencyEnabled;
        var recordQuality = PipelineSettings.Current.QualityMeasurementEnabled;
        // Empfänger kennt den Codec nicht → "—".
        var run = RunContext.New("—", protocol.ProtocolId, PipelineSettings.Current.Gop, 0, 0, "—", "—");
        await StartReceptionAsync(protocol.ProtocolId, port, measure, recordQuality, run);
    }

    // TestVideos-Ordner neben der App (per Git auf beiden PCs identisch) – Standard-Referenzquelle für die Qualität.
    private static string TestVideosFolder => Path.Combine(AppContext.BaseDirectory, "TestVideos");

    // Sucht die Referenz zu einem Quell-Dateinamen im Quellordner, dann in TestVideos; erster Treffer oder "".
    private static string FindReferenceInFolders(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName) || sourceName is "—")
            return string.Empty;
        foreach (var folder in new[] { PipelineSettings.Current.SourceFolder, TestVideosFolder })
        {
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            var candidate = Path.Combine(folder, sourceName);
            if (File.Exists(candidate))
                return candidate;
        }
        return string.Empty;
    }

    [RelayCommand]
    private async Task PickReferenceAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Referenz-Original wählen" });
        if (result is not null)
            ReferencePath = result.FullPath;
    }

    [RelayCommand]
    private async Task ToggleHarnessAsync()
    {
        if (HarnessMode)
        {
            await StopReceptionAsync();
            await _reporter.StopAsync();
            HarnessMode = false;
            ConnectionStatus = "Getrennt";
            Debug.WriteLine("[Harness] Modus verlassen.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReturnHostText))
        {
            ConnectionStatus = "Harness: Sender-IP (Rückkanal) fehlt";
            Debug.WriteLine("[Harness] Sender-IP fehlt – Modus nicht gestartet.");
            return;
        }

        HarnessMode = true;
        _reporter.Start(ReturnHostText.Trim());
        WebRtcSignaling.Send = sig => _reporter.SendWebRtc(sig);
        ConnectionStatus = "Harness: verbunden, warte auf Kommando";
        Debug.WriteLine($"[Harness] Verbunden mit Sender {ReturnHostText.Trim()}, warte auf Kommando.");
    }

    // Steuerbefehl des Senders verarbeiten (Hintergrund-Thread → auf UI-Thread marshallen).
    private void OnControlCommand(ControlCommand cmd)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                switch (cmd.Command)
                {
                    case "prepare":
                        await StopReceptionAsync(); // vorigen Lauf beenden (rechnet ggf. Qualität)
                        SelectedProtocol = Protocols.FirstOrDefault(p => p.ProtocolId == cmd.Protocol) ?? SelectedProtocol;
                        ListenPortText = cmd.Port.ToString();
                        PipelineSettings.Current.MeasureLatencyEnabled = cmd.MeasureLatency;

                        if (cmd.RecordQuality)
                        {
                            var auto = FindReferenceInFolders(cmd.SourceName);
                            if (!string.IsNullOrEmpty(auto))
                                ReferencePath = auto;
                        }

                        // Erst nach dem Analysieren des Vorlaufs (StopReceptionAsync) auf diesen Lauf umstellen.
                        _currentRunId = cmd.RunId;
                        var run = RunContext.New(cmd.Codec, cmd.Protocol, cmd.Gop, 0, 0,
                            cmd.NetworkCondition, cmd.SourceName, cmd.RateControl, cmd.TargetBitrateKbps);
                        var ok = await StartReceptionAsync(cmd.Protocol, cmd.Port, cmd.MeasureLatency, cmd.RecordQuality, run);

                        if (ok)
                        {
                            ConnectionStatus = $"Harness: empfange {cmd.Protocol} (Quelle {cmd.SourceName})";
                            _reporter.SendControl(new ControlCommand("ready", RunId: cmd.RunId));
                        }
                        else
                        {
                            ConnectionStatus = "Harness: Empfangsstart fehlgeschlagen";
                        }
                        break;

                    case "stop":
                        await StopReceptionAsync();
                        ConnectionStatus = "Harness: bereit, warte auf Kommando";
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Harness] Steuerbefehl '{cmd.Command}' fehlgeschlagen: {ex.Message}");
                ConnectionStatus = "Harness: Fehler";
            }
        });
    }

    // Startet den Rückkanal-Reporter nur im manuellen Modus (im Harness läuft er bereits). Wirft nicht.
    private async Task<bool> StartReceptionAsync(string protocolId, int port, bool measure, bool recordQuality, RunContext run)
    {
        try
        {
            // Qualitäts-Modus: Stream mitschneiden. AV1/VP9 lassen sich nicht in MPEG-TS muxen → Matroska.
            _recordPath = null;
            string? recordPath = null;
            var recordFormat = "mpegts";
            if (recordQuality && !measure)
            {
                recordFormat = run.Codec is "av1" or "vp9" ? "matroska" : "mpegts";
                var ext = recordFormat == "matroska" ? "mkv" : "ts";
                recordPath = Path.Combine(Path.GetTempPath(), $"ba_recv_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
                _recordPath = recordPath;
            }

            // Auf allen Schnittstellen lauschen; LocalIpAddress ist nur die Anzeige fürs Senden.
            var playbackUrl = _receiver.Start(protocolId, "0.0.0.0", port, measure, recordPath, recordFormat);

            // Im Mess-Modus liefert der Empfänger keine Wiedergabe-URL → kein VLC (Anzeige nicht mitmessen).
            if (!string.IsNullOrEmpty(playbackUrl))
            {
                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVLC, playbackUrl, FromType.FromLocation);
                var caching = PipelineSettings.Current.VlcCaching;
                _currentMedia.AddOption($":network-caching={caching}");
                _currentMedia.AddOption($":live-caching={caching}");
                _currentMedia.AddOption(":clock-jitter=0");
                _currentMedia.AddOption(":clock-synchro=0");
                MediaPlayer.Play(_currentMedia);
            }

            MetricsService.Current.BeginRun(run, resetSamples: false);
            if (!HarnessMode && !string.IsNullOrWhiteSpace(ReturnHostText))
            {
                _reporter.Start(ReturnHostText.Trim());
                WebRtcSignaling.Send = sig => _reporter.SendWebRtc(sig);
            }

            IsReceiving = true;
            ConnectionStatus = measure ? "Latenz-Messmodus (ohne Bild)"
                : recordPath is not null ? "Qualitäts-Modus (mitschneiden, ohne Bild)"
                : "Wartet auf Verbindung";
            Debug.WriteLine(
                $"[Empfang] Start ({ConnectionStatus}) – Protokoll={protocolId}; " +
                $"Port={port}; Rückkanal={(string.IsNullOrWhiteSpace(ReturnHostText) ? "aus" : ReturnHostText)}");
            return true;
        }
        catch (Exception ex)
        {
            await StopReceptionAsync();
            ConnectionStatus = "Fehler";
            Debug.WriteLine($"[Empfang] Fehlgeschlagen: {ex.Message}");
            BA.Services.Metrics.DiagLog.Write($"[EMPFAENGER-FEHLER] proto={protocolId} port={port}: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    // Der Reporter wird nur im manuellen Modus mitgestoppt.
    private async Task StopReceptionAsync()
    {
        MediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
        await _receiver.StopAsync();
        if (!HarnessMode)
            await _reporter.StopAsync();
        IsReceiving = false;

        // Aufnahmedatei ist jetzt finalisiert → offline gegen das Original messen.
        var recorded = _recordPath;
        _recordPath = null;
        if (recorded is not null)
            await AnalyzeRecordedQualityAsync(recorded);
    }

    // Berechnet offline SSIM/PSNR/VMAF gegen die Referenz, meldet sie und löscht die temporäre Aufnahmedatei.
    private async Task AnalyzeRecordedQualityAsync(string recordedPath)
    {
        try
        {
            // Manuell gewählter Pfad hat Vorrang; sonst automatisch aus TestVideos/Quellordner.
            var reference = !string.IsNullOrWhiteSpace(ReferencePath) && File.Exists(ReferencePath)
                ? ReferencePath
                : FindReferenceInFolders(MetricsService.Current.CurrentRun.SourceName);

            if (string.IsNullOrWhiteSpace(reference) || !File.Exists(reference))
            {
                ConnectionStatus = "Qualität: keine gültige Referenz-Datei (in TestVideos gefunden?)";
                Debug.WriteLine($"[Qualität] Keine Referenz für Quelle '{MetricsService.Current.CurrentRun.SourceName}' " +
                                $"(manuell '{ReferencePath}', TestVideos '{TestVideosFolder}') – Analyse übersprungen.");
                return;
            }
            if (!File.Exists(recordedPath))
            {
                Debug.WriteLine("[Qualität] Keine Aufnahmedatei – Analyse übersprungen.");
                return;
            }

            ConnectionStatus = "Qualität wird berechnet (SSIM/PSNR/VMAF) …";
            var q = await QualityAnalyzer.ComputeAsync(reference, recordedPath);
            MetricsService.Current.SetReceivedQuality(q.Ssim, q.Psnr, q.Vmaf, _currentRunId);
            ConnectionStatus = $"Qualität: SSIM {q.Ssim?.ToString("0.0000") ?? "n/v"} · " +
                               $"PSNR {(q.Psnr is { } p ? p.ToString("0.0") + " dB" : "n/v")} · " +
                               $"VMAF {q.Vmaf?.ToString("0.0") ?? "n/v"}";
            Debug.WriteLine($"[Qualität] {ConnectionStatus}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Qualität] Analyse fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            try { File.Delete(recordedPath); } catch { }
        }
    }

    // Erste nicht-loopback, nicht-APIPA IPv4-Adresse einer aktiven Schnittstelle.
    private static string GetLocalIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) // APIPA/Link-Local überspringen
                        continue;

                    return ip;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Empfang] IP-Ermittlung fehlgeschlagen: {ex.Message}");
        }

        return "nicht ermittelbar";
    }
}
