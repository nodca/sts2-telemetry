using System.Text.Json;
using Sts2Telemetry.Cli;
using Sts2Telemetry.Inspector;

var tests = new (string Name, Action Body)[]
{
    ("jsonl parser reports malformed records", JsonlParserReportsMalformedRecords),
    ("inspect json has stable top-level sections", InspectJsonHasStableTopLevelSections),
    ("coverage detection finds telemetry surfaces", CoverageDetectionFindsTelemetrySurfaces),
    ("readiness summary tracks trainable decisions and combat detail", ReadinessSummaryTracksTrainableDecisionsAndCombatDetail),
    ("readiness ignores unknown legal actions and combat identities", ReadinessIgnoresUnknownLegalActionsAndCombatIdentities),
    ("coverage output separates readiness categories and combat process detail", CoverageOutputSeparatesReadinessCategoriesAndCombatProcessDetail),
    ("combat process detail keeps action step and action index separate", CombatProcessDetailKeepsActionStepAndActionIndexSeparate),
    ("expected unknown markers stay readiness gaps", ExpectedUnknownMarkersStayReadinessGaps),
    ("runtime expected unknown markers stay readiness gaps", RuntimeExpectedUnknownMarkersStayReadinessGaps),
    ("contract-risk unknown markers hard fail validation", ContractRiskUnknownMarkersHardFailValidation),
    ("shop readiness dedupes repeated logical signals", ShopReadinessDedupesRepeatedLogicalSignals),
    ("branch validation detects matched run loaded without match record", BranchValidationDetectsMatchedRunLoadedWithoutMatchRecord),
    ("branch validation accepts unmatched explicit run load", BranchValidationAcceptsUnmatchedExplicitRunLoad),
    ("operational directory is inferred from run path", OperationalDirectoryIsInferredFromRunPath),
    ("operational callback failures are hard validation failures", OperationalCallbackFailuresAreHardValidationFailures),
    ("malformed operational JSON is a hard validation failure", MalformedOperationalJsonIsHardValidationFailure),
    ("missing operational log does not fail inspection", MissingOperationalLogDoesNotFailInspection),
    ("CLI operational-dir option reads callback failures", CliOperationalDirOptionReadsCallbackFailures),
    ("pending post-state frame does not require branch decision", PendingPostStateFrameDoesNotRequireBranchDecision),
    ("show prints exact record by sequence", ShowPrintsExactRecordBySequence),
    ("logical run directory aggregates segment files", LogicalRunDirectoryAggregatesSegmentFiles),
    ("latest discovery handles logical run directories", LatestDiscoveryHandlesLogicalRunDirectories),
    ("runs command lists recent runs with surface indicators", RunsCommandListsRecentRunsWithSurfaceIndicators),
    ("runs command filters beyond latest by surface", RunsCommandFiltersBeyondLatestBySurface),
    ("CLI smoke commands work", CliSmokeCommandsWork),
    ("validate exits non-zero for hard failures", ValidateExitsNonZeroForHardFailures),
    ("validate exits zero for clean run", ValidateExitsZeroForCleanRun)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed");
    Environment.ExitCode = 1;
}

static void JsonlParserReportsMalformedRecords()
{
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        "{not valid json"
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        AssertEqual(2, report.RunSummary.RecordCount, "record count includes malformed lines");
        AssertEqual(1, report.Health.MalformedRecords, "malformed record count");
        AssertTrue(report.Validation.Errors.Any(error => error.Code == "malformed_json"), "malformed JSON should be a hard failure");
    });
}

static void InspectJsonHasStableTopLevelSections()
{
    WithTelemetryFile(new[] { DecisionFrame(seq: 1, stateType: "combat", actionType: "end_turn") }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        string json = JsonSerializer.Serialize(report, TelemetryInspectorJson.IndentedOptions);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        AssertHasProperty(root, "run_summary", "inspect JSON should include run_summary");
        AssertHasProperty(root, "health", "inspect JSON should include health");
        AssertHasProperty(root, "readiness", "inspect JSON should include readiness");
        AssertHasProperty(root, "performance", "inspect JSON should include performance");
        AssertHasProperty(root, "branch", "inspect JSON should include branch");
        AssertHasProperty(root, "coverage", "inspect JSON should include coverage");
        AssertHasProperty(root, "suspicious", "inspect JSON should include suspicious");
        AssertHasProperty(root, "largest_records", "inspect JSON should include largest_records");
        AssertHasProperty(root, "top_examples", "inspect JSON should include top_examples");
        AssertHasProperty(root, "validation", "inspect JSON should include validation");
        AssertEqual("sts2.telemetry.inspection.v1", root.GetProperty("schema_version").GetString(), "inspect JSON schema version");
        JsonElement readiness = root.GetProperty("readiness");
        AssertHasProperty(readiness, "training_critical", "readiness JSON should classify training-critical metrics");
        AssertHasProperty(readiness, "warnings", "readiness JSON should classify warnings");
        AssertHasProperty(readiness, "diagnostics", "readiness JSON should classify diagnostics");
        AssertHasProperty(readiness.GetProperty("combat"), "process_detail", "combat readiness JSON should include process detail");
    });
}

static void ReadinessSummaryTracksTrainableDecisionsAndCombatDetail()
{
    WithTelemetryFile(new[]
    {
        CombatDecisionFrame(seq: 1, actionType: "play_card"),
        DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card"),
        DecisionContextUnavailable(seq: 3, stateType: "event", actionType: "event_typed_builder_unavailable"),
        ContextMatchedSignal(seq: 4, recordType: "decision/ui_signal", stateType: "shop", matchReason: "normalized_typed_action_key_hash_match", matched: true),
        ContextMatchedSignal(seq: 5, recordType: "decision/ui_signal", stateType: "event", matchReason: "selected_action_normalized_typed_action_key_unavailable", matched: false)
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(2, report.Readiness.TrainableDecisionCount, "trainable decision count");
        AssertEqual(1, report.Readiness.DecisionFrameCount, "decision frame count");
        AssertEqual(2, report.Readiness.LegalActionContextCount, "legal action context count");
        AssertEqual(1, report.Readiness.TrainableLegalActionContextCount, "trainable legal action context count");
        AssertNear(0.5, report.Readiness.LegalActionCoverage, 0.0001, "legal action coverage");
        AssertEqual(2, report.Readiness.SelectedActionContextReferenceCount, "selected-action context ref count");
        AssertEqual(1, report.Readiness.SelectedActionMatchCount, "selected-action match count");
        AssertNear(0.5, report.Readiness.SelectedActionMatchRate, 0.0001, "selected-action match rate");
        AssertEqual(2, report.Readiness.ContextOnlyCount, "context-only count");
        AssertEqual(2, report.Readiness.SignalOnlyCount, "signal-only count");
        AssertEqual("ready", report.Readiness.TrainingCritical.Status, "training-critical readiness status");
        AssertTrue(report.Readiness.TrainingCritical.Metrics.Any(metric => metric.Code == "trainable_decisions"),
            "training-critical readiness should expose trainable decision metric");
        AssertEqual("warnings_present", report.Readiness.Warnings.Status, "readiness warning status");
        AssertTrue(report.Readiness.Warnings.Metrics.Any(metric => metric.Code == "untrainable_legal_action_contexts"),
            "readiness warnings should include untrainable context gap");
        AssertEqual("diagnostic_only", report.Readiness.Diagnostics.Status, "readiness diagnostics status");
        AssertTrue(report.Readiness.Diagnostics.Metrics.Any(metric => metric.Code == "selected_action_context_refs"),
            "readiness diagnostics should include selected-action context refs");

        AssertEqual(1, report.Readiness.Combat.DecisionFrameCount, "combat readiness frame count");
        AssertEqual(1, report.Readiness.Combat.FramesWithTurnMarkers, "combat turn markers");
        AssertEqual(1, report.Readiness.Combat.FramesWithPhaseMarkers, "combat phase markers");
        AssertEqual(1, report.Readiness.Combat.FramesWithActionStepMarkers, "combat action-step markers");
        AssertEqual(1, report.Readiness.Combat.FramesWithActionIndexMarkers, "combat action-index markers");
        AssertEqual(1, report.Readiness.Combat.FramesWithDetailedState, "combat detailed state");
        AssertEqual(1, report.Readiness.Combat.FramesWithStableActionIdentity, "combat stable action identity");
        AssertEqual(1, report.Readiness.Combat.ProcessDetail.FramesWithRecorderCombatProcess, "combat recorder process detail");
        AssertEqual(1, report.Readiness.Combat.ProcessDetail.FramesWithSnapshotCombatProcess, "combat snapshot process detail");
        AssertTrue(report.Readiness.Combat.ProcessDetail.OptionalMarkers.Any(marker => marker.Marker == "action_step" && marker.Present == 1),
            "combat process detail should expose explicit action-step marker coverage");
        AssertTrue(report.Readiness.Combat.ProcessDetail.OptionalMarkers.Any(marker => marker.Marker == "action_index" && marker.Present == 1),
            "combat process detail should expose explicit action-index marker coverage");
    });
}

static void ReadinessIgnoresUnknownLegalActionsAndCombatIdentities()
{
    WithTelemetryFile(new[]
    {
        CombatDecisionFrame(seq: 1, actionType: "unknown"),
        DecisionContextUnknown(seq: 2, stateType: "overlay/unknown")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(0, report.Readiness.TrainableDecisionCount, "unknown records should not count as trainable decisions");
        AssertEqual(1, report.Readiness.DecisionFrameCount, "decision frame count");
        AssertEqual(1, report.Readiness.LegalActionContextCount, "legal action context count");
        AssertEqual(0, report.Readiness.TrainableLegalActionContextCount, "unknown legal action should not count as trainable");
        AssertNear(0, report.Readiness.LegalActionCoverage, 0.0001, "unknown legal action coverage");
        AssertEqual(1, report.Readiness.Combat.DecisionFrameCount, "combat readiness frame count");
        AssertEqual(0, report.Readiness.Combat.FramesWithStableActionIdentity, "unknown combat action should not count as stable identity");
        AssertNear(0, report.Readiness.Combat.StableActionIdentityCoverage, 0.0001, "unknown combat identity coverage");
    });
}

static void CoverageOutputSeparatesReadinessCategoriesAndCombatProcessDetail()
{
    WithTelemetryFile(new[]
    {
        CombatDecisionFrame(seq: 1, actionType: "play_card", includeActionStep: false, includeActionIndex: false),
        DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card"),
        ContextMatchedSignal(seq: 3, recordType: "decision/ui_signal", stateType: "shop", matchReason: "normalized_typed_action_key_hash_match", matched: true),
        ContextMatchedSignal(seq: 4, recordType: "decision/ui_signal", stateType: "event", matchReason: "latest_context_legal_actions_unavailable_or_placeholder_only", matched: false)
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        CombatMarkerCoverageSummary actionStep = report.Readiness.Combat.ProcessDetail.OptionalMarkers
            .First(marker => marker.Marker == "action_step");
        CombatMarkerCoverageSummary actionIndex = report.Readiness.Combat.ProcessDetail.OptionalMarkers
            .First(marker => marker.Marker == "action_index");

        AssertEqual(0, actionStep.Present, "explicit action-step should be unavailable in structured detail");
        AssertEqual(1, actionStep.Unavailable, "action-step unavailable should be classified as expected detail gap");
        AssertEqual(0, actionIndex.Present, "explicit action-index should be unavailable in structured detail");
        AssertEqual(1, actionIndex.Unavailable, "action-index unavailable should be classified as expected detail gap");

        AssertCliExit(0, new[] { "coverage", path }, "coverage readiness categories", out string output, out string error);
        AssertEqual("", error, "coverage readiness categories stderr");
        AssertTrue(output.Contains("training-critical:", StringComparison.Ordinal), "coverage should print training-critical readiness");
        AssertTrue(output.Contains("warnings:", StringComparison.Ordinal), "coverage should print readiness warnings");
        AssertTrue(output.Contains("diagnostics:", StringComparison.Ordinal), "coverage should print readiness diagnostics");
        AssertTrue(output.Contains("selected-action context refs (diagnostic/non-blocking):", StringComparison.Ordinal),
            "selected-action context refs should be labeled diagnostic/non-blocking");
        AssertTrue(output.Contains("combat detail: core", StringComparison.Ordinal), "coverage should print combat core detail");
        AssertTrue(output.Contains("combat process markers (optional):", StringComparison.Ordinal),
            "coverage should print optional combat process marker detail");
        AssertTrue(output.Contains("action_step: present=0/1 unavailable=1", StringComparison.Ordinal),
            "coverage should render unavailable action-step as optional process detail");
        AssertTrue(output.Contains("action_index: present=0/1", StringComparison.Ordinal),
            "coverage should render action-index separately from action-step");
    });
}

static void CombatProcessDetailKeepsActionStepAndActionIndexSeparate()
{
    WithTelemetryFile(new[]
    {
        CombatDecisionFrame(seq: 1, actionType: "play_card", includeActionStep: false, includeActionIndex: true)
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        CombatMarkerCoverageSummary actionStep = report.Readiness.Combat.ProcessDetail.OptionalMarkers
            .First(marker => marker.Marker == "action_step");
        CombatMarkerCoverageSummary actionIndex = report.Readiness.Combat.ProcessDetail.OptionalMarkers
            .First(marker => marker.Marker == "action_index");

        AssertEqual(0, report.Readiness.Combat.FramesWithActionStepMarkers,
            "legacy action-step count should not be inflated by action-index evidence");
        AssertNear(0, report.Readiness.Combat.ActionStepMarkerCoverage, 0.0001,
            "legacy action-step coverage should remain explicit action-step coverage");
        AssertEqual(1, report.Readiness.Combat.FramesWithActionIndexMarkers,
            "action-index count should use explicit action-index evidence");
        AssertNear(1, report.Readiness.Combat.ActionIndexMarkerCoverage, 0.0001,
            "action-index coverage should use explicit action-index evidence");
        AssertEqual(0, actionStep.Present, "action-step marker should be absent when only action-index is available");
        AssertEqual(1, actionStep.Unavailable, "missing action-step should be an expected optional detail gap");
        AssertEqual("expected_gap", actionStep.Status, "action-step absence should not be a hard readiness failure");
        AssertEqual(1, actionIndex.Present, "action-index marker should be present independently");
        AssertEqual("complete", actionIndex.Status, "action-index coverage should be complete");

        string reportJson = JsonSerializer.Serialize(report, TelemetryInspectorJson.CompactOptions);
        using JsonDocument document = JsonDocument.Parse(reportJson);
        JsonElement combat = document.RootElement.GetProperty("readiness").GetProperty("combat");
        AssertEqual(0, combat.GetProperty("frames_with_action_step_markers").GetInt32(),
            "report JSON should keep action-step count explicit");
        AssertEqual(1, combat.GetProperty("frames_with_action_index_markers").GetInt32(),
            "report JSON should expose action-index count separately");
        JsonElement optionalMarkers = combat.GetProperty("process_detail").GetProperty("optional_markers");
        AssertTrue(optionalMarkers.EnumerateArray().Any(marker =>
                marker.GetProperty("marker").GetString() == "action_step"
                && marker.GetProperty("status").GetString() == "expected_gap"),
            "report JSON should label unavailable action-step as an expected detail gap");
        AssertTrue(optionalMarkers.EnumerateArray().Any(marker =>
                marker.GetProperty("marker").GetString() == "action_index"
                && marker.GetProperty("present").GetInt32() == 1),
            "report JSON should preserve action-index evidence without fabricating action-step");
    });
}

static void ExpectedUnknownMarkersStayReadinessGaps()
{
    WithTelemetryFile(new[]
    {
        Json(new Dictionary<string, object?>
        {
            ["schema_version"] = "sts2.telemetry.local.v1",
            ["record_type"] = "decision/ui_signal",
            ["run_id"] = "run-test",
            ["local_sequence"] = 1,
            ["recorded_at_utc"] = Timestamp(1),
            ["capture_policy"] = "signal_only_no_state_snapshot_no_legal_actions",
            ["pre_state"] = new Dictionary<string, object?>
            {
                ["state_type"] = "combat",
                ["raw_snapshot"] = new Dictionary<string, object?>
                {
                    ["game"] = new Dictionary<string, object?> { ["game_version"] = "unknown" },
                    ["combat"] = new Dictionary<string, object?>
                    {
                        ["process"] = new Dictionary<string, object?>
                        {
                            ["marker_status"] = new Dictionary<string, object?>
                            {
                                ["action_step"] = "unavailable"
                            }
                        }
                    }
                },
                ["canonical_snapshot"] = new Dictionary<string, object?>
                {
                    ["game"] = new Dictionary<string, object?> { ["game_version"] = "unknown" },
                    ["combat"] = new Dictionary<string, object?>
                    {
                        ["process"] = new Dictionary<string, object?>
                        {
                            ["marker_status"] = new Dictionary<string, object?>
                            {
                                ["action_step"] = "unavailable"
                            }
                        }
                    }
                }
            },
            ["combat_process"] = new Dictionary<string, object?>
            {
                ["marker_status"] = new Dictionary<string, object?>
                {
                    ["action_step"] = "unavailable"
                }
            },
            ["ui_signal"] = new Dictionary<string, object?>
            {
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["effect_summary_status"] = "effect_summary_unavailable"
                }
            },
            ["branch"] = Branch("branch-0001", "attempt-0001")
        })
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(6, report.Health.UnknownUnavailableMarkers, "raw unknown/unavailable marker count");
        AssertEqual(6, report.Health.UnknownUnavailable.RawCount, "raw summary marker count");
        AssertEqual(4, report.Health.UnknownUnavailable.UniqueNormalizedCount, "unique normalized marker count");
        AssertEqual(6, report.Health.UnknownUnavailable.ExpectedGapRawCount, "expected gap raw count");
        AssertEqual(4, report.Health.UnknownUnavailable.ExpectedGapUniqueCount, "expected gap unique count");
        AssertEqual(0, report.Health.UnknownUnavailable.ContractRiskRawCount, "expected gaps should not become contract risk");
        AssertFalse(report.Validation.Errors.Any(error => error.Code == "contract_risk_unknown_unavailable_markers_exceed_threshold"),
            "expected readiness gaps should not hard-fail validation");
    });
}

static void ContractRiskUnknownMarkersHardFailValidation()
{
    WithTelemetryFile(new[]
    {
        Json(new Dictionary<string, object?>
        {
            ["schema_version"] = "sts2.telemetry.local.v1",
            ["record_type"] = "decision/context",
            ["run_id"] = "run-test",
            ["local_sequence"] = 1,
            ["recorded_at_utc"] = Timestamp(1),
            ["decision_context_id"] = "ctx-1",
            ["pre_state"] = new Dictionary<string, object?>
            {
                ["state_type"] = "event",
                ["raw_snapshot"] = new Dictionary<string, object?>
                {
                    ["details"] = new Dictionary<string, object?>
                    {
                        ["typed_builder_status"] = "unknown"
                    }
                }
            },
            ["legal_actions"] = new Dictionary<string, object?>
            {
                ["actions"] = new[] { new Dictionary<string, object?> { ["action_type"] = "choose_event_option" } }
            },
            ["branch"] = Branch("branch-0001", "attempt-0001")
        })
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(1, report.Health.UnknownUnavailable.ContractRiskRawCount, "contract-risk marker count");
        AssertTrue(report.Validation.Errors.Any(error => error.Code == "contract_risk_unknown_unavailable_markers_exceed_threshold"),
            "unexpected unknown/unavailable markers should hard-fail validation");
    });
}

static void RuntimeExpectedUnknownMarkersStayReadinessGaps()
{
    WithTelemetryFile(new[]
    {
        Json(new Dictionary<string, object?>
        {
            ["schema_version"] = "sts2.telemetry.local.v1",
            ["record_type"] = "decision/frame",
            ["run_id"] = "run-test",
            ["local_sequence"] = 1,
            ["recorded_at_utc"] = Timestamp(1),
            ["state"] = new Dictionary<string, object?>
            {
                ["state_type"] = "room/unknown",
                ["snapshot"] = new Dictionary<string, object?>
                {
                    ["state_type"] = "room/unknown"
                }
            },
            ["branch"] = new Dictionary<string, object?>
            {
                ["branch_id"] = "branch-0001",
                ["branch_status"] = "unknown",
                ["attempt_id"] = "attempt-0002"
            },
            ["pre_state"] = new Dictionary<string, object?>
            {
                ["state_type"] = "combat"
            },
            ["post_state"] = new Dictionary<string, object?>
            {
                ["state_type"] = "combat"
            },
            ["legal_actions"] = new Dictionary<string, object?>
            {
                ["actions"] = new[]
                {
                    new Dictionary<string, object?> { ["action_type"] = "play_card" }
                }
            },
            ["selected_action"] = new Dictionary<string, object?>
            {
                ["raw"] = new Dictionary<string, object?>
                {
                    ["action_type"] = "play_card",
                    ["extraction_status"] = new Dictionary<string, object?>
                    {
                        ["target"] = "suppressed_selected_card_target_type_unavailable",
                        ["target_card_target_type"] = "unavailable"
                    }
                },
                ["normalized_typed_action_key"] = new Dictionary<string, object?>
                {
                    ["action_type"] = "play_card",
                    ["card_id"] = "STRIKE"
                },
                ["canonical_action_hash"] = "hash-1"
            },
            ["branch_decision"] = new Dictionary<string, object?>
            {
                ["forked"] = false,
                ["trajectory_replayed"] = false
            }
        })
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(5, report.Health.UnknownUnavailableMarkers, "runtime expected-gap marker count");
        AssertEqual(5, report.Health.UnknownUnavailable.ExpectedGapRawCount, "runtime expected-gap raw count");
        AssertEqual(5, report.Health.UnknownUnavailable.ExpectedGapUniqueCount, "runtime expected-gap unique count");
        AssertEqual(0, report.Health.UnknownUnavailable.ContractRiskRawCount, "runtime expected markers should not become contract risk");
        AssertFalse(report.Validation.Errors.Any(error => error.Code == "contract_risk_unknown_unavailable_markers_exceed_threshold"),
            "runtime expected markers should not hard-fail validation");
    });
}

static void ShopReadinessDedupesRepeatedLogicalSignals()
{
    WithTelemetryFile(new[]
    {
        DecisionContext(seq: 1, stateType: "shop", actionType: "buy_shop_relic"),
        ShopContextMatchedSignal(seq: 2, contextId: "ctx-1", purchaseStatus: "attempted", selectedActionHash: "shop-relic-1"),
        ShopContextMatchedSignal(seq: 3, contextId: "ctx-1", purchaseStatus: "completed", selectedActionHash: "shop-relic-1"),
        ShopContextMatchedSignal(seq: 4, contextId: "ctx-1", purchaseStatus: "completed", selectedActionHash: "shop-relic-1")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(1, report.Readiness.TrainableDecisionCount, "logical shop signals should count once");
        AssertEqual(3, report.Readiness.RawTrainableDecisionCount, "raw shop signals should remain visible");
        AssertEqual(1, report.Readiness.SelectedActionContextReferenceCount, "logical selected-action context refs");
        AssertEqual(1, report.Readiness.SelectedActionMatchCount, "logical selected-action matches");
        AssertEqual(3, report.Readiness.RawSelectedActionContextReferenceCount, "raw selected-action context refs");
        AssertEqual(3, report.Readiness.RawSelectedActionMatchCount, "raw selected-action matches");
    });
}

static void CoverageDetectionFindsTelemetrySurfaces()
{
    WithTelemetryFile(new[]
    {
        DecisionFrame(seq: 1, stateType: "combat", actionType: "end_turn"),
        Signal(seq: 2, "decision/action_signal", "choose_map_node", "map"),
        DecisionContext(seq: 3, stateType: "shop", actionType: "buy_shop_card"),
        DecisionContext(seq: 4, stateType: "event", actionType: "choose_event_option"),
        DecisionContext(seq: 5, stateType: "card_reward", actionType: "choose_reward_card"),
        DecisionContext(seq: 6, stateType: "rest_site", actionType: "choose_rest_option"),
        DecisionContext(seq: 7, stateType: "treasure", actionType: "choose_treasure_relic"),
        DecisionContext(seq: 8, stateType: "relic_select", actionType: "choose_relic_select"),
        DecisionContext(seq: 9, stateType: "bundle_select", actionType: "choose_card_bundle"),
        RelicTrigger(seq: 10)
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        AssertTrue(report.Coverage.Combat.Appeared, "combat coverage");
        AssertTrue(report.Coverage.Map.Appeared, "map coverage");
        AssertTrue(report.Coverage.Shop.Appeared, "shop coverage");
        AssertTrue(report.Coverage.Event.Appeared, "event coverage");
        AssertTrue(report.Coverage.Reward.Appeared, "reward coverage");
        AssertTrue(report.Coverage.Rest.Appeared, "rest coverage");
        AssertTrue(report.Coverage.Treasure.Appeared, "treasure coverage");
        AssertTrue(report.Coverage.CardReward.Appeared, "card reward coverage");
        AssertTrue(report.Coverage.RelicSelect.Appeared, "relic select coverage");
        AssertTrue(report.Coverage.BundleSelect.Appeared, "bundle select coverage");
        AssertTrue(report.Coverage.RelicTrigger.Appeared, "relic trigger coverage");
    });
}

static void BranchValidationDetectsMatchedRunLoadedWithoutMatchRecord()
{
    WithTelemetryFile(new[]
    {
        RunLoaded(seq: 1, attempt: "attempt-0002", branch: "branch-0001", matched: true),
        DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        AssertTrue(report.Validation.Errors.Any(error => error.Code == "run_loaded_without_later_branch_matched"),
            "matched run_loaded without later branch_matched should be hard failure");
    });
}

static void BranchValidationAcceptsUnmatchedExplicitRunLoad()
{
    WithTelemetryFile(new[]
    {
        RunLoaded(seq: 1, attempt: "attempt-0002", branch: "branch-0001", matched: false),
        DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        AssertFalse(report.Validation.Errors.Any(error => error.Code == "run_loaded_without_later_branch_matched"),
            "unmatched explicit run_loaded should not require branch_matched");
    });
}

static void OperationalDirectoryIsInferredFromRunPath()
{
    string root = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-inspector-test-{Guid.NewGuid():N}");
    string telemetryPath = Path.Combine(root, "telemetry", "runs", "run-test", "telemetry.jsonl");
    string expected = Path.Combine(root, "telemetry", "operational");
    string actual = TelemetryRunLocator.ResolveOperationalDirectory(telemetryPath, new TelemetryInspectorOptions());

    AssertEqual(expected, actual, "operational directory should be inferred as sibling of runs directory");
}

static void OperationalCallbackFailuresAreHardValidationFailures()
{
    WithTelemetryFiles(
        new[] { RunStarted(seq: 1) },
        new[] { OperationalCallbackFailed(seq: 1) },
        path =>
        {
            TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

            AssertEqual(1, report.Health.CallbackFailures, "operational callback failure count");
            AssertEqual(1, report.Health.OperationalRecords, "operational record count");
            AssertTrue(report.Health.OperationalSourcePaths.Any(path => path.EndsWith($"{Path.DirectorySeparatorChar}20260505.jsonl", StringComparison.Ordinal)),
                "operational source path should point at dated JSONL file");
            AssertTrue(report.Validation.Errors.Any(error => error.Code == "telemetry_callback_failed"),
                "operational callback failure should be a hard validation failure");
        });
}

static void MalformedOperationalJsonIsHardValidationFailure()
{
    WithTelemetryFiles(
        new[] { RunStarted(seq: 1) },
        new[] { "{broken operational json" },
        path =>
        {
            TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

            AssertEqual(1, report.Health.OperationalRecords, "operational record count");
            AssertEqual(1, report.Health.OperationalMalformedRecords, "operational malformed count");
            AssertTrue(report.Validation.Errors.Any(error => error.Code == "malformed_operational_json"),
                "malformed operational JSON should be a hard validation failure");
        });
}

static void MissingOperationalLogDoesNotFailInspection()
{
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);

        AssertEqual(0, report.Health.OperationalRecords, "missing operational log should not create operational records");
        AssertEqual(0, report.Health.OperationalMalformedRecords, "missing operational log should not create malformed records");
        AssertFalse(report.Validation.Errors.Any(error => error.Code is "malformed_operational_json" or "telemetry_callback_failed"),
            "missing operational log should not create validation errors");
    });
}

static void CliOperationalDirOptionReadsCallbackFailures()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-inspector-test-{Guid.NewGuid():N}");
    try
    {
        string runDirectory = Path.Combine(directory, "telemetry", "runs", "run-test");
        Directory.CreateDirectory(runDirectory);
        string path = Path.Combine(runDirectory, "telemetry.jsonl");
        File.WriteAllLines(path, new[] { RunStarted(seq: 1) });

        string operationalDirectory = Path.Combine(directory, "custom-operational");
        Directory.CreateDirectory(operationalDirectory);
        File.WriteAllLines(Path.Combine(operationalDirectory, "20260505.jsonl"), new[] { OperationalCallbackFailed(seq: 1) });

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = TelemetryCli.Run(new[] { "validate", path, "--operational-dir", operationalDirectory }, stdout, stderr);

        AssertEqual(1, exitCode, "validate should fail operational callback failures from CLI override");
        AssertTrue(stderr.ToString().Contains("telemetry_callback_failed", StringComparison.Ordinal),
            "validate should report operational callback failure from CLI override");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void PendingPostStateFrameDoesNotRequireBranchDecision()
{
    WithTelemetryFile(new[]
    {
        PendingPostStateFrame(seq: 1, stateType: "map", actionType: "choose_map_node")
    }, path =>
    {
        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(path);
        AssertFalse(report.Validation.Errors.Any(error => error.Code == "missing_branch_decision"),
            "pending post-state frame should not require branch_decision");
        AssertFalse(report.Validation.Errors.Any(error => error.Code == "missing_post_state"),
            "pending post-state frame should not be classified as missing post_state");
    });
}

static void ShowPrintsExactRecordBySequence()
{
    string expected = DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn");
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        expected
    }, path =>
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = TelemetryCli.Run(new[] { "show", path, "--seq", "2" }, stdout, stderr);

        AssertEqual(0, exitCode, "show exit code");
        AssertEqual(expected, stdout.ToString().Trim(), "show should print exact JSON line");
        AssertEqual("", stderr.ToString(), "show should not write stderr");
    });
}

static void LogicalRunDirectoryAggregatesSegmentFiles()
{
    WithRunsDirectory(runsDirectory =>
    {
        DateTime baseTime = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        string expected = DecisionContext(seq: 3, stateType: "event", actionType: "choose_event_option");
        string logicalDirectory = WriteLogicalRun(
            runsDirectory,
            "logical-run-abc123",
            new[] { RunStarted(seq: 1), DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card") },
            new[] { expected },
            baseTime);

        TelemetryRunSource source = TelemetryRunLocator.Resolve(logicalDirectory);
        AssertEqual(logicalDirectory, source.TelemetryPath, "logical source path should be the logical run directory");
        AssertEqual(2, source.TelemetryPaths.Count, "logical source should expose both segment files");

        TelemetryInspectionReport report = TelemetryRunInspector.Inspect(source);
        AssertEqual(3, report.RunSummary.RecordCount, "logical run should aggregate segment record counts");
        AssertEqual(2, report.RunSummary.SegmentCount, "logical run summary segment count");
        AssertTrue(report.Coverage.Shop.Appeared, "logical aggregate should include shop coverage from first segment");
        AssertTrue(report.Coverage.Event.Appeared, "logical aggregate should include event coverage from second segment");

        AssertCliExit(0, new[] { "show", logicalDirectory, "--seq", "3" }, "show logical run", out string output, out string error);
        AssertEqual(expected, output.Trim(), "show should read across logical segments");
        AssertEqual("", error, "show logical stderr");
    });
}

static void LatestDiscoveryHandlesLogicalRunDirectories()
{
    WithRunsDirectory(runsDirectory =>
    {
        DateTime baseTime = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        WriteRun(runsDirectory, "run-legacy-older", new[]
        {
            RunStarted(seq: 1),
            DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
        }, baseTime.AddMinutes(1));

        string logicalDirectory = WriteLogicalRun(
            runsDirectory,
            "logical-run-newer",
            new[] { RunStarted(seq: 1) },
            new[] { DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card") },
            baseTime.AddMinutes(2));

        TelemetryRunSource latest = TelemetryRunLocator.Resolve("latest", runsDirectory);
        AssertEqual(logicalDirectory, latest.TelemetryPath, "latest should resolve to newest logical run directory");
        AssertEqual(2, latest.TelemetryPaths.Count, "latest logical run should include both segment paths");

        AssertCliExit(0, new[] { "runs", "--runs-dir", runsDirectory, "--limit", "2" }, "runs mixed layouts", out string output, out string error);
        AssertEqual("", error, "runs mixed stderr");
        AssertTrue(output.Contains("run=logical-run-newer", StringComparison.Ordinal), "runs should list logical run");
        AssertTrue(output.Contains("segments=2", StringComparison.Ordinal), "runs should show logical segment count");
        AssertTrue(output.Contains("run=run-legacy-older", StringComparison.Ordinal), "runs should still list legacy run");
    });
}

static void RunsCommandListsRecentRunsWithSurfaceIndicators()
{
    WithRunsDirectory(runsDirectory =>
    {
        DateTime baseTime = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        WriteRun(runsDirectory, "run-short-latest", new[]
        {
            RunStarted(seq: 1),
            DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn"),
            RelicTrigger(seq: 3)
        }, baseTime.AddMinutes(2));

        WriteRun(runsDirectory, "run-rich-previous", new[]
        {
            RunStarted(seq: 1),
            DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card"),
            DecisionContext(seq: 3, stateType: "event", actionType: "choose_event_option"),
            DecisionContext(seq: 4, stateType: "card_reward", actionType: "choose_reward_card"),
            DecisionContext(seq: 5, stateType: "relic_select", actionType: "choose_relic_select"),
            BranchMatched(seq: 6)
        }, baseTime.AddMinutes(1));

        AssertCliExit(0, new[] { "runs", "--runs-dir", runsDirectory, "--top-size", "2" }, "runs", out string output, out string error);

        AssertEqual("", error, "runs stderr");
        AssertTrue(output.Contains("run=run-short-latest", StringComparison.Ordinal), "runs should include latest short run");
        AssertTrue(output.Contains("run=run-rich-previous", StringComparison.Ordinal), "runs should include previous rich run");
        AssertTrue(output.Contains("combat=1", StringComparison.Ordinal), "runs should show combat indicator");
        AssertTrue(output.Contains("shop=1", StringComparison.Ordinal), "runs should show shop indicator");
        AssertTrue(output.Contains("event=1", StringComparison.Ordinal), "runs should show event indicator");
        AssertTrue(output.Contains("card_reward=1", StringComparison.Ordinal), "runs should show card reward indicator");
        AssertTrue(output.Contains("relic_select=1", StringComparison.Ordinal), "runs should show relic select indicator");
        AssertTrue(output.Contains("branch_matched=1", StringComparison.Ordinal), "runs should show branch matched indicator");
    });
}

static void RunsCommandFiltersBeyondLatestBySurface()
{
    WithRunsDirectory(runsDirectory =>
    {
        DateTime baseTime = new(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        WriteRun(runsDirectory, "run-short-latest", new[]
        {
            RunStarted(seq: 1),
            DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
        }, baseTime.AddMinutes(2));

        WriteRun(runsDirectory, "run-rich-previous", new[]
        {
            RunStarted(seq: 1),
            DecisionContext(seq: 2, stateType: "shop", actionType: "buy_shop_card"),
            DecisionContext(seq: 3, stateType: "event", actionType: "choose_event_option")
        }, baseTime.AddMinutes(1));

        AssertCliExit(0, new[] { "runs", "--runs-dir", runsDirectory, "--surface", "shop,event", "--limit", "1" }, "runs filter", out string output, out string error);

        AssertEqual("", error, "runs filter stderr");
        AssertTrue(output.Contains("filters: shop,event", StringComparison.Ordinal), "runs filter should echo normalized filters");
        AssertTrue(output.Contains("scanned: 2", StringComparison.Ordinal), "runs filter should scan past the latest run");
        AssertTrue(output.Contains("run=run-rich-previous", StringComparison.Ordinal), "runs filter should return the matching previous run");
        AssertFalse(output.Contains("run=run-short-latest", StringComparison.Ordinal), "runs filter should exclude the latest non-matching run");
    });
}

static void CliSmokeCommandsWork()
{
    string expected = DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn");
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        expected
    }, path =>
    {
        AssertCliExit(0, new[] { "inspect", path, "--json" }, "inspect --json", out string inspectJson, out string inspectError);
        using (JsonDocument document = JsonDocument.Parse(inspectJson))
            AssertHasProperty(document.RootElement, "run_summary", "inspect --json output should be parseable report JSON");
        AssertEqual("", inspectError, "inspect --json stderr");

        AssertCliExit(0, new[] { "frames", path, "--top-size", "2" }, "frames", out _, out string framesError);
        AssertEqual("", framesError, "frames stderr");

        AssertCliExit(0, new[] { "branch", path }, "branch", out _, out string branchError);
        AssertEqual("", branchError, "branch stderr");

        AssertCliExit(0, new[] { "coverage", path }, "coverage", out string coverageOutput, out string coverageError);
        AssertEqual("", coverageError, "coverage stderr");
        AssertTrue(coverageOutput.Contains("Readiness", StringComparison.Ordinal), "coverage should print readiness block");
        AssertTrue(coverageOutput.Contains("combat detail:", StringComparison.Ordinal), "coverage should print combat readiness summary");

        AssertCliExit(0, new[] { "perf", path }, "perf", out _, out string perfError);
        AssertEqual("", perfError, "perf stderr");

        AssertCliExit(0, new[] { "show", path, "--seq", "2" }, "show", out string showOutput, out string showError);
        AssertEqual(expected, showOutput.Trim(), "show should print exact JSONL record");
        AssertEqual("", showError, "show stderr");

        AssertCliExit(0, new[] { "validate", path }, "validate", out string validateOutput, out string validateError);
        AssertTrue(validateOutput.Contains("OK:", StringComparison.Ordinal), "validate should report clean run");
        AssertEqual("", validateError, "validate stderr");
    });
}

static void ValidateExitsNonZeroForHardFailures()
{
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        "{broken"
    }, path =>
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = TelemetryCli.Run(new[] { "validate", path }, stdout, stderr);

        AssertEqual(1, exitCode, "validate should fail hard failures");
        AssertTrue(stderr.ToString().Contains("Validation failed", StringComparison.Ordinal), "validate should explain failure");
    });
}

static void ValidateExitsZeroForCleanRun()
{
    WithTelemetryFile(new[]
    {
        RunStarted(seq: 1),
        DecisionFrame(seq: 2, stateType: "combat", actionType: "end_turn")
    }, path =>
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode = TelemetryCli.Run(new[] { "validate", path }, stdout, stderr);

        AssertEqual(0, exitCode, "validate clean exit code");
        AssertTrue(stdout.ToString().Contains("OK:", StringComparison.Ordinal), "validate clean output");
        AssertEqual("", stderr.ToString(), "validate clean stderr");
    });
}

static void WithTelemetryFile(IReadOnlyList<string> lines, Action<string> body)
    => WithTelemetryFiles(lines, Array.Empty<string>(), body);

static void WithTelemetryFiles(
    IReadOnlyList<string> runLines,
    IReadOnlyList<string> operationalLines,
    Action<string> body)
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-inspector-test-{Guid.NewGuid():N}");
    try
    {
        string runDirectory = Path.Combine(directory, "telemetry", "runs", "run-test");
        Directory.CreateDirectory(runDirectory);
        string path = Path.Combine(runDirectory, "telemetry.jsonl");
        File.WriteAllLines(path, runLines);

        if (operationalLines.Count > 0)
        {
            string operationalDirectory = Path.Combine(directory, "telemetry", "operational");
            Directory.CreateDirectory(operationalDirectory);
            File.WriteAllLines(Path.Combine(operationalDirectory, "20260505.jsonl"), operationalLines);
        }

        body(path);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void WithRunsDirectory(Action<string> body)
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-inspector-test-{Guid.NewGuid():N}");
    try
    {
        string runsDirectory = Path.Combine(directory, "telemetry", "runs");
        Directory.CreateDirectory(runsDirectory);
        body(runsDirectory);
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static string WriteRun(string runsDirectory, string runName, IReadOnlyList<string> lines, DateTime lastWriteUtc)
{
    string runDirectory = Path.Combine(runsDirectory, runName);
    Directory.CreateDirectory(runDirectory);
    string telemetryPath = Path.Combine(runDirectory, "telemetry.jsonl");
    File.WriteAllLines(telemetryPath, lines);
    File.SetLastWriteTimeUtc(telemetryPath, lastWriteUtc);
    Directory.SetLastWriteTimeUtc(runDirectory, lastWriteUtc);
    return telemetryPath;
}

static string WriteLogicalRun(
    string runsDirectory,
    string runName,
    IReadOnlyList<string> firstSegmentLines,
    IReadOnlyList<string> secondSegmentLines,
    DateTime lastWriteUtc)
{
    string runDirectory = Path.Combine(runsDirectory, runName);
    string segmentsDirectory = Path.Combine(runDirectory, "segments");
    Directory.CreateDirectory(segmentsDirectory);

    string firstSegment = Path.Combine(segmentsDirectory, "run-segment-a.jsonl");
    string secondSegment = Path.Combine(segmentsDirectory, "run-segment-b.jsonl");
    File.WriteAllLines(firstSegment, firstSegmentLines);
    File.WriteAllLines(secondSegment, secondSegmentLines);
    File.SetLastWriteTimeUtc(firstSegment, lastWriteUtc.AddSeconds(-1));
    File.SetLastWriteTimeUtc(secondSegment, lastWriteUtc);
    Directory.SetLastWriteTimeUtc(segmentsDirectory, lastWriteUtc);
    Directory.SetLastWriteTimeUtc(runDirectory, lastWriteUtc);
    return runDirectory;
}

static string RunStarted(int seq)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "lifecycle/run_started",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["branch"] = Branch("branch-0001", "attempt-0001"),
        ["state"] = new Dictionary<string, object?>
        {
            ["state_type"] = "room/start",
            ["raw_snapshot"] = new Dictionary<string, object?>
            {
                ["local_player"] = new Dictionary<string, object?> { ["relic_count"] = 0 }
            }
        }
    });

static string RunLoaded(int seq, string attempt, string branch, bool matched)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "lifecycle/run_loaded",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["branch"] = Branch(branch, attempt),
        ["branch_match"] = new Dictionary<string, object?>
        {
            ["matched"] = matched,
            ["reason"] = matched ? "matched_known_state" : "resume_state_not_found",
            ["parent_canonical_state_hash"] = matched ? "state-a" : null
        }
    });

static string DecisionFrame(int seq, string stateType, string actionType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/frame",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["pre_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["post_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[] { new Dictionary<string, object?> { ["action_type"] = actionType } }
        },
        ["selected_action"] = new Dictionary<string, object?>
        {
            ["normalized_typed_action_key"] = new Dictionary<string, object?> { ["action_type"] = actionType },
            ["canonical_action_hash"] = $"hash-{seq}"
        },
        ["branch"] = Branch("branch-0001", "attempt-0001"),
        ["branch_decision"] = new Dictionary<string, object?> { ["forked"] = false, ["trajectory_replayed"] = false },
        ["operational_metadata"] = new Dictionary<string, object?>
        {
            ["decision_timing"] = new Dictionary<string, object?>
            {
                ["pre_snapshot_us"] = 5,
                ["post_snapshot_us"] = 8
            }
        }
    });

static string DecisionContext(int seq, string stateType, string actionType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/context",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["decision_context_id"] = $"ctx-{seq}",
        ["pre_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[] { new Dictionary<string, object?> { ["action_type"] = actionType } }
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string DecisionContextUnavailable(int seq, string stateType, string actionType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/context",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["decision_context_id"] = $"ctx-{seq}",
        ["pre_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[]
            {
                new Dictionary<string, object?> { ["action_type"] = actionType, ["availability"] = "typed_builder_unavailable" }
            }
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string DecisionContextUnknown(int seq, string stateType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/context",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["decision_context_id"] = $"ctx-{seq}",
        ["pre_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[]
            {
                new Dictionary<string, object?> { ["action_type"] = "unknown", ["availability"] = "no_legal_action_builder_match" }
            }
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string PendingPostStateFrame(int seq, string stateType, string actionType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/frame",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["pre_state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["post_state"] = new Dictionary<string, object?> { ["status"] = "pending" },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[] { new Dictionary<string, object?> { ["action_type"] = actionType } }
        },
        ["selected_action"] = new Dictionary<string, object?>
        {
            ["normalized_typed_action_key"] = new Dictionary<string, object?> { ["action_type"] = actionType },
            ["canonical_action_hash"] = $"hash-{seq}"
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string Signal(int seq, string recordType, string actionType, string stateType)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = recordType,
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["state"] = new Dictionary<string, object?> { ["state_type"] = stateType },
        ["action_type"] = actionType,
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string ContextMatchedSignal(int seq, string recordType, string stateType, string matchReason, bool matched)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = recordType,
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["capture_policy"] = "signal_only_no_state_snapshot_no_legal_actions",
        ["decision_context"] = new Dictionary<string, object?>
        {
            ["decision_context_id"] = $"ctx-{seq}",
            ["state_type"] = stateType,
            ["match_policy"] = "latest_context_by_surface",
            ["context_reference_status"] = "latest_context_by_surface_resolved",
            ["context_reference_reason"] = matched ? "latest_context_by_surface" : "latest_context_legal_actions_unavailable_or_placeholder_only",
            ["selected_action_match"] = new Dictionary<string, object?>
            {
                ["matched"] = matched,
                ["reason"] = matchReason
            }
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string ShopContextMatchedSignal(int seq, string contextId, string purchaseStatus, string selectedActionHash)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/ui_signal",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["capture_policy"] = "signal_only_no_state_snapshot_no_legal_actions",
        ["ui_signal"] = new Dictionary<string, object?>
        {
            ["metadata"] = new Dictionary<string, object?>
            {
                ["action_type"] = "buy_shop_relic",
                ["category"] = "relic",
                ["relic_id"] = "RAZOR_TOOTH",
                ["purchase_status"] = purchaseStatus,
                ["normalized_typed_action_key"] = new Dictionary<string, object?>
                {
                    ["action_type"] = "buy_shop_relic",
                    ["category"] = "relic",
                    ["relic_id"] = "RAZOR_TOOTH"
                }
            }
        },
        ["decision_context"] = new Dictionary<string, object?>
        {
            ["decision_context_id"] = contextId,
            ["state_type"] = "shop",
            ["match_policy"] = "latest_context_by_surface",
            ["context_reference_status"] = "latest_context_by_surface_resolved",
            ["context_reference_reason"] = "latest_context_by_surface",
            ["selected_action_match"] = new Dictionary<string, object?>
            {
                ["matched"] = true,
                ["reason"] = "normalized_typed_action_key_hash_match",
                ["selected_action_canonical_hash"] = selectedActionHash,
                ["matched_legal_action_index"] = 0,
                ["matched_action_type"] = "buy_shop_relic",
                ["matched_legal_action_hash"] = selectedActionHash
            }
        },
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string CombatDecisionFrame(
    int seq,
    string actionType,
    bool includeActionStep = true,
    bool includeActionIndex = true)
{
    var snapshotProcess = new Dictionary<string, object?>
    {
        ["turn_index"] = 1,
        ["turn_side"] = "player",
        ["phase"] = "play",
        ["marker_status"] = new Dictionary<string, object?>
        {
            ["turn_index"] = "present",
            ["turn_side"] = "present",
            ["phase"] = "present",
            ["action_step"] = includeActionStep ? "present" : "unavailable"
        }
    };
    if (includeActionStep)
        snapshotProcess["action_step"] = "choose_action";
    if (includeActionIndex)
        snapshotProcess["action_index"] = 2;

    var recorderProcessPre = new Dictionary<string, object?>
    {
        ["round"] = 1,
        ["turn_side"] = "player",
        ["phase"] = "play"
    };
    if (includeActionStep)
        recorderProcessPre["action_step"] = "choose_action";
    if (includeActionIndex)
        recorderProcessPre["action_index"] = 2;

    return Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "decision/frame",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["pre_state"] = new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["raw_snapshot"] = new Dictionary<string, object?>
            {
                ["state_type"] = "combat",
                ["local_player"] = new Dictionary<string, object?>
                {
                    ["energy"] = 3,
                    ["draw_pile"] = new[] { new Dictionary<string, object?> { ["card_id"] = "DRAW" } },
                    ["discard_pile"] = new[] { new Dictionary<string, object?> { ["card_id"] = "DISCARD" } },
                    ["exhaust_pile"] = new[] { new Dictionary<string, object?> { ["card_id"] = "EXHAUST" } },
                    ["powers"] = new[] { new Dictionary<string, object?> { ["power_id"] = "VULNERABLE" } }
                },
                ["combat"] = new Dictionary<string, object?>
                {
                    ["round"] = 1,
                    ["current_side"] = "player",
                    ["is_play_phase"] = true,
                    ["process"] = snapshotProcess,
                    ["target_candidates"] = new[] { new Dictionary<string, object?> { ["entity_id"] = "JAW_WORM_0" } },
                    ["enemies"] = new[] { new Dictionary<string, object?> { ["entity_id"] = "JAW_WORM_0", ["intent"] = "ATTACK" } }
                }
            }
        },
        ["post_state"] = new Dictionary<string, object?> { ["state_type"] = "combat" },
        ["legal_actions"] = new Dictionary<string, object?>
        {
            ["actions"] = new[] { new Dictionary<string, object?> { ["action_type"] = actionType } }
        },
        ["selected_action"] = new Dictionary<string, object?>
        {
            ["normalized_typed_action_key"] = new Dictionary<string, object?>
            {
                ["action_type"] = actionType,
                ["card_id"] = "STRIKE",
                ["hand_index"] = 0,
                ["target"] = new Dictionary<string, object?>
                {
                    ["target_id"] = 7,
                    ["target_index_space"] = "enemies",
                    ["target_index"] = 0,
                    ["target_entity_id"] = "JAW_WORM_0"
                }
            },
            ["canonical_action_hash"] = $"hash-{seq}"
        },
        ["combat_process"] = new Dictionary<string, object?>
        {
            ["marker_status"] = new Dictionary<string, object?>
            {
                ["turn"] = "present",
                ["phase"] = "present",
                ["action_step"] = includeActionStep || includeActionIndex ? "present" : "unavailable"
            },
            ["pre"] = recorderProcessPre
        },
        ["branch"] = Branch("branch-0001", "attempt-0001"),
        ["branch_decision"] = new Dictionary<string, object?> { ["forked"] = false, ["trajectory_replayed"] = false }
    });
}

static string RelicTrigger(int seq)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "effect/relic_trigger",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["relic_id"] = "BAG_OF_PREP",
        ["target_count"] = 1,
        ["branch"] = Branch("branch-0001", "attempt-0001")
    });

static string BranchMatched(int seq)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "lifecycle/branch_matched",
        ["run_id"] = "run-test",
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["branch"] = Branch("branch-0001", "attempt-0002"),
        ["branch_match"] = new Dictionary<string, object?>
        {
            ["matched"] = true,
            ["reason"] = "matched_known_state"
        }
    });

static string OperationalCallbackFailed(int seq)
    => Json(new Dictionary<string, object?>
    {
        ["schema_version"] = "sts2.telemetry.local.v1",
        ["record_type"] = "lifecycle/telemetry_callback_failed",
        ["run_id"] = null,
        ["local_sequence"] = seq,
        ["recorded_at_utc"] = Timestamp(seq),
        ["source"] = "test.callback",
        ["exception"] = new Dictionary<string, object?>
        {
            ["type"] = "System.InvalidOperationException",
            ["message"] = "boom"
        }
    });

static Dictionary<string, object?> Branch(string branchId, string attemptId)
    => new() { ["branch_id"] = branchId, ["attempt_id"] = attemptId };

static string Timestamp(int seq)
    => $"2026-05-05T00:00:{seq:00}Z";

static string Json(IReadOnlyDictionary<string, object?> value)
    => JsonSerializer.Serialize(value, TelemetryInspectorJson.CompactOptions);

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void AssertNear(double expected, double actual, double tolerance, string message)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void AssertCliExit(
    int expectedExitCode,
    string[] args,
    string commandName,
    out string stdout,
    out string stderr)
{
    using var output = new StringWriter();
    using var error = new StringWriter();
    int exitCode = TelemetryCli.Run(args, output, error);
    stdout = output.ToString();
    stderr = error.ToString();
    AssertEqual(expectedExitCode, exitCode, $"{commandName} exit code");
}

static void AssertHasProperty(JsonElement element, string propertyName, string message)
{
    if (!element.TryGetProperty(propertyName, out _))
        throw new InvalidOperationException(message);
}
