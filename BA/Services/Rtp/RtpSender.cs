using System.Net;
using System.Net.Sockets;
using BA.Services.Transport;

namespace BA.Services.Rtp;

// Native RTP-Sendepumpe 
public sealed class RtpSender : ITransportStatsSource, IAsyncDisposable
{
    private UdpClient? _local;   // Eingang von ffmpeg (localhost)
    private UdpClient? _net;     // Ausgang zum Empfänger
    private Thread? _pump;
    private volatile bool _stop;

    private readonly ushort _initialSeq = (ushort)Random.Shared.Next(0, 0xFFFF);
    private readonly uint _ssrc = (uint)Random.Shared.Next();
    private long _packetsSent;

    // 90-kHz-Basis für den RTP-Zeitstempel (RFC 2250 MP2T).
    private long _startTicks;

    public string Protocol => "rtp";
    public string Role => "Sender";

    private const int RtpHeaderSize = 12;
    private const byte Mp2tPayloadType = 33; // RFC 2250

    // Rückgabe: die ffmpeg-Ausgabe-URL (localhost-UDP), auf die ffmpeg MPEG-TS schreibt.
    public string Start(string host, int port)
    {
        _local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)_local.Client.LocalEndPoint!).Port;

        _net = new UdpClient();
        _net.Connect(host, port); // fixes Ziel; Send() ohne Endpoint

        _startTicks = DateTime.UtcNow.Ticks;
        TransportStatsRegistry.Register(this);

        _pump = new Thread(Pump) { IsBackground = true, Name = "RTP-Sendepumpe" };
        _pump.Start();

        return $"udp://127.0.0.1:{localPort}?pkt_size=1316";
    }

    private void Pump()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        ushort seq = _initialSeq;
        var packet = new byte[RtpHeaderSize + 1500];

        try
        {
            while (!_stop)
            {
                byte[] ts = _local!.Receive(ref remote); // blockiert bis ffmpeg sendet
                if (ts.Length == 0)
                    continue;

                int total = RtpHeaderSize + ts.Length;
                if (total > packet.Length)
                    packet = new byte[total];

                // 90-kHz-Zeitstempel aus der verstrichenen Zeit seit Start (für RFC-3550-Jitter beim Empfänger).
                uint rtpTs = (uint)((DateTime.UtcNow.Ticks - _startTicks) / (TimeSpan.TicksPerSecond / 90000));
                WriteHeader(packet, seq, rtpTs, _ssrc);
                Buffer.BlockCopy(ts, 0, packet, RtpHeaderSize, ts.Length);

                _net!.Send(packet, total);
                seq++;
                Interlocked.Increment(ref _packetsSent);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static void WriteHeader(byte[] buf, ushort seq, uint timestamp, uint ssrc)
    {
        buf[0] = 0x80;               // V=2, P=0, X=0, CC=0
        buf[1] = Mp2tPayloadType;    // M=0, PT=33
        buf[2] = (byte)(seq >> 8);
        buf[3] = (byte)seq;
        buf[4] = (byte)(timestamp >> 24);
        buf[5] = (byte)(timestamp >> 16);
        buf[6] = (byte)(timestamp >> 8);
        buf[7] = (byte)timestamp;
        buf[8] = (byte)(ssrc >> 24);
        buf[9] = (byte)(ssrc >> 16);
        buf[10] = (byte)(ssrc >> 8);
        buf[11] = (byte)ssrc;
    }

    public TransportStats? TryGetStats()
        => new TransportStats("rtp", Role, PacketsSent: Interlocked.Read(ref _packetsSent));

    public async Task StopAsync()
    {
        _stop = true;
        TransportStatsRegistry.Unregister(this);
        try { _local?.Close(); } catch { }
        try { _net?.Close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        _local?.Dispose();
        _net?.Dispose();
        _local = null;
        _net = null;
        _pump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
