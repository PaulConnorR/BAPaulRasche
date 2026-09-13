#if WINDOWS
using System.Net;
using System.Net.Sockets;

namespace BA.Services.Srt;

/// <summary>
/// SRT-Sendepumpe (Caller): ffmpeg schreibt sein MPEG-TS auf einen lokalen UDP-Port, diese Pumpe liest jedes
/// Datagramm (ein 1316-Byte-TS-Block = natürliche SRT-Nachrichtenrahmung) und sendet es per <c>srt_send</c>.
/// Connect (blockierend, mit Wiederholung) + Pumpe auf einem Hintergrund-Thread

/// </summary>
public sealed class SrtSender : ISrtStatsSource, IAsyncDisposable
{
    private UdpClient? _udp;
    private SrtSocket? _socket;
    private Thread? _pump;
    private volatile bool _stop;

    private string _host = "";
    private int _port;
    private int _latencyMs;

    public string Role => "Sender";

    // Liefert die ffmpeg-Ausgabe-URL (localhost-UDP), auf die ffmpeg sein MPEG-TS schreiben soll.
    public string Start(string host, int port, int latencyMs)
    {
        _host = host;
        _port = port;
        _latencyMs = latencyMs;

        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        SrtStatsRegistry.Register(this);

        _pump = new Thread(ConnectAndPump) { IsBackground = true, Name = "SRT-Sendepumpe" };
        _pump.Start();

        return $"udp://127.0.0.1:{localPort}?pkt_size={SrtSocket.LivePayloadSize}";
    }

    private void ConnectAndPump()
    {
        while (!_stop)
        {
            try
            {
                var s = SrtSocket.Create();
                s.ConfigureLive(_latencyMs);
                s.Connect(_host, _port); // blockiert; wirft, wenn kein Listener
                _socket = s;
                break;
            }
            catch (SrtException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SRT-Sender] Connect fehlgeschlagen ({ex.Message}) – erneuter Versuch …");
                Thread.Sleep(500);
            }
        }
        if (_stop || _socket is null)
            return;

        System.Diagnostics.Debug.WriteLine($"[SRT-Sender] Verbunden mit {_host}:{_port}.");

        var remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_stop)
            {
                byte[] datagram = _udp!.Receive(ref remote); // blockiert bis ffmpeg sendet
                if (datagram.Length == 0)
                    continue;
                _socket.Send(datagram, datagram.Length);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (SrtException ex) { System.Diagnostics.Debug.WriteLine("[SRT-Sender] Sendefehler: " + ex.Message); }
    }

    public SrtStats? TryGetStats()
    {
        var s = _socket;
        try { return s?.IsConnected == true ? s.GetStats() : null; }
        catch { return null; }
    }

    public async Task StopAsync()
    {
        _stop = true;
        SrtStatsRegistry.Unregister(this);
        try { _udp?.Close(); } catch { }
        try { _socket?.Close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        _udp?.Dispose();
        _udp = null;
        _socket = null;
        _pump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
#endif
