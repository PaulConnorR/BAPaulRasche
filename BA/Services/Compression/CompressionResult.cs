namespace BA.Services.Compression;

// Einheitliches Ergebnis eines Kompressionslaufs (von VideoCompressor erzeugt).
public sealed record CompressionResult
{
    public required bool Success { get; init; }
    public string? OutputPath { get; init; }
    public long OutputSizeBytes { get; init; }
    public TimeSpan ProcessingTime { get; init; }
    public string? ErrorMessage { get; init; }

    public static CompressionResult Succeeded(string outputPath, long outputSizeBytes, TimeSpan processingTime) => new()
    {
        Success = true,
        OutputPath = outputPath,
        OutputSizeBytes = outputSizeBytes,
        ProcessingTime = processingTime,
    };

    public static CompressionResult Failed(string errorMessage, TimeSpan processingTime = default) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        ProcessingTime = processingTime,
    };
}
