using System.Net;
using System.Net.Sockets;
using BA.Services.Transport;

namespace BA.Services.Rist;

/// <summary>
/// Native RIST-Sendepumpe (RIST Simple Profile), wie <see cref="BA.Services.Rtp.RtpSender"/>, aber mit Retransmission
/// </summary>
public sealed class RistSender : ITransportStatsSource, IAsyncDisposable
{
    private UdpClient? _local;   // Eingang von ffmpeg (localhost)
    private UdpClient? _net;     // Ausgang zum Empfänger (und NACK-Eingang)
    private Thread? _pump;       // ffmpeg → RTP → Netz
    private Thread? _nackPump;   // NACK-Empfang → Retransmit
    private volatile bool _stop;

    private readonly ushort _initialSeq = (ushort)Random.Shared.Next(0, 0xFFFF);
    private readonly uint _ssrc = (uint)Random.Shared.Next();
    private long _packetsSent;
    private long _retransmitted;

    private long _startTicks;

    // Retransmit-Ringpuffer (Index = seq & Mask). 8192 Pakete ≈ mehrere Sekunden → deckt den RIST-Puffer ab.
    private const int RingSize = 8192;
    private const int RingMask = RingSize - 1;
    private readonly (ushort seq, int len, byte[] data)[] _ring = new (ushort, int, byte[])[RingSize];
    private readonly object _ringGate = new();

    public string Protocol => "rist";
    public string Role => "Sender";

    private const int RtpHeaderSize = 12;
    private const byte Mp2tPayloadType = 33; // RFC 2250

    // Rückgabe: die ffmpeg-Ausgabe-URL (localhost-UDP), auf die ffmpeg schreibt.
    public string Start(string host, int port)
    {
        _local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int localPort = ((IPEndPoint)_local.Client.LocalEndPoint!).Port;

        _net = new UdpClient();
        _net.Connect(host, port); // fixes Ziel; Send() ohne Endpoint, Receive() nur vom Empfänger (NACKs)

        _startTicks = DateTime.UtcNow.Ticks;
        TransportStatsRegistry.Register(this);

        _pump = new Thread(Pump) { IsBackground = true, Name = "RIST-Sendepumpe" };
        _pump.Start();
        _nackPump = new Thread(NackPump) { IsBackground = true, Name = "RIST-NACK-Empfang" };
        _nackPump.Start();

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
                byte[] payload = _local!.Receive(ref remote); // blockiert bis ffmpeg sendet
                if (payload.Length == 0)
                    continue;

                int total = RtpHeaderSize + payload.Length;
                if (total > packet.Length)
                    packet = new byte[total];

                uint rtpTs = (uint)((DateTime.UtcNow.Ticks - _startTicks) / (TimeSpan.TicksPerSecond / 90000));
                WriteHeader(packet, seq, rtpTs, _ssrc);
                Buffer.BlockCopy(payload, 0, packet, RtpHeaderSize, payload.Length);

                // Kopie für den Ringpuffer (das wiederverwendete packet-Array darf nicht referenziert werden).
                var stored = new byte[total];
                Buffer.BlockCopy(packet, 0, stored, 0, total);
                lock (_ringGate)
                    _ring[seq & RingMask] = (seq, total, stored);

                _net!.Send(packet, total);
                seq++;
                Interlocked.Increment(ref _packetsSent);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // Empfängt NACKs und überträgt die angeforderten Pakete aus dem Ringpuffer erneut.
    private void NackPump()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_stop)
            {
                byte[] pkt = _net!.Receive(ref remote); // NACKs vom Empfänger (Connect fixiert die Quelle)
                if (!RistNack.IsNack(pkt, pkt.Length))
                    continue;

                foreach (var reqSeq in RistNack.Parse(pkt, pkt.Length))
                {
                    (ushort seq, int len, byte[] data) slot;
                    lock (_ringGate)
                        slot = _ring[reqSeq & RingMask];

                    // Nur senden, wenn genau diese Seq noch im Puffer liegt (nicht bereits überschrieben).
                    if (slot.data is not null && slot.seq == reqSeq)
                    {
                        try { _net!.Send(slot.data, slot.len); } catch { break; }
                        Interlocked.Increment(ref _retransmitted);
                    }
                }
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
        => new TransportStats("rist", Role,
            PacketsSent: Interlocked.Read(ref _packetsSent),
            Retransmitted: Interlocked.Read(ref _retransmitted));

    public async Task StopAsync()
    {
        _stop = true;
        TransportStatsRegistry.Unregister(this);
        try { _local?.Close(); } catch { }
        try { _net?.Close(); } catch { }
        if (_pump is { IsAlive: true })
            await Task.Run(() => _pump.Join(1000));
        if (_nackPump is { IsAlive: true })
            await Task.Run(() => _nackPump.Join(1000));
        _local?.Dispose();
        _net?.Dispose();
        _local = null;
        _net = null;
        _pump = null;
        _nackPump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
