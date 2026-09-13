using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BA.Services.Metrics;

public static class MetricsReturnChannel
{
    // Bewusst getrennt vom Medien-Port: bei clumsy muss dieser Port vom Filter ausgenommen werden,
    // sonst verfälscht der induzierte Verlust die zurückgemeldeten Messwerte und die Uhr-Offset-Messung.
    public const int Port = 9100;
}

public sealed record ReceiverMetricsReport(
    string RunId,
    double ReceiverBitrateKbps,
    double ReceiverFps,
    long ReceiverFrames,
    double ClockOffsetMs,
    int TsContinuityErrors = 0,
    // ReceivedQualityRunId: Lauf, zu dem die Werte gehören – verhindert, dass ein spät gemeldeter Wert dem falschen Lauf zufällt.
    double? ReceivedSsim = null,
    double? ReceivedPsnr = null,
    double? ReceivedVmaf = null,
    string ReceivedQualityRunId = "",
    long TransportPacketsReceived = 0,
    long TransportPacketsLost = 0,
    double TransportLossPercent = 0,
    double TransportJitterMs = 0);

/// <param name="Command">"prepare" (konfigurieren + Empfang starten) | "stop" | "ready" (Empfänger→Sender: bereit).</param>
public sealed record ControlCommand(
    string Command,
    string Protocol = "srt",
    int Port = 9000,
    string Codec = "—",
    int Gop = 0,
    string RateControl = "—",
    int TargetBitrateKbps = 0,
    string SourceName = "—",
    string NetworkCondition = "—",
    bool MeasureLatency = false,
    string RunId = "—",
    bool RecordQuality = false);

/// <param name="T1Ticks">Empfänger-Sendezeit der Anfrage – in timereq und zurückgespiegelt in timeresp.</param>
/// <param name="T2Ticks">Sender-Empfangszeit der Anfrage (nur timeresp).</param>
/// <param name="T3Ticks">Sender-Sendezeit der Antwort (nur timeresp).</param>
/// <param name="RecvTicks">Decode-Wall-Clock des Frames auf der Empfängerseite (nur "latency").</param>
public sealed record ReturnChannelMessage(
    string Type,
    ReceiverMetricsReport? Report = null,
    long T1Ticks = 0,
    long T2Ticks = 0,
    long T3Ticks = 0,
    long FrameIndex = 0,
    long RecvTicks = 0,
    ControlCommand? Control = null,
    WebRtcSignal? WebRtc = null);

public sealed record WebRtcSignal(string Kind, string Sdp);

public static class WebRtcSignaling
{
    public static Action<WebRtcSignal>? Send { get; set; }
    public static Action<WebRtcSignal>? OnSignal { get; set; }
}

// Sender-seitiger Rückkanal-Server: faltet die NDJSON-Meldungen der Empfänger ein und beantwortet timereq → timeresp.
public sealed class MetricsCollector : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // Aktive Verbindungen: nötig, um Steuerbefehle aktiv sender→empfänger zu pushen (Harness-Modus).
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private volatile TaskCompletionSource<bool>? _pendingReady;

    // Writer plus Schreib-Lock: Read-Loop (timeresp) und control-Pushes teilen sich denselben Writer.
    private sealed class Connection
    {
        public required StreamWriter Writer { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    public bool IsRunning => _listener is not null;

    public void Start(int port = MetricsReturnChannel.Port)
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_listener, _cts.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = ReadClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ReadClientAsync(TcpClient client, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                var conn = new Connection { Writer = writer };
                _connections[id] = conn;
                MetricsService.Current.SetRemoteReceiverConnected(true);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    long t2 = DateTime.UtcNow.Ticks; // Empfangszeit sofort festhalten (Zeitabgleich)
                    if (line is null)
                        break;
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    ReturnChannelMessage? msg;
                    try { msg = JsonSerializer.Deserialize<ReturnChannelMessage>(line); }
                    catch (JsonException) { continue; }
                    if (msg is null)
                        continue;

                    switch (msg.Type)
                    {
                        case "report" when msg.Report is not null:
                            MetricsService.Current.ApplyRemoteReceiverMetrics(msg.Report);
                            break;

                        case "timereq":
                            var resp = new ReturnChannelMessage("timeresp",
                                T1Ticks: msg.T1Ticks, T2Ticks: t2, T3Ticks: DateTime.UtcNow.Ticks);
                            await SendAsync(conn, resp, ct);
                            break;

                        case "latency":
                            // Nur bei synchroner Uhr, sonst verfälscht die Uhrdifferenz die Latenz.
                            if (MetricsService.Current.ClockSynced)
                            {
                                var lat = LatencyTracker.ComputeLatencyMs(
                                    msg.FrameIndex, msg.RecvTicks, MetricsService.Current.ClockOffsetMs);
                                if (lat is { } v)
                                    MetricsService.Current.SetEncodeDecodeLatency(v);
                            }
                            break;

                        case "control" when msg.Control is { Command: "ready" }:
                            _pendingReady?.TrySetResult(true);
                            break;

                        case "webrtc" when msg.WebRtc is not null:
                            WebRtcSignaling.OnSignal?.Invoke(msg.WebRtc);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            _connections.TryRemove(id, out _);
            if (_connections.IsEmpty)
                MetricsService.Current.SetRemoteReceiverConnected(false);
        }
    }

    private static async Task SendAsync(Connection conn, ReturnChannelMessage msg, CancellationToken ct)
    {
        await conn.WriteLock.WaitAsync(ct);
        try { await conn.Writer.WriteLineAsync(JsonSerializer.Serialize(msg).AsMemory(), ct); }
        finally { conn.WriteLock.Release(); }
    }

    // Bei "prepare" wird bis readyTimeout auf "ready" gewartet, damit der Empfang VOR dem Senden startet.
    public async Task<bool> SendControlAsync(ControlCommand cmd, TimeSpan readyTimeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReady = tcs;
        var msg = new ReturnChannelMessage("control", Control: cmd);

        bool sent = false;
        foreach (var conn in _connections.Values)
        {
            try { await SendAsync(conn, msg, ct); sent = true; }
            catch { }
        }

        if (!sent || cmd.Command != "prepare")
        {
            _pendingReady = null;
            return sent;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(readyTimeout);
        try { return await tcs.Task.WaitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _pendingReady = null; }
    }

    public async Task SendWebRtcAsync(WebRtcSignal sig, CancellationToken ct = default)
    {
        var msg = new ReturnChannelMessage("webrtc", WebRtc: sig);
        foreach (var conn in _connections.Values)
        {
            try { await SendAsync(conn, msg, ct); } catch { }
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { }
        }

        _listener = null;
        _acceptTask = null;
        _cts.Dispose();
        _cts = null;
        _connections.Clear();
        MetricsService.Current.SetRemoteReceiverConnected(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

// Empfänger-seitiger Rückkanal-Client: meldet 1×/s die Kennzahlen als NDJSON und misst per SNTP-artigem
// Austausch den Uhr-Offset beider PCs. Verbindet nach Abbruch selbstständig neu.
public sealed class MetricsReporter : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Bestes (delay-minimales) Offset-Sample seit Verbindungsbeginn (SNTP-Min-Delay-Filter): das Sample mit der
    // kleinsten Round-Trip-Verzögerung ist am wenigsten durch Netz-Jitter und Pfad-Asymmetrie verfälscht.
    // Bewusst persistent statt gleitendes Fenster, damit der Offset stabil bleibt und nicht mit dem Jitter schwankt;
    // Annahme: über eine Messsitzung driften NTP-synchronisierte Uhren vernachlässigbar.
    private double _bestDelayMs = double.MaxValue;
    private double _bestOffsetMs;

    private readonly ConcurrentQueue<ControlCommand> _outbound = new();

    // Aufgerufen je empfangenem Steuerbefehl, auf einem Hintergrund-Thread (ViewModel marshallt selbst auf UI).
    public Action<ControlCommand>? OnControl { get; set; }

    public void SendControl(ControlCommand cmd) => _outbound.Enqueue(cmd);

    private readonly ConcurrentQueue<WebRtcSignal> _outboundWebRtc = new();

    public void SendWebRtc(WebRtcSignal sig) => _outboundWebRtc.Enqueue(sig);

    public bool IsRunning => _cts is not null;

    public void Start(string host, int port = MetricsReturnChannel.Port)
    {
        if (IsRunning)
            return;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(host, port, _cts.Token);
    }

    private async Task RunAsync(string host, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                await using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                // Voll-duplex: Antworten (timeresp) parallel lesen, während wir Reports/Anfragen senden.
                var readTask = ReadResponsesAsync(reader, ct);

                while (!ct.IsCancellationRequested)
                {
                    var m = MetricsService.Current;

                    var report = new ReceiverMetricsReport(
                        m.CurrentRun.RunId,
                        m.ReceiverBitrateKbps,
                        m.ReceiverFps,
                        m.ReceiverFrames,
                        m.ClockOffsetMs,
                        m.TsContinuityErrors,
                        m.ReceivedSsimValue,
                        m.ReceivedPsnrValue,
                        m.ReceivedVmafValue,
                        m.ReceivedQualityRunId,
                        m.TransportPacketsReceived,
                        m.TransportPacketsLost,
                        m.TransportLossPercent,
                        m.TransportJitterMs);

                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new ReturnChannelMessage("report", Report: report)).AsMemory(), ct);

                    // Zeitabgleich anstoßen: t1 = jetzt (Empfänger-Uhr).
                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new ReturnChannelMessage("timereq", T1Ticks: DateTime.UtcNow.Ticks)).AsMemory(), ct);

                    foreach (var s in LatencyTracker.DrainReceiverSamples())
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(new ReturnChannelMessage("latency", FrameIndex: s.FrameIndex, RecvTicks: s.RecvTicks)).AsMemory(), ct);

                    while (_outbound.TryDequeue(out var ctrl))
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(new ReturnChannelMessage("control", Control: ctrl)).AsMemory(), ct);

                    while (_outboundWebRtc.TryDequeue(out var sig))
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(new ReturnChannelMessage("webrtc", WebRtc: sig)).AsMemory(), ct);

                    await Task.Delay(1000, ct);
                }

                await readTask;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // Sender (noch) nicht erreichbar → kurz warten und neu verbinden.
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ReadResponsesAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                long t4 = DateTime.UtcNow.Ticks;
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ReturnChannelMessage? msg;
                try { msg = JsonSerializer.Deserialize<ReturnChannelMessage>(line); }
                catch (JsonException) { continue; }
                if (msg is null)
                    continue;

                if (msg.Type == "control" && msg.Control is not null)
                {
                    OnControl?.Invoke(msg.Control);
                    continue;
                }

                if (msg.Type == "webrtc" && msg.WebRtc is not null)
                {
                    WebRtcSignaling.OnSignal?.Invoke(msg.WebRtc);
                    continue;
                }

                if (msg.Type != "timeresp")
                    continue;

                // SNTP-Formeln (Ticks): Offset θ = ((t2−t1) + (t3−t4)) / 2 (Sender−Empfänger); Verzögerung δ = (t4−t1) − (t3−t2).
                double offsetMs = new TimeSpan(((msg.T2Ticks - msg.T1Ticks) + (msg.T3Ticks - t4)) / 2).TotalMilliseconds;
                double delayMs = new TimeSpan((t4 - msg.T1Ticks) - (msg.T3Ticks - msg.T2Ticks)).TotalMilliseconds;

                // Min-Delay-Filter: nur das Sample mit der kleinsten Round-Trip-Verzögerung übernehmen
                // (negative Verzögerungen = Quantisierungsrauschen verwerfen).
                if (delayMs >= 0 && delayMs < _bestDelayMs)
                {
                    _bestDelayMs = delayMs;
                    _bestOffsetMs = offsetMs;
                    MetricsService.Current.SetClockOffset(_bestOffsetMs, _bestDelayMs);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        try { _cts.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
