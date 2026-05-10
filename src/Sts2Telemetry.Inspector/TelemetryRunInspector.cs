using System.Text.Json;

namespace Sts2Telemetry.Inspector;

public static class TelemetryRunInspector
{
    private const string ErrorSeverity = "error";
    private const string WarningSeverity = "warning";

    private enum CombatMarkerAvailability
    {
        Present,
        Unavailable,
        Missing
    }

    public static TelemetryInspectionReport Inspect(string telemetryPath, TelemetryInspectorOptions? options = null)
    {
        TelemetryRunSource source = TelemetryRunLocator.Resolve(telemetryPath, options?.RunsDirectory);
        return Inspect(source, options);
    }

    public static TelemetryInspectionReport Inspect(TelemetryRunSource source, TelemetryInspectorOptions? options = null)
    {
        TelemetryInspectorOptions effectiveOptions = options ?? new TelemetryInspectorOptions();
        IReadOnlyList<TelemetryRecord> records = TelemetryJsonlReader.ReadMany(source.TelemetryPaths);
        List<TelemetryRecord> validRecords = records.Where(record => !record.IsMalformed && record.Root.HasValue).ToList();
        UnknownUnavailableMarkerAnalysis markerAnalysis = UnknownUnavailableMarkerAnalyzer.Analyze(validRecords);
        IReadOnlyList<TelemetryRecord> operationalRecords = ReadOperationalRecords(source, effectiveOptions, validRecords);
        List<TelemetryRecord> validOperationalRecords = operationalRecords
            .Where(record => !record.IsMalformed && record.Root.HasValue)
            .ToList();
        var findings = new List<TelemetryFinding>();

        AddHardValidationFindings(records, effectiveOptions, markerAnalysis, findings);
        AddOperationalFindings(operationalRecords, findings);
        CoverageSummary coverage = BuildCoverage(validRecords);
        ReadinessSummary readiness = BuildReadiness(validRecords);
        AddBranchFindings(validRecords, findings);
        AddCoverageWarnings(validRecords, coverage, findings);

        IReadOnlyList<TelemetryFinding> errors = findings
            .Where(finding => finding.Severity == ErrorSeverity)
            .ToArray();
        IReadOnlyList<TelemetryFinding> warnings = findings
            .Where(finding => finding.Severity == WarningSeverity)
            .ToArray();

        IReadOnlyList<FrameSummary> largestRecords = BuildLargestRecords(records, effectiveOptions, count: 20);

        return new TelemetryInspectionReport
        {
            RunSummary = BuildRunSummary(source, validRecords, records.Count),
            Health = new HealthSummary
            {
                CallbackFailures = validRecords.Count(record => record.RecordType == "lifecycle/telemetry_callback_failed")
                    + validOperationalRecords.Count(record => record.RecordType == "lifecycle/telemetry_callback_failed"),
                OversizedFrames = records.Count(record => record.ByteSize > effectiveOptions.MaxFrameBytes),
                MissingPostState = findings.Count(finding => finding.Code == "missing_post_state"),
                UnknownUnavailableMarkers = markerAnalysis.RawCount,
                UnknownUnavailable = markerAnalysis.ToSummary(),
                MalformedRecords = records.Count(record => record.IsMalformed) + operationalRecords.Count(record => record.IsMalformed),
                OperationalRecords = operationalRecords.Count,
                OperationalMalformedRecords = operationalRecords.Count(record => record.IsMalformed),
                OperationalSourcePaths = operationalRecords
                    .Select(record => record.SourcePath)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                HardFailures = errors.Count,
                Warnings = warnings.Count
            },
            Readiness = readiness,
            Performance = BuildPerformance(records, validRecords, largestRecords),
            Branch = BuildBranchSummary(validRecords, findings),
            Coverage = coverage,
            Suspicious = new SuspiciousSummary
            {
                HardFailureCount = errors.Count,
                WarningCount = warnings.Count,
                Findings = findings
                    .OrderBy(finding => finding.Severity == ErrorSeverity ? 0 : 1)
                    .ThenBy(finding => finding.Sequence ?? long.MaxValue)
                    .ThenBy(finding => finding.LineNumber ?? int.MaxValue)
                    .ToArray()
            },
            LargestRecords = largestRecords,
            TopExamples = BuildTopExamples(records, findings, largestRecords, effectiveOptions.TopExamples),
            Validation = new ValidationSummary
            {
                Errors = errors,
                Warnings = warnings
            }
        };
    }

    public static TelemetryRecord? FindBySequence(string telemetryPath, long sequence)
        => FindBySequence(TelemetryRunLocator.Resolve(telemetryPath), sequence);

    public static TelemetryRecord? FindBySequence(TelemetryRunSource source, long sequence)
        => TelemetryJsonlReader.ReadMany(source.TelemetryPaths)
            .FirstOrDefault(record => record.LocalSequence == sequence);

    private static void AddHardValidationFindings(
        IReadOnlyList<TelemetryRecord> records,
        TelemetryInspectorOptions options,
        UnknownUnavailableMarkerAnalysis markerAnalysis,
        List<TelemetryFinding> findings)
    {
        foreach (TelemetryRecord record in records)
        {
            if (record.IsMalformed)
            {
                findings.Add(Finding(ErrorSeverity, "malformed_json", $"Line {record.LineNumber} is malformed JSON: {record.ParseError}", record));
                continue;
            }

            JsonElement root = record.Root!.Value;
            string? recordType = record.RecordType;

            if (record.ByteSize > options.MaxFrameBytes)
            {
                findings.Add(Finding(
                    ErrorSeverity,
                    "oversized_frame",
                    $"Record is {record.ByteSize} bytes, above the configured {options.MaxFrameBytes} byte maximum.",
                    record));
            }

            if (recordType == "lifecycle/telemetry_callback_failed")
                findings.Add(Finding(ErrorSeverity, "telemetry_callback_failed", "Telemetry callback failure record exists.", record));

            if (recordType == "decision/frame")
            {
                if (!JsonElementAccess.HasPath(root, "selected_action.normalized_typed_action_key"))
                {
                    findings.Add(Finding(
                        ErrorSeverity,
                        "missing_normalized_typed_action_key",
                        "Decision frame is missing selected_action.normalized_typed_action_key.",
                        record));
                }

                if (!JsonElementAccess.HasPath(root, "selected_action.canonical_action_hash"))
                {
                    findings.Add(Finding(
                        ErrorSeverity,
                        "missing_canonical_action_hash",
                        "Decision frame is missing selected_action.canonical_action_hash.",
                        record));
                }

                if (!IsPendingDecisionFrame(root) && !JsonElementAccess.HasPath(root, "post_state"))
                {
                    findings.Add(Finding(
                        ErrorSeverity,
                        "missing_post_state",
                        "Completed decision frame is missing post_state.",
                        record));
                }

                if (!IsPendingDecisionFrame(root) && !JsonElementAccess.HasPath(root, "branch_decision"))
                {
                    findings.Add(Finding(
                        ErrorSeverity,
                        "missing_branch_decision",
                        "Completed decision frame is missing branch_decision.",
                        record));
                }
            }

            if (recordType == "lifecycle/branch_forked" && !JsonElementAccess.HasPath(root, "branch_decision"))
            {
                findings.Add(Finding(
                    ErrorSeverity,
                    "branch_forked_missing_branch_decision",
                    "lifecycle/branch_forked is missing branch_decision.",
                    record));
            }
        }

        if (markerAnalysis.ContractRiskRawCount > options.UnknownMarkerThreshold)
        {
            findings.Add(new TelemetryFinding
            {
                Severity = ErrorSeverity,
                Code = "contract_risk_unknown_unavailable_markers_exceed_threshold",
                Message =
                    $"Found {markerAnalysis.ContractRiskRawCount} contract-risk unknown/unavailable markers above the configured threshold of {options.UnknownMarkerThreshold}. Expected readiness gaps remain visible separately ({markerAnalysis.ExpectedGapRawCount} raw markers, {markerAnalysis.ExpectedGapUniqueCount} unique normalized categories)."
            });
        }
    }

    private static void AddOperationalFindings(
        IReadOnlyList<TelemetryRecord> operationalRecords,
        List<TelemetryFinding> findings)
    {
        foreach (TelemetryRecord record in operationalRecords)
        {
            if (record.IsMalformed)
            {
                findings.Add(Finding(
                    ErrorSeverity,
                    "malformed_operational_json",
                    $"Operational line {record.LineNumber} is malformed JSON: {record.ParseError}",
                    record));
                continue;
            }

            if (record.RecordType == "lifecycle/telemetry_callback_failed")
            {
                findings.Add(Finding(
                    ErrorSeverity,
                    "telemetry_callback_failed",
                    "Telemetry callback failure record exists in operational telemetry.",
                    record));
            }
        }
    }

    private static IReadOnlyList<TelemetryRecord> ReadOperationalRecords(
        TelemetryRunSource source,
        TelemetryInspectorOptions options,
        IReadOnlyList<TelemetryRecord> validRunRecords)
    {
        string operationalDirectory = TelemetryRunLocator.ResolveOperationalDirectory(source.TelemetryPath, options);
        if (!Directory.Exists(operationalDirectory))
            return Array.Empty<TelemetryRecord>();

        var records = new List<TelemetryRecord>();
        foreach (DateOnly date in OperationalDates(source, validRunRecords))
        {
            string path = Path.Combine(operationalDirectory, $"{date:yyyyMMdd}.jsonl");
            if (File.Exists(path))
                records.AddRange(TelemetryJsonlReader.Read(path));
        }

        return records;
    }

    private static IReadOnlyList<DateOnly> OperationalDates(
        TelemetryRunSource source,
        IReadOnlyList<TelemetryRecord> validRunRecords)
    {
        DateTimeOffset? first = validRunRecords
            .Select(record => record.RecordedAtUtc)
            .Where(value => value.HasValue)
            .OrderBy(value => value)
            .FirstOrDefault();
        DateTimeOffset? last = validRunRecords
            .Select(record => record.RecordedAtUtc)
            .Where(value => value.HasValue)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        if (first == null || last == null)
        {
            DateTime fallback = LastWriteTimeUtc(source) ?? DateTime.UtcNow;
            return new[] { DateOnly.FromDateTime(fallback) };
        }

        DateOnly start = DateOnly.FromDateTime(first.Value.UtcDateTime);
        DateOnly end = DateOnly.FromDateTime(last.Value.UtcDateTime);
        var dates = new List<DateOnly>();
        for (DateOnly date = start; date <= end; date = date.AddDays(1))
            dates.Add(date);

        return dates;
    }

    private static void AddBranchFindings(IReadOnlyList<TelemetryRecord> records, List<TelemetryFinding> findings)
    {
        List<(TelemetryRecord Record, int Index)> branchMatchedRecords = records
            .Select((record, index) => (Record: record, Index: index))
            .Where(item => item.Record.RecordType == "lifecycle/branch_matched")
            .ToList();

        foreach ((TelemetryRecord loaded, int loadedIndex) in records
            .Select((record, index) => (Record: record, Index: index))
            .Where(item => item.Record.RecordType == "lifecycle/run_loaded"))
        {
            JsonElement root = loaded.Root!.Value;
            if (JsonElementAccess.GetBoolean(root, "branch_match.matched") != true)
                continue;

            string? loadedAttempt = JsonElementAccess.GetString(root, "branch.attempt_id");
            string? loadedBranch = JsonElementAccess.GetString(root, "branch.branch_id");
            bool hasLaterMatch = branchMatchedRecords.Any(match =>
                match.Index > loadedIndex
                && MatchesBranchOrAttempt(match.Record.Root!.Value, loadedAttempt, loadedBranch));

            if (!hasLaterMatch)
            {
                findings.Add(Finding(
                    ErrorSeverity,
                    "run_loaded_without_later_branch_matched",
                    "lifecycle/run_loaded is not followed by a later lifecycle/branch_matched for the loaded attempt/branch.",
                    loaded));
            }
        }

        int firstReplayBoundary = records
            .Select((record, index) => (Record: record, Index: index))
            .Where(item => item.Record.RecordType is "lifecycle/run_loaded" or "lifecycle/branch_matched")
            .Select(item => item.Index)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        foreach ((TelemetryRecord record, int recordIndex) in records
            .Select((record, index) => (Record: record, Index: index))
            .Where(item => item.Record.RecordType == "decision/frame"))
        {
            JsonElement root = record.Root!.Value;
            bool trajectoryReplayed = JsonElementAccess.GetBoolean(root, "branch_decision.trajectory_replayed") == true;
            if (trajectoryReplayed && recordIndex < firstReplayBoundary)
            {
                findings.Add(Finding(
                    WarningSeverity,
                    "replay_prefix_before_load_or_match",
                    "trajectory_replayed=true appears before any run_loaded or branch_matched event.",
                    record));
            }
        }
    }

    private static bool MatchesBranchOrAttempt(JsonElement root, string? attemptId, string? branchId)
    {
        string? matchAttempt = JsonElementAccess.GetString(root, "branch.attempt_id");
        string? matchBranch = JsonElementAccess.GetString(root, "branch.branch_id");

        if (!string.IsNullOrWhiteSpace(attemptId) && string.Equals(attemptId, matchAttempt, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrWhiteSpace(branchId) && string.Equals(branchId, matchBranch, StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(attemptId) && string.IsNullOrWhiteSpace(branchId);
    }

    private static void AddCoverageWarnings(
        IReadOnlyList<TelemetryRecord> records,
        CoverageSummary coverage,
        List<TelemetryFinding> findings)
    {
        bool shopActionAppeared = records.Any(record =>
            record.Root is JsonElement root
            && (ContainsToken(root, "buy_shop")
                || ContainsToken(root, "remove_card_at_shop")
                || ContainsToken(root, "shop_purchase")));
        if (coverage.Shop.Appeared && !shopActionAppeared)
        {
            findings.Add(new TelemetryFinding
            {
                Severity = WarningSeverity,
                Code = "shop_surface_without_shop_action",
                Message = "Shop surface appears, but no shop action signal or selected action appears."
            });
        }

        bool relicCountObserved = records.Any(record =>
            record.Root is JsonElement root
            && JsonElementAccess.TryFindPositiveNumberByPropertyName(root, "relic_count"));
        if (relicCountObserved && !coverage.RelicTrigger.Appeared)
        {
            findings.Add(new TelemetryFinding
            {
                Severity = WarningSeverity,
                Code = "relic_count_without_relic_trigger",
                Message = "A positive relic_count appears, but no effect/relic_trigger record appears."
            });
        }
    }

    private static RunSummary BuildRunSummary(
        TelemetryRunSource source,
        IReadOnlyList<TelemetryRecord> validRecords,
        int totalRecordCount)
    {
        DateTimeOffset? firstRecordedAt = validRecords
            .Select(record => record.RecordedAtUtc)
            .Where(value => value.HasValue)
            .OrderBy(value => value)
            .FirstOrDefault();
        DateTimeOffset? lastRecordedAt = validRecords
            .Select(record => record.RecordedAtUtc)
            .Where(value => value.HasValue)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        string? runId = validRecords
            .Select(record => record.Root is JsonElement root ? JsonElementAccess.GetString(root, "run_id") : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new RunSummary
        {
            SourcePath = source.TelemetryPath,
            SourcePaths = source.TelemetryPaths,
            RunDirectory = source.RunDirectory,
            SegmentCount = source.SegmentCount,
            RunId = runId,
            RecordCount = totalRecordCount,
            MalformedRecordCount = totalRecordCount - validRecords.Count,
            FileSizeBytes = source.TelemetryPaths.Sum(path => File.Exists(path) ? new FileInfo(path).Length : 0),
            FirstRecordedAtUtc = firstRecordedAt,
            LastRecordedAtUtc = lastRecordedAt,
            NormalEnded = validRecords.Any(record => record.RecordType == "lifecycle/run_ended")
        };
    }

    private static DateTime? LastWriteTimeUtc(TelemetryRunSource source)
    {
        DateTime lastWrite = source.TelemetryPaths
            .Select(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default)
            .DefaultIfEmpty(default)
            .Max();
        return lastWrite == default ? null : lastWrite;
    }

    private static PerformanceSummary BuildPerformance(
        IReadOnlyList<TelemetryRecord> records,
        IReadOnlyList<TelemetryRecord> validRecords,
        IReadOnlyList<FrameSummary> largestRecords)
    {
        List<TelemetryRecord> decisionFrames = validRecords
            .Where(record => record.RecordType == "decision/frame")
            .ToList();

        List<TimingPhaseSummary> timingPhases = BuildTimingPhases(decisionFrames);

        int peakWritesPerSecond = validRecords
            .Select(record => record.RecordedAtUtc)
            .Where(value => value.HasValue)
            .GroupBy(value => value!.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .Select(group => group.Count())
            .DefaultIfEmpty(0)
            .Max();

        return new PerformanceSummary
        {
            LargestRecord = largestRecords.FirstOrDefault(),
            LargestDecisionFrame = BuildLargestRecords(decisionFrames, new TelemetryInspectorOptions { MaxFrameBytes = long.MaxValue }, 1).FirstOrDefault(),
            AverageRecordBytes = records.Count == 0 ? 0 : records.Average(record => record.ByteSize),
            AverageDecisionFrameBytes = decisionFrames.Count == 0 ? 0 : decisionFrames.Average(record => record.ByteSize),
            PeakWritesPerSecond = peakWritesPerSecond,
            SlowestTimingPhase = timingPhases.FirstOrDefault(),
            SlowestTimingPhases = timingPhases.Take(8).ToArray()
        };
    }

    private static ReadinessSummary BuildReadiness(IReadOnlyList<TelemetryRecord> records)
    {
        List<TelemetryRecord> decisionFrames = records
            .Where(record => record.RecordType == "decision/frame" && !IsPendingDecisionFrame(record.Root!.Value))
            .ToList();
        List<TelemetryRecord> contexts = records
            .Where(record => record.RecordType == "decision/context")
            .ToList();
        List<TelemetryRecord> signalOnlyRecords = records
            .Where(record => record.Root is JsonElement root && IsSignalOnlyRecord(root))
            .ToList();
        List<TelemetryRecord> rawContextReferences = signalOnlyRecords
            .Where(record => JsonElementAccess.HasPath(record.Root!.Value, "decision_context"))
            .ToList();
        IReadOnlyList<TelemetryRecord> contextReferences = DeduplicateLogicalSelectedActionReferences(rawContextReferences);

        int trainableDecisionFrameCount = decisionFrames.Count(record =>
            record.Root is JsonElement root
            && HasUsableLegalActions(root)
            && JsonElementAccess.HasPath(root, "selected_action.normalized_typed_action_key")
            && JsonElementAccess.HasPath(root, "post_state"));
        int rawSelectedActionMatchCount = rawContextReferences.Count(record =>
            JsonElementAccess.GetBoolean(record.Root!.Value, "decision_context.selected_action_match.matched") == true);
        int trainableLegalActionContextCount = contexts.Count(record =>
            record.Root is JsonElement root && HasUsableLegalActions(root));
        int selectedActionMatchCount = contextReferences.Count(record =>
            JsonElementAccess.GetBoolean(record.Root!.Value, "decision_context.selected_action_match.matched") == true);
        int trainableDecisionCount = trainableDecisionFrameCount + selectedActionMatchCount;
        int rawTrainableDecisionCount = trainableDecisionFrameCount + rawSelectedActionMatchCount;
        CombatReadinessSummary combat = BuildCombatReadiness(decisionFrames);

        return new ReadinessSummary
        {
            TrainableDecisionCount = trainableDecisionCount,
            RawTrainableDecisionCount = rawTrainableDecisionCount,
            DecisionFrameCount = decisionFrames.Count,
            LegalActionContextCount = contexts.Count,
            TrainableLegalActionContextCount = trainableLegalActionContextCount,
            LegalActionCoverage = Ratio(trainableLegalActionContextCount, contexts.Count),
            SelectedActionContextReferenceCount = contextReferences.Count,
            SelectedActionMatchCount = selectedActionMatchCount,
            SelectedActionMatchRate = Ratio(selectedActionMatchCount, contextReferences.Count),
            RawSelectedActionContextReferenceCount = rawContextReferences.Count,
            RawSelectedActionMatchCount = rawSelectedActionMatchCount,
            RawSelectedActionMatchRate = Ratio(rawSelectedActionMatchCount, rawContextReferences.Count),
            ContextOnlyCount = contexts.Count,
            SignalOnlyCount = signalOnlyRecords.Count,
            TrainingCritical = BuildTrainingCriticalReadiness(
                trainableDecisionCount,
                trainableDecisionFrameCount,
                decisionFrames.Count,
                trainableLegalActionContextCount,
                contexts.Count,
                selectedActionMatchCount,
                contextReferences.Count),
            Warnings = BuildReadinessWarnings(
                trainableDecisionFrameCount,
                decisionFrames.Count,
                trainableLegalActionContextCount,
                contexts.Count,
                combat),
            Diagnostics = BuildReadinessDiagnostics(
                rawTrainableDecisionCount,
                rawContextReferences.Count,
                rawSelectedActionMatchCount,
                contextReferences.Count,
                selectedActionMatchCount,
                contexts.Count,
                signalOnlyRecords.Count),
            Combat = combat
        };
    }

    private static ReadinessCategorySummary BuildTrainingCriticalReadiness(
        int trainableDecisionCount,
        int trainableDecisionFrameCount,
        int decisionFrameCount,
        int trainableLegalActionContextCount,
        int legalActionContextCount,
        int selectedActionMatchCount,
        int selectedActionReferenceCount)
    {
        int trainableOpportunityCount = decisionFrameCount + selectedActionReferenceCount;
        string status = trainableOpportunityCount == 0
            ? "no_decisions_observed"
            : trainableDecisionCount > 0 ? "ready" : "not_ready";

        return new ReadinessCategorySummary
        {
            Status = status,
            Metrics = new[]
            {
                Metric(
                    "trainable_decisions",
                    "trainable decisions",
                    status,
                    trainableDecisionCount,
                    trainableOpportunityCount,
                    Ratio(trainableDecisionCount, trainableOpportunityCount),
                    "completed decision frames plus matched context-backed signal actions"),
                Metric(
                    "decision_frames",
                    "decision frames with trainable action data",
                    decisionFrameCount == 0 ? "not_observed" : trainableDecisionFrameCount > 0 ? "ready" : "not_ready",
                    trainableDecisionFrameCount,
                    decisionFrameCount,
                    Ratio(trainableDecisionFrameCount, decisionFrameCount),
                    "requires usable legal actions, selected-action typed key, and post_state"),
                Metric(
                    "legal_action_contexts",
                    "trainable legal-action contexts",
                    legalActionContextCount == 0 ? "not_observed" : trainableLegalActionContextCount > 0 ? "ready" : "not_ready",
                    trainableLegalActionContextCount,
                    legalActionContextCount,
                    Ratio(trainableLegalActionContextCount, legalActionContextCount),
                    "placeholder and unavailable legal actions are excluded"),
                Metric(
                    "context_backed_signal_actions",
                    "context-backed signal actions",
                    selectedActionReferenceCount == 0 ? "not_observed" : selectedActionMatchCount > 0 ? "ready" : "not_ready",
                    selectedActionMatchCount,
                    selectedActionReferenceCount,
                    Ratio(selectedActionMatchCount, selectedActionReferenceCount),
                    "matched signal-only selected actions count toward trainable decisions")
            }
        };
    }

    private static ReadinessCategorySummary BuildReadinessWarnings(
        int trainableDecisionFrameCount,
        int decisionFrameCount,
        int trainableLegalActionContextCount,
        int legalActionContextCount,
        CombatReadinessSummary combat)
    {
        var metrics = new List<ReadinessMetricSummary>();
        int untrainableDecisionFrames = decisionFrameCount - trainableDecisionFrameCount;
        if (untrainableDecisionFrames > 0)
        {
            metrics.Add(Metric(
                "untrainable_decision_frames",
                "decision frames missing trainable action data",
                "warning",
                untrainableDecisionFrames,
                decisionFrameCount,
                Ratio(untrainableDecisionFrames, decisionFrameCount),
                "review legal actions, selected-action typed key, and post_state"));
        }

        int untrainableContexts = legalActionContextCount - trainableLegalActionContextCount;
        if (untrainableContexts > 0)
        {
            metrics.Add(Metric(
                "untrainable_legal_action_contexts",
                "legal-action contexts not trainable",
                "warning",
                untrainableContexts,
                legalActionContextCount,
                Ratio(untrainableContexts, legalActionContextCount),
                "expected for typed-builder gaps and placeholder-only contexts"));
        }

        AddCombatWarning(metrics, "combat_turn_markers_missing", "combat turn markers missing", combat.DecisionFrameCount - combat.FramesWithTurnMarkers, combat.DecisionFrameCount);
        AddCombatWarning(metrics, "combat_phase_markers_missing", "combat phase markers missing", combat.DecisionFrameCount - combat.FramesWithPhaseMarkers, combat.DecisionFrameCount);
        AddCombatWarning(metrics, "combat_detailed_state_missing", "combat detailed state missing", combat.DecisionFrameCount - combat.FramesWithDetailedState, combat.DecisionFrameCount);
        AddCombatWarning(metrics, "combat_stable_action_identity_missing", "combat stable action identity missing", combat.DecisionFrameCount - combat.FramesWithStableActionIdentity, combat.DecisionFrameCount);

        return new ReadinessCategorySummary
        {
            Status = metrics.Count == 0 ? "clear" : "warnings_present",
            Metrics = metrics
        };
    }

    private static void AddCombatWarning(
        ICollection<ReadinessMetricSummary> metrics,
        string code,
        string label,
        int missing,
        int total)
    {
        if (total == 0 || missing <= 0)
            return;

        metrics.Add(Metric(
            code,
            label,
            "warning",
            missing,
            total,
            Ratio(missing, total),
            "non-blocking combat detail gap"));
    }

    private static ReadinessCategorySummary BuildReadinessDiagnostics(
        int rawTrainableDecisionCount,
        int rawSelectedActionReferenceCount,
        int rawSelectedActionMatchCount,
        int selectedActionReferenceCount,
        int selectedActionMatchCount,
        int contextOnlyCount,
        int signalOnlyCount)
        => new()
        {
            Status = "diagnostic_only",
            Metrics = new[]
            {
                Metric(
                    "selected_action_context_refs",
                    "selected-action context refs",
                    "diagnostic",
                    selectedActionMatchCount,
                    selectedActionReferenceCount,
                    Ratio(selectedActionMatchCount, selectedActionReferenceCount),
                    "logical deduped references; non-blocking unless matched actions are needed for trainable decision availability"),
                Metric(
                    "raw_selected_action_context_refs",
                    "raw selected-action context refs",
                    "diagnostic",
                    rawSelectedActionMatchCount,
                    rawSelectedActionReferenceCount,
                    Ratio(rawSelectedActionMatchCount, rawSelectedActionReferenceCount),
                    "pre-dedup signal evidence"),
                Metric(
                    "raw_trainable_decisions",
                    "raw trainable decisions",
                    "diagnostic",
                    rawTrainableDecisionCount,
                    null,
                    null,
                    "decision frames plus every raw matched selected-action signal"),
                Metric(
                    "context_only_records",
                    "context-only records",
                    "diagnostic",
                    contextOnlyCount),
                Metric(
                    "signal_only_records",
                    "signal-only records",
                    "diagnostic",
                    signalOnlyCount)
            }
        };

    private static IReadOnlyList<TelemetryRecord> DeduplicateLogicalSelectedActionReferences(
        IReadOnlyList<TelemetryRecord> contextReferences)
    {
        var representatives = new Dictionary<string, TelemetryRecord>(StringComparer.Ordinal);
        var passthrough = new List<TelemetryRecord>();

        foreach (TelemetryRecord record in contextReferences)
        {
            JsonElement root = record.Root!.Value;
            string? logicalKey = TryGetLogicalSelectedActionReferenceKey(root);
            if (logicalKey == null)
            {
                passthrough.Add(record);
                continue;
            }

            if (!representatives.TryGetValue(logicalKey, out TelemetryRecord? existing)
                || CompareSelectedActionReferencePreference(root, existing.Root!.Value) > 0)
            {
                representatives[logicalKey] = record;
            }
        }

        return passthrough
            .Concat(representatives.Values)
            .OrderBy(record => record.LocalSequence ?? long.MaxValue)
            .ToArray();
    }

    private static string? TryGetLogicalSelectedActionReferenceKey(JsonElement root)
    {
        if (!string.Equals(JsonElementAccess.GetString(root, "decision_context.state_type"), "shop", StringComparison.Ordinal))
            return null;

        string? contextId = JsonElementAccess.GetString(root, "decision_context.decision_context_id")
            ?? JsonElementAccess.GetString(root, "decision_context.canonical_state_hash")
            ?? JsonElementAccess.GetString(root, "decision_context.raw_state_hash");
        if (string.IsNullOrWhiteSpace(contextId))
            return null;

        string? selectedActionHash = JsonElementAccess.GetString(
            root,
            "decision_context.selected_action_match.selected_action_canonical_hash");
        if (!string.IsNullOrWhiteSpace(selectedActionHash))
            return $"{contextId}|{selectedActionHash}";

        if (!JsonElementAccess.TryGetPath(root, "ui_signal.metadata.normalized_typed_action_key", out JsonElement normalizedKey))
            return null;

        return $"{contextId}|{normalizedKey.GetRawText()}";
    }

    private static int CompareSelectedActionReferencePreference(JsonElement left, JsonElement right)
    {
        int leftScore = SelectedActionReferencePreferenceScore(left);
        int rightScore = SelectedActionReferencePreferenceScore(right);
        if (leftScore != rightScore)
            return leftScore.CompareTo(rightScore);

        long leftSequence = JsonElementAccess.GetInt64(left, "local_sequence") ?? long.MinValue;
        long rightSequence = JsonElementAccess.GetInt64(right, "local_sequence") ?? long.MinValue;
        return leftSequence.CompareTo(rightSequence);
    }

    private static int SelectedActionReferencePreferenceScore(JsonElement root)
    {
        int score = 0;
        if (JsonElementAccess.GetBoolean(root, "decision_context.selected_action_match.matched") == true)
            score += 2;

        if (string.Equals(
                JsonElementAccess.GetString(root, "ui_signal.metadata.purchase_status"),
                "completed",
                StringComparison.Ordinal))
        {
            score += 4;
        }

        return score;
    }

    private static CombatReadinessSummary BuildCombatReadiness(IReadOnlyList<TelemetryRecord> decisionFrames)
    {
        List<TelemetryRecord> combatFrames = decisionFrames
            .Where(record => record.Root is JsonElement root && HasStateType(root, "combat"))
            .ToList();
        int total = combatFrames.Count;
        CombatMarkerCoverageSummary turnMarker = BuildCombatMarkerCoverage(
            combatFrames,
            "turn",
            "core",
            "turn_index/round from recorder combat_process or snapshot combat.process");
        CombatMarkerCoverageSummary phaseMarker = BuildCombatMarkerCoverage(
            combatFrames,
            "phase",
            "core",
            "phase/turn_side/current_side/is_play_phase from recorder combat_process or snapshot combat.process");
        CombatMarkerCoverageSummary actionStepMarker = BuildCombatMarkerCoverage(
            combatFrames,
            "action_step",
            "optional",
            "explicit action_step value only; unavailable is an expected detail gap");
        CombatMarkerCoverageSummary actionIndexMarker = BuildCombatMarkerCoverage(
            combatFrames,
            "action_index",
            "optional",
            "explicit action_index value only; unavailable is an expected detail gap");
        int detailedState = combatFrames.Count(record => HasDetailedCombatState(record.Root!.Value));
        int stableActionIdentity = combatFrames.Count(record => HasStableCombatActionIdentity(record.Root!.Value));
        int recorderCombatProcess = combatFrames.Count(record => HasRecorderCombatProcess(record.Root!.Value));
        int snapshotCombatProcess = combatFrames.Count(record => HasSnapshotCombatProcess(record.Root!.Value));

        return new CombatReadinessSummary
        {
            DecisionFrameCount = total,
            FramesWithTurnMarkers = turnMarker.Present,
            FramesWithPhaseMarkers = phaseMarker.Present,
            FramesWithActionStepMarkers = actionStepMarker.Present,
            FramesWithActionIndexMarkers = actionIndexMarker.Present,
            FramesWithDetailedState = detailedState,
            FramesWithStableActionIdentity = stableActionIdentity,
            TurnMarkerCoverage = Ratio(turnMarker.Present, total),
            PhaseMarkerCoverage = Ratio(phaseMarker.Present, total),
            ActionStepMarkerCoverage = Ratio(actionStepMarker.Present, total),
            ActionIndexMarkerCoverage = Ratio(actionIndexMarker.Present, total),
            DetailedStateCoverage = Ratio(detailedState, total),
            StableActionIdentityCoverage = Ratio(stableActionIdentity, total),
            ProcessDetail = new CombatProcessDetailSummary
            {
                FramesWithRecorderCombatProcess = recorderCombatProcess,
                FramesWithSnapshotCombatProcess = snapshotCombatProcess,
                FramesWithAnyProcessDetail = combatFrames.Count(record =>
                    HasRecorderCombatProcess(record.Root!.Value) || HasSnapshotCombatProcess(record.Root!.Value)),
                CoreMarkers = new[] { turnMarker, phaseMarker },
                OptionalMarkers = new[] { actionStepMarker, actionIndexMarker }
            }
        };
    }

    private static List<TimingPhaseSummary> BuildTimingPhases(IReadOnlyList<TelemetryRecord> decisionFrames)
    {
        var valuesByPhase = new Dictionary<string, List<(long Value, long? Sequence)>>(StringComparer.Ordinal);

        foreach (TelemetryRecord frame in decisionFrames)
        {
            JsonElement root = frame.Root!.Value;
            if (!JsonElementAccess.TryGetPath(root, "operational_metadata.decision_timing", out JsonElement timing)
                || timing.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty property in timing.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out long value))
                    continue;

                if (!valuesByPhase.TryGetValue(property.Name, out List<(long Value, long? Sequence)>? values))
                {
                    values = new List<(long Value, long? Sequence)>();
                    valuesByPhase[property.Name] = values;
                }

                values.Add((value, frame.LocalSequence));
            }
        }

        return valuesByPhase
            .Select(pair =>
            {
                (long Value, long? Sequence) max = pair.Value.OrderByDescending(value => value.Value).First();
                return new TimingPhaseSummary
                {
                    Phase = pair.Key,
                    MaxMicroseconds = max.Value,
                    AverageMicroseconds = pair.Value.Average(value => value.Value),
                    Count = pair.Value.Count,
                    ExampleSequence = max.Sequence
                };
            })
            .OrderByDescending(summary => summary.MaxMicroseconds)
            .ThenBy(summary => summary.Phase, StringComparer.Ordinal)
            .ToList();
    }

    private static BranchSummary BuildBranchSummary(IReadOnlyList<TelemetryRecord> records, IReadOnlyList<TelemetryFinding> findings)
    {
        Dictionary<string, int> branchCounts = CountByPath(records, "branch.branch_id");
        Dictionary<string, int> attemptCounts = CountByPath(records, "branch.attempt_id");
        List<BranchTimelineEntry> timeline = records
            .Where(record => record.RecordType is "lifecycle/run_loaded" or "lifecycle/branch_matched" or "lifecycle/branch_forked"
                || JsonElementAccess.GetBoolean(record.Root!.Value, "branch_decision.trajectory_replayed") == true
                || JsonElementAccess.GetBoolean(record.Root!.Value, "branch_decision.forked") == true)
            .Select(record =>
            {
                JsonElement root = record.Root!.Value;
                return new BranchTimelineEntry
                {
                    Sequence = record.LocalSequence,
                    RecordType = record.RecordType,
                    BranchId = JsonElementAccess.GetString(root, "branch.branch_id"),
                    BranchStatus = JsonElementAccess.GetString(root, "branch.branch_status"),
                    AttemptId = JsonElementAccess.GetString(root, "branch.attempt_id"),
                    AttemptStatus = JsonElementAccess.GetString(root, "branch.attempt_status"),
                    TrajectoryReplayed = JsonElementAccess.GetBoolean(root, "branch_decision.trajectory_replayed"),
                    Forked = JsonElementAccess.GetBoolean(root, "branch_decision.forked"),
                    Reason = JsonElementAccess.GetString(root, "branch_decision.reason")
                        ?? JsonElementAccess.GetString(root, "branch_match.reason")
                };
            })
            .Take(50)
            .ToList();

        return new BranchSummary
        {
            BranchRecordCounts = branchCounts,
            AttemptRecordCounts = attemptCounts,
            RunLoadedCount = records.Count(record => record.RecordType == "lifecycle/run_loaded"),
            BranchMatchedCount = records.Count(record => record.RecordType == "lifecycle/branch_matched"),
            BranchForkedCount = records.Count(record => record.RecordType == "lifecycle/branch_forked"),
            ReplayedDecisionFrameCount = records.Count(record =>
                record.Root is JsonElement root
                && JsonElementAccess.GetBoolean(root, "branch_decision.trajectory_replayed") == true),
            ReplayPrefixInconsistencyCount = findings.Count(finding => finding.Code == "replay_prefix_before_load_or_match"),
            Timeline = timeline
        };
    }

    private static Dictionary<string, int> CountByPath(IReadOnlyList<TelemetryRecord> records, string path)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (TelemetryRecord record in records)
        {
            JsonElement root = record.Root!.Value;
            string? value = JsonElementAccess.GetString(root, path);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            counts[value] = counts.TryGetValue(value, out int count) ? count + 1 : 1;
        }

        return counts;
    }

    private static CoverageSummary BuildCoverage(IReadOnlyList<TelemetryRecord> records)
    {
        return new CoverageSummary
        {
            Combat = Surface("combat", records, root => ContainsToken(root, "combat") || HasStateType(root, "combat")),
            Map = Surface("map", records, root => ContainsToken(root, "map") || ContainsToken(root, "coord") || HasStateType(root, "map")),
            Shop = Surface("shop", records, root => ContainsToken(root, "shop") || HasStateType(root, "shop")),
            Event = Surface("event", records, root => ContainsToken(root, "event") || HasStateType(root, "event")),
            Reward = Surface("reward", records, root => ContainsToken(root, "reward") || HasStateType(root, "reward")),
            Rest = Surface("rest", records, root => ContainsToken(root, "rest") || HasStateType(root, "rest")),
            Treasure = Surface("treasure", records, root => ContainsToken(root, "treasure") || ContainsToken(root, "choose_treasure_relic")),
            CardReward = Surface("card_reward", records, root =>
                ContainsToken(root, "card_reward")
                || ContainsToken(root, "choose_reward_card")
                || ContainsToken(root, "skip_card_reward")
                || ContainsToken(root, "reroll_card_reward")
                || HasStateType(root, "card_reward")),
            RelicSelect = Surface("relic_select", records, root =>
                ContainsToken(root, "relic_select")
                || ContainsToken(root, "choose_relic_select")
                || ContainsToken(root, "skip_relic_select")
                || HasStateType(root, "relic_select")),
            BundleSelect = Surface("bundle_select", records, root =>
                ContainsToken(root, "bundle_select")
                || ContainsToken(root, "choose_card_bundle")
                || HasStateType(root, "bundle_select")),
            RelicTrigger = Surface("relic_trigger", records, root =>
                JsonElementAccess.GetString(root, "record_type") == "effect/relic_trigger")
        };
    }

    private static SurfaceCoverage Surface(
        string surface,
        IReadOnlyList<TelemetryRecord> records,
        Func<JsonElement, bool> predicate)
    {
        List<TelemetryRecord> matches = records
            .Where(record => record.Root is JsonElement root && predicate(root))
            .ToList();

        return new SurfaceCoverage(surface)
        {
            Appeared = matches.Count > 0,
            RecordCount = matches.Count,
            ExampleSequences = matches
                .Select(record => record.LocalSequence)
                .Where(sequence => sequence.HasValue)
                .Select(sequence => sequence!.Value)
                .Distinct()
                .Take(5)
                .ToArray()
        };
    }

    private static IReadOnlyList<FrameSummary> BuildLargestRecords(
        IReadOnlyList<TelemetryRecord> records,
        TelemetryInspectorOptions options,
        int count)
    {
        return records
            .OrderByDescending(record => record.ByteSize)
            .ThenBy(record => record.LineNumber)
            .Take(count)
            .Select(record => new FrameSummary
            {
                Sequence = record.LocalSequence,
                LineNumber = record.LineNumber,
                RecordType = record.RecordType,
                ByteSize = record.ByteSize,
                StateHint = record.Root is JsonElement root ? StateHint(root) : null,
                ActionHint = record.Root is JsonElement root2 ? ActionHint(root2) : null,
                SuspiciousFlags = BuildFrameFlags(record, options).ToArray()
            })
            .ToArray();
    }

    private static IEnumerable<string> BuildFrameFlags(TelemetryRecord record, TelemetryInspectorOptions options)
    {
        if (record.IsMalformed)
        {
            yield return "malformed_json";
            yield break;
        }

        JsonElement root = record.Root!.Value;
        if (record.ByteSize > options.MaxFrameBytes)
            yield return "oversized";

        if (record.RecordType == "lifecycle/telemetry_callback_failed")
            yield return "callback_failure";

        if (record.RecordType == "decision/frame")
        {
            if (!JsonElementAccess.HasPath(root, "selected_action.canonical_action_hash"))
                yield return "missing_canonical_action_hash";

            if (!IsPendingDecisionFrame(root) && !JsonElementAccess.HasPath(root, "post_state"))
                yield return "missing_post_state";
        }
    }

    private static IReadOnlyList<ExampleRecord> BuildTopExamples(
        IReadOnlyList<TelemetryRecord> records,
        IReadOnlyList<TelemetryFinding> findings,
        IReadOnlyList<FrameSummary> largestRecords,
        int count)
    {
        var examples = new List<ExampleRecord>();

        TelemetryFinding? firstFinding = findings.FirstOrDefault();
        if (firstFinding != null)
        {
            examples.Add(new ExampleRecord
            {
                Label = "first_finding",
                Sequence = firstFinding.Sequence,
                LineNumber = firstFinding.LineNumber,
                RecordType = firstFinding.RecordType,
                Detail = $"{firstFinding.Code}: {firstFinding.Message}"
            });
        }

        FrameSummary? largest = largestRecords.FirstOrDefault();
        if (largest != null)
        {
            examples.Add(new ExampleRecord
            {
                Label = "largest_record",
                Sequence = largest.Sequence,
                LineNumber = largest.LineNumber,
                RecordType = largest.RecordType,
                Detail = $"{largest.ByteSize} bytes"
            });
        }

        AddExample(records, examples, "run_loaded", record => record.RecordType == "lifecycle/run_loaded");
        AddExample(records, examples, "branch_forked", record => record.RecordType == "lifecycle/branch_forked"
            || (record.Root is JsonElement root && JsonElementAccess.GetBoolean(root, "branch_decision.forked") == true));
        AddExample(records, examples, "relic_trigger", record => record.RecordType == "effect/relic_trigger");
        AddExample(records, examples, "decision_context", record => record.RecordType == "decision/context");
        AddExample(records, examples, "decision_frame", record => record.RecordType == "decision/frame");

        return examples
            .GroupBy(example => new { example.Sequence, example.LineNumber, example.Label })
            .Select(group => group.First())
            .Take(Math.Max(1, count))
            .ToArray();
    }

    private static void AddExample(
        IReadOnlyList<TelemetryRecord> records,
        List<ExampleRecord> examples,
        string label,
        Func<TelemetryRecord, bool> predicate)
    {
        TelemetryRecord? record = records.FirstOrDefault(predicate);
        if (record == null)
            return;

        examples.Add(new ExampleRecord
        {
            Label = label,
            Sequence = record.LocalSequence,
            LineNumber = record.LineNumber,
            RecordType = record.RecordType,
            Detail = record.RecordType ?? "record"
        });
    }

    private static bool IsSignalOnlyRecord(JsonElement root)
        => string.Equals(
            JsonElementAccess.GetString(root, "capture_policy"),
            "signal_only_no_state_snapshot_no_legal_actions",
            StringComparison.Ordinal);

    private static bool HasUsableLegalActions(JsonElement root)
    {
        if (!JsonElementAccess.TryGetPath(root, "legal_actions.actions", out JsonElement actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement action in actions.EnumerateArray())
        {
            string? actionType = JsonElementAccess.GetString(action, "action_type");
            if (!IsUnavailableOrPlaceholderActionType(actionType))
                return true;
        }

        return false;
    }

    private static bool IsUnavailableOrPlaceholderActionType(string? actionType)
        => string.IsNullOrWhiteSpace(actionType)
            || string.Equals(actionType, "unknown", StringComparison.Ordinal)
            || actionType.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || actionType.Contains("pending", StringComparison.OrdinalIgnoreCase);

    private static ReadinessMetricSummary Metric(
        string code,
        string label,
        string status,
        int count,
        int? total = null,
        double? rate = null,
        string? detail = null)
        => new()
        {
            Code = code,
            Label = label,
            Status = status,
            Count = count,
            Total = total,
            Rate = rate,
            Detail = detail
        };

    private static CombatMarkerCoverageSummary BuildCombatMarkerCoverage(
        IReadOnlyList<TelemetryRecord> combatFrames,
        string marker,
        string importance,
        string detail)
    {
        int present = 0;
        int unavailable = 0;
        int missing = 0;

        foreach (TelemetryRecord record in combatFrames)
        {
            switch (GetCombatMarkerAvailability(record.Root!.Value, marker))
            {
                case CombatMarkerAvailability.Present:
                    present++;
                    break;
                case CombatMarkerAvailability.Unavailable:
                    unavailable++;
                    break;
                default:
                    missing++;
                    break;
            }
        }

        int total = combatFrames.Count;
        return new CombatMarkerCoverageSummary
        {
            Marker = marker,
            Importance = importance,
            Present = present,
            Unavailable = unavailable,
            Missing = missing,
            Total = total,
            Coverage = Ratio(present, total),
            Status = total == 0
                ? "not_observed"
                : present == total
                    ? "complete"
                    : present > 0
                        ? "partial"
                        : unavailable > 0 ? "expected_gap" : "missing",
            Detail = detail
        };
    }

    private static bool HasCombatMarker(JsonElement root, string marker)
        => GetCombatMarkerAvailability(root, marker) == CombatMarkerAvailability.Present;

    private static CombatMarkerAvailability GetCombatMarkerAvailability(JsonElement root, string marker)
    {
        bool presentStatus = marker is "action_step" or "action_index"
            ? HasSnapshotCombatMarkerStatus(root, marker, "present")
            : HasCombatMarkerStatus(root, marker, "present");

        if (HasCombatMarkerValue(root, marker) || presentStatus)
            return CombatMarkerAvailability.Present;

        if (HasCombatMarkerStatus(root, marker, "unavailable"))
            return CombatMarkerAvailability.Unavailable;

        if (marker is "action_step" or "action_index"
            && (HasRecorderCombatProcess(root) || HasSnapshotCombatProcess(root)))
        {
            return CombatMarkerAvailability.Unavailable;
        }

        return CombatMarkerAvailability.Missing;
    }

    private static bool HasCombatMarkerStatus(JsonElement root, string marker, string status)
    {
        foreach (string parentPath in CombatProcessStatusParentPaths())
        {
            foreach (string statusKey in CombatMarkerStatusKeys(marker))
            {
                if (string.Equals(
                        JsonElementAccess.GetString(root, $"{parentPath}.{statusKey}"),
                        status,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasSnapshotCombatMarkerStatus(JsonElement root, string marker, string status)
    {
        foreach (string parentPath in CombatProcessStatusParentPaths().Where(path =>
                     !string.Equals(path, "combat_process.marker_status", StringComparison.Ordinal)))
        {
            foreach (string statusKey in CombatMarkerStatusKeys(marker))
            {
                if (string.Equals(
                        JsonElementAccess.GetString(root, $"{parentPath}.{statusKey}"),
                        status,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasCombatMarkerValue(JsonElement root, string marker)
    {
        foreach (string parentPath in CombatProcessValueParentPaths())
        {
            foreach (string valueKey in CombatMarkerValueKeys(marker))
            {
                if (HasMeaningfulValue(root, $"{parentPath}.{valueKey}"))
                    return true;
            }
        }

        foreach (string parentPath in CombatRootValueParentPaths())
        {
            foreach (string valueKey in CombatMarkerRootValueKeys(marker))
            {
                if (HasMeaningfulValue(root, $"{parentPath}.{valueKey}"))
                    return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> CombatMarkerStatusKeys(string marker)
        => marker switch
        {
            "turn" => new[] { "turn", "turn_index", "round" },
            "phase" => new[] { "phase", "turn_side", "current_side", "is_play_phase" },
            "action_step" => new[] { "action_step" },
            "action_index" => new[] { "action_index" },
            _ => new[] { marker }
        };

    private static IReadOnlyList<string> CombatMarkerValueKeys(string marker)
        => marker switch
        {
            "turn" => new[] { "turn_index", "round" },
            "phase" => new[] { "phase", "turn_side", "current_side", "is_play_phase" },
            "action_step" => new[] { "action_step" },
            "action_index" => new[] { "action_index" },
            _ => new[] { marker }
        };

    private static IReadOnlyList<string> CombatMarkerRootValueKeys(string marker)
        => marker switch
        {
            "turn" => new[] { "round" },
            "phase" => new[] { "current_side", "is_play_phase" },
            _ => Array.Empty<string>()
        };

    private static IReadOnlyList<string> CombatProcessStatusParentPaths()
        => new[]
        {
            "combat_process.marker_status",
            "pre_state.raw_snapshot.combat.process.marker_status",
            "post_state.raw_snapshot.combat.process.marker_status",
            "pre_state.canonical_snapshot.combat.process.marker_status",
            "post_state.canonical_snapshot.combat.process.marker_status",
            "state.raw_snapshot.combat.process.marker_status",
            "state.canonical_snapshot.combat.process.marker_status"
        };

    private static IReadOnlyList<string> CombatProcessValueParentPaths()
        => new[]
        {
            "combat_process.pre",
            "combat_process.post",
            "pre_state.raw_snapshot.combat.process",
            "post_state.raw_snapshot.combat.process",
            "pre_state.canonical_snapshot.combat.process",
            "post_state.canonical_snapshot.combat.process",
            "state.raw_snapshot.combat.process",
            "state.canonical_snapshot.combat.process"
        };

    private static IReadOnlyList<string> CombatRootValueParentPaths()
        => new[]
        {
            "pre_state.raw_snapshot.combat",
            "post_state.raw_snapshot.combat",
            "pre_state.canonical_snapshot.combat",
            "post_state.canonical_snapshot.combat",
            "state.raw_snapshot.combat",
            "state.canonical_snapshot.combat"
        };

    private static bool HasMeaningfulValue(JsonElement root, string path)
    {
        if (!JsonElementAccess.TryGetPath(root, path, out JsonElement value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true
        };
    }

    private static bool HasRecorderCombatProcess(JsonElement root)
        => HasNonEmptyObject(root, "combat_process");

    private static bool HasSnapshotCombatProcess(JsonElement root)
        => CombatProcessValueParentPaths()
            .Where(path => !path.StartsWith("combat_process.", StringComparison.Ordinal))
            .Any(path => HasNonEmptyObject(root, path));

    private static bool HasNonEmptyObject(JsonElement root, string path)
        => JsonElementAccess.TryGetPath(root, path, out JsonElement value)
            && value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Any();

    private static bool HasDetailedCombatState(JsonElement root)
    {
        bool hasEnergy = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.local_player.energy", out JsonElement energy)
            && energy.ValueKind == JsonValueKind.Number;
        bool hasDrawPile = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.local_player.draw_pile", out JsonElement drawPile)
            && drawPile.ValueKind == JsonValueKind.Array;
        bool hasDiscardPile = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.local_player.discard_pile", out JsonElement discardPile)
            && discardPile.ValueKind == JsonValueKind.Array;
        bool hasExhaustPile = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.local_player.exhaust_pile", out JsonElement exhaustPile)
            && exhaustPile.ValueKind == JsonValueKind.Array;
        bool hasPowers = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.local_player.powers", out JsonElement powers)
            && powers.ValueKind == JsonValueKind.Array;
        bool hasTargets = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.combat.target_candidates", out JsonElement targets)
            && targets.ValueKind == JsonValueKind.Array
            && targets.GetArrayLength() > 0;
        bool hasIntent = JsonElementAccess.TryGetPath(root, "pre_state.raw_snapshot.combat.enemies", out JsonElement enemies)
            && enemies.ValueKind == JsonValueKind.Array
            && enemies.EnumerateArray().Any(enemy => JsonElementAccess.GetString(enemy, "intent") != null);

        return hasEnergy && hasDrawPile && hasDiscardPile && hasExhaustPile && hasPowers && hasTargets && hasIntent;
    }

    private static bool HasStableCombatActionIdentity(JsonElement root)
    {
        if (!JsonElementAccess.TryGetPath(root, "selected_action.normalized_typed_action_key", out JsonElement key)
            || key.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? actionType = JsonElementAccess.GetString(key, "action_type");
        if (string.IsNullOrWhiteSpace(actionType)
            || string.Equals(actionType, "unknown", StringComparison.Ordinal))
        {
            return false;
        }

        return actionType switch
        {
            "play_card" => HasCombatCardIdentity(key),
            "use_potion" => HasCombatPotionIdentity(key),
            "discard_potion" => HasCombatDiscardPotionIdentity(key),
            "end_turn" => true,
            _ => true
        };
    }

    private static bool HasCombatCardIdentity(JsonElement key)
    {
        if (JsonElementAccess.GetString(key, "card_id") == null)
            return false;

        string? targetType = JsonElementAccess.GetString(key, "target.target_index_space");
        if (targetType == null)
            return true;

        return JsonElementAccess.GetString(key, "target.target_entity_id") != null
            || JsonElementAccess.GetString(key, "target.target_id") != null;
    }

    private static bool HasCombatPotionIdentity(JsonElement key)
    {
        if (JsonElementAccess.GetString(key, "potion_id") == null)
            return false;

        if (!JsonElementAccess.HasPath(key, "slot"))
            return false;

        string? targetSpace = JsonElementAccess.GetString(key, "target.target_index_space");
        if (targetSpace == null)
            return true;

        return JsonElementAccess.GetString(key, "target.target_entity_id") != null
            || JsonElementAccess.GetString(key, "target.target_id") != null;
    }

    private static bool HasCombatDiscardPotionIdentity(JsonElement key)
        => JsonElementAccess.GetString(key, "potion_id") != null
            && JsonElementAccess.HasPath(key, "slot");

    private static double Ratio(int numerator, int denominator)
        => denominator == 0 ? 0 : (double)numerator / denominator;

    private static bool ContainsToken(JsonElement root, string token)
        => JsonElementAccess.ContainsString(root, value => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool HasStateType(JsonElement root, string stateType)
        => string.Equals(JsonElementAccess.GetString(root, "state.state_type"), stateType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(JsonElementAccess.GetString(root, "pre_state.state_type"), stateType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(JsonElementAccess.GetString(root, "post_state.state_type"), stateType, StringComparison.OrdinalIgnoreCase);

    private static string? StateHint(JsonElement root)
        => JsonElementAccess.GetString(root, "state.state_type")
            ?? JsonElementAccess.GetString(root, "pre_state.state_type")
            ?? JsonElementAccess.GetString(root, "post_state.state_type")
            ?? JsonElementAccess.GetString(root, "state.raw_snapshot.state_type")
            ?? JsonElementAccess.GetString(root, "pre_state.raw_snapshot.state_type");

    private static string? ActionHint(JsonElement root)
        => JsonElementAccess.GetString(root, "selected_action.normalized_typed_action_key.action_type")
            ?? JsonElementAccess.GetString(root, "selected_action.metadata.action_type")
            ?? JsonElementAccess.GetString(root, "action_type")
            ?? JsonElementAccess.GetString(root, "action_signal.metadata.action_type")
            ?? JsonElementAccess.GetString(root, "ui_signal.metadata.action_type");

    private static bool IsPendingDecisionFrame(JsonElement root)
    {
        string? decisionStatus = JsonElementAccess.GetString(root, "decision_status");
        string? postStateStatus = JsonElementAccess.GetString(root, "post_state_status");
        string? nestedPostStateStatus = JsonElementAccess.GetString(root, "post_state.status");
        string? completionStatus = JsonElementAccess.GetString(root, "completion_status");

        return IsPendingStatus(decisionStatus)
            || IsPendingStatus(postStateStatus)
            || IsPendingStatus(nestedPostStateStatus)
            || IsPendingStatus(completionStatus);
    }

    private static bool IsPendingStatus(string? status)
        => status != null
            && (status.Contains("pending", StringComparison.OrdinalIgnoreCase)
                || status.Contains("transition", StringComparison.OrdinalIgnoreCase));

    private static TelemetryFinding Finding(string severity, string code, string message, TelemetryRecord record)
        => new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            Sequence = record.LocalSequence,
            LineNumber = record.LineNumber,
            SourcePath = record.SourcePath,
            RecordType = record.RecordType
        };
}
