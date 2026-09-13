#if WINDOWS
using System.Net;
using System.Net.Sockets;

namespace BA.Services.Srt;

/// <summary>
/// SRT-Empfangspumpe (Listener), Gegenstück zu <see cref="SrtSender"/>: nimmt den SRT-Link entgegen und reicht
/// jedes Paket per localhost-UDP an die ffmpeg-/VLC-Kette weiter. Accept + Pumpe auf einem Hintergrund-Thread.
/// </summary>
public sealed class SrtReceiver : ISrtStatsSource, IAsyncDisposable
{
    private SrtSocket? _listener;
    private SrtSocket? _accepted;
    private UdpClient? _udp;
    private IPEndPoint? _target;
    private Thread? _pump;
    private volatile bool _stop;

    public string Role => "Empfänger";

    // Rückgabe: localhost-UDP-URL, die die nachgelagerte ffmpeg-/VLC-Kette als Eingang lesen soll.
    public string Start(string bindAddress, int port, int latencyMs)
    {
        _listener = BindListener(bindAddress, port, latencyMs);

        _udp = new UdpClient();
        int localPort = GetFreeUdpPort();
        _target = new IPEndPoint(IPAddress.Loopback, localPort);

        SrtStatsRegistry.Register(this);

        _pump = new Thread(AcceptAndPump) { IsBackground = true, Name = "SRT-Empfangspumpe" };
        _pump.Start();

        return $"udp://127.0.0.1:{localPort}";
    }

    private void AcceptAndPump()
    {
        try
        {
            _accepted = _listener!.Accept(); // blockiert bis zum Verbindungsaufbau
        }
        catch (SrtException ex)
        {
            if (!_stop)
                System.Diagnostics.Debug.WriteLine("[SRT-Empf.] Accept fehlgeschlagen: " + ex.Message);
            return;
        }

        System.Diagnostics.Debug.WriteLine("[SRT-Empf.] Sender verbunden.");

        var buffer = new byte[1500]; // > größte Live-Nachricht (1316)
        try
        {
            while (!_stop)
            {
                int n = _accepted.Recv(buffer, buffer.Length);
                if (SrtSocket.IsError(n) || n == 0)
                    break;
                _udp!.Send(buffer, n, _target!);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public SrtStats? TryGetStats()
    {
        var s = _accepted;
        try { return s?.IsConnected == true ? s.GetStats() : null; }
        catch { return null; }
    }

    // libsrt gibt beim schnellen Lauf-Wechsel im Harness den vorigen Socket verzögert frei → Bind wirft.
    // Bis ~6 s wiederholen (unter dem 8-s-"ready"-Timeout); Socket je Versuch neu, da ein Fehl-Bind ihn ruiniert.
    private static SrtSocket BindListener(string bindAddress, int port, int latencyMs)
    {
        const int maxAttempts = 60;
        for (int attempt = 1; ; attempt++)
        {
            var sock = SrtSocket.Create();
            try
            {
                sock.ConfigureLive(latencyMs);
                sock.Bind(bindAddress, port);
                sock.Listen(1);
                if (attempt > 1)
                    BA.Services.Metrics.DiagLog.Write($"[SRT-BIND] Port {port} gebunden nach {attempt} Versuchen");
                return sock;
            }
            catch (SrtException ex)
            {
                try { sock.Close(); } catch { }
                if (attempt >= maxAttempts)
                {
                    BA.Services.Metrics.DiagLog.Write($"[SRT-BIND] FEHLER Port {port} nach {attempt} Versuchen: {ex.Message}");
                    throw;
                }
                if (attempt == 1 || attempt % 10 == 0)
                    BA.Services.Metrics.DiagLog.Write($"[SRT-BIND] Port {port} belegt (Versuch {attempt}) – warte …");
                Thread.Sleep(100);
            }
        }
    }

    private static int GetFreeUdpPort()
    {
        using var u = new UdpClient(0, AddressFamily.InterNetwork);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public async Task StopAsync()
    {
        _stop = true;
        SrtStatsRegistry.Unregister(this);
        try { _accepted?.Close(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _udp?.Close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        _udp?.Dispose();
        _listener = null;
        _accepted = null;
        _udp = null;
        _pump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
#endif
