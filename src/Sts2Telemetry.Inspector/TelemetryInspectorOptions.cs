namespace Sts2Telemetry.Inspector;

public sealed record TelemetryInspectorOptions
{
    public const long DefaultMaxFrameBytes = 262_144;
    public const int DefaultUnknownMarkerThreshold = 0;
    public const int DefaultTopExamples = 5;

    public string? RunsDirectory { get; init; }
    public string? OperationalDirectory { get; init; }
    public long MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;
    public int UnknownMarkerThreshold { get; init; } = DefaultUnknownMarkerThreshold;
    public int TopExamples { get; init; } = DefaultTopExamples;
}
