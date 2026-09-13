#if WINDOWS
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BA.Services.Metrics;
using BA.Services.Transport;
using SIPSorcery.Net;

namespace BA.Services.WebRtc;

/// <summary>
/// WebRTC-Empfangspumpe (SIPSorcery), Gegenstück zu <see cref="WebRtcSender"/>
/// </summary>
public sealed class WebRtcReceiver : ITransportStatsSource, IAsyncDisposable
{
    private RTCPeerConnection? _pc;
    private UdpClient? _udp;
    private IPEndPoint? _target;
    private System.Threading.Timer? _answerTimer;
    private int _answerSent;

    private long _packetsReceived;
    private long _bytesReceived;
    private long _lastBytes;
    private long _lastRateTicks;

    public string Protocol => "webrtc";
    public string Role => "Empfänger";

    // Liefert die localhost-UDP-URL, die die nachgelagerte ffmpeg-Kette als Eingang lesen soll.
    public string Start(string bindAddress, int port)
    {
        _packetsReceived = 0;
        _bytesReceived = 0;
        _lastBytes = 0;
        _lastRateTicks = Stopwatch.GetTimestamp();

        _udp = new UdpClient();
        int localPort = GetFreeUdpPort();
        _target = new IPEndPoint(IPAddress.Loopback, localPort);

        TransportStatsRegistry.Register(this);

        WebRtcSignaling.OnSignal = sig =>
        {
            DiagLog.Write($"[WEBRTC-EMPF] Signal empfangen: {sig.Kind} (len={sig.Sdp.Length})");
            if (sig.Kind == "offer")
                _ = HandleOfferAsync(sig.Sdp);
        };
        DiagLog.Write("[WEBRTC-EMPF] bereit, warte auf Offer …");

        return $"udp://127.0.0.1:{localPort}";
    }

    private async Task HandleOfferAsync(string offerSdp)
    {
        try
        {
            _answerSent = 0;
            // Gleiche ICE-Härtung wie beim Sender (s. WebRtcSender).
            _pc = new RTCPeerConnection(new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>(),
                X_GatherTimeoutMs = 3000,
                X_ICEIncludeAllInterfaceAddresses = true,
            });

            _pc.onconnectionstatechange += state => DiagLog.Write($"[WEBRTC-EMPF] connectionState={state}");
            _pc.oniceconnectionstatechange += state => DiagLog.Write($"[WEBRTC-EMPF] iceState={state}");

            _pc.onicegatheringstatechange += state =>
            {
                DiagLog.Write($"[WEBRTC-EMPF] iceGathering={state}");
                if (state == RTCIceGatheringState.complete)
                    TrySendAnswer("gathering");
            };

            // "ba-probe" → Echo für die RTT-Messung; "ba-media" → Bytestrom an ffmpeg.
            _pc.ondatachannel += dc =>
            {
                DiagLog.Write($"[WEBRTC-EMPF] Datenkanal '{dc.label}' empfangen");
                if (dc.label == "ba-probe")
                    dc.onmessage += (RTCDataChannel c, DataChannelPayloadProtocols _, byte[] data) =>
                    {
                        try { c.send(data, 0, data.Length); } catch { }
                    };
                else
                    dc.onmessage += (RTCDataChannel _, DataChannelPayloadProtocols _, byte[] data) => OnMedia(data);
            };

            var res = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
            DiagLog.Write($"[WEBRTC-EMPF] Offer gesetzt: {res}");

            var answer = _pc.createAnswer(null);
            await _pc.setLocalDescription(answer);
            DiagLog.Write("[WEBRTC-EMPF] Answer erstellt, warte auf ICE-Gathering …");
            // Fallback, falls "complete" ausbleibt: Answer senden, sobald localDescription steht.
            _answerTimer = new System.Threading.Timer(_ => TrySendAnswer("timeout"), null, 2000, 1000);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[WEBRTC-EMPF] Offer-Verarbeitung fehlgeschlagen: {ex.Message}");
        }
    }

    private void TrySendAnswer(string why)
    {
        var ld = _pc?.localDescription;
        if (ld is null) return;
        if (Interlocked.Exchange(ref _answerSent, 1) == 1) return;
        try { _answerTimer?.Dispose(); } catch { }
        _answerTimer = null;
        var send = WebRtcSignaling.Send;
        DiagLog.Write($"[WEBRTC-EMPF] Answer senden ({why}), Send-gesetzt={send is not null}, sdpLen={ld.sdp.ToString().Length}");
        send?.Invoke(new WebRtcSignal("answer", ld.sdp.ToString()));
    }

    private void OnMedia(byte[] data)
    {
        if (data.Length == 0) return;
        try
        {
            _udp!.Send(data, data.Length, _target!);
            Interlocked.Increment(ref _packetsReceived);
            Interlocked.Add(ref _bytesReceived, data.Length);
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public TransportStats? TryGetStats()
    {
        if (_pc is null) return null;
        long now = Stopwatch.GetTimestamp();
        long total = Interlocked.Read(ref _bytesReceived);
        double elapsed = (now - Interlocked.Exchange(ref _lastRateTicks, now)) / (double)Stopwatch.Frequency;
        long delta = total - _lastBytes;
        _lastBytes = total;
        double rateMbps = elapsed > 0 ? delta * 8.0 / 1_000_000.0 / elapsed : 0;
        // Zuverlässiger, geordneter Datenkanal (SCTP retransmittiert) → kein App-Paketverlust, kein Jitter.
        return new TransportStats("webrtc", "Empfänger",
            PacketsReceived: Interlocked.Read(ref _packetsReceived),
            RecvRateMbps: rateMbps);
    }

    private static int GetFreeUdpPort()
    {
        using var u = new UdpClient(0, AddressFamily.InterNetwork);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public Task StopAsync()
    {
        WebRtcSignaling.OnSignal = null;
        TransportStatsRegistry.Unregister(this);
        try { _answerTimer?.Dispose(); } catch { }
        _answerTimer = null;
        try { _pc?.close(); } catch { }
        try { _udp?.Close(); } catch { }
        _udp?.Dispose();
        _pc = null;
        _udp = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
#endif
