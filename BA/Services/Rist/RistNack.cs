namespace BA.Services.Rist;

/// <summary>
/// RTCP-Generic-NACK-Kodierung (RFC 4585, RTPFB/PT=205, FMT=1) für die RIST-Retransmission: der Empfänger meldet
/// fehlende Sequenznummern, der Sender überträgt sie erneut. Die NACKs laufen auf dem Medien-Socket zurück (kein
/// separater RTCP-Port). Ein Eintrag = PID (erste verlorene Seq) + BLP (Bitmaske der 16 folgenden).
/// </summary>
internal static class RistNack
{
    private const byte Version = 0x80; // V=2, P=0
    private const byte Fmt = 1;        // Generic NACK
    public const byte RtcpRtpfb = 205; // PT für Transport-Layer-Feedback
    private const int HeaderSize = 12;

    // True, wenn das Datagramm ein RTCP-RTPFB-Paket ist (Unterscheidung von RTP-Medienpaketen, PT=33).
    public static bool IsNack(byte[] pkt, int len) => len >= HeaderSize && pkt[1] == RtcpRtpfb;

    public static byte[] Build(uint senderSsrc, uint mediaSsrc, IReadOnlyList<ushort> missing)
    {
        // FCI-Einträge zusammenfassen: pro Eintrag deckt PID + BLP bis zu 17 aufeinanderfolgende Seqs ab.
        var fci = new List<(ushort pid, ushort blp)>();
        int i = 0;
        var sorted = missing.Distinct().OrderBy(x => x).ToList();
        while (i < sorted.Count)
        {
            ushort pid = sorted[i];
            ushort blp = 0;
            int j = i + 1;
            while (j < sorted.Count)
            {
                int diff = (ushort)(sorted[j] - pid);
                if (diff is >= 1 and <= 16)
                    blp |= (ushort)(1 << (diff - 1));
                else if (diff > 16)
                    break;
                j++;
            }
            fci.Add((pid, blp));
            i = j;
        }

        var buf = new byte[HeaderSize + fci.Count * 4];
        buf[0] = Version | Fmt;
        buf[1] = RtcpRtpfb;
        int words = buf.Length / 4 - 1; // Länge in 32-Bit-Wörtern minus 1 (RTCP-Konvention)
        buf[2] = (byte)(words >> 8);
        buf[3] = (byte)words;
        WriteU32(buf, 4, senderSsrc);
        WriteU32(buf, 8, mediaSsrc);
        int off = HeaderSize;
        foreach (var (pid, blp) in fci)
        {
            buf[off] = (byte)(pid >> 8);
            buf[off + 1] = (byte)pid;
            buf[off + 2] = (byte)(blp >> 8);
            buf[off + 3] = (byte)blp;
            off += 4;
        }
        return buf;
    }

    public static List<ushort> Parse(byte[] pkt, int len)
    {
        var result = new List<ushort>();
        for (int off = HeaderSize; off + 4 <= len; off += 4)
        {
            ushort pid = (ushort)((pkt[off] << 8) | pkt[off + 1]);
            ushort blp = (ushort)((pkt[off + 2] << 8) | pkt[off + 3]);
            result.Add(pid);
            for (int b = 0; b < 16; b++)
                if ((blp & (1 << b)) != 0)
                    result.Add((ushort)(pid + b + 1));
        }
        return result;
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }
}
