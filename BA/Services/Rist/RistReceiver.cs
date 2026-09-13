using System.Net;
using System.Net.Sockets;
using BA.Services.Transport;

namespace BA.Services.Rist;

/// <summary>
/// Native RIST-Empfangspumpe (RIST Simple Profile), Gegenstück zu <see cref="RistSender"/>
/// </summary>
public sealed class RistReceiver : ITransportStatsSource, IAsyncDisposable
{
    private UdpClient? _net;     // Eingang vom Sender (und NACK-Ausgang)
    private UdpClient? _local;   // Ausgang an ffmpeg (localhost)
    private IPEndPoint? _target; // localhost-Ziel für ffmpeg
    private IPEndPoint? _senderEp; // gelernter Sender-Endpunkt (Ziel der NACKs)
    private Thread? _pump;
    private Thread? _timeoutPump;
    private volatile bool _stop;

    private const int RtpHeaderSize = 12;
    private readonly uint _ssrc = (uint)Random.Shared.Next();

    private readonly object _gate = new();

    // Sequenz-Erweiterung (16-Bit → monoton) analog RFC 3550.
    private bool _seqInit;
    private ushort _maxSeqLow;
    private uint _cycles;

    // Reorder-Puffer: erweiterte Seq → reine Container-Nutzlast (RTP-Header bereits entfernt).
    private readonly Dictionary<uint, byte[]> _buffer = new();
    private readonly HashSet<uint> _requested = new(); // per NACK angefragte, noch offene Seqs
    private bool _relInit;
    private uint _nextRelease;   // nächste zu forwardende erweiterte Seq
    private uint _maxSeen;       // höchste bisher gesehene erweiterte Seq
    private long _lastReleaseTicks;

    // Stats (unter _gate geschrieben).
    private long _received;      // distinkt akzeptierte Pakete (inkl. per NACK zurückgeholte)
    private long _lost;          // Restverlust (nach Timeout übersprungen)
    private long _recovered;     // per NACK erfolgreich zurückgeholte Pakete
    private long _bytes;
    private double _jitter;      // Sekunden (RFC 3550)
    private double _prevTransit;
    private long _startTicks;

    public string Protocol => "rist";
    public string Role => "Empfänger";

    private static int BufferMs => Math.Max(20, BA.Services.PipelineSettings.Current.SrtLatency);

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

        _pump = new Thread(Pump) { IsBackground = true, Name = "RIST-Empfangspumpe" };
        _pump.Start();
        _timeoutPump = new Thread(TimeoutPump) { IsBackground = true, Name = "RIST-Reorder-Timeout" };
        _timeoutPump.Start();

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

                _senderEp ??= new IPEndPoint(remote.Address, remote.Port);

                ushort seq = (ushort)((pkt[2] << 8) | pkt[3]);
                uint rtpTs = (uint)((pkt[4] << 24) | (pkt[5] << 16) | (pkt[6] << 8) | pkt[7]);

                // Nutzlast kopieren – das Empfangs-Array wird wiederverwendet.
                int payloadLen = pkt.Length - RtpHeaderSize;
                var payload = new byte[payloadLen];
                Buffer.BlockCopy(pkt, RtpHeaderSize, payload, 0, payloadLen);

                List<ushort>? nackSeqs = null;
                lock (_gate)
                {
                    _bytes += pkt.Length;
                    UpdateJitter(rtpTs);

                    uint ext = ExtendAndTrack(seq);
                    if (ext > _maxSeen) _maxSeen = ext;

                    if (!_relInit)
                    {
                        _relInit = true;
                        _nextRelease = ext;
                        _lastReleaseTicks = DateTime.UtcNow.Ticks;
                    }

                    if (ext < _nextRelease)
                    {
                        // Bereits freigegeben: verspätet/duplikat. Falls angefragt → Recovery zählen.
                        if (_requested.Remove(ext)) _recovered++;
                    }
                    else if (!_buffer.ContainsKey(ext))
                    {
                        _buffer[ext] = payload;
                        _received++;
                        if (_requested.Remove(ext)) _recovered++;

                        ReleaseContiguous();
                        nackSeqs = CollectGaps();
                    }
                }

                if (nackSeqs is { Count: > 0 })
                    SendNack(nackSeqs);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    // Gibt den Kopf nach BufferMs auf (Restverlust) und wiederholt offene NACKs; begrenzt die Latenz auf ~buffer_size.
    private void TimeoutPump()
    {
        try
        {
            while (!_stop)
            {
                Thread.Sleep(15);
                List<ushort>? nackSeqs = null;
                lock (_gate)
                {
                    if (!_relInit)
                        continue;

                    // Kopf fehlt länger als der Puffer → überspringen (Restverlust), dann weiter freigeben.
                    while (!_buffer.ContainsKey(_nextRelease) && _nextRelease <= _maxSeen
                           && (DateTime.UtcNow.Ticks - _lastReleaseTicks) / (double)TimeSpan.TicksPerMillisecond > BufferMs)
                    {
                        _lost++;
                        _requested.Remove(_nextRelease);
                        _nextRelease++;
                        _lastReleaseTicks = DateTime.UtcNow.Ticks;
                        ReleaseContiguous();
                    }

                    nackSeqs = CollectGaps(); // offene Lücken erneut anfragen (Retry)
                }

                if (nackSeqs is { Count: > 0 })
                    SendNack(nackSeqs);
            }
        }
        catch (ObjectDisposedException) { }
    }

    // Gibt alle ab _nextRelease lückenlos vorhandenen Pakete in Reihenfolge an ffmpeg weiter.
    private void ReleaseContiguous()
    {
        while (_buffer.TryGetValue(_nextRelease, out var p))
        {
            // Teardown-Race: StopAsync disposed _local; vor dem Senden prüfen, sonst NRE auf dem disposed Socket.
            var local = _local;
            if (_stop || local is null || _target is null)
                break;
            try { local.Send(p, p.Length, _target); }
            catch { }
            _buffer.Remove(_nextRelease);
            _nextRelease++;
            _lastReleaseTicks = DateTime.UtcNow.Ticks;
        }
    }

    private List<ushort>? CollectGaps()
    {
        if (_nextRelease >= _maxSeen)
            return null;

        List<ushort>? gaps = null;
        // Fenster begrenzen (Schutz vor riesigen NACK-Bursts nach langen Aussetzern).
        uint end = Math.Min(_maxSeen, _nextRelease + 256);
        for (uint s = _nextRelease; s < end; s++)
        {
            if (_buffer.ContainsKey(s) || _requested.Contains(s))
                continue;
            _requested.Add(s);
            (gaps ??= new()).Add((ushort)(s & 0xFFFF));
        }
        return gaps;
    }

    private void SendNack(List<ushort> seqs)
    {
        if (_senderEp is null)
            return;
        try
        {
            var nack = RistNack.Build(_ssrc, _ssrc, seqs);
            _net!.Send(nack, nack.Length, _senderEp);
        }
        catch { } // Retry im nächsten Tick
    }

    // Erweiterte (monotone) Sequenznummer aus der 16-Bit-Seq bestimmen und den Höchststand fortschreiben.
    private uint ExtendAndTrack(ushort seq)
    {
        if (!_seqInit)
        {
            _seqInit = true;
            _maxSeqLow = seq;
            _cycles = 0;
            return seq;
        }

        short delta = (short)(seq - _maxSeqLow);
        long ext = (long)_cycles + _maxSeqLow + delta;
        if (ext < 0) ext += 0x10000;

        if (delta > 0)
        {
            if (seq < _maxSeqLow) // Vorwärts-Wrap
                _cycles += 0x10000;
            _maxSeqLow = seq;
        }
        return (uint)ext;
    }

    private void UpdateJitter(uint rtpTs)
    {
        double arrivalSec = (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerSecond;
        double transit = arrivalSec - rtpTs / 90000.0;
        if (_prevTransit != 0)
        {
            double d = Math.Abs(transit - _prevTransit);
            _jitter += (d - _jitter) / 16.0;
        }
        _prevTransit = transit;
    }

    public TransportStats? TryGetStats()
    {
        lock (_gate)
        {
            if (!_relInit)
                return new TransportStats("rist", Role);

            long expected = _received + _lost;
            double lossPct = expected > 0 ? 100.0 * _lost / expected : 0;
            double secs = Math.Max(0.001, (DateTime.UtcNow.Ticks - _startTicks) / (double)TimeSpan.TicksPerSecond);
            double recvMbps = _bytes * 8.0 / secs / 1_000_000.0;

            return new TransportStats("rist", Role,
                PacketsReceived: _received, PacketsLost: _lost, LossPercent: lossPct,
                JitterMs: _jitter * 1000.0, RecvRateMbps: recvMbps, Retransmitted: _recovered);
        }
    }

    private static int GetFreeUdpPort()
    {
        using var u = new UdpClient(0, AddressFamily.InterNetwork);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    // Robuster Bind (SO_REUSEADDR + Wiederholung), analog RtpReceiver – s. dort.
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
                    BA.Services.Metrics.DiagLog.Write($"[RIST-BIND] Port {port} gebunden nach {attempt} Versuchen");
                return udp;
            }
            catch (SocketException ex)
            {
                try { udp.Dispose(); } catch { }
                if (attempt >= maxAttempts)
                {
                    BA.Services.Metrics.DiagLog.Write($"[RIST-BIND] FEHLER Port {port} nach {attempt} Versuchen: {ex.SocketErrorCode} ({ex.Message})");
                    throw;
                }
                if (attempt == 1 || attempt % 10 == 0)
                    BA.Services.Metrics.DiagLog.Write($"[RIST-BIND] Port {port} belegt (Versuch {attempt}): {ex.SocketErrorCode} – warte …");
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
        if (_timeoutPump is { IsAlive: true })
            await Task.Run(() => _timeoutPump.Join(1000));
        _net?.Dispose();
        _local?.Dispose();
        _net = null;
        _local = null;
        _pump = null;
        _timeoutPump = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
