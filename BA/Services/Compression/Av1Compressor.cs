using FFMpegCore;

namespace BA.Services.Compression;

public sealed class Av1Compressor : VideoCompressor
{
    public override string Name => "AV1";

    public override string CodecId => "av1";

    public override string FileExtension => ".mp4";

    public override string Description =>
        "Lizenzfreier moderner Codec mit hoher Effizienz, aber vergleichsweise langsamem Encoding.";

    // SVT-AV1 (deutlich schneller als libaom): numerisches Preset 0-13 (8 = ausgewogen),
    // CRF 35. Der x264-Speed-Enum passt hier nicht, daher Preset als Custom-Argument.
    protected override Task ExecuteCompressionAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
        => RunFFMpegAsync(inputPath, outputPath, options => options
            .WithVideoCodec("libsvtav1")
            .WithConstantRateFactor(35)
            .WithCustomArgument("-preset 8")
            .WithFastStart(),
            cancellationToken);

    // Live: SVT-AV1 im schnellsten Realtime-Preset (13). SVT-AV1 unterstützt kein echtes CBR/-maxrate/-bufsize
    // (der Encoder startet sonst nicht), daher im CBR-Modus nur -b:v als VBR-Ziel.
    public override void ApplyLiveEncoding(FFMpegArgumentOptions options)
    {
        var s = PipelineSettings.Current;
        string rc = LiveRateControlArgs(
            $"-b:v {s.TargetBitrate}k",
            $"-crf {s.Crf}");
        options
            .WithVideoCodec("libsvtav1")
            .WithCustomArgument($"-preset 13 -svtav1-params lp=4 -g {s.Gop} -pix_fmt yuv420p {rc}");
    }
}
