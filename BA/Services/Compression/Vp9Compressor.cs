using FFMpegCore;

namespace BA.Services.Compression;

public sealed class Vp9Compressor : VideoCompressor
{
    public override string Name => "VP9";

    public override string CodecId => "vp9";

    public override string FileExtension => ".webm";

    public override string Description =>
        "Offener Codec von Google, verbreitet im Web-Streaming, effizienter als H.264.";

    // libvpx-vp9 im Constant-Quality-Modus: CRF 31 erfordert "-b:v 0".
    // deadline/cpu-used steuern den Geschwindigkeit-/Qualitaets-Kompromiss (VP9 nutzt keinen -preset).
    protected override Task ExecuteCompressionAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
        => RunFFMpegAsync(inputPath, outputPath, options => options
            .WithVideoCodec("libvpx-vp9")
            .WithConstantRateFactor(31)
            .WithCustomArgument("-b:v 0")
            .WithCustomArgument("-deadline good -cpu-used 2"),
            cancellationToken);

    // Live: Realtime-Modus (deadline realtime, hohes cpu-used) für minimale Latenz. Ratenkontrolle aus
    // PipelineSettings: CRF braucht bei VP9 "-b:v 0"; capped/CBR braucht minrate=maxrate=b:v (echtes CBR).
    public override void ApplyLiveEncoding(FFMpegArgumentOptions options)
    {
        var s = PipelineSettings.Current;
        string rc = LiveRateControlArgs(
            $"-b:v {s.TargetBitrate}k -minrate {s.TargetBitrate}k -maxrate {s.TargetBitrate}k -bufsize {s.Bufsize}k",
            $"-crf {s.Crf} -b:v 0");
        options
            .WithVideoCodec("libvpx-vp9")
            .WithCustomArgument($"-deadline realtime -cpu-used 8 -g {s.Gop} -pix_fmt yuv420p {rc}");
    }
}
