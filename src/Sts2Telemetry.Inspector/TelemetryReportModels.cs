namespace Sts2Telemetry.Inspector;

public sealed record TelemetryRunSource(
    string SourceArgument,
    string TelemetryPath,
    string? RunDirectory,
    string Resolution)
{
    public IReadOnlyList<string> TelemetryPaths { get; init; } =
        string.IsNullOrWhiteSpace(TelemetryPath) ? Array.Empty<string>() : new[] { TelemetryPath };

    public int SegmentCount => TelemetryPaths.Count;
}

public sealed record RecentRunsReport
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.runs.v1";
    public string RunsDirectory { get; init; } = "";
    public IReadOnlyList<string> SurfaceFilters { get; init; } = Array.Empty<string>();
    public int ScannedRunCount { get; init; }
    public IReadOnlyList<RecentRunSummary> Runs { get; init; } = Array.Empty<RecentRunSummary>();
}

public sealed record RecentRunSummary
{
    public string RunName { get; init; } = "";
    public string TelemetryPath { get; init; } = "";
    public IReadOnlyList<string> TelemetryPaths { get; init; } = Array.Empty<string>();
    public string? RunDirectory { get; init; }
    public int SegmentCount { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public string? RunId { get; init; }
    public int RecordCount { get; init; }
    public DateTimeOffset? FirstRecordedAtUtc { get; init; }
    public DateTimeOffset? LastRecordedAtUtc { get; init; }
    public bool NormalEnded { get; init; }
    public bool IsValid { get; init; }
    public int HardFailureCount { get; init; }
    public IReadOnlyList<SurfaceCoverage> Indicators { get; init; } = Array.Empty<SurfaceCoverage>();
}

public sealed record TelemetryInspectionReport
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.inspection.v1";
    public RunSummary RunSummary { get; init; } = new();
    public HealthSummary Health { get; init; } = new();
    public ReadinessSummary Readiness { get; init; } = new();
    public PerformanceSummary Performance { get; init; } = new();
    public BranchSummary Branch { get; init; } = new();
    public CoverageSummary Coverage { get; init; } = new();
    public SuspiciousSummary Suspicious { get; init; } = new();
    public IReadOnlyList<FrameSummary> LargestRecords { get; init; } = Array.Empty<FrameSummary>();
    public IReadOnlyList<ExampleRecord> TopExamples { get; init; } = Array.Empty<ExampleRecord>();
    public ValidationSummary Validation { get; init; } = new();
}

public sealed record RunSummary
{
    public string SourcePath { get; init; } = "";
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
    public string? RunDirectory { get; init; }
    public int SegmentCount { get; init; }
    public string? RunId { get; init; }
    public int RecordCount { get; init; }
    public int MalformedRecordCount { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTimeOffset? FirstRecordedAtUtc { get; init; }
    public DateTimeOffset? LastRecordedAtUtc { get; init; }
    public bool NormalEnded { get; init; }
}

public sealed record HealthSummary
{
    public int CallbackFailures { get; init; }
    public int OversizedFrames { get; init; }
    public int MissingPostState { get; init; }
    public int UnknownUnavailableMarkers { get; init; }
    public UnknownUnavailableSummary UnknownUnavailable { get; init; } = new();
    public int MalformedRecords { get; init; }
    public int OperationalRecords { get; init; }
    public int OperationalMalformedRecords { get; init; }
    public IReadOnlyList<string> OperationalSourcePaths { get; init; } = Array.Empty<string>();
    public int HardFailures { get; init; }
    public int Warnings { get; init; }
}

public sealed record ReadinessSummary
{
    public int TrainableDecisionCount { get; init; }
    public int RawTrainableDecisionCount { get; init; }
    public int DecisionFrameCount { get; init; }
    public int LegalActionContextCount { get; init; }
    public int TrainableLegalActionContextCount { get; init; }
    public double LegalActionCoverage { get; init; }
    public int SelectedActionContextReferenceCount { get; init; }
    public int SelectedActionMatchCount { get; init; }
    public double SelectedActionMatchRate { get; init; }
    public int RawSelectedActionContextReferenceCount { get; init; }
    public int RawSelectedActionMatchCount { get; init; }
    public double RawSelectedActionMatchRate { get; init; }
    public int ContextOnlyCount { get; init; }
    public int SignalOnlyCount { get; init; }
    public ReadinessCategorySummary TrainingCritical { get; init; } = new();
    public ReadinessCategorySummary Warnings { get; init; } = new();
    public ReadinessCategorySummary Diagnostics { get; init; } = new();
    public CombatReadinessSummary Combat { get; init; } = new();
}

public sealed record ReadinessCategorySummary
{
    public string Status { get; init; } = "";
    public IReadOnlyList<ReadinessMetricSummary> Metrics { get; init; } = Array.Empty<ReadinessMetricSummary>();
}

public sealed record ReadinessMetricSummary
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
    public string Status { get; init; } = "";
    public int? Count { get; init; }
    public int? Total { get; init; }
    public double? Rate { get; init; }
    public string? Detail { get; init; }
}

public sealed record CombatReadinessSummary
{
    public int DecisionFrameCount { get; init; }
    public int FramesWithTurnMarkers { get; init; }
    public int FramesWithPhaseMarkers { get; init; }
    public int FramesWithActionStepMarkers { get; init; }
    public int FramesWithActionIndexMarkers { get; init; }
    public int FramesWithDetailedState { get; init; }
    public int FramesWithStableActionIdentity { get; init; }
    public double TurnMarkerCoverage { get; init; }
    public double PhaseMarkerCoverage { get; init; }
    public double ActionStepMarkerCoverage { get; init; }
    public double ActionIndexMarkerCoverage { get; init; }
    public double DetailedStateCoverage { get; init; }
    public double StableActionIdentityCoverage { get; init; }
    public CombatProcessDetailSummary ProcessDetail { get; init; } = new();
}

public sealed record CombatProcessDetailSummary
{
    public int FramesWithRecorderCombatProcess { get; init; }
    public int FramesWithSnapshotCombatProcess { get; init; }
    public int FramesWithAnyProcessDetail { get; init; }
    public IReadOnlyList<CombatMarkerCoverageSummary> CoreMarkers { get; init; } = Array.Empty<CombatMarkerCoverageSummary>();
    public IReadOnlyList<CombatMarkerCoverageSummary> OptionalMarkers { get; init; } = Array.Empty<CombatMarkerCoverageSummary>();
}

public sealed record CombatMarkerCoverageSummary
{
    public string Marker { get; init; } = "";
    public string Importance { get; init; } = "";
    public int Present { get; init; }
    public int Unavailable { get; init; }
    public int Missing { get; init; }
    public int Total { get; init; }
    public double Coverage { get; init; }
    public string Status { get; init; } = "";
    public string? Detail { get; init; }
}

public sealed record PerformanceSummary
{
    public FrameSummary? LargestRecord { get; init; }
    public FrameSummary? LargestDecisionFrame { get; init; }
    public double AverageRecordBytes { get; init; }
    public double AverageDecisionFrameBytes { get; init; }
    public int PeakWritesPerSecond { get; init; }
    public TimingPhaseSummary? SlowestTimingPhase { get; init; }
    public IReadOnlyList<TimingPhaseSummary> SlowestTimingPhases { get; init; } = Array.Empty<TimingPhaseSummary>();
}

public sealed record TimingPhaseSummary
{
    public string Phase { get; init; } = "";
    public long MaxMicroseconds { get; init; }
    public double AverageMicroseconds { get; init; }
    public int Count { get; init; }
    public long? ExampleSequence { get; init; }
}

public sealed record BranchSummary
{
    public IReadOnlyDictionary<string, int> BranchRecordCounts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> AttemptRecordCounts { get; init; } = new Dictionary<string, int>();
    public int RunLoadedCount { get; init; }
    public int BranchMatchedCount { get; init; }
    public int BranchForkedCount { get; init; }
    public int ReplayedDecisionFrameCount { get; init; }
    public int ReplayPrefixInconsistencyCount { get; init; }
    public IReadOnlyList<BranchTimelineEntry> Timeline { get; init; } = Array.Empty<BranchTimelineEntry>();
}

public sealed record BranchTimelineEntry
{
    public long? Sequence { get; init; }
    public string? RecordType { get; init; }
    public string? BranchId { get; init; }
    public string? BranchStatus { get; init; }
    public string? AttemptId { get; init; }
    public string? AttemptStatus { get; init; }
    public bool? TrajectoryReplayed { get; init; }
    public bool? Forked { get; init; }
    public string? Reason { get; init; }
}

public sealed record CoverageSummary
{
    public SurfaceCoverage Combat { get; init; } = new("combat");
    public SurfaceCoverage Map { get; init; } = new("map");
    public SurfaceCoverage Shop { get; init; } = new("shop");
    public SurfaceCoverage Event { get; init; } = new("event");
    public SurfaceCoverage Reward { get; init; } = new("reward");
    public SurfaceCoverage Rest { get; init; } = new("rest");
    public SurfaceCoverage Treasure { get; init; } = new("treasure");
    public SurfaceCoverage CardReward { get; init; } = new("card_reward");
    public SurfaceCoverage RelicSelect { get; init; } = new("relic_select");
    public SurfaceCoverage BundleSelect { get; init; } = new("bundle_select");
    public SurfaceCoverage RelicTrigger { get; init; } = new("relic_trigger");
    public int CoveredSurfaceCount => Surfaces.Count(surface => surface.Appeared);
    public IReadOnlyList<SurfaceCoverage> Surfaces =>
        new[] { Combat, Map, Shop, Event, Reward, Rest, Treasure, CardReward, RelicSelect, BundleSelect, RelicTrigger };
}

public sealed record SurfaceCoverage(string Surface)
{
    public bool Appeared { get; init; }
    public int RecordCount { get; init; }
    public IReadOnlyList<long> ExampleSequences { get; init; } = Array.Empty<long>();
}

public sealed record SuspiciousSummary
{
    public int HardFailureCount { get; init; }
    public int WarningCount { get; init; }
    public IReadOnlyList<TelemetryFinding> Findings { get; init; } = Array.Empty<TelemetryFinding>();
}

public sealed record TelemetryFinding
{
    public string Severity { get; init; } = "";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public long? Sequence { get; init; }
    public int? LineNumber { get; init; }
    public string? SourcePath { get; init; }
    public string? RecordType { get; init; }
}

public sealed record FrameSummary
{
    public long? Sequence { get; init; }
    public int LineNumber { get; init; }
    public string? RecordType { get; init; }
    public long ByteSize { get; init; }
    public string? StateHint { get; init; }
    public string? ActionHint { get; init; }
    public IReadOnlyList<string> SuspiciousFlags { get; init; } = Array.Empty<string>();
}

public sealed record ExampleRecord
{
    public string Label { get; init; } = "";
    public long? Sequence { get; init; }
    public int? LineNumber { get; init; }
    public string? RecordType { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record ValidationSummary
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<TelemetryFinding> Errors { get; init; } = Array.Empty<TelemetryFinding>();
    public IReadOnlyList<TelemetryFinding> Warnings { get; init; } = Array.Empty<TelemetryFinding>();
}
