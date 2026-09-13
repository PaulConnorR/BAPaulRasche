namespace BA.Services.Transmission;

// Einheitliches Ergebnis eines Übertragungslaufs (von VideoTransmitter erzeugt), Pendant zu CompressionResult.
public sealed record TransmissionResult
{
    public required bool Success { get; init; }
    public string? Endpoint { get; init; }
    public long BytesSent { get; init; }
    public TimeSpan TransmissionTime { get; init; }
    public string? ErrorMessage { get; init; }

    public static TransmissionResult Succeeded(string endpoint, long bytesSent, TimeSpan transmissionTime) => new()
    {
        Success = true,
        Endpoint = endpoint,
        BytesSent = bytesSent,
        TransmissionTime = transmissionTime,
    };

    public static TransmissionResult Failed(string errorMessage, TimeSpan transmissionTime = default) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        TransmissionTime = transmissionTime,
    };
}
