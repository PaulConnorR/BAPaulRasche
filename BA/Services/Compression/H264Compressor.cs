using FFMpegCore;
using FFMpegCore.Enums;

namespace BA.Services.Compression;

public sealed class H264Compressor : VideoCompressor
{
    public override string Name => "H.264 (AVC)";

    public override string CodecId => "h264";

    public override string FileExtension => ".mp4";

    public override string Description =>
        "Weit verbreiteter Standard mit sehr guter Hardware-Unterstuetzung und geringer Encoding-Latenz.";

    protected override Task ExecuteCompressionAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
        => RunFFMpegAsync(inputPath, outputPath, options => options
            .WithVideoCodec("libx264")
            .WithConstantRateFactor(23)
            .WithSpeedPreset(Speed.Fast)
            .WithCustomArgument("-tune zerolatency")
            .WithFastStart(),
            cancellationToken);

    // Live: minimale Encoding-Latenz (ultrafast + zerolatency). Ratenkontrolle aus PipelineSettings.
    public override void ApplyLiveEncoding(FFMpegArgumentOptions options)
    {
        var s = PipelineSettings.Current;
        string rc = LiveRateControlArgs(
            $"-b:v {s.TargetBitrate}k -maxrate {s.TargetBitrate}k -bufsize {s.Bufsize}k",
            $"-crf {s.Crf}");
        options
            .WithVideoCodec("libx264")
            .WithSpeedPreset(Speed.UltraFast)
            .WithCustomArgument($"-tune zerolatency -g {s.Gop} -pix_fmt yuv420p {rc}");
    }
}
