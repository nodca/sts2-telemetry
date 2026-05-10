using System.Text.Json;

namespace Sts2Telemetry.Inspector;

public sealed record TelemetryRecord(
    string SourcePath,
    int LineNumber,
    long ByteSize,
    string RawJson,
    JsonElement? Root,
    string? ParseError)
{
    public bool IsMalformed => ParseError != null;
    public string? RecordType => Root is JsonElement root
        ? JsonElementAccess.GetString(root, "record_type")
        : null;
    public long? LocalSequence => Root is JsonElement root
        ? JsonElementAccess.GetInt64(root, "local_sequence")
        : null;
    public DateTimeOffset? RecordedAtUtc => Root is JsonElement root
        ? JsonElementAccess.GetDateTimeOffset(root, "recorded_at_utc")
        : null;
}
