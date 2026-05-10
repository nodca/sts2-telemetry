namespace Sts2Telemetry;

public sealed record StateSnapshot(
    string StateType,
    IReadOnlyDictionary<string, object?> RawSnapshot,
    IReadOnlyDictionary<string, object?> CanonicalSnapshot,
    string RawStateHash,
    string CanonicalStateHash
);
