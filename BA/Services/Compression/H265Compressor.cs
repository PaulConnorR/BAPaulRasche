using FFMpegCore;
using FFMpegCore.Enums;

namespace BA.Services.Compression;

public sealed class H265Compressor : VideoCompressor
{
    public override string Name => "H.265 (HEVC)";

    public override string CodecId => "hevc";

    public override string FileExtension => ".mp4";

    public override string Description =>
        "Nachfolger von H.264 mit deutlich besserer Kompressionsrate, dafuer hoeherem Rechenaufwand.";

    // libx265, CRF 28 (entspricht qualitativ etwa x264 CRF 23). hvc1-Tag fuer Apple-Kompatibilitaet.
    protected override Task ExecuteCompressionAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
        => RunFFMpegAsync(inputPath, outputPath, options => options
            .WithVideoCodec("libx265")
            .WithConstantRateFactor(28)
            .WithSpeedPreset(Speed.Fast)
            .WithCustomArgument("-tag:v hvc1")
            .WithFastStart(),
            cancellationToken);

    // Live: ultrafast + zerolatency; kurzer GOP. Ratenkontrolle (CRF vs. capped VBR) aus PipelineSettings.
    public override void ApplyLiveEncoding(FFMpegArgumentOptions options)
    {
        var s = PipelineSettings.Current;
        string rc = LiveRateControlArgs(
            $"-b:v {s.TargetBitrate}k -maxrate {s.TargetBitrate}k -bufsize {s.Bufsize}k",
            $"-crf {s.Crf}");
        options
            .WithVideoCodec("libx265")
            .WithSpeedPreset(Speed.UltraFast)
            .WithCustomArgument($"-tune zerolatency -g {s.Gop} -pix_fmt yuv420p {rc}");
    }
}
