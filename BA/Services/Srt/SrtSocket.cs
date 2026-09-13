#if WINDOWS
using System.Runtime.InteropServices;
using SrtSharp;

namespace BA.Services.Srt;

/// <summary>
/// Typisierte Hülle um einen libsrt-Socket (SrtSharp/SWIG). Betriebsmodell BLOCKIEREND (SRT-Standard),
/// daher gehören Accept/Connect/Send/Recv auf Hintergrund-Threads. Nur unter Windows (native DLL).
/// </summary>
public sealed class SrtSocket : IDisposable
{
    // Länge einer sockaddr_in (AF_INET); libsrt erwartet mindestens diese Größe.
    private const int NameLen = 16;

    // SRT-Live-Nutzlast = 7×188 TS-Pakete; jeder srt_send im LIVE-Modus darf höchstens so groß sein.
    public const int LivePayloadSize = 1316;

    private static readonly object StartupGate = new();
    private static bool _started;

    private readonly int _handle;
    private bool _closed;

    private SrtSocket(int handle) => _handle = handle;

    public int Handle => _handle;
    public SRT_SOCKSTATUS State => srt.srt_getsockstate(_handle);
    public bool IsConnected => State == SRT_SOCKSTATUS.SRTS_CONNECTED;

    // Idempotent und thread-sicher: initialisiert libsrt genau einmal pro Prozess.
    public static void EnsureStartup()
    {
        if (_started) return;
        lock (StartupGate)
        {
            if (_started) return;
            if (srt.srt_startup() == srt.SRT_ERROR)
                throw new SrtException("srt_startup: " + srt.srt_getlasterror_str());
            _started = true;
        }
    }

    public static SrtSocket Create()
    {
        EnsureStartup();
        int h = srt.srt_create_socket();
        if (h == srt.SRT_INVALID_SOCK)
            throw new SrtException("srt_create_socket: " + srt.srt_getlasterror_str());
        return new SrtSocket(h);
    }

    // SWIG erwartet einen void*-Zeiger auf den Wert.
    public void SetOption(SRT_SOCKOPT option, int value)
    {
        IntPtr p = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(p, value);
            if (srt.srt_setsockflag(_handle, option, new SWIGTYPE_p_void(p, false), sizeof(int)) == srt.SRT_ERROR)
                throw new SrtException($"srt_setsockflag({option}): " + srt.srt_getlasterror_str());
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // RCVLATENCY greift empfangsseitig, PEERLATENCY handelt den Wert für die Gegenseite aus; beide zu setzen
    // wirkt unabhängig von der Rolle.
    public void ConfigureLive(int latencyMs, int payloadSize = LivePayloadSize)
    {
        SetOption(SRT_SOCKOPT.SRTO_TRANSTYPE, (int)SRT_TRANSTYPE.SRTT_LIVE);
        SetOption(SRT_SOCKOPT.SRTO_PAYLOADSIZE, payloadSize);
        SetOption(SRT_SOCKOPT.SRTO_RCVLATENCY, latencyMs);
        SetOption(SRT_SOCKOPT.SRTO_PEERLATENCY, latencyMs);
    }

    public void Bind(string address, int port)
    {
        var addr = SocketHelper.CreateSocketAddress(address, port);
        if (srt.srt_bind(_handle, addr, NameLen) == srt.SRT_ERROR)
            throw new SrtException("srt_bind: " + srt.srt_getlasterror_str());
    }

    public void Listen(int backlog = 1)
    {
        if (srt.srt_listen(_handle, backlog) == srt.SRT_ERROR)
            throw new SrtException("srt_listen: " + srt.srt_getlasterror_str());
    }

    // Blockiert, bis sich ein Sender verbindet.
    public SrtSocket Accept()
    {
        int c = srt.srt_accept(_handle, null, null);
        if (c == srt.SRT_INVALID_SOCK)
            throw new SrtException("srt_accept: " + srt.srt_getlasterror_str());
        return new SrtSocket(c);
    }

    public void Connect(string host, int port)
    {
        var addr = SocketHelper.CreateSocketAddress(host, port);
        if (srt.srt_connect(_handle, addr, NameLen) == srt.SRT_ERROR)
            throw new SrtException("srt_connect: " + srt.srt_getlasterror_str());
    }

    // Im LIVE-Modus darf length höchstens LivePayloadSize sein.
    public int Send(byte[] buffer, int length)
    {
        int n = srt.srt_send(_handle, buffer, length);
        if (n == srt.SRT_ERROR)
            throw new SrtException("srt_send: " + srt.srt_getlasterror_str());
        return n;
    }

    // Rückgabe: Byteanzahl, oder SRT_ERROR (-1) bei Fehler/Abbruch – kein Wurf, da regulärer Stream-Endfall.
    public int Recv(byte[] buffer, int length) => srt.srt_recv(_handle, buffer, length);

    public static bool IsError(int recvResult) => recvResult == srt.SRT_ERROR;

    public SrtStats GetStats(bool clearInterval = true)
    {
        var mon = new CBytePerfMon();
        if (srt.srt_bstats(_handle, mon, clearInterval ? 1 : 0) == srt.SRT_ERROR)
            throw new SrtException("srt_bstats: " + srt.srt_getlasterror_str());

        return new SrtStats(
            RttMs: mon.msRTT,
            SendRateMbps: mon.mbpsSendRate,
            RecvRateMbps: mon.mbpsRecvRate,
            PktSndLoss: mon.pktSndLoss,
            PktRcvLoss: mon.pktRcvLoss,
            PktRetrans: mon.pktRetrans,
            RcvBufMs: mon.msRcvBuf,
            RcvAvgBelatedMs: mon.pktRcvAvgBelatedTime,
            PktSent: mon.pktSent,
            PktRecv: mon.pktRecv);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        if (_handle != srt.SRT_INVALID_SOCK)
            srt.srt_close(_handle);
    }

    public void Dispose() => Close();
}
#endif
