namespace Sts2Telemetry;

internal static class TelemetryRunQuality
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string LoadOnly = "load_only";
    public const string Diagnostic = "diagnostic";

    public static string Merge(string current, string next)
        => Rank(next) > Rank(current) ? next : current;

    private static int Rank(string quality)
        => quality switch
        {
            Complete => 4,
            Partial => 3,
            LoadOnly => 2,
            Diagnostic => 1,
            _ => 0
        };
}

internal sealed class TelemetryRunQualityAccumulator
{
    private bool _readError;
    private bool _hasComplete;
    private bool _hasGameplay;
    private bool _hasLoadOnly;
    private bool _hasDiagnostic;

    public void AddRecordType(string? recordType)
    {
        string normalized = recordType?.Trim() ?? "";
        if (normalized.Length == 0)
        {
            _hasDiagnostic = true;
            return;
        }

        if (normalized == "lifecycle/run_ended")
        {
            _hasComplete = true;
            return;
        }

        if (IsLoadOnlyRecord(normalized))
        {
            _hasLoadOnly = true;
            return;
        }

        if (IsDiagnosticRecord(normalized))
        {
            _hasDiagnostic = true;
            return;
        }

        _hasGameplay = true;
    }

    public void MarkReadError()
        => _readError = true;

    public string Build()
    {
        if (_hasComplete)
            return TelemetryRunQuality.Complete;
        if (_hasGameplay || _readError)
            return TelemetryRunQuality.Partial;
        if (_hasLoadOnly)
            return TelemetryRunQuality.LoadOnly;
        if (_hasDiagnostic)
            return TelemetryRunQuality.Diagnostic;
        return TelemetryRunQuality.Diagnostic;
    }

    private static bool IsLoadOnlyRecord(string recordType)
        => recordType is "lifecycle/run_loaded" or "lifecycle/branch_matched";

    private static bool IsDiagnosticRecord(string recordType)
        => recordType is
            "lifecycle/unsupported_run" or
            "lifecycle/mod_initialized" or
            "lifecycle/telemetry_callback_failed" or
            "lifecycle/harmony_patch_status" or
            "lifecycle/harmony_native_dependency_status" or
            "lifecycle/save_preview" or
            "lifecycle/save_observed" or
            "lifecycle/pending_decision_missing";
}
