#if WINDOWS
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BA.Services.Metrics;
using BA.Services.Transport;
using SIPSorcery.Net;

namespace BA.Services.WebRtc;

/// <summary>
/// WebRTC-Sendepumpe (SIPSorcery): PeerConnection über den Rückkanal
/// </summary>
public sealed class WebRtcSender : ITransportStatsSource, IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _media;
    private RTCDataChannel? _probe;
    private UdpClient? _udp;
    private Thread? _pump;
    private System.Threading.Timer? _probeTimer;
    private System.Threading.Timer? _offerTimer;
    private int _offerSent;
    private volatile bool _stop;

    private long _packetsSent;
    private double _transportLatencyMs;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Vor dem Öffnen des Datenkanals gepufferte Datagramme (Absicherung gegen ein Restdatagramm im Rennen).
    private readonly Queue<byte[]> _preOpen = new();
    private long _preOpenBytes;
    private const long PreOpenCapBytes = 64L * 1024 * 1024;

    public string Protocol => "webrtc";
    public string Role => "Sender";

    // Erfüllt, sobald der Medien-Datenkanal offen ist (oder StopAsync lief).
    public Task Ready => _ready.Task;

    // Liefert die ffmpeg-Ausgabe-URL (localhost-UDP), auf die ffmpeg seinen Container schreiben soll.
    public string Start(string host, int port)
    {
        _stop = false;
        _packetsSent = 0;
        _transportLatencyMs = 0;
        _offerSent = 0;

        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        // X_GatherTimeoutMs erzwingt ICE-Gathering-"complete" (sonst wurde das Offer nie gesendet);
        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>(),
            X_GatherTimeoutMs = 3000,
            X_ICEIncludeAllInterfaceAddresses = true,
        });

        // Datenkanäle VOR dem Offer anlegen, damit die SDP die SCTP-Anwendung enthält. Default zuverlässig+geordnet
        // (wie SRT) → der Bytestrom kommt lückentreu an.
        _pc.createDataChannel("ba-media").ContinueWith(OnMediaChannel, TaskScheduler.Default);
        _pc.createDataChannel("ba-probe").ContinueWith(OnProbeChannel, TaskScheduler.Default);

        _pc.onconnectionstatechange += state =>
        {
            DiagLog.Write($"[WEBRTC-SENDER] connectionState={state}");
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                _ready.TrySetResult(); // nicht ewig blockieren, falls der Aufbau scheitert
        };
        _pc.oniceconnectionstatechange += state => DiagLog.Write($"[WEBRTC-SENDER] iceState={state}");

        // Non-Trickle: Offer erst senden, wenn die ICE-Kandidaten gesammelt sind (dann stecken sie im SDP).
        _pc.onicegatheringstatechange += state =>
        {
            DiagLog.Write($"[WEBRTC-SENDER] iceGathering={state}");
            if (state == RTCIceGatheringState.complete)
                TrySendOffer("gathering");
        };

        WebRtcSignaling.OnSignal = sig =>
        {
            if (sig.Kind != "answer" || _pc is null)
                return;
            var res = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sig.Sdp });
            DiagLog.Write($"[WEBRTC-SENDER] Answer gesetzt: {res}");
        };

        var offer = _pc.createOffer(null);
        _ = _pc.setLocalDescription(offer);
        DiagLog.Write("[WEBRTC-SENDER] Offer erstellt, warte auf ICE-Gathering …");
        // Fallback, falls "complete" ausbleibt: Offer senden, sobald localDescription steht.
        _offerTimer = new System.Threading.Timer(_ => TrySendOffer("timeout"), null, 2000, 1000);

        TransportStatsRegistry.Register(this);

        _pump = new Thread(Pump) { IsBackground = true, Name = "WebRTC-Sendepumpe" };
        _pump.Start();

        return $"udp://127.0.0.1:{localPort}?pkt_size=1316";
    }

    // Sendet das Offer genau EINMAL (per Gathering-Complete ODER Fallback-Timer).
    private void TrySendOffer(string why)
    {
        var ld = _pc?.localDescription;
        if (ld is null) return;
        if (Interlocked.Exchange(ref _offerSent, 1) == 1) return;
        try { _offerTimer?.Dispose(); } catch { }
        _offerTimer = null;
        var send = WebRtcSignaling.Send;
        DiagLog.Write($"[WEBRTC-SENDER] Offer senden ({why}), Send-gesetzt={send is not null}, sdpLen={ld.sdp.ToString().Length}");
        send?.Invoke(new WebRtcSignal("offer", ld.sdp.ToString()));
    }

    private void OnMediaChannel(Task<RTCDataChannel> t)
    {
        if (!t.IsCompletedSuccessfully) { DiagLog.Write("[WEBRTC-SENDER] Medienkanal-Erzeugung fehlgeschlagen"); return; }
        _media = t.Result;
        _media.onopen += () =>
        {
            DiagLog.Write("[WEBRTC-SENDER] Medienkanal offen → Übertragung startet");
            FlushPreOpen();
            _ready.TrySetResult();
        };
    }

    private void OnProbeChannel(Task<RTCDataChannel> t)
    {
        if (!t.IsCompletedSuccessfully) return;
        _probe = t.Result;
        // Echo des Empfängers → aus dem mitgeschickten Sende-Zeitstempel die Transport-RTT ableiten.
        _probe.onmessage += (RTCDataChannel _, DataChannelPayloadProtocols _, byte[] data) =>
        {
            if (data.Length < 8) return;
            long sentTicks = BitConverter.ToInt64(data, 0);
            double rttMs = (Stopwatch.GetTimestamp() - sentTicks) * 1000.0 / Stopwatch.Frequency;
            if (rttMs is >= 0 and < 60000)
                _transportLatencyMs = rttMs / 2.0; // Einweg ≈ RTT/2
        };
        _probe.onopen += () =>
        {
            _probeTimer = new System.Threading.Timer(_ => SendProbe(), null, 500, 500);
        };
    }

    private void SendProbe()
    {
        var p = _probe;
        if (_stop || p is null || p.readyState != RTCDataChannelState.open) return;
        try { var b = BitConverter.GetBytes(Stopwatch.GetTimestamp()); p.send(b, 0, b.Length); }
        catch (Exception ex) { DiagLog.Write($"[WEBRTC-SENDER] Probe-Fehler: {ex.Message}"); }
    }

    private void Pump()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_stop)
            {
                byte[] datagram = _udp!.Receive(ref remote); // blockiert bis ffmpeg sendet
                if (datagram.Length == 0)
                    continue;

                var m = _media;
                if (m is not null && m.readyState == RTCDataChannelState.open)
                {
                    FlushPreOpen();
                    SendMedia(m, datagram);
                }
                else
                {
                    // Kanal noch nicht offen: puffern statt verwerfen (Bytestrom lückenlos), gegen Überlauf gedeckelt.
                    lock (_preOpen)
                    {
                        if (_preOpenBytes + datagram.Length <= PreOpenCapBytes)
                        {
                            _preOpen.Enqueue(datagram);
                            _preOpenBytes += datagram.Length;
                        }
                    }
                }
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void FlushPreOpen()
    {
        var m = _media;
        if (m is null || m.readyState != RTCDataChannelState.open) return;
        lock (_preOpen)
        {
            while (_preOpen.Count > 0)
                SendMedia(m, _preOpen.Dequeue());
            _preOpenBytes = 0;
        }
    }

    private void SendMedia(RTCDataChannel m, byte[] datagram)
    {
        try
        {
            m.send(datagram, 0, datagram.Length);
            Interlocked.Increment(ref _packetsSent);
        }
        catch (Exception ex) { DiagLog.Write($"[WEBRTC-SENDER] Sendefehler: {ex.Message}"); }
    }

    public TransportStats? TryGetStats()
    {
        if (_pc is null) return null;
        return new TransportStats("webrtc", "Sender",
            PacketsSent: Interlocked.Read(ref _packetsSent),
            TransportLatencyMs: _transportLatencyMs);
    }

    public async Task StopAsync()
    {
        _stop = true;
        _ready.TrySetResult();
        WebRtcSignaling.OnSignal = null;
        TransportStatsRegistry.Unregister(this);
        if (_offerTimer is not null) { await _offerTimer.DisposeAsync(); _offerTimer = null; }
        if (_probeTimer is not null) { await _probeTimer.DisposeAsync(); _probeTimer = null; }
        try { _udp?.Close(); } catch { }
        try { _pc?.close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        _udp?.Dispose();
        _udp = null;
        _pc = null;
        _media = null;
        _probe = null;
        _pump = null;
        lock (_preOpen) { _preOpen.Clear(); _preOpenBytes = 0; }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
#endif
