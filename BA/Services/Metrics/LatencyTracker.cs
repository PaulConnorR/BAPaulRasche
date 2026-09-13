using System.Text.RegularExpressions;

namespace BA.Services.Metrics;

/// <summary>
/// Misst die Encode→Decode-Latenz (Encode + Transport + Decode, ohne Anzeige).
/// </summary>
public static partial class LatencyTracker
{
    // Frame-Index n: → Encode-Wall-Clock (UTC-Ticks), Senderseite. FIFO-begrenzt.
    private static readonly Dictionary<long, long> EncodeTimes = new();
    private static readonly Queue<long> EncodeOrder = new();

    // Empfängerseite: gesammelte (Frame-Index, Decode-Ticks)-Proben für den Rückkanal (Zwei-PC).
    private static readonly Queue<(long FrameIndex, long RecvTicks)> ReceiverSamples = new();

    private static readonly object Gate = new();
    private const int MaxEntries = 1200; // ~40 s bei 30 fps

    private static long _cEncode, _cComputeTry, _cMatched, _cNoEntry, _cNegative;

    [GeneratedRegex(@"\bn:\s*(\d+)")]
    private static partial Regex FrameIndexRx();

    public static long? ParseFrameIndex(string line)
    {
        // Nur echte showinfo-Zeilen (die tragen "pts_time"); schließt andere "n:"-Vorkommen aus.
        if (!line.Contains("pts_time"))
            return null;
        var m = FrameIndexRx().Match(line);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static void RecordEncode(long frameIndex)
    {
        lock (Gate)
        {
            EncodeTimes[frameIndex] = DateTime.UtcNow.Ticks;
            EncodeOrder.Enqueue(frameIndex);
            _cEncode++;
            while (EncodeOrder.Count > MaxEntries)
                EncodeTimes.Remove(EncodeOrder.Dequeue());
        }
    }

    // Puffert für den Rückkanal (Zwei-PC) und verrechnet im Ein-PC-Test sofort lokal (Offset 0).
    public static void RecordDecode(long frameIndex)
    {
        long recvTicks = DateTime.UtcNow.Ticks;

        lock (Gate)
        {
            ReceiverSamples.Enqueue((frameIndex, recvTicks));
            while (ReceiverSamples.Count > MaxEntries)
                ReceiverSamples.Dequeue();
        }

        var local = ComputeLatencyMs(frameIndex, recvTicks, 0);
        if (local is { } v)
            MetricsService.Current.SetEncodeDecodeLatency(v);
    }

    // Liefert null, wenn der Frame senderseitig nicht bekannt ist; entfernt den Treffer (einmalige Verrechnung).
    public static double? ComputeLatencyMs(long frameIndex, long recvTicks, double clockOffsetMs)
    {
        long encTicks;
        lock (Gate)
        {
            _cComputeTry++;
            if (!EncodeTimes.Remove(frameIndex, out encTicks))
            {
                _cNoEntry++;
                return null;
            }
        }
        double offsetTicks = clockOffsetMs * TimeSpan.TicksPerMillisecond;
        // t_recv (Empfänger-Uhr) + Offset → Sender-Uhr; minus Encode-Zeit = Encode+Transport+Decode.
        var ms = (recvTicks + offsetTicks - encTicks) / (double)TimeSpan.TicksPerMillisecond;
        // Negativ ist physikalisch unmöglich (Offset noch ungültig oder Frame-Fehlausrichtung) → verwerfen.
        if (ms < 0)
        {
            lock (Gate) _cNegative++;
            return null;
        }
        lock (Gate) _cMatched++;
        return ms;
    }

    public static (long FrameIndex, long RecvTicks)[] DrainReceiverSamples()
    {
        lock (Gate)
        {
            if (ReceiverSamples.Count == 0)
                return Array.Empty<(long, long)>();
            var arr = ReceiverSamples.ToArray();
            ReceiverSamples.Clear();
            return arr;
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            if (_cEncode > 0 || _cComputeTry > 0)
                DiagLog.Write($"[LATENZ-RESET] enc={_cEncode} tryCompute={_cComputeTry} " +
                              $"matched={_cMatched} noEntry={_cNoEntry} negativ={_cNegative} " +
                              $"encMapRest={EncodeTimes.Count} recvBufRest={ReceiverSamples.Count}");

            EncodeTimes.Clear();
            EncodeOrder.Clear();
            ReceiverSamples.Clear();
            _cEncode = _cComputeTry = _cMatched = _cNoEntry = _cNegative = 0;
        }
    }

    public static void LogPeriodicIfActive()
    {
        lock (Gate)
        {
            if (_cEncode == 0 && _cComputeTry == 0)
                return;
            DiagLog.Write($"[LATENZ] enc={_cEncode} tryCompute={_cComputeTry} matched={_cMatched} " +
                          $"noEntry={_cNoEntry} negativ={_cNegative} encMap={EncodeTimes.Count} recvBuf={ReceiverSamples.Count}");
        }
    }
}
