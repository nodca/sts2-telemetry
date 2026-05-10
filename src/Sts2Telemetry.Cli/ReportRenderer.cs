using Sts2Telemetry.Inspector;

namespace Sts2Telemetry.Cli;

internal static class ReportRenderer
{
    public static void WriteInspect(TelemetryInspectionReport report, TextWriter writer)
    {
        WriteRunSummary(report, writer);
        writer.WriteLine();
        WriteHealth(report, writer);
        writer.WriteLine();
        WriteReadiness(report, writer);
        writer.WriteLine();
        WritePerf(report, writer);
        writer.WriteLine();
        WriteBranch(report, writer);
        writer.WriteLine();
        WriteCoverage(report, writer);
        writer.WriteLine();
        WriteSuspicious(report, writer);
        writer.WriteLine();
        WriteTopExamples(report, writer);
    }

    public static void WriteFrames(TelemetryInspectionReport report, int topSize, TextWriter writer)
    {
        writer.WriteLine($"Largest Records (top {topSize})");
        foreach (FrameSummary frame in report.LargestRecords.Take(topSize))
        {
            string flags = frame.SuspiciousFlags.Count == 0 ? "-" : string.Join(",", frame.SuspiciousFlags);
            writer.WriteLine(
                $"seq={Format(frame.Sequence)} line={frame.LineNumber} type={frame.RecordType ?? "unknown"} bytes={frame.ByteSize} state={frame.StateHint ?? "-"} action={frame.ActionHint ?? "-"} flags={flags}");
        }
    }

    public static void WriteBranch(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Branch");
        writer.WriteLine($"  run_loaded: {report.Branch.RunLoadedCount}");
        writer.WriteLine($"  branch_matched: {report.Branch.BranchMatchedCount}");
        writer.WriteLine($"  branch_forked: {report.Branch.BranchForkedCount}");
        writer.WriteLine($"  replayed decision frames: {report.Branch.ReplayedDecisionFrameCount}");
        writer.WriteLine($"  replay prefix inconsistencies: {report.Branch.ReplayPrefixInconsistencyCount}");
        writer.WriteLine($"  branches: {FormatCounts(report.Branch.BranchRecordCounts)}");
        writer.WriteLine($"  attempts: {FormatCounts(report.Branch.AttemptRecordCounts)}");
        if (report.Branch.Timeline.Count > 0)
        {
            writer.WriteLine("  timeline:");
            foreach (BranchTimelineEntry entry in report.Branch.Timeline)
            {
                writer.WriteLine(
                    $"    seq={Format(entry.Sequence)} type={entry.RecordType ?? "-"} branch={entry.BranchId ?? "-"} attempt={entry.AttemptId ?? "-"} replayed={Format(entry.TrajectoryReplayed)} forked={Format(entry.Forked)} reason={entry.Reason ?? "-"}");
            }
        }
    }

    public static void WriteCoverage(TelemetryInspectionReport report, TextWriter writer)
    {
        WriteReadiness(report, writer);
        writer.WriteLine();
        writer.WriteLine("Coverage");
        foreach (SurfaceCoverage surface in report.Coverage.Surfaces)
        {
            string examples = surface.ExampleSequences.Count == 0 ? "-" : string.Join(",", surface.ExampleSequences);
            writer.WriteLine($"  {surface.Surface}: {(surface.Appeared ? "yes" : "no")} count={surface.RecordCount} examples={examples}");
        }
    }

    public static void WritePerf(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Performance");
        writer.WriteLine($"  largest record: {FormatFrame(report.Performance.LargestRecord)}");
        writer.WriteLine($"  largest decision frame: {FormatFrame(report.Performance.LargestDecisionFrame)}");
        writer.WriteLine($"  average record bytes: {report.Performance.AverageRecordBytes:F1}");
        writer.WriteLine($"  average decision frame bytes: {report.Performance.AverageDecisionFrameBytes:F1}");
        writer.WriteLine($"  peak writes per second: {report.Performance.PeakWritesPerSecond}");
        writer.WriteLine($"  slowest timing phase: {FormatTiming(report.Performance.SlowestTimingPhase)}");
        if (report.Performance.SlowestTimingPhases.Count > 0)
        {
            writer.WriteLine("  timing phases:");
            foreach (TimingPhaseSummary timing in report.Performance.SlowestTimingPhases)
                writer.WriteLine($"    {FormatTiming(timing)}");
        }
    }

    public static void WriteRuns(RecentRunsReport report, TextWriter writer)
    {
        writer.WriteLine("Runs");
        writer.WriteLine($"  directory: {report.RunsDirectory}");
        writer.WriteLine($"  filters: {(report.SurfaceFilters.Count == 0 ? "-" : string.Join(",", report.SurfaceFilters))}");
        writer.WriteLine($"  scanned: {report.ScannedRunCount}");
        writer.WriteLine($"  returned: {report.Runs.Count}");

        if (report.Runs.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (RecentRunSummary run in report.Runs)
        {
            writer.WriteLine(
                $"  run={run.RunName} records={run.RecordCount} segments={run.SegmentCount} valid={run.IsValid} hard_failures={run.HardFailureCount} normal_ended={run.NormalEnded} time={Format(run.FirstRecordedAtUtc)}..{Format(run.LastRecordedAtUtc)} last_write_utc={run.LastWriteTimeUtc.ToUniversalTime():O}");
            writer.WriteLine($"    path: {run.TelemetryPath}");
            if (run.TelemetryPaths.Count > 1)
                writer.WriteLine($"    segment_paths: {string.Join(", ", run.TelemetryPaths)}");
            writer.WriteLine($"    indicators: {FormatIndicators(run.Indicators)}");
        }
    }

    private static void WriteRunSummary(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Run Summary");
        writer.WriteLine($"  run_id: {report.RunSummary.RunId ?? "-"}");
        writer.WriteLine($"  records: {report.RunSummary.RecordCount}");
        writer.WriteLine($"  segments: {report.RunSummary.SegmentCount}");
        writer.WriteLine($"  malformed records: {report.RunSummary.MalformedRecordCount}");
        writer.WriteLine($"  file size bytes: {report.RunSummary.FileSizeBytes}");
        writer.WriteLine($"  time range: {Format(report.RunSummary.FirstRecordedAtUtc)} to {Format(report.RunSummary.LastRecordedAtUtc)}");
        writer.WriteLine($"  normal ended: {report.RunSummary.NormalEnded}");
        writer.WriteLine($"  source: {report.RunSummary.SourcePath}");
        if (report.RunSummary.SourcePaths.Count > 1)
            writer.WriteLine($"  source segments: {string.Join(", ", report.RunSummary.SourcePaths)}");
    }

    private static void WriteHealth(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Health");
        writer.WriteLine($"  callback failures: {report.Health.CallbackFailures}");
        writer.WriteLine($"  oversized frames: {report.Health.OversizedFrames}");
        writer.WriteLine($"  missing post_state: {report.Health.MissingPostState}");
        writer.WriteLine(
            $"  unknown/unavailable markers: {report.Health.UnknownUnavailableMarkers} expected_gaps={report.Health.UnknownUnavailable.ExpectedGapRawCount} contract_risks={report.Health.UnknownUnavailable.ContractRiskRawCount}");
        writer.WriteLine(
            $"  unknown/unavailable unique normalized: {report.Health.UnknownUnavailable.UniqueNormalizedCount} expected_gaps={report.Health.UnknownUnavailable.ExpectedGapUniqueCount} contract_risks={report.Health.UnknownUnavailable.ContractRiskUniqueCount}");
        writer.WriteLine($"  operational records: {report.Health.OperationalRecords}");
        writer.WriteLine($"  operational malformed records: {report.Health.OperationalMalformedRecords}");
        if (report.Health.OperationalSourcePaths.Count > 0)
            writer.WriteLine($"  operational sources: {string.Join(", ", report.Health.OperationalSourcePaths)}");
        writer.WriteLine($"  hard failures: {report.Health.HardFailures}");
        writer.WriteLine($"  warnings: {report.Health.Warnings}");
        if (report.Health.UnknownUnavailable.TopExpectedGapCategories.Count > 0)
        {
            writer.WriteLine("  expected readiness gaps:");
            foreach (UnknownUnavailableMarkerCategory category in report.Health.UnknownUnavailable.TopExpectedGapCategories)
                writer.WriteLine($"    {category.Code} x{category.Count} path={category.NormalizedPath}");
        }

        if (report.Health.UnknownUnavailable.TopContractRiskCategories.Count > 0)
        {
            writer.WriteLine("  contract-risk markers:");
            foreach (UnknownUnavailableMarkerCategory category in report.Health.UnknownUnavailable.TopContractRiskCategories)
                writer.WriteLine($"    {category.Code} x{category.Count} path={category.NormalizedPath}");
        }
    }

    private static void WriteReadiness(TelemetryInspectionReport report, TextWriter writer)
    {
        ReadinessSummary readiness = report.Readiness;
        CombatReadinessSummary combat = readiness.Combat;

        writer.WriteLine("Readiness");
        writer.WriteLine($"  training-critical: status={readiness.TrainingCritical.Status}");
        writer.WriteLine(
            $"    trainable decisions: {readiness.TrainableDecisionCount} / opportunities={readiness.DecisionFrameCount + readiness.SelectedActionContextReferenceCount} raw={readiness.RawTrainableDecisionCount}");
        writer.WriteLine(
            $"    decision frames: {CountMetric(readiness.TrainingCritical, "decision_frames")}");
        writer.WriteLine(
            $"    legal-action contexts: {readiness.TrainableLegalActionContextCount}/{readiness.LegalActionContextCount} coverage={FormatPercent(readiness.LegalActionCoverage)}");
        writer.WriteLine(
            $"    context-backed signal actions: {CountMetric(readiness.TrainingCritical, "context_backed_signal_actions")}");

        writer.WriteLine($"  warnings: status={readiness.Warnings.Status}");
        if (readiness.Warnings.Metrics.Count == 0)
        {
            writer.WriteLine("    none");
        }
        else
        {
            foreach (ReadinessMetricSummary metric in readiness.Warnings.Metrics)
                writer.WriteLine($"    {FormatReadinessMetric(metric)}");
        }

        writer.WriteLine("  diagnostics:");
        writer.WriteLine(
            $"    selected-action context refs (diagnostic/non-blocking): {readiness.SelectedActionMatchCount}/{readiness.SelectedActionContextReferenceCount} match_rate={FormatPercent(readiness.SelectedActionMatchRate)} raw={readiness.RawSelectedActionMatchCount}/{readiness.RawSelectedActionContextReferenceCount} raw_rate={FormatPercent(readiness.RawSelectedActionMatchRate)}");
        writer.WriteLine($"    context-only records: {readiness.ContextOnlyCount}");
        writer.WriteLine($"    signal-only records: {readiness.SignalOnlyCount}");
        writer.WriteLine($"    raw trainable decisions: {readiness.RawTrainableDecisionCount}");

        writer.WriteLine(
            $"  combat detail: core frames={combat.DecisionFrameCount} turn={combat.FramesWithTurnMarkers}/{combat.DecisionFrameCount} phase={combat.FramesWithPhaseMarkers}/{combat.DecisionFrameCount} state={combat.FramesWithDetailedState}/{combat.DecisionFrameCount} stable_action_identity={combat.FramesWithStableActionIdentity}/{combat.DecisionFrameCount}");
        writer.WriteLine(
            $"  combat process markers (optional): process={combat.ProcessDetail.FramesWithAnyProcessDetail}/{combat.DecisionFrameCount} recorder={combat.ProcessDetail.FramesWithRecorderCombatProcess}/{combat.DecisionFrameCount} snapshot={combat.ProcessDetail.FramesWithSnapshotCombatProcess}/{combat.DecisionFrameCount}");
        foreach (CombatMarkerCoverageSummary marker in combat.ProcessDetail.OptionalMarkers)
            writer.WriteLine($"    {FormatCombatMarker(marker)}");
    }

    private static void WriteSuspicious(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Suspicious");
        if (report.Suspicious.Findings.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (TelemetryFinding finding in report.Suspicious.Findings.Take(30))
        {
            string location = finding.Sequence.HasValue
                ? $"seq={finding.Sequence.Value}"
                : finding.LineNumber.HasValue ? $"line={finding.LineNumber.Value}" : "run";
            string source = string.IsNullOrWhiteSpace(finding.SourcePath) ? "" : $" source={finding.SourcePath}";
            writer.WriteLine($"  [{finding.Severity}] {finding.Code} {location}{source}: {finding.Message}");
        }
    }

    private static void WriteTopExamples(TelemetryInspectionReport report, TextWriter writer)
    {
        writer.WriteLine("Top Examples");
        if (report.TopExamples.Count == 0)
        {
            writer.WriteLine("  none");
            return;
        }

        foreach (ExampleRecord example in report.TopExamples)
            writer.WriteLine($"  {example.Label}: seq={Format(example.Sequence)} line={Format(example.LineNumber)} type={example.RecordType ?? "-"} detail={example.Detail}");
    }

    private static string FormatFrame(FrameSummary? frame)
        => frame == null
            ? "unavailable"
            : $"seq={Format(frame.Sequence)} type={frame.RecordType ?? "-"} bytes={frame.ByteSize}";

    private static string FormatTiming(TimingPhaseSummary? timing)
        => timing == null
            ? "unavailable"
            : $"{timing.Phase} max={timing.MaxMicroseconds}us avg={timing.AverageMicroseconds:F1}us count={timing.Count} seq={Format(timing.ExampleSequence)}";

    private static string CountMetric(ReadinessCategorySummary category, string code)
    {
        ReadinessMetricSummary? metric = category.Metrics.FirstOrDefault(metric => metric.Code == code);
        return metric == null ? "-" : FormatReadinessMetricValue(metric);
    }

    private static string FormatReadinessMetric(ReadinessMetricSummary metric)
        => $"{metric.Label}: {FormatReadinessMetricValue(metric)} status={metric.Status}";

    private static string FormatReadinessMetricValue(ReadinessMetricSummary metric)
    {
        string count = metric.Total.HasValue
            ? $"{Format(metric.Count)}/{metric.Total.Value}"
            : Format(metric.Count);
        string rate = metric.Rate.HasValue ? $" rate={FormatPercent(metric.Rate.Value)}" : "";
        return $"{count}{rate}";
    }

    private static string FormatCombatMarker(CombatMarkerCoverageSummary marker)
        => $"{marker.Marker}: present={marker.Present}/{marker.Total} unavailable={marker.Unavailable} missing={marker.Missing} coverage={FormatPercent(marker.Coverage)} status={marker.Status}";

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts)
        => counts.Count == 0
            ? "-"
            : string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    private static string FormatPercent(double value)
        => $"{value:P1}";

    private static string FormatIndicators(IReadOnlyList<SurfaceCoverage> indicators)
        => string.Join(",", indicators.Select(indicator => $"{indicator.Surface}={indicator.RecordCount}"));

    private static string Format(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O") ?? "-";

    private static string Format(long? value)
        => value?.ToString() ?? "-";

    private static string Format(int? value)
        => value?.ToString() ?? "-";

    private static string Format(bool? value)
        => value?.ToString() ?? "-";
}
