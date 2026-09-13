using System.Net;
using System.Net.Sockets;
using BA.Services.Transport;

namespace BA.Services.Rtp;

/// <summary>
/// Native RTP-Empfangspumpe (Gegenstück zu <see cref="RtpSender"/>)
/// </summary>
public sealed class RtpReceiver : ITransportStatsSource, IAsyncDisposable
{
    private UdpClient? _net;     // Eingang vom Sender
    private UdpClient? _local;   // Ausgang an ffmpeg (localhost)
    private IPEndPoint? _target;
    private Thread? _pump;
    private volatile bool _stop;

    private const int RtpHeaderSize = 12;

    private bool _init;
    private ushort _maxSeqLow;
    private uint _cycles;       // 0x10000 je Sequenz-Überlauf
    private uint _baseExt;      // erweiterte Sequenznummer des ersten Pakets
    private long _received;
    private long _bytes;
    private double _jitter;     // Sekunden
    private double _prevTransit;
    private long _startTicks;

    public string Protocol => "rtp";
    public string Role => "Empfänger";

    // Rückgabe: localhost-UDP-URL, die die nachgelagerte ffmpeg-Kette als Eingang liest.
    public string Start(string bindAddress, int port)
    {
        var bindIp = bindAddress is "0.0.0.0" or "" ? IPAddress.Any : IPAddress.Parse(bindAddress);
        _net = BindReceiveSocket(bindIp, port);

        _local = new UdpClient();
        int localPort = GetFreeUdpPort();
        _target = new IPEndPoint(IPAddress.Loopback, localPort);

        _startTicks = DateTime.UtcNow.Ticks;
        TransportStatsRegistry.Register(this);

        _pump = new Thread(Pump) { IsBackground = true, Name = "RTP-Empfangspumpe" };
        _pump.Start();

        return $"udp://127.0.0.1:{localPort}";
    }

    private void Pump()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_stop)
            {
                byte[] pkt = _net!.Receive(ref remote); // blockiert
                if (pkt.Length < RtpHeaderSize)
                    continue;

                ushort seq = (ushort)((pkt[2] << 8) | pkt[3]);
                uint ts = (uint)((pkt[4] << 24) | (pkt[5] << 16) | (pkt[6] << 8) | pkt[7]);

                UpdateStats(seq, ts, pkt.Length);

                // RTP-Header abschneiden, reine MPEG-TS-Nutzlast an ffmpeg weiterreichen.
                _local!.Client.SendTo(pkt, RtpHeaderSize, pkt.Length - RtpHeaderSize, SocketFlags.None, _target!);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void UpdateStats(ushort seq, uint rtpTs, int len)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        _received++;
        _bytes += len;

        if (!_init)
        {
            _init = true;
            _maxSeqLow = seq;
            _cycles = 0;
            _baseExt = seq;
            _prevTransit = 0;
            return;
        }

        // Erweiterte Sequenznummer fortschreiben (Vorwärtssprünge zählen, Wraparound erkennen).
        ushort udelta = (ushort)(seq - _maxSeqLow);
        if (udelta < 0x8000)
        {
            if (seq < _maxSeqLow)
                _cycles += 0x10000;
            _maxSeqLow = seq;
        }

        // Jitter: Transit = Ankunft(s) − RTP-Zeitstempel(s). D = Transit − PrevTransit; J += (|D|−J)/16.
        double arrivalSec = (nowTicks - _startTicks) / (double)TimeSpan.TicksPerSecond;
        double transit = arrivalSec - rtpTs / 90000.0;
        double d = Math.Abs(transit - _prevTransit);
        _prevTransit = transit;
        _jitter += (d - _jitter) / 16.0;
    }

    public TransportStats? TryGetStats()
    {
        if (!_init)
            return new TransportStats("rtp", Role);

        uint maxExt = _cycles + _maxSeqLow;
        long expected = maxExt - _baseExt + 1;
        long received = _received;
        long lost = Math.Max(0, expected - received);
        double lossPct = expected > 0 ? 100.0 * lost / expected : 0;
        double secs = Math.Max(0.001, (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerSecond);
        double recvMbps = _bytes * 8.0 / secs / 1_000_000.0;

        return new TransportStats("rtp", Role,
            PacketsReceived: received, PacketsLost: lost, LossPercent: lossPct,
            JitterMs: _jitter * 1000.0, RecvRateMbps: recvMbps);
    }

    private static int GetFreeUdpPort()
    {
        using var u = new UdpClient(0, AddressFamily.InterNetwork);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    // Beim schnellen Protokollwechsel im Harness (z.B. SRT→RTP auf Port 9000) ist der Vorgänger-Socket (libsrt)
    // evtl. noch nicht freigegeben. ReuseAddress + Wiederholung bis ~6 s (libsrt schließt asynchron); bleibt unter
    // dem 8-s-"ready"-Timeout, sodass "Empfang vor Senden" erhalten bleibt.
    private static UdpClient BindReceiveSocket(IPAddress ip, int port)
    {
        const int maxAttempts = 60;
        for (int attempt = 1; ; attempt++)
        {
            var udp = new UdpClient();
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(ip, port));
                if (attempt > 1)
                    BA.Services.Metrics.DiagLog.Write($"[RTP-BIND] Port {port} gebunden nach {attempt} Versuchen");
                return udp;
            }
            catch (SocketException ex)
            {
                try { udp.Dispose(); } catch { }
                if (attempt >= maxAttempts)
                {
                    BA.Services.Metrics.DiagLog.Write($"[RTP-BIND] FEHLER Port {port} nach {attempt} Versuchen: {ex.SocketErrorCode} ({ex.Message})");
                    throw;
                }
                if (attempt == 1 || attempt % 10 == 0)
                    BA.Services.Metrics.DiagLog.Write($"[RTP-BIND] Port {port} belegt (Versuch {attempt}): {ex.SocketErrorCode} – warte …");
                Thread.Sleep(100);
            }
        }
    }

    public async Task StopAsync()
    {
        _stop = true;
        TransportStatsRegistry.Unregister(this);
        try { _net?.Close(); } catch { }
        try { _local?.Close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        _net?.Dispose();
        _local?.Dispose();
        _net = null;
        _local = null;
        _pump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
