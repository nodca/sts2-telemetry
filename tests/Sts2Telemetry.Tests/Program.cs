using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Runs;
using Sts2Telemetry;

if (args.Length == 1 && string.Equals(args[0], "--staging-upload-e2e", StringComparison.Ordinal))
{
    Environment.ExitCode = RunStagingUploadE2e().GetAwaiter().GetResult();
    return;
}

var tests = new (string Name, Action Body)[]
{
    ("canonical hash ignores operational-only fields", CanonicalHashIgnoresOperationalFields),
    ("canonical hash ignores capture projection notes", CanonicalHashIgnoresProjectionNotes),
    ("canonical payload hash matches canonicalized raw hash", CanonicalPayloadHashMatchesCanonicalizedRawHash),
    ("branch tracker delays fork until resumed decision diverges", BranchTrackerDelaysForkUntilDecisionDiverges),
    ("branch tracker keeps inconclusive divergence out of replay metadata", BranchTrackerKeepsInconclusiveDivergenceOutOfReplayMetadata),
    ("branch tracker preserves unknown resumed parentage", BranchTrackerPreservesUnknownResume),
    ("logical run identity uses only stable run fields", LogicalRunIdentityUsesStableRunFields),
    ("logical run identity uses STS2 runtime fallback fields", LogicalRunIdentityUsesRuntimeFallbackFields),
    ("logical run identity reports incomplete stable fields", LogicalRunIdentityReportsIncompleteStableFields),
    ("logical run identity marks zero start time as degraded", LogicalRunIdentityMarksZeroStartTimeAsDegraded),
    ("run support detector does not probe game singletons for test doubles", RunSupportDetectorDoesNotProbeGameSingletonsForTestDoubles),
    ("recorder delays same-process run started until matching stable resume", RecorderDelaysRunStartedUntilMatchingStableResume),
    ("recorder resets delayed run started when decision pre-state is unmatched", RecorderResetsDelayedRunStartedWhenDecisionPreStateUnmatched),
    ("recorder keeps pending resume across unmatched lifecycle until matching decision", RecorderKeepsPendingResumeAcrossUnmatchedLifecycleUntilMatchingDecision),
    ("recorder resolves delayed event resume from settled lifecycle snapshot", RecorderResolvesDelayedEventResumeFromSettledLifecycleSnapshot),
    ("recorder resolves explicit unstable event load from settled lifecycle snapshot", RecorderResolvesExplicitUnstableEventLoadFromSettledLifecycleSnapshot),
    ("recorder explicit unstable load resolves unmatched action pre-state as unknown", RecorderExplicitUnstableLoadResolvesUnmatchedActionPreStateAsUnknown),
    ("recorder abandon forces fresh run id for next run", RecorderAbandonForcesFreshRunIdForNextRun),
    ("recorder preserves branch matching across non-abandon cleanup run started", RecorderPreservesBranchMatchingAcrossNonAbandonCleanupRunStarted),
    ("recorder rotates run id when stable logical identity changes", RecorderRotatesRunIdWhenStableLogicalIdentityChanges),
    ("recorder rotates run id when post-load run started identity changes", RecorderRotatesRunIdWhenPostLoadRunStartedIdentityChanges),
    ("recorder reconciles degraded loaded-save identity to recent stable run", RecorderReconcilesDegradedLoadedSaveIdentityToRecentStableRun),
    ("settled shop lifecycle emits legal actions and shop offers", SettledShopLifecycleEmitsNonCombatDecisionContext),
    ("reward runtime cache emits reward decision context", RewardRuntimeCacheEmitsRewardDecisionContext),
    ("reward generation schedules settled reward decision context", RewardGenerationSchedulesSettledRewardDecisionContext),
    ("reward cache survives room entered for scheduled context", RewardCacheSurvivesRoomEnteredForScheduledContext),
    ("reward UI signal references reward decision context without snapshots", RewardUiSignalReferencesRewardDecisionContextWithoutSnapshots),
    ("card reward cache emits legal actions and card reward signals stay signal only", CardRewardCacheEmitsLegalActionsAndSignalsStaySignalOnly),
    ("card reward selected card signal closes card reward context", CardRewardSelectedCardSignalClosesCardRewardContext),
    ("card reward SelectCard holder signal closes card reward context", CardRewardSelectCardHolderSignalClosesCardRewardContext),
    ("card reward screen selected cards close card reward context", CardRewardScreenSelectedCardsCloseCardRewardContext),
    ("card reward immediate selected signal creates context before scheduled callback", CardRewardImmediateSelectedSignalCreatesContextBeforeScheduledCallback),
    ("card reward immediate signal without identity references context", CardRewardImmediateSignalWithoutIdentityReferencesContext),
    ("rewards card reward action links to child card reward context", RewardsCardRewardActionLinksToChildCardRewardContext),
    ("potion reward signal closes rewards context", PotionRewardSignalClosesRewardsContext),
    ("PAELS_WING sacrifice action closes card reward context", PaelsWingSacrificeActionClosesCardRewardContext),
    ("runtime opening patches schedule typed card reward relic and bundle contexts", RuntimeOpeningPatchesScheduleTypedSelectionContexts),
    ("selection choice cache emits relic and bundle legal actions", SelectionChoiceCacheEmitsRelicAndBundleLegalActions),
    ("player choice signal matches relic and bundle decision contexts", PlayerChoiceSignalMatchesRelicAndBundleDecisionContexts),
    ("card reward signal without settled context exposes live gap", CardRewardSignalWithoutSettledContextExposesLiveGap),
    ("event signal with unavailable context reports placeholder reason", EventSignalWithUnavailableContextReportsPlaceholderReason),
    ("event UI signal references settled decision context without snapshots", EventUiSignalReferencesSettledDecisionContextWithoutSnapshots),
    ("shop UI signal references settled decision context without snapshots", ShopUiSignalReferencesSettledDecisionContextWithoutSnapshots),
    ("shop card signal matches typed card legal category by card id", ShopCardSignalMatchesTypedCardLegalCategoryByCardId),
    ("shop card completion inherits identity from prior attempt", ShopCardCompletionInheritsIdentityFromPriorAttempt),
    ("shop relic completion inherits identity from prior attempt", ShopRelicCompletionInheritsIdentityFromPriorAttempt),
    ("shop card completion does not use stale attempt after unrelated signal", ShopCardCompletionDoesNotUseStaleAttemptAfterUnrelatedSignal),
    ("shop completion coalesces duplicate callbacks and refreshes context", ShopCompletionCoalescesDuplicateCallbacksAndRefreshesContext),
    ("non-combat closure summary marks trainable matched signals", NonCombatClosureSummaryMarksTrainableMatchedSignals),
    ("upload status excludes diagnostic card reward CardsSelected no-identity signals", UploadStatusExcludesDiagnosticCardRewardCardsSelectedNoIdentitySignals),
    ("upload status counts unmatched actionable card reward signals", UploadStatusCountsUnmatchedActionableCardRewardSignals),
    ("native save privacy scrub removes local identity", NativeSavePrivacyScrubRemovesLocalIdentity),
    ("native save capture dedupes manifest entries", NativeSaveCaptureDedupesManifestEntries),
    ("native save capture discovers Flatpak profile saves", NativeSaveCaptureDiscoversFlatpakProfileSaves),
    ("native save root resolver includes cross-platform roots", NativeSaveRootResolverIncludesCrossPlatformRoots),
    ("native save capture prunes local object cache", NativeSaveCapturePrunesLocalObjectCache),
    ("save observed emits uploadable native save capture record", SaveObservedEmitsUploadableNativeSaveCaptureRecord),
    ("decision context includes recent native save ref", DecisionContextIncludesRecentNativeSaveRef),
    ("recorder explicit run load matches known state before later fork", RecorderExplicitRunLoadMatchesKnownStateBeforeLaterFork),
    ("recorder decision frames mark replayed known edges", RecorderDecisionFramesMarkReplayedKnownEdges),
    ("recorder pending transition markers omit branch decision", RecorderPendingTransitionMarkersOmitBranchDecision),
    ("recorder save observed signal only does not capture or seed branch match", RecorderSaveObservedSignalOnlyDoesNotCaptureOrSeedBranchMatch),
    ("recorder explicit unmatched load does not fabricate branch match", RecorderExplicitUnmatchedLoadDoesNotFabricateBranchMatch),
    ("LoadRunSave patch records save preview only", LoadRunSavePatchRecordsSavePreviewOnly),
    ("SaveRun and Saved hooks record save observed signal only", SaveRunAndSavedHooksRecordSaveObservedSignalOnly),
    ("run cleanup abandon and ended hooks record signal only", RunCleanupAbandonAndEndedHooksRecordSignalOnly),
    ("patched action metadata is shallow for unsafe runtime objects", PatchedActionMetadataIsShallowForUnsafeRuntimeObjects),
    ("runtime action metadata is shallow when stringification is unsafe", RuntimeActionMetadataIsShallowWhenStringificationIsUnsafe),
    ("reflection cached member lookup preserves null versus missing", ReflectionCachedMemberLookupPreservesNullVersusMissing),
    ("normalized typed play-card key uses trusted fields", NormalizedTypedPlayCardKeyUsesTrustedFields),
    ("normalized typed play-card key suppresses untrusted fields", NormalizedTypedPlayCardKeySuppressesUntrustedFields),
    ("normalized typed use-potion key uses trusted fields", NormalizedTypedUsePotionKeyUsesTrustedFields),
    ("normalized typed discard-potion key uses matched slot", NormalizedTypedDiscardPotionKeyUsesMatchedSlot),
    ("normalized typed treasure relic key uses pick or skip", NormalizedTypedTreasureRelicKeyUsesPickOrSkip),
    ("runtime play-card metadata ignores net combat card index for hand match", RuntimePlayCardMetadataIgnoresNetCombatCardIndexForHandMatch),
    ("runtime play-card metadata falls back to unique card id", RuntimePlayCardMetadataFallsBackToUniqueCardId),
    ("runtime play-card metadata preserves generated action identity", RuntimePlayCardMetadataPreservesGeneratedActionIdentity),
    ("runtime play-card metadata falls back to pre-state target id", RuntimePlayCardMetadataFallsBackToPreStateTargetId),
    ("runtime play-card metadata reports duplicate card ids ambiguous", RuntimePlayCardMetadataReportsDuplicateCardIdsAmbiguous),
    ("runtime play-card metadata suppresses invalid selected target", RuntimePlayCardMetadataSuppressesInvalidSelectedTarget),
    ("runtime use-potion metadata enriches from combat pre-state", RuntimeUsePotionMetadataEnrichesFromCombatPreState),
    ("runtime use-potion metadata reports extraction gaps", RuntimeUsePotionMetadataReportsExtractionGaps),
    ("ActionExecutor capture policy marks approved volatile actions signal only", ActionExecutorCapturePolicyMarksApprovedVolatileActionsSignalOnly),
    ("ActionExecutor signal-only recording does not capture snapshots or pending decisions", ActionExecutorSignalOnlyRecordingDoesNotCaptureSnapshotsOrPendingDecisions),
    ("ActionExecutor map vote signal closes typed map context", ActionExecutorMapVoteSignalClosesTypedMapContext),
    ("ActionExecutor callbacks route signal-only actions without pending-missing markers", ActionExecutorCallbacksRouteSignalOnlyActionsWithoutPendingMissingMarkers),
    ("ActionExecutor callbacks keep normal actions full frame", ActionExecutorCallbacksKeepNormalActionsFullFrame),
    ("patched UI callback records signal only", PatchedUiCallbackRecordsSignalOnly),
    ("event option UI signal records scalar option index only", EventOptionUiSignalRecordsScalarOptionIndexOnly),
    ("event decision context retry requires usable legal actions", EventDecisionContextRetryRequiresUsableLegalActions),
    ("runtime rest option signal records scalar option index only", RuntimeRestOptionSignalRecordsScalarOptionIndexOnly),
    ("shop signal metadata records normalized typed key", ShopSignalMetadataRecordsNormalizedTypedKey),
    ("shop inventory potion completion matches settled context", ShopInventoryPotionCompletionMatchesSettledContext),
    ("player choice signal projects shop card removal from typed runtime context", PlayerChoiceSignalProjectsShopCardRemovalFromTypedRuntimeContext),
    ("UI decision patch targets keep map selection on ActionExecutor", UiDecisionPatchTargetsKeepMapSelectionOnActionExecutor),
    ("removed reward wrapper hook is optional while typed reward coverage remains required", RemovedRewardWrapperHookIsOptionalWhileTypedCoverageRemainsRequired),
    ("shop hook targets include removal and purchase completion", ShopHookTargetsIncludeRemovalAndPurchaseCompletion),
    ("runtime signal patch targets include relic and shop completion", RuntimeSignalPatchTargetsIncludeRelicAndShopCompletion),
    ("runtime opening patch targets include card reward relic and bundle", RuntimeOpeningPatchTargetsIncludeCardRewardRelicAndBundle),
    ("main menu upload UI patch targets are isolated", MainMenuUploadUiPatchTargetsAreIsolated),
    ("Harmony native dependency preload is available on Linux", HarmonyNativeDependencyPreloadIsAvailableOnLinux),
    ("act-entered lifecycle callback records signal only", ActEnteredLifecycleCallbackRecordsSignalOnly),
    ("room-entered lifecycle callback records signal only", RoomEnteredLifecycleCallbackRecordsSignalOnly),
    ("room-exited public event subscription is disabled", RoomExitedPublicEventSubscriptionIsDisabled),
    ("room-exited lifecycle callback records signal only without settled snapshot", RoomExitedLifecycleCallbackRecordsSignalOnlyWithoutSettledSnapshot),
    ("action executor snapshots use runtime-only screen policy", ActionExecutorSnapshotsUseRuntimeOnlyScreenPolicy),
    ("legal action builder returns pending typed builder placeholder", LegalActionBuilderReturnsPendingTypedBuilderPlaceholder),
    ("legal action builder extracts typed map actions", LegalActionBuilderExtractsTypedMapActions),
    ("legal action builder extracts act-start map actions", LegalActionBuilderExtractsActStartMapActions),
    ("legal action builder extracts typed event rest and treasure actions", LegalActionBuilderExtractsTypedEventRestAndTreasureActions),
    ("legal action builder extracts non-combat discard potion actions", LegalActionBuilderExtractsNonCombatDiscardPotionActions),
    ("legal action builder returns surface-specific unavailable markers", LegalActionBuilderReturnsSurfaceSpecificUnavailableMarkers),
    ("legal action builder marks combat unavailable outside play phase", LegalActionBuilderMarksCombatUnavailableOutsidePlayPhase),
    ("legal action builder marks combat runtime unavailable without local player", LegalActionBuilderMarksCombatRuntimeUnavailableWithoutLocalPlayer),
    ("legal action builder enriches combat actions", LegalActionBuilderEnrichesCombatActions),
    ("combat decision frame deduplicates target candidates", CombatDecisionFrameDeduplicatesTargetCandidates),
    ("legal action builder marks potion availability and target semantics", LegalActionBuilderMarksPotionAvailabilityAndTargetSemantics),
    ("state snapshot builder enriches combat projections", StateSnapshotBuilderEnrichesCombatProjections),
    ("legal action builder extracts typed shop inventory actions", LegalActionBuilderExtractsTypedShopInventoryActions),
    ("local game assembly exposes required hook targets", LocalGameAssemblyExposesRequiredHookTargets),
    ("telemetry directory resolver keeps runtime data outside mod directory", TelemetryDirectoryResolverKeepsRuntimeDataOutsideModDirectory),
    ("jsonl writer persists one line per record", JsonlWriterPersistsRecords),
    ("jsonl writer routes logical run records to segment files", JsonlWriterRoutesLogicalRunRecordsToSegmentFiles),
    ("recorder envelope includes capture session and segment identity", RecorderEnvelopeIncludesCaptureSessionAndSegmentIdentity),
    ("jsonl writer persists non-finite numeric values", JsonlWriterPersistsNonFiniteNumbers),
    ("upload settings default enabled and disable marker works", UploadSettingsDefaultEnabledAndDisableMarkerWorks),
    ("update planner auto-authorizes patch and gates minor", UpdatePlannerAutoAuthorizesPatchAndGatesMinor),
    ("update store writes status and install request under update directory", UpdateStoreWritesStatusAndInstallRequestUnderUpdateDirectory),
    ("update installer rejects bad hash and applies valid package", UpdateInstallerRejectsBadHashAndAppliesValidPackage),
    ("upload queue packages JSONL segment as gzip manifest", UploadQueuePackagesJsonlSegmentAsGzipManifest),
    ("upload queue bundles native save capture payload", UploadQueueBundlesNativeSaveCapturePayload),
    ("upload queue skips duplicate source digest", UploadQueueSkipsDuplicateSourceDigest),
    ("upload queue packages next chunk for duplicate source digest", UploadQueuePackagesNextChunkForDuplicateSourceDigest),
    ("upload queue mark uploaded removes payload but keeps status coverage", UploadQueueMarkUploadedRemovesPayloadButKeepsStatusCoverage),
    ("upload queue prunes only old fully uploaded run sources", UploadQueuePrunesOnlyOldFullyUploadedRunSources),
    ("upload queue retains old run sources with unsuccessful evidence", UploadQueueRetainsOldRunSourcesWithUnsuccessfulEvidence),
    ("upload queue bounds drop oldest bundle with reason", UploadQueueBoundsDropOldestBundleWithReason),
    ("upload signing material matches server order", UploadSigningMaterialMatchesServerOrder),
    ("upload client sends signed multipart bundle shape", UploadClientSendsSignedMultipartBundleShape),
    ("upload client retrieves signed reward status", UploadClientRetrievesSignedRewardStatus),
    ("upload service refreshes reward status by logical run id", UploadServiceRefreshesRewardStatusByLogicalRunId),
    ("upload service refreshes rejected upload token and retries once", UploadServiceRefreshesRejectedUploadTokenAndRetriesOnce),
    ("upload service marks registration failure after rejected upload token", UploadServiceMarksRegistrationFailureAfterRejectedUploadToken),
    ("upload service marks retry failure after token refresh", UploadServiceMarksRetryFailureAfterTokenRefresh),
    ("upload summary persists retrieved reward status", UploadSummaryPersistsRetrievedRewardStatus),
    ("upload status view groups logical runs and rewards", UploadStatusViewGroupsLogicalRunsAndRewards),
    ("upload status view marks load-only run quality", UploadStatusViewMarksLoadOnlyRunQuality),
    ("upload status view marks rewards disabled from local summary", UploadStatusViewMarksRewardsDisabledFromLocalSummary),
    ("upload status renderer shows panel text", UploadStatusRendererShowsPanelText),
    ("upload status renderer chooses current status by precedence", UploadStatusRendererChoosesCurrentStatusByPrecedence),
    ("relic trigger signal records typed attribution without snapshots", RelicTriggerSignalRecordsTypedAttributionWithoutSnapshots),
    ("relic observer diagnostics report no player and relic gaps", RelicObserverDiagnosticsReportNoPlayerAndRelicGaps),
    ("recorder relic diagnostics report no player from runtime bridge", RecorderRelicDiagnosticsReportNoPlayerFromRuntimeBridge),
    ("relic observer diagnostics report missing flashed signal", RelicObserverDiagnosticsReportMissingFlashedSignal),
    ("relic observer diagnostics report field-backed flashed subscription", RelicObserverDiagnosticsReportFieldBackedFlashedSubscription),
    ("observed relic flashed event records typed attribution without snapshots", ObservedRelicFlashedEventRecordsTypedAttributionWithoutSnapshots),
    ("observed relic flashed field records typed attribution without snapshots", ObservedRelicFlashedFieldRecordsTypedAttributionWithoutSnapshots),
    ("newly obtained relic flashed field records typed attribution without snapshots", NewlyObtainedRelicFlashedFieldRecordsTypedAttributionWithoutSnapshots),
    ("shop purchase completed signal records typed metadata without snapshots", ShopPurchaseCompletedSignalRecordsTypedMetadataWithoutSnapshots),
    ("recorder persists telemetry callback failures", RecorderPersistsTelemetryCallbackFailures)
};

string filter = "";
if (args.Length == 2 && string.Equals(args[0], "--filter", StringComparison.Ordinal))
    filter = args[1];

var failures = new List<string>();
foreach (var test in tests)
{
    if (!string.IsNullOrWhiteSpace(filter)
        && test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) != true)
        continue;

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

static void CanonicalHashIgnoresOperationalFields()
{
    var left = new Dictionary<string, object?>
    {
        ["floor"] = 4,
        ["recorded_at_utc"] = "2026-05-04T01:00:00Z",
        ["ui_hover_target"] = "card-a",
        ["nested"] = new Dictionary<string, object?>
        {
            ["hp"] = 31,
            ["animation_timer"] = 10
        }
    };

    var right = new Dictionary<string, object?>
    {
        ["ui_hover_target"] = "card-b",
        ["nested"] = new Dictionary<string, object?>
        {
            ["animation_timer"] = 99,
            ["hp"] = 31
        },
        ["recorded_at_utc"] = "2026-05-04T02:00:00Z",
        ["floor"] = 4
    };

    AssertEqual(TelemetryHash.HashCanonical(left), TelemetryHash.HashCanonical(right), "canonical hashes should match");

    right["nested"] = new Dictionary<string, object?> { ["hp"] = 30 };
    AssertNotEqual(TelemetryHash.HashCanonical(left), TelemetryHash.HashCanonical(right), "gameplay field changes should hash differently");
}

static void CanonicalHashIgnoresProjectionNotes()
{
    var left = new Dictionary<string, object?>
    {
        ["state_type"] = "combat",
        ["game"] = new Dictionary<string, object?>
        {
            ["game_version"] = "ea-1",
            ["mod_version"] = "0.1.0",
            ["schema_version"] = "schema-a"
        },
        ["run"] = new Dictionary<string, object?> { ["floor"] = 2 },
        ["projection_notes"] = new Dictionary<string, object?>
        {
            ["reason"] = "action_executor:pre",
            ["builder"] = "reflection_prototype"
        }
    };

    var right = new Dictionary<string, object?>
    {
        ["projection_notes"] = new Dictionary<string, object?>
        {
            ["reason"] = "save_observed",
            ["builder"] = "reflection_prototype"
        },
        ["run"] = new Dictionary<string, object?> { ["floor"] = 2 },
        ["game"] = new Dictionary<string, object?>
        {
            ["game_version"] = "ea-1",
            ["mod_version"] = "0.2.0",
            ["schema_version"] = "schema-b"
        },
        ["state_type"] = "combat"
    };

    AssertEqual(TelemetryHash.HashCanonical(left), TelemetryHash.HashCanonical(right),
        "capture notes and recorder versions should not affect state identity");

    ((Dictionary<string, object?>)right["run"]!)["floor"] = 3;
    AssertNotEqual(TelemetryHash.HashCanonical(left), TelemetryHash.HashCanonical(right),
        "gameplay state changes should still affect state identity");
}

static void CanonicalPayloadHashMatchesCanonicalizedRawHash()
{
    var raw = new Dictionary<string, object?>
    {
        ["state_type"] = "combat",
        ["recorded_at_utc"] = "2026-05-04T01:00:00Z",
        ["run"] = new Dictionary<string, object?>
        {
            ["floor"] = 7,
            ["seed"] = "abc"
        },
        ["screen"] = new Dictionary<string, object?>
        {
            ["ui_hover_target"] = "card-a",
            ["is_map_open"] = false
        },
        ["non_finite"] = double.NaN
    };

    object? canonical = TelemetryHash.Canonicalize(raw);

    AssertEqual(
        TelemetryHash.HashCanonical(raw),
        TelemetryHash.HashCanonicalPayload(canonical),
        "hashing an already-canonical payload should match raw canonical hashing");
}

static void BranchTrackerDelaysForkUntilDecisionDiverges()
{
    var tracker = new BranchTracker();
    tracker.ObserveState("state-a", "run_start");
    AssertEqual("attempt-0001", tracker.BuildMetadata()["attempt_id"], "initial run attempt id");
    BranchDecisionResult first = tracker.RecordDecisionEdge(
        "state-a",
        "state-b",
        "decision-1",
        selectedActionCanonicalHash: "action-b");
    AssertTrue(!first.TrajectoryReplayed, "new edge from first attempt should not be replayed");

    BranchResumeResult resume = tracker.ObserveResume("state-a");
    AssertTrue(resume.Matched, "resume should match earlier canonical state");
    AssertTrue(!resume.Forked, "resume should not fork just because the matched node has children");
    AssertTrue(resume.PendingDivergence, "resume to a non-leaf should wait for decision-edge comparison");
    AssertEqual("state-a", resume.ParentCanonicalStateHash, "resume parent hash");
    AssertEqual("branch-0001", tracker.CurrentBranchId, "resume should keep the matched branch id");
    AssertEqual("attempt-0002", tracker.BuildMetadata()["attempt_id"], "matched resume should allocate a new attempt id");

    BranchDecisionResult continuation = tracker.RecordDecisionEdge(
        "state-a",
        "state-b",
        "decision-2",
        selectedActionCanonicalHash: "action-b");
    AssertTrue(!continuation.Forked, "matching a known child should remain continuation");
    AssertTrue(continuation.TrajectoryReplayed, "matching a known child should mark trajectory replay");
    AssertEqual("decision-1", continuation.MatchedDecisionFrameId, "matched edge should point to original decision");
    AssertEqual("node-000002", continuation.MatchedChildNodeId, "matched edge should point to known child node");
    AssertEqual("branch-0001", tracker.CurrentBranchId, "known child continuation branch id");
    AssertEqual("attempt-0002", tracker.BuildMetadata()["attempt_id"], "known child replay should remain in resume attempt");

    tracker.ObserveResume("state-a");
    AssertEqual("attempt-0003", tracker.BuildMetadata()["attempt_id"], "second resume should allocate another attempt id");
    BranchDecisionResult fork = tracker.RecordDecisionEdge(
        "state-a",
        "state-c",
        "decision-3",
        selectedActionCanonicalHash: "action-c");
    AssertTrue(fork.Forked, "a different decision edge from a known parent should fork");
    AssertTrue(!fork.TrajectoryReplayed, "forked edge should not claim replay");
    AssertNotEqual("branch-0001", tracker.CurrentBranchId, "divergent edge should allocate a later branch id");
}

static void BranchTrackerKeepsInconclusiveDivergenceOutOfReplayMetadata()
{
    var tracker = new BranchTracker();
    tracker.ObserveState("state-a", "run_start");
    tracker.RecordDecisionEdge(
        "state-a",
        "state-b",
        "decision-1",
        selectedActionCanonicalHash: "action-b");

    tracker.ObserveResume("state-a");
    BranchDecisionResult unknown = tracker.RecordDecisionEdge(
        preCanonicalStateHash: "state-a",
        postCanonicalStateHash: null,
        decisionFrameId: "decision-unknown",
        selectedActionCanonicalHash: null);

    AssertTrue(unknown.DivergenceUnknown, "inconclusive edge should report unknown divergence");
    AssertTrue(!unknown.Forked, "inconclusive edge should not fork");
    AssertTrue(!unknown.TrajectoryReplayed, "inconclusive edge should not claim replay");
    AssertEqual(null, unknown.MatchedDecisionFrameId, "inconclusive edge should not point to matched decision");
    AssertEqual(null, unknown.MatchedChildNodeId, "inconclusive edge should not point to matched child");
    AssertEqual("branch-0001", unknown.BranchId, "inconclusive edge should preserve branch id");
}

static void BranchTrackerPreservesUnknownResume()
{
    var tracker = new BranchTracker();
    BranchResumeResult resume = tracker.ObserveResume("state-from-previous-session");

    AssertTrue(!resume.Matched, "resume should be unmatched without an indexed canonical state");
    AssertTrue(!resume.Forked, "unmatched resume should not claim a fork");
    AssertEqual("branch-0001", resume.BranchId, "fresh unknown resume should not skip the first branch id");
    AssertEqual(null, resume.ParentCanonicalStateHash, "unknown resume parent hash");

    var metadata = tracker.BuildMetadata();
    AssertEqual("unknown", metadata["branch_status"], "unknown resume branch status");
    AssertEqual(null, metadata["current_state_node_id"], "unknown resume current node");
}

static void LogicalRunIdentityUsesStableRunFields()
{
    var first = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 7,
        AscensionLevel = 3,
        Seed = "seed-a",
        GameMode = "normal",
        StartTime = "2026-05-05T01:02:03Z",
        Character = new TestCharacter { Id = "ironclad" },
        Modifiers = new[] { "mod-b", "mod-a" },
        CurrentRoom = new MerchantRoom()
    });
    var second = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 42,
        AscensionLevel = 3,
        Seed = "seed-a",
        GameMode = "normal",
        StartTime = "2026-05-05T01:02:03Z",
        Character = new TestCharacter { Id = "ironclad" },
        Modifiers = new[] { "mod-a", "mod-b" },
        CurrentRoom = new MerchantRoom()
    });
    var changedSeed = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 7,
        AscensionLevel = 3,
        Seed = "seed-b",
        GameMode = "normal",
        StartTime = "2026-05-05T01:02:03Z",
        Character = new TestCharacter { Id = "ironclad" },
        Modifiers = new[] { "mod-b", "mod-a" },
        CurrentRoom = new MerchantRoom()
    });

    var firstIdentity = (IReadOnlyDictionary<string, object?>)first["logical_run_identity"]!;
    var secondIdentity = (IReadOnlyDictionary<string, object?>)second["logical_run_identity"]!;
    var changedIdentity = (IReadOnlyDictionary<string, object?>)changedSeed["logical_run_identity"]!;

    AssertEqual("complete", firstIdentity["status"], "complete logical identity status");
    AssertEqual(firstIdentity["logical_run_key"], secondIdentity["logical_run_key"],
        "floor and room changes should not affect logical run identity");
    AssertNotEqual(firstIdentity["logical_run_key"], changedIdentity["logical_run_key"],
        "seed changes should affect logical run identity");

    var fields = (IReadOnlyDictionary<string, object?>)firstIdentity["fields"]!;
    AssertTrue(!fields.ContainsKey("floor"), "logical run identity must not include floor");
    AssertTrue(!fields.ContainsKey("current_room"), "logical run identity must not include current room");
}

static void LogicalRunIdentityUsesRuntimeFallbackFields()
{
    var runManager = new TestRunManager(startTime: 123456789);
    var first = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 6,
        AscensionLevel = 0,
        GameMode = "Standard",
        Rng = new TestRunRng { StringSeed = "seed-from-rng", Seed = 42 },
        Players = new[] { new TestPlayer { Character = new TestCharacter { Id = "NECROBINDER" } } },
        Modifiers = Array.Empty<string>(),
        CurrentRoom = new MerchantRoom()
    }, runManager);
    var second = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 9,
        AscensionLevel = 0,
        GameMode = "Standard",
        Rng = new TestRunRng { StringSeed = "seed-from-rng", Seed = 42 },
        Players = new[] { new TestPlayer { Character = new TestCharacter { Id = "NECROBINDER" } } },
        Modifiers = Array.Empty<string>(),
        CurrentRoom = new MerchantRoom()
    }, runManager);
    var changedStartTime = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 6,
        AscensionLevel = 0,
        GameMode = "Standard",
        Rng = new TestRunRng { StringSeed = "seed-from-rng", Seed = 42 },
        Players = new[] { new TestPlayer { Character = new TestCharacter { Id = "NECROBINDER" } } },
        Modifiers = Array.Empty<string>(),
        CurrentRoom = new MerchantRoom()
    }, new TestRunManager(startTime: 987654321));
    var changedCharacter = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        Floor = 6,
        AscensionLevel = 0,
        GameMode = "Standard",
        Rng = new TestRunRng { StringSeed = "seed-from-rng", Seed = 42 },
        Players = new[] { new TestPlayer { Character = new TestCharacter { Id = "IRONCLAD" } } },
        Modifiers = Array.Empty<string>(),
        CurrentRoom = new MerchantRoom()
    }, runManager);

    var firstIdentity = (IReadOnlyDictionary<string, object?>)first["logical_run_identity"]!;
    var secondIdentity = (IReadOnlyDictionary<string, object?>)second["logical_run_identity"]!;
    var changedStartTimeIdentity = (IReadOnlyDictionary<string, object?>)changedStartTime["logical_run_identity"]!;
    var changedCharacterIdentity = (IReadOnlyDictionary<string, object?>)changedCharacter["logical_run_identity"]!;

    AssertEqual("complete", firstIdentity["status"], "fallback logical identity status");
    AssertEqual(firstIdentity["logical_run_key"], secondIdentity["logical_run_key"],
        "runtime fallback identity should ignore floor changes");
    var changedStartTimeFields =
        (IReadOnlyDictionary<string, object?>)changedStartTimeIdentity["fields"]!;
    AssertEqual("987654321", changedStartTimeFields["start_time"],
        "changed start_time should come from the changed RunManager _startTime");
    AssertNotEqual(firstIdentity["logical_run_key"], changedStartTimeIdentity["logical_run_key"],
        "runtime manager start time changes should affect logical run identity");
    AssertNotEqual(firstIdentity["logical_run_key"], changedCharacterIdentity["logical_run_key"],
        "player character changes should affect logical run identity");

    var fields = (IReadOnlyDictionary<string, object?>)firstIdentity["fields"]!;
    AssertEqual("seed-from-rng", fields["seed"], "seed should come from RunState.Rng.StringSeed");
    AssertEqual("NECROBINDER", fields["character"], "character should come from RunState.Players[].Character.Id");
    AssertEqual("123456789", fields["start_time"], "start_time should come from RunManager _startTime");
}

static void LogicalRunIdentityReportsIncompleteStableFields()
{
    var run = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        AscensionLevel = 1,
        Seed = "seed-a",
        GameMode = "normal",
        Character = new TestCharacter { Id = "ironclad" },
        Modifiers = new[] { "mod-a" }
    });

    var identity = (IReadOnlyDictionary<string, object?>)run["logical_run_identity"]!;
    AssertEqual("incomplete", identity["status"], "missing stable fields should be explicit");
    AssertTrue(!identity.ContainsKey("logical_run_key"), "incomplete logical identity should not guess a key");
    var missing = (IReadOnlyList<string>)identity["missing_fields"]!;
    AssertTrue(missing.Contains("start_time"), "start_time should be reported missing");
}

static void LogicalRunIdentityMarksZeroStartTimeAsDegraded()
{
    var run = StateSnapshotBuilder.BuildRunMetadataForTests(new TestRunState
    {
        AscensionLevel = 0,
        Seed = "20VBG069DW",
        GameMode = "standard",
        StartTime = "0",
        Character = new TestCharacter { Id = "SILENT" },
        Modifiers = Array.Empty<string>()
    });

    var identity = (IReadOnlyDictionary<string, object?>)run["logical_run_identity"]!;
    AssertEqual("degraded", identity["status"], "zero start_time should be degraded");
    AssertEqual("degraded", identity["identity_quality"], "identity quality");
    AssertEqual("start_time_zero_loaded_save_identity", identity["degraded_reason"], "degraded reason");
    AssertTrue(!identity.ContainsKey("logical_run_id"), "degraded identity should not become the effective logical run id");
    AssertTrue(identity.ContainsKey("observed_logical_run_id"), "degraded observed id should be preserved diagnostically");
    var degradedFields = (IReadOnlyList<string>)identity["degraded_fields"]!;
    AssertTrue(degradedFields.Contains("start_time"), "start_time should be the degraded field");
}

static void RunSupportDetectorDoesNotProbeGameSingletonsForTestDoubles()
{
    RunSupportResult support = RunSupportDetector.Inspect(new TestRunState
    {
        GameMode = "normal"
    });

    AssertTrue(support.IsSupported, "test double normal run should be supported");
    AssertEqual("single_player_normal", support.Mode, "test double support mode");
    AssertTrue(!support.Detected.ContainsKey("net_type"), "test doubles should not touch STS2 game singletons");
}

static void RecorderDelaysRunStartedUntilMatchingStableResume()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/combat_setup", "combat_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "matching delayed resume record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/run_loaded", records[1], "delayed resume load");
            AssertRecordType("lifecycle/branch_matched", records[2], "delayed resume branch match");
            AssertRecordType("lifecycle/combat_setup", records[3], "stable record after delayed resume");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("run_id").GetString()
                    == records[0].RootElement.GetProperty("run_id").GetString()),
                "same-process resume should preserve run_id");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "matching resume should not emit branch_forked");
            AssertEqual(true,
                records[1].RootElement.GetProperty("branch_match").GetProperty("matched").GetBoolean(),
                "delayed resume should match known state");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderResetsDelayedRunStartedWhenDecisionPreStateUnmatched()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-new"),
            TestSnapshotWithHash("combat", "state-after-new"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(
                writer,
                snapshotBuilder,
                new LegalActionBuilder(),
                NoopNativeSaveCapture.Instance);
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            var action = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(action);
            recorder.CompleteActionExecutorDecision(action);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "unmatched delayed new-run record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/run_started", records[1], "delayed new run started");
            AssertRecordType("decision/frame", records[2], "decision frame after delayed new run");

            string initialRunId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("initial run id missing");
            string newRunId = records[1].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("new run id missing");
            AssertNotEqual(initialRunId, newRunId, "unmatched stable snapshot should reset run_id");
            AssertEqual(newRunId, records[2].RootElement.GetProperty("run_id").GetString(),
                "decision frame should use delayed new run id");
            AssertEqual(1L, records[1].RootElement.GetProperty("local_sequence").GetInt64(),
                "delayed new run should reset local sequence");
            AssertEqual(2L, records[2].RootElement.GetProperty("local_sequence").GetInt64(),
                "decision frame should follow delayed run_started");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/run_loaded"),
                "unmatched delayed new run should not emit run_loaded");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "unmatched delayed new run should not emit branch_forked");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderKeepsPendingResumeAcrossUnmatchedLifecycleUntilMatchingDecision()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-start"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "lifecycle-unmatched"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            var firstAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(firstAction);
            recorder.CompleteActionExecutorDecision(firstAction);

            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/combat_setup", "combat_manager");

            var resumedAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(resumedAction);
            recorder.CompleteActionExecutorDecision(resumedAction);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(6, records.Length, "pending lifecycle then matching decision record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "known edge decision frame");
            AssertRecordType("lifecycle/combat_setup", records[2], "unmatched lifecycle while pending resume");
            AssertRecordType("lifecycle/run_loaded", records[3], "decision pre-state resolves delayed load");
            AssertRecordType("lifecycle/branch_matched", records[4], "decision pre-state matches branch");
            AssertRecordType("decision/frame", records[5], "matching resumed decision frame");

            string runId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("run id missing");
            AssertTrue(records.All(record => record.RootElement.GetProperty("run_id").GetString() == runId),
                "unmatched lifecycle must not reset the capture run id");
            AssertEqual(1, records.Count(record =>
                    record.RootElement.GetProperty("record_type").GetString() == "lifecycle/run_started"),
                "unmatched lifecycle must not emit delayed new run_started");
            AssertEqual("pending_unmatched_stable_snapshot",
                records[2].RootElement.GetProperty("details").GetProperty("resume_classification").GetString(),
                "unmatched lifecycle should mark pending resume classification");
            AssertEqual(true,
                records[3].RootElement.GetProperty("branch_match").GetProperty("matched").GetBoolean(),
                "later decision pre-state should match known branch state");
            AssertEqual("state-a",
                records[3].RootElement.GetProperty("branch_match").GetProperty("parent_canonical_state_hash").GetString(),
                "later decision pre-state should match the known decision parent");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "matching resumed decision should not emit branch_forked");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderResolvesDelayedEventResumeFromSettledLifecycleSnapshot()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("map", "run-start"),
            TestSnapshotWithHash("event", "event-state-a"),
            TestSnapshotWithHash("event", "event-state-a"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");

            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(7, records.Length, "settled event resume record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/room_entered_settled", records[1], "known settled event state");
            AssertRecordType("decision/context", records[2], "known settled event decision context");
            AssertRecordType("lifecycle/run_loaded", records[3], "settled event state resolves delayed load");
            AssertRecordType("lifecycle/branch_matched", records[4], "settled event state matches branch");
            AssertRecordType("lifecycle/room_entered_settled", records[5], "settled lifecycle record after delayed resume");
            AssertRecordType("decision/context", records[6], "settled lifecycle context after delayed resume");

            string runId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("run id missing");
            AssertTrue(records.All(record => record.RootElement.GetProperty("run_id").GetString() == runId),
                "settled event resume should preserve same-process run id");
            AssertEqual("event-state-a",
                records[3].RootElement.GetProperty("branch_match").GetProperty("parent_canonical_state_hash").GetString(),
                "run_loaded should match the indexed settled event state");
            AssertEqual(true,
                records[3].RootElement.GetProperty("branch_match").GetProperty("matched").GetBoolean(),
                "settled event resume should be matched");
            AssertEqual("lifecycle/room_entered_settled",
                records[3].RootElement.GetProperty("details").GetProperty("stable_record_source").GetString(),
                "run_loaded should name the safe settled classification source");
            AssertEqual("event",
                records[3].RootElement.GetProperty("state").GetProperty("state_type").GetString(),
                "run_loaded state should be the event settled snapshot");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "settled event resume should not emit branch_forked by itself");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderResolvesExplicitUnstableEventLoadFromSettledLifecycleSnapshot()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("map", "run-start"),
            TestSnapshotWithHash("event", "event-state-a"),
            TestSnapshotWithHash("room/unknown", "unstable-load-snapshot"),
            TestSnapshotWithHash("event", "event-state-a"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");

            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(7, records.Length, "explicit unstable settled event resume record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/room_entered_settled", records[1], "known settled event state");
            AssertRecordType("decision/context", records[2], "known settled event decision context");
            AssertRecordType("lifecycle/run_loaded", records[3], "settled event state resolves explicit load");
            AssertRecordType("lifecycle/branch_matched", records[4], "settled event state matches explicit load");
            AssertRecordType("lifecycle/room_entered_settled", records[5], "settled lifecycle record after explicit load");
            AssertRecordType("decision/context", records[6], "settled lifecycle context after explicit load");

            AssertEqual(1,
                records.Count(record => record.RootElement.GetProperty("record_type").GetString() == "lifecycle/run_loaded"),
                "unstable explicit load should wait for the safe settled snapshot before run_loaded");
            AssertEqual("run_manager.set_up_saved_single_player",
                records[3].RootElement.GetProperty("source").GetString(),
                "delayed explicit load should keep the explicit load source");
            AssertEqual("explicit_run_loaded_delayed_until_stable_snapshot",
                records[3].RootElement.GetProperty("details").GetProperty("classification").GetString(),
                "run_loaded should name the explicit delayed classification");
            AssertEqual("lifecycle/room_entered_settled",
                records[3].RootElement.GetProperty("details").GetProperty("stable_record_source").GetString(),
                "run_loaded should name the safe settled classification source");
            AssertEqual(true,
                records[3].RootElement.GetProperty("branch_match").GetProperty("matched").GetBoolean(),
                "settled event explicit load should be matched");
            AssertEqual("event-state-a",
                records[3].RootElement.GetProperty("branch_match").GetProperty("parent_canonical_state_hash").GetString(),
                "run_loaded should match the indexed settled event state");
            AssertEqual("event",
                records[3].RootElement.GetProperty("state").GetProperty("state_type").GetString(),
                "run_loaded state should be the event settled snapshot");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "settled explicit load should not emit branch_forked by itself");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderExplicitUnstableLoadResolvesUnmatchedActionPreStateAsUnknown()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("room/unknown", "unstable-load-snapshot"),
            TestSnapshotWithHash("combat", "state-from-unknown-parent"),
            TestSnapshotWithHash("combat", "state-after-unknown-parent"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");

            var action = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(action);
            recorder.CompleteActionExecutorDecision(action);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "explicit unstable unmatched action record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/run_loaded", records[1], "action pre-state resolves explicit unknown load");
            AssertRecordType("decision/frame", records[2], "decision frame after explicit unknown load");

            string runId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("run id missing");
            AssertTrue(records.All(record => record.RootElement.GetProperty("run_id").GetString() == runId),
                "explicit unknown load should preserve the capture run id");
            AssertEqual("run_manager.set_up_saved_single_player",
                records[1].RootElement.GetProperty("source").GetString(),
                "delayed unknown load should keep the explicit load source");
            AssertEqual("explicit_run_loaded_delayed_until_stable_snapshot",
                records[1].RootElement.GetProperty("details").GetProperty("classification").GetString(),
                "run_loaded should name the explicit delayed classification");
            AssertEqual("action_executor:pre",
                records[1].RootElement.GetProperty("details").GetProperty("stable_record_source").GetString(),
                "run_loaded should be resolved by the branch-comparable action pre-state");

            JsonElement branchMatch = records[1].RootElement.GetProperty("branch_match");
            AssertEqual(false, branchMatch.GetProperty("matched").GetBoolean(),
                "unmatched action pre-state should be explicit");
            AssertEqual("resume_state_not_found", branchMatch.GetProperty("reason").GetString(),
                "unmatched action pre-state reason");
            AssertEqual(JsonValueKind.Null,
                branchMatch.GetProperty("matched_node_id").ValueKind,
                "unmatched action pre-state should not fabricate matched node id");
            AssertEqual(JsonValueKind.Null,
                branchMatch.GetProperty("parent_canonical_state_hash").ValueKind,
                "unmatched action pre-state should not fabricate parent hash");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_matched"),
                "unmatched action pre-state should not emit branch_matched");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "unmatched action pre-state should not fork by itself");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderAbandonForcesFreshRunIdForNextRun()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithLogicalRun("combat", "necrobinder-state", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "ironclad-state", "logical-run-ironclad", "IRONCLAD"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunSuspendedOrCleanedUp("run_manager.abandon", new Dictionary<string, object?>
            {
                ["reason"] = "abandon"
            });
            recorder.RecordLifecycleSignal("lifecycle/act_entered", "run_manager");
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "abandon boundary record count");
            AssertEqual(2,
                records.Count(record => record.RootElement.GetProperty("record_type").GetString() == "lifecycle/run_started"),
                "fresh run should write a second run_started");
            AssertEqual(1,
                records.Count(record => record.RootElement.GetProperty("record_type").GetString() == "lifecycle/run_suspended"),
                "abandon should preserve the suspended signal");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/act_entered"),
                "pre-run lifecycle signals after abandon should be suppressed");

            JsonElement necrobinder = records
                .Select(record => record.RootElement)
                .Single(root => root.GetProperty("record_type").GetString() == "lifecycle/run_started"
                    && root.GetProperty("logical_run_id").GetString() == "logical-run-necrobinder");
            JsonElement ironclad = records
                .Select(record => record.RootElement)
                .Single(root => root.GetProperty("record_type").GetString() == "lifecycle/run_started"
                    && root.GetProperty("logical_run_id").GetString() == "logical-run-ironclad");
            string necrobinderRunId = necrobinder.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("necrobinder run id missing");
            string ironcladRunId = ironclad.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("ironclad run id missing");

            AssertNotEqual(necrobinderRunId, ironcladRunId,
                "new run after abandon must not reuse the abandoned capture run id");
            AssertEqual(1L, ironclad.GetProperty("local_sequence").GetInt64(),
                "fresh run after abandon should reset local sequence");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderPreservesBranchMatchingAcrossNonAbandonCleanupRunStarted()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithLogicalRun("combat", "state-a", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "state-a", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "state-b", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "state-a", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "state-a", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "state-b", "logical-run-necrobinder", "NECROBINDER"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            var firstAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(firstAction);
            recorder.CompleteActionExecutorDecision(firstAction);

            recorder.OnRunSuspendedOrCleanedUp("run_manager.cleanup", new Dictionary<string, object?>
            {
                ["reason"] = "load_cleanup"
            });
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            var resumedAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(resumedAction);
            recorder.CompleteActionExecutorDecision(resumedAction);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(6, records.Length, "non-abandon cleanup resume record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "known decision edge");
            AssertRecordType("lifecycle/run_suspended", records[2], "non-abandon cleanup signal");
            AssertRecordType("lifecycle/run_loaded", records[3], "run started after cleanup resolves same run");
            AssertRecordType("lifecycle/branch_matched", records[4], "run started after cleanup matches branch");
            AssertRecordType("decision/frame", records[5], "resumed decision frame");

            string initialRunId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("initial run id missing");
            string resumedRunId = records[3].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("resumed run id missing");
            AssertNotEqual(initialRunId, resumedRunId,
                "non-abandon cleanup should use a fresh capture segment after reload");
            AssertEqual(resumedRunId, records[5].RootElement.GetProperty("run_id").GetString(),
                "resumed decision should use the fresh capture segment");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("logical_run_id").GetString() == "logical-run-necrobinder"),
                "same logical run id should continue across non-abandon cleanup reload");
            AssertEqual(true,
                records[4].RootElement.GetProperty("branch_match").GetProperty("matched").GetBoolean(),
                "cleanup reload should match the preserved branch state");
            AssertEqual(true,
                records[5].RootElement.GetProperty("branch_decision").GetProperty("trajectory_replayed").GetBoolean(),
                "resumed same decision should replay the preserved known edge");
            AssertEqual(records[1].RootElement.GetProperty("decision_frame_id").GetString(),
                records[5].RootElement.GetProperty("branch_decision").GetProperty("matched_decision_frame_id").GetString(),
                "resumed decision should point back to the original matched frame");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderRotatesRunIdWhenStableLogicalIdentityChanges()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithLogicalRun("combat", "necrobinder-state", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "ironclad-state", "logical-run-ironclad", "IRONCLAD"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "logical identity rotation record count");
            JsonElement[] runStarted = records
                .Select(record => record.RootElement)
                .Where(root => root.GetProperty("record_type").GetString() == "lifecycle/run_started")
                .ToArray();
            AssertEqual(2, runStarted.Length, "identity change should emit a new run_started");

            JsonElement necrobinder = runStarted.Single(root =>
                root.GetProperty("logical_run_id").GetString() == "logical-run-necrobinder");
            JsonElement ironclad = runStarted.Single(root =>
                root.GetProperty("logical_run_id").GetString() == "logical-run-ironclad");
            string necrobinderRunId = necrobinder.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("necrobinder run id missing");
            string ironcladRunId = ironclad.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("ironclad run id missing");

            AssertNotEqual(necrobinderRunId, ironcladRunId,
                "complete logical identity change should rotate capture run id");
            AssertEqual("logical_run_identity_changed",
                ironclad.GetProperty("details").GetProperty("classification").GetString(),
                "rotated run_started should explain the identity boundary");

            JsonElement lifecycle = records
                .Select(record => record.RootElement)
                .Single(root => root.GetProperty("record_type").GetString() == "lifecycle/room_entered_settled");
            AssertEqual(ironcladRunId, lifecycle.GetProperty("run_id").GetString(),
                "stable lifecycle record should use rotated run id");
            AssertEqual("logical-run-ironclad", lifecycle.GetProperty("logical_run_id").GetString(),
                "stable lifecycle record should use current logical id");
            AssertEqual(2L, lifecycle.GetProperty("local_sequence").GetInt64(),
                "stable lifecycle record should follow rotated run_started");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderRotatesRunIdWhenPostLoadRunStartedIdentityChanges()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithLogicalRun("combat", "necrobinder-state", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "necrobinder-state", "logical-run-necrobinder", "NECROBINDER"),
            TestSnapshotWithLogicalRun("combat", "ironclad-state", "logical-run-ironclad", "IRONCLAD"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "post-load identity rotation record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/run_loaded", records[1], "same logical run load");
            AssertRecordType("lifecycle/branch_matched", records[2], "same logical run branch match");
            AssertRecordType("lifecycle/run_started", records[3], "changed logical run should start fresh segment");

            string necrobinderRunId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("necrobinder run id missing");
            string ironcladRunId = records[3].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("ironclad run id missing");

            AssertNotEqual(necrobinderRunId, ironcladRunId,
                "post-load logical identity change should rotate capture run id");
            AssertEqual("logical-run-ironclad", records[3].RootElement.GetProperty("logical_run_id").GetString(),
                "fresh post-load segment should carry the changed logical id");
            AssertEqual("logical_run_identity_changed",
                records[3].RootElement.GetProperty("details").GetProperty("classification").GetString(),
                "fresh post-load segment should explain the identity boundary");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/run_started_after_load"),
                "changed logical identity must not be written as run_started_after_load");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderReconcilesDegradedLoadedSaveIdentityToRecentStableRun()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithLogicalRun("combat", "stable-state", "logical-run-stable", "SILENT"),
            TestSnapshotWithDegradedStartTime("combat", "stable-state", "logical-run-stable", "SILENT"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunSuspendedOrCleanedUp("run_manager.cleanup", new Dictionary<string, object?>
            {
                ["reason"] = "cleanup_before_saved_run_reload"
            });
            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement runLoaded = records
                .Select(record => record.RootElement)
                .Single(root => root.GetProperty("record_type").GetString() == "lifecycle/run_loaded");

            AssertEqual("logical-run-stable", runLoaded.GetProperty("logical_run_id").GetString(),
                "degraded load should use the recent stable logical run id");
            JsonElement identity = runLoaded.GetProperty("logical_run_identity");
            AssertEqual("reconciled_degraded_start_time", identity.GetProperty("identity_quality").GetString(),
                "effective identity should expose reconciliation quality");
            JsonElement reconciliation = identity.GetProperty("identity_reconciliation");
            AssertEqual("reconciled", reconciliation.GetProperty("status").GetString(), "reconciliation status");
            AssertEqual(
                "degraded_start_time_zero_matches_recent_stable_identity",
                reconciliation.GetProperty("reason").GetString(),
                "reconciliation reason");
            AssertEqual(
                "degraded",
                reconciliation.GetProperty("observed_identity").GetProperty("status").GetString(),
                "observed degraded identity should be preserved");
            AssertEqual(
                "0",
                reconciliation.GetProperty("observed_identity").GetProperty("fields").GetProperty("start_time").GetString(),
                "observed degraded start_time should be preserved");
            AssertTrue(records
                    .Select(record => record.RootElement)
                    .Where(root => root.TryGetProperty("logical_run_id", out _))
                    .All(root => root.GetProperty("logical_run_id").GetString() == "logical-run-stable"),
                "all identity-known records should stay on the stable logical run");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void SettledShopLifecycleEmitsNonCombatDecisionContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var availableCardEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "card-a", Title = "Useful Card" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var unaffordableRelicEntry = new TestMerchantEntry
        {
            Relic = new TestShopItem { Id = "relic-a", Title = "Costly Relic" },
            Cost = 240,
            IsStocked = true,
            EnoughGold = false,
            Used = false
        };
        var unstockedPotionEntry = new TestMerchantEntry
        {
            Potion = new TestShopItem { Id = "potion-a", Title = "Gone Potion" },
            Cost = 45,
            IsStocked = false,
            EnoughGold = true,
            Used = false
        };
        var usedRemovalEntry = new TestMerchantEntry
        {
            Name = "Card Removal",
            Cost = 75,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    CardEntries = new object?[] { availableCardEntry },
                    RelicEntries = new object?[] { unaffordableRelicEntry },
                    PotionEntries = new object?[] { unstockedPotionEntry },
                    CardRemovalEntry = usedRemovalEntry
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-context");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "settled shop context record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "settled shop lifecycle record");
            AssertRecordType("decision/context", records[1], "settled shop decision context");

            JsonElement context = records[1].RootElement;
            AssertTrue(context.TryGetProperty("decision_context_id", out _), "context id should be present");
            AssertEqual("run_manager", context.GetProperty("source").GetString(), "context source");
            AssertEqual("lifecycle/room_entered_settled",
                context.GetProperty("context_source").GetString(),
                "context source record type");
            AssertEqual("stable_snapshot_context_no_selected_action_no_post_state",
                context.GetProperty("capture_policy").GetString(),
                "decision context capture policy");
            AssertEqual("visible_pre_decision",
                context.GetProperty("pre_state").GetProperty("role").GetString(),
                "context pre-state role");
            AssertEqual("shop",
                context.GetProperty("pre_state").GetProperty("state_type").GetString(),
                "context pre-state type");
            AssertEqual("shop-context-state",
                context.GetProperty("hashes").GetProperty("pre_canonical_state_hash").GetString(),
                "context pre-state hash");

            JsonElement legalActions = context.GetProperty("legal_actions");
            AssertEqual(1, legalActions.GetProperty("action_count").GetInt32(), "shop context legal action count");
            JsonElement action = legalActions.GetProperty("actions").EnumerateArray().Single();
            AssertEqual("buy_shop_card", action.GetProperty("action_type").GetString(), "shop context legal action type");
            AssertEqual("card-a", action.GetProperty("card_id").GetString(), "shop context legal card id");

            JsonElement shopOffers = context.GetProperty("shop_offers");
            AssertEqual("visible_pre_decision",
                shopOffers.GetProperty("role").GetString(),
                "shop offers role");
            AssertEqual(4, shopOffers.GetProperty("offer_count").GetInt32(), "shop offer count");
            JsonElement[] offers = shopOffers.GetProperty("offers").EnumerateArray().ToArray();
            AssertEqual(4, offers.Length, "shop offers array count");

            JsonElement cardOffer = offers.Single(offer =>
                offer.GetProperty("action_type").GetString() == "buy_shop_card"
                && offer.GetProperty("id").GetString() == "card-a");
            AssertEqual(true, cardOffer.GetProperty("can_buy").GetBoolean(), "available card offer can buy");
            AssertEqual("available", cardOffer.GetProperty("availability").GetString(), "available card offer availability");
            AssertEqual("card-a", cardOffer.GetProperty("card_id").GetString(), "card offer card id");
            AssertMissingProperty(cardOffer, "name", "shop offers should not include display text");

            JsonElement relicOffer = offers.Single(offer =>
                offer.GetProperty("action_type").GetString() == "buy_shop_relic");
            AssertEqual("relic-a", relicOffer.GetProperty("relic_id").GetString(), "relic offer relic id");
            AssertEqual(false, relicOffer.GetProperty("can_buy").GetBoolean(), "unaffordable relic offer can buy");
            AssertEqual(false, relicOffer.GetProperty("enough_gold").GetBoolean(), "unaffordable relic enough gold");
            AssertEqual("insufficient_gold", relicOffer.GetProperty("availability").GetString(),
                "unaffordable relic offer availability");

            JsonElement potionOffer = offers.Single(offer =>
                offer.GetProperty("action_type").GetString() == "buy_shop_potion");
            AssertEqual("potion-a", potionOffer.GetProperty("potion_id").GetString(), "potion offer potion id");
            AssertEqual(false, potionOffer.GetProperty("can_buy").GetBoolean(), "unstocked potion offer can buy");
            AssertEqual(false, potionOffer.GetProperty("is_stocked").GetBoolean(), "unstocked potion stocked flag");
            AssertEqual("not_stocked", potionOffer.GetProperty("availability").GetString(),
                "unstocked potion offer availability");

            JsonElement removalOffer = offers.Single(offer =>
                offer.GetProperty("action_type").GetString() == "remove_card_at_shop");
            AssertEqual("card_removal", removalOffer.GetProperty("removal_id").GetString(), "removal offer id");
            AssertEqual(false, removalOffer.GetProperty("can_buy").GetBoolean(), "used removal offer can buy");
            AssertEqual(true, removalOffer.GetProperty("used").GetBoolean(), "used removal offer used flag");
            AssertEqual("used", removalOffer.GetProperty("availability").GetString(), "used removal offer availability");

            AssertTrue(!legalActions.GetProperty("actions").EnumerateArray().Any(legalAction =>
                    legalAction.GetProperty("action_type").GetString() is "buy_shop_relic" or "buy_shop_potion"
                    or "remove_card_at_shop"),
                "unavailable visible shop offers should not become legal actions");
            AssertEqual(64,
                context.GetProperty("hashes").GetProperty("shop_offers_canonical_hash").GetString()?.Length,
                "shop offers canonical hash length");
            AssertTrue(context.GetProperty("role_visibility").EnumerateArray().Any(role => role.GetString() == "shop_offers"),
                "shop offers should be listed in role visibility");
            AssertMissingProperty(context, "selected_action", "context should not include selected action");
            AssertMissingProperty(context, "post_state", "context should not include post-state");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RewardRuntimeCacheEmitsRewardDecisionContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    RewardChoiceCache.Shared.Clear();
    try
    {
        var goldReward = new TestGoldReward { Amount = 17 };
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" }, Type = "Attack", Rarity = "Common" }
            },
            CanSkip = true,
            CanReroll = true
        };
        RewardChoiceCache.Shared.CaptureRewardsSet(new TestRewardsSet
        {
            Rewards = new object?[] { goldReward, cardReward },
            DisallowSkipping = false,
            Room = new MerchantRoom()
        }, "test.rewards_set.generated");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("rewards", "reward-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-reward-context");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "reward context record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "reward lifecycle record");
            AssertRecordType("decision/context", records[1], "reward decision context");

            JsonElement context = records[1].RootElement;
            AssertEqual("rewards",
                context.GetProperty("pre_state").GetProperty("state_type").GetString(),
                "reward context state type");
            JsonElement actions = context.GetProperty("legal_actions").GetProperty("actions");
            AssertEqual(3, actions.GetArrayLength(), "reward legal action count");

            JsonElement goldAction = actions.EnumerateArray().First(action =>
                action.GetProperty("reward_type").GetString() == "gold");
            AssertEqual("claim_reward", goldAction.GetProperty("action_type").GetString(), "gold reward action type");
            AssertEqual(0, goldAction.GetProperty("reward_index").GetInt32(), "gold reward index");
            AssertEqual(17, goldAction.GetProperty("gold_amount").GetInt32(), "gold reward amount");
            AssertEqual("gold", goldAction.GetProperty("reward_id").GetString(), "gold reward id");
            AssertEqual("typed_reward_runtime_cache",
                goldAction.GetProperty("projection_policy").GetString(),
                "gold reward projection policy");

            JsonElement cardRewardAction = actions.EnumerateArray().First(action =>
                action.GetProperty("reward_type").GetString() == "card");
            AssertEqual("claim_reward", cardRewardAction.GetProperty("action_type").GetString(),
                "card reward open action type");
            AssertEqual(1, cardRewardAction.GetProperty("reward_index").GetInt32(), "card reward index");
            AssertEqual(1, cardRewardAction.GetProperty("card_count").GetInt32(), "card option count");
            AssertEqual(true, cardRewardAction.GetProperty("can_skip").GetBoolean(), "card reward skip flag");
            AssertEqual(true, cardRewardAction.GetProperty("can_reroll").GetBoolean(), "card reward reroll flag");

            JsonElement skipAction = actions.EnumerateArray().Single(action =>
                action.GetProperty("action_type").GetString() == "skip_reward");
            AssertEqual("available", skipAction.GetProperty("availability").GetString(), "skip reward availability");
            AssertEqual(JsonValueKind.Null, skipAction.GetProperty("reward_index").ValueKind, "skip reward index");
            AssertMissingProperty(context, "selected_action", "reward context should not include selected action");
            AssertMissingProperty(context, "post_state", "reward context should not include post-state");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RewardGenerationSchedulesSettledRewardDecisionContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    RewardChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var goldReward = new TestGoldReward { Amount = 21 };
        var rewardsSet = new TestRewardsSet
        {
            Rewards = new object?[] { goldReward },
            DisallowSkipping = false
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "reward-context-too-early"),
            TestSnapshotWithHash("rewards", "reward-generated-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-reward-generated-context");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnRewardsGeneratedFromPatch(
                "runtime.rewards_set.generate_without_offering",
                rewardsSet,
                rewardsSet.Rewards);

            AssertEqual(1, scheduled.Count, "reward generation should schedule first settled context attempt");
            AssertEqual(0, snapshotBuilder.CaptureCount,
                "reward generation callback should not synchronously capture a snapshot");
            AssertEqual(0, GetPendingDecisionCount(recorder),
                "reward generation callback should not create a pending decision");

            scheduled[0].Callback();
            AssertEqual(2, scheduled.Count,
                "non-reward first settled snapshot should schedule a bounded retry");
            AssertEqual(1, snapshotBuilder.CaptureCount,
                "first settled attempt should capture after the callback");

            scheduled[1].Callback();
            AssertEqual(2, snapshotBuilder.CaptureCount,
                "second settled attempt should capture the reward surface");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(1, records.Length, "reward generation should emit one decision context");
            AssertRecordType("decision/context", records[0], "reward generation context record");
            JsonElement context = records[0].RootElement;
            AssertEqual("runtime.rewards_set.generate_without_offering",
                context.GetProperty("decision_source").GetString(),
                "reward generation context source");
            AssertEqual("runtime.rewards_set.generate_without_offering.reward_context_settled.retry_2",
                context.GetProperty("source").GetString(),
                "reward generation settled callback source");
            AssertEqual("stable_snapshot_context_no_selected_action_no_post_state",
                context.GetProperty("capture_policy").GetString(),
                "reward generation context capture policy");
            AssertEqual(2,
                context.GetProperty("details").GetProperty("settle_attempt").GetInt32(),
                "reward generation should record settled retry attempt");
            AssertEqual("rewards",
                context.GetProperty("pre_state").GetProperty("state_type").GetString(),
                "reward generation context state type");
            AssertMissingProperty(context, "selected_action", "reward generation context should not include selected action");
            AssertMissingProperty(context, "post_state", "reward generation context should not include post-state");

            JsonElement actions = context.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(actions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "claim_reward"
                    && action.GetProperty("reward_type").GetString() == "gold"
                    && action.GetProperty("gold_amount").GetInt32() == 21),
                "reward generation context should include cached gold reward action");
            AssertTrue(actions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "skip_reward"),
                "reward generation context should include typed skip action when skipping is allowed");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RewardCacheSurvivesRoomEnteredForScheduledContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    RewardChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var goldReward = new TestGoldReward { Amount = 34 };
        var rewardsSet = new TestRewardsSet
        {
            Rewards = new object?[] { goldReward },
            DisallowSkipping = false
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("rewards", "reward-context-after-room-entered"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-reward-cache-room-entered");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnRewardsGeneratedFromPatch(
                "runtime.rewards_set.generate_without_offering",
                rewardsSet,
                rewardsSet.Rewards);
            AssertEqual(1, scheduled.Count, "reward generation should schedule reward context");

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnRoomEntered");
            AssertEqual(2, scheduled.Count, "room entered should schedule settled lifecycle without clearing reward context");

            scheduled[0].Callback();

            Dictionary<string, object?> rewardAction = RewardChoiceCache.Shared.BuildLegalActions("rewards")!
                .Single(action => Equals(action["action_type"], "claim_reward")
                    && Equals(action["reward_type"], "gold"));
            AssertEqual(34, rewardAction["gold_amount"],
                "reward cache should remain available in the same reward room for matching selection signals");

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnRoomEntered");
            AssertEqual(null, RewardChoiceCache.Shared.BuildLegalActions("rewards"),
                "next room-entered should clear reward cache to avoid stale reward actions");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertTrue(records.Any(record =>
                    record.RootElement.GetProperty("record_type").GetString() == "decision/context"),
                "scheduled reward context should be recorded after room-entered clear path");

            JsonElement context = records.First(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/context").RootElement;
            JsonElement actions = context.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(actions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "claim_reward"
                    && action.GetProperty("reward_type").GetString() == "gold"
                    && action.GetProperty("gold_amount").GetInt32() == 34),
                "reward context should use generated reward cache instead of unavailable marker");
            AssertTrue(!actions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "rewards_typed_builder_unavailable"),
                "reward context should not degrade to unavailable marker after room-entered");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RewardUiSignalReferencesRewardDecisionContextWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var goldReward = new TestGoldReward { Amount = 17 };
        RewardChoiceCache.Shared.CaptureRewardsSet(new TestRewardsSet
        {
            Rewards = new object?[] { goldReward }
        }, "test.rewards_set.generated");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("rewards", "reward-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-reward-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Rewards.Reward.OnSelectWrapper",
                goldReward,
                Array.Empty<object?>());
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "reward context plus signal record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "reward lifecycle record");
            AssertRecordType("decision/context", records[1], "reward decision context");
            AssertRecordType("decision/ui_signal", records[2], "reward signal record");

            string contextId = records[1].RootElement.GetProperty("decision_context_id").GetString()
                ?? throw new InvalidOperationException("context id missing");
            JsonElement signal = records[2].RootElement;
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                signal.GetProperty("capture_policy").GetString(),
                "reward signal capture policy");
            AssertMissingProperty(signal, "pre_state", "reward signal should not capture pre-state");
            AssertMissingProperty(signal, "post_state", "reward signal should not capture post-state");
            AssertMissingProperty(signal, "legal_actions", "reward signal should not build legal actions");
            AssertMissingProperty(signal, "selected_action", "reward signal should not include selected action");

            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("claim_reward", metadata.GetProperty("action_type").GetString(), "reward signal action type");
            AssertEqual(0, metadata.GetProperty("reward_index").GetInt32(), "reward signal index");
            AssertEqual("gold", metadata.GetProperty("reward_type").GetString(), "reward signal type");
            JsonElement reference = signal.GetProperty("decision_context");
            AssertEqual(contextId, reference.GetProperty("decision_context_id").GetString(),
                "reward signal should reference prior context");
            AssertEqual("rewards", reference.GetProperty("state_type").GetString(), "reward context state type");
            JsonElement match = reference.GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(), "reward selected action should match context");
            AssertEqual(0, match.GetProperty("matched_legal_action_index").GetInt32(), "reward matched index");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardCacheEmitsLegalActionsAndSignalsStaySignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                new TestCard
                {
                    Id = new TestModelId { Entry = "STRIKE_PLUS" },
                    Type = "Attack",
                    Rarity = "Common",
                    IsUpgraded = true,
                    EnergyCost = new TestEnergyCost { Amount = 1 },
                    TargetType = "AnyEnemy"
                },
                new TestCard
                {
                    Id = new TestModelId { Entry = "DEFEND_PLUS" },
                    Type = "Skill",
                    Rarity = "Common",
                    IsUpgraded = true,
                    EnergyCost = new TestEnergyCost { Amount = 1 },
                    TargetType = "Self"
                }
            },
            CanSkip = true,
            CanReroll = true
        };
        RewardChoiceCache.Shared.CaptureRewardsSet(new TestRewardsSet
        {
            Rewards = new object?[] { cardReward }
        }, "test.rewards_set.generated");
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.selected");

        var directActions = new LegalActionBuilder().Build(TestSnapshot("card_reward"), runState: null, localPlayer: null);
        AssertEqual(4, directActions.Count, "card reward direct legal action count");
        AssertTrue(directActions.Any(action => Equals(action["action_type"], "choose_reward_card")
            && Equals(action["card_id"], "STRIKE_PLUS")
            && Equals(action["card_index"], 0)), "first card reward action should be present");
        AssertTrue(directActions.Any(action => Equals(action["action_type"], "skip_card_reward")
            && Equals(action["alternative_id"], "Skip")), "skip card reward action should be present");
        AssertTrue(directActions.Any(action => Equals(action["action_type"], "reroll_card_reward")
            && Equals(action["alternative_id"], "REROLL")), "reroll card reward action should be present");
        AssertTrue(!directActions.Any(action => Equals(action["action_type"], "card_reward_typed_builder_unavailable")),
            "card reward cache should replace unavailable marker");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "card-reward-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.cards_selected",
                new ThrowingProjectionObject(),
                Array.Empty<object?>());
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "card reward context plus signal record count");
            AssertRecordType("decision/context", records[1], "card reward decision context");
            AssertRecordType("decision/ui_signal", records[2], "card reward signal record");

            JsonElement contextActions = records[1].RootElement
                .GetProperty("legal_actions")
                .GetProperty("actions");
            AssertTrue(contextActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "choose_reward_card"
                    && action.GetProperty("card_id").GetString() == "STRIKE_PLUS"),
                "card reward context should include typed card choice");

            JsonElement signal = records[2].RootElement;
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                signal.GetProperty("capture_policy").GetString(),
                "card reward signal capture policy");
            AssertMissingProperty(signal, "pre_state", "card reward signal should not capture pre-state");
            AssertMissingProperty(signal, "post_state", "card reward signal should not capture post-state");
            AssertMissingProperty(signal, "legal_actions", "card reward signal should not build legal actions");
            AssertMissingProperty(signal, "selected_action", "card reward signal should not include selected action");
            AssertEqual("card_reward",
                signal.GetProperty("decision_context").GetProperty("state_type").GetString(),
                "card reward signal should reference prior card reward context");
            JsonElement match = signal.GetProperty("decision_context").GetProperty("selected_action_match");
            AssertEqual(false, match.GetProperty("matched").GetBoolean(),
                "CardsSelected await signal has no selected card identity to match");
            AssertEqual("selected_action_normalized_typed_action_key_unavailable",
                match.GetProperty("reason").GetString(),
                "card reward await signal should explain missing typed action identity");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardSelectedCardSignalClosesCardRewardContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var selected = new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } };
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                selected,
                new TestCard { Id = new TestModelId { Entry = "DEFEND_PLUS" } }
            }
        };
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.selected");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "card-reward-close-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-close");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.cards_selected",
                null,
                new object?[] { selected });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement signal = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/ui_signal").RootElement;
            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("choose_reward_card", metadata.GetProperty("action_type").GetString(),
                "card reward selected-card action type");
            AssertEqual("STRIKE_PLUS", metadata.GetProperty("card_id").GetString(),
                "card reward selected-card id");
            AssertEqual("card_reward", signal.GetProperty("decision_context").GetProperty("state_type").GetString(),
                "selected card signal should close card_reward surface");
            AssertEqual(true,
                signal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "selected card signal should match card_reward legal action");
            AssertEqual(true,
                signal.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "selected card signal should be trainable closure");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardSelectCardHolderSignalClosesCardRewardContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var selected = new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } };
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                selected,
                new TestCard { Id = new TestModelId { Entry = "DEFEND_PLUS" } }
            }
        };
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.select_card_holder");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "card-reward-holder-close-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-holder-close");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen.SelectCard",
                null,
                new object?[] { new TestCardHolder(selected) });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement signal = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/ui_signal").RootElement;
            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("choose_reward_card", metadata.GetProperty("action_type").GetString(),
                "SelectCard holder signal action type");
            AssertEqual("STRIKE_PLUS", metadata.GetProperty("card_id").GetString(),
                "SelectCard holder signal should unwrap NCardHolder.CardModel");
            AssertEqual("matched_card_reward_card_id", metadata.GetProperty("selection_identity_status").GetString(),
                "SelectCard holder signal identity status");
            AssertEqual(true,
                signal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "SelectCard holder signal should match card_reward legal action");
            AssertEqual(true,
                signal.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "SelectCard holder signal should be trainable");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardScreenSelectedCardsCloseCardRewardContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var selected = new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } };
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                selected,
                new TestCard { Id = new TestModelId { Entry = "DEFEND_PLUS" } }
            }
        };
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.screen_selected");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "card-reward-screen-close-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-screen-close");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen.CardsSelected",
                new TestCardRewardSelectionScreen(new object?[] { selected }),
                Array.Empty<object?>());
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement signal = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/ui_signal").RootElement;
            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("card_reward", metadata.GetProperty("state_type").GetString(),
                "card reward screen signal should keep explicit state type");
            AssertEqual("choose_reward_card", metadata.GetProperty("action_type").GetString(),
                "card reward screen selected-card action type");
            AssertEqual("STRIKE_PLUS", metadata.GetProperty("card_id").GetString(),
                "card reward screen selected-card id");
            AssertEqual("card_reward", signal.GetProperty("decision_context").GetProperty("state_type").GetString(),
                "screen selected card signal should attach to card_reward context");
            AssertEqual(true,
                signal.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "screen selected card signal should be trainable");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardImmediateSelectedSignalCreatesContextBeforeScheduledCallback()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    RewardChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var selected = new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } };
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                selected,
                new TestCard { Id = new TestModelId { Entry = "DEFEND_PLUS" } }
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("card_reward", "card-reward-scheduled-context"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-immediate-close");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnCardRewardOpenedFromPatch("runtime.card_reward.on_select", cardReward);
            AssertEqual(1, scheduled.Count, "card reward open should schedule settled context");
            AssertEqual(0, snapshotBuilder.CaptureCount,
                "card reward opening callback should not synchronously capture");

            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.cards_selected",
                null,
                new object?[] { selected });
            AssertEqual(0, snapshotBuilder.CaptureCount,
                "selection signal should create cached context without immediate snapshot capture");

            scheduled[0].Callback();
            AssertEqual(1, snapshotBuilder.CaptureCount,
                "later scheduled card reward context should still be harmless");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "immediate card reward context, signal, and scheduled context");
            AssertRecordType("decision/context", records[0], "immediate card reward context");
            AssertRecordType("decision/ui_signal", records[1], "card reward selected signal");
            AssertRecordType("decision/context", records[2], "scheduled card reward context");

            string contextId = records[0].RootElement.GetProperty("decision_context_id").GetString()
                ?? throw new InvalidOperationException("immediate context id missing");
            JsonElement signal = records[1].RootElement;
            JsonElement decisionContext = signal.GetProperty("decision_context");
            AssertEqual(contextId, decisionContext.GetProperty("decision_context_id").GetString(),
                "selected signal should reference immediate context");
            AssertEqual("runtime.card_reward.selection_signal_immediate",
                records[0].RootElement.GetProperty("decision_source").GetString(),
                "immediate context source");
            AssertEqual("cached_runtime_context_no_selected_action_no_post_state",
                records[0].RootElement.GetProperty("capture_policy").GetString(),
                "immediate context should be backed by the cached card reward surface");
            AssertEqual("ui.card_reward.cards_selected.card_reward_context_immediate",
                records[0].RootElement.GetProperty("source").GetString(),
                "immediate context callback source");
            AssertEqual(true,
                decisionContext.GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "selected signal should match immediate card reward legal action");
            AssertEqual(true,
                signal.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "selected signal should be trainable even before scheduled context");

            TelemetryUploadRunStatus run = TelemetryUploadStatusReader.Build(directory, maxRuns: 10).Runs.Single();
            TelemetryNonCombatMatchQuality quality = run.NonCombatMatchQuality.Single(item => item.Surface == "card_reward");
            AssertEqual(2, quality.ContextRecords, "card reward aggregate context count");
            AssertEqual(1, quality.SignalRecords, "card reward aggregate signal count");
            AssertEqual(1, quality.MatchedSignals, "card reward aggregate matched count");
            AssertEqual(1, quality.TrainableClosedChoices, "card reward aggregate trainable count");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardImmediateSignalWithoutIdentityReferencesContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    RewardChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } }
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "card-reward-immediate-no-identity"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-immediate-no-identity");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnCardRewardOpenedFromPatch("runtime.card_reward.on_select", cardReward);
            AssertEqual(1, scheduled.Count, "card reward open should schedule settled context");

            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.cards_selected",
                null,
                Array.Empty<object?>());
            AssertEqual(0, snapshotBuilder.CaptureCount,
                "no-identity signal should still use cached context without snapshot capture");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "immediate context plus no-identity signal");
            AssertRecordType("decision/context", records[0], "immediate no-identity card reward context");
            AssertRecordType("decision/ui_signal", records[1], "card reward no-identity signal");

            JsonElement decisionContext = records[1].RootElement.GetProperty("decision_context");
            AssertEqual("latest_context_by_surface_resolved",
                decisionContext.GetProperty("context_reference_status").GetString(),
                "no-identity signal should reference immediate card reward context");
            JsonElement match = decisionContext.GetProperty("selected_action_match");
            AssertEqual(false, match.GetProperty("matched").GetBoolean(),
                "no-identity signal should remain unmatched");
            AssertEqual("selected_action_normalized_typed_action_key_unavailable",
                match.GetProperty("reason").GetString(),
                "no-identity signal should explain missing selected action identity");
            AssertEqual("unmatched",
                records[1].RootElement.GetProperty("non_combat_closure").GetProperty("selected_action_match_status").GetString(),
                "no-identity closure status");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RewardsCardRewardActionLinksToChildCardRewardContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    RewardChoiceCache.Shared.Clear();
    try
    {
        var cardReward = new TestCardReward
        {
            Cards = new object?[] { new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } } }
        };
        RewardChoiceCache.Shared.CaptureRewardsSet(new TestRewardsSet
        {
            Rewards = new object?[] { cardReward }
        }, "test.rewards_set.generated");
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.opened");

        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("rewards", "rewards-parent-state"),
            TestSnapshotWithHash("card_reward", "card-reward-child-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-reward-link");
            recorder.RecordDecisionContextIfCurrentSurface("test.rewards.context", "test.rewards.context", new[] { "rewards" });
            recorder.RecordDecisionContextIfCurrentSurface("test.card_reward.context", "test.card_reward.context", new[] { "card_reward" });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement rewardsContext = records[0].RootElement;
            JsonElement cardRewardAction = rewardsContext.GetProperty("legal_actions").GetProperty("actions")
                .EnumerateArray()
                .Single(action => action.TryGetProperty("reward_id", out JsonElement rewardId)
                    && rewardId.GetString() == "card_reward");
            AssertEqual("card_reward", cardRewardAction.GetProperty("child_decision_surface").GetString(),
                "card reward claim should name child surface");

            JsonElement childContext = records[1].RootElement;
            JsonElement link = childContext.GetProperty("parent_decision_context");
            AssertEqual(rewardsContext.GetProperty("decision_context_id").GetString(),
                link.GetProperty("parent_decision_context_id").GetString(),
                "child card_reward context should link to rewards context");
            AssertEqual(childContext.GetProperty("decision_context_id").GetString(),
                link.GetProperty("child_decision_context_id").GetString(),
                "link should name child context id");
            AssertEqual("claim_reward", link.GetProperty("parent_action_type").GetString(),
                "link parent action type");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void PotionRewardSignalClosesRewardsContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var potionReward = new TestPotionReward
        {
            Potion = new TestPotion { Id = new TestModelId { Entry = "SWIFT_POTION" } }
        };
        RewardChoiceCache.Shared.CaptureRewardsSet(new TestRewardsSet
        {
            Rewards = new object?[] { potionReward }
        }, "test.rewards_set.generated");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("rewards", "potion-reward-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-potion-reward");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Rewards.Reward.OnSelectWrapper",
                potionReward,
                Array.Empty<object?>());
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement signal = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/ui_signal").RootElement;
            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("claim_reward", metadata.GetProperty("action_type").GetString(),
                "potion reward claim action type");
            AssertEqual("potion", metadata.GetProperty("reward_type").GetString(),
                "potion reward type");
            AssertEqual("SWIFT_POTION", metadata.GetProperty("potion_id").GetString(),
                "potion reward id");
            AssertEqual(true,
                signal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "potion reward signal should match rewards legal action");
            AssertEqual(true,
                signal.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "potion reward signal should close rewards surface");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void PaelsWingSacrificeActionClosesCardRewardContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    RewardChoiceCache.Shared.Clear();
    try
    {
        var sacrificeCard = new TestCard { Id = new TestModelId { Entry = "DEFEND" } };
        var cardReward = new TestCardReward
        {
            Cards = new object?[] { new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } } },
            HasPaelsWingSacrifice = true,
            SacrificeOptions = new object?[] { sacrificeCard }
        };
        RewardChoiceCache.Shared.CaptureCardReward(cardReward, "test.card_reward.paels_wing");

        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("card_reward", "paels-wing-state"));
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-paels-wing");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.sacrifice_selected",
                null,
                new object?[] { sacrificeCard });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement context = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/context").RootElement;
            AssertTrue(context.GetProperty("legal_actions").GetProperty("actions").EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "sacrifice_reward_card"
                    && action.GetProperty("relic_id").GetString() == "RELIC.PAELS_WING"),
                "PAELS_WING sacrifice legal action should be present");

            JsonElement signal = records.Single(record =>
                record.RootElement.GetProperty("record_type").GetString() == "decision/ui_signal").RootElement;
            JsonElement metadata = signal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("sacrifice_reward_card", metadata.GetProperty("action_type").GetString(),
                "PAELS_WING selected action type");
            AssertEqual(true,
                signal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "PAELS_WING sacrifice signal should match context");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RuntimeOpeningPatchesScheduleTypedSelectionContexts()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    RewardChoiceCache.Shared.Clear();
    SelectionChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var player = new TestPlayer();
        var cardReward = new TestCardReward
        {
            Cards = new object?[]
            {
                new TestCard { Id = new TestModelId { Entry = "STRIKE_PLUS" } }
            },
            CanSkip = true
        };
        object?[] relics =
        {
            new TestRelic { Id = new TestModelId { Entry = "boss_relic_a" }, Rarity = "Boss" }
        };
        object?[] bundles =
        {
            new object?[]
            {
                new TestCard { Id = new TestModelId { Entry = "bundle_card_a" } }
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("card_reward", "card-reward-opened"),
            TestSnapshotWithHash("relic_select", "relic-select-opened"),
            TestSnapshotWithHash("bundle_select", "bundle-select-opened"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-runtime-openings");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnCardRewardOpenedFromPatch("runtime.card_reward.on_select", cardReward);
            Sts2TelemetryMod.OnRelicSelectOpenedFromPatch("runtime.relic_select.choose_a_relic", player, relics);
            Sts2TelemetryMod.OnBundleSelectOpenedFromPatch("runtime.bundle_select.choose_a_bundle", player, bundles);

            AssertEqual(3, scheduled.Count, "runtime openings should schedule bounded settled contexts");
            AssertEqual(0, snapshotBuilder.CaptureCount,
                "runtime opening callbacks should not synchronously capture snapshots");
            AssertEqual(0, GetPendingDecisionCount(recorder),
                "runtime opening callbacks should not create pending decisions");

            foreach (var item in scheduled)
                item.Callback();
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "runtime opening context record count");
            AssertRecordType("decision/context", records[0], "card reward opening context");
            AssertRecordType("decision/context", records[1], "relic select opening context");
            AssertRecordType("decision/context", records[2], "bundle select opening context");

            JsonElement cardActions = records[0].RootElement.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(cardActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "choose_reward_card"),
                "card reward opening context should include cached card choices");

            JsonElement relicActions = records[1].RootElement.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(relicActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "choose_relic_select"
                    && action.GetProperty("relic_id").GetString() == "boss_relic_a"),
                "relic select opening context should include cached relic choice");
            AssertTrue(relicActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "skip_relic_select"),
                "relic select opening context should include typed skip");

            JsonElement bundleActions = records[2].RootElement.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(bundleActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "choose_card_bundle"
                    && action.GetProperty("bundle_index").GetInt32() == 0),
                "bundle opening context should include cached bundle choice");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SelectionChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void SelectionChoiceCacheEmitsRelicAndBundleLegalActions()
{
    SelectionChoiceCache.Shared.Clear();
    try
    {
        var player = new TestPlayer();
        SelectionChoiceCache.Shared.CaptureRelicSelect(
            player,
            new object?[]
            {
                new TestRelic { Id = new TestModelId { Entry = "relic_a" }, Rarity = "Boss" },
                new TestRelic { Id = new TestModelId { Entry = "relic_b" }, Rarity = "Rare" }
            },
            "test.relic_select.opened");
        SelectionChoiceCache.Shared.CaptureBundleSelect(
            player,
            new object?[]
            {
                new object?[]
                {
                    new TestCard { Id = new TestModelId { Entry = "bundle_card_a" }, Type = "Attack" },
                    new TestCard { Id = new TestModelId { Entry = "bundle_card_b" }, Type = "Skill" }
                }
            },
            "test.bundle_select.opened");

        var builder = new LegalActionBuilder();
        var relicActions = builder.Build(TestSnapshot("relic_select"), runState: null, localPlayer: null);
        AssertTrue(relicActions.Any(action => Equals(action["action_type"], "choose_relic_select")
            && Equals(action["relic_id"], "relic_a")), "relic select should emit typed relic options");
        AssertTrue(relicActions.Any(action => Equals(action["action_type"], "skip_relic_select")),
            "relic select should emit typed skip");
        AssertTrue(!relicActions.Any(action => Equals(action["action_type"], "relic_select_typed_builder_unavailable")),
            "relic select cache should replace unavailable marker");

        var bundleActions = builder.Build(TestSnapshot("bundle_select"), runState: null, localPlayer: null);
        Dictionary<string, object?> bundle = bundleActions.Single(action => Equals(action["action_type"], "choose_card_bundle"));
        AssertEqual(0, bundle["bundle_index"], "bundle option index");
        AssertEqual(2, bundle["card_count"], "bundle card count");
        AssertTrue(!bundleActions.Any(action => Equals(action["action_type"], "bundle_select_typed_builder_unavailable")),
            "bundle select cache should replace unavailable marker");
    }
    finally
    {
        SelectionChoiceCache.Shared.Clear();
    }
}

static void PlayerChoiceSignalMatchesRelicAndBundleDecisionContexts()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    SelectionChoiceCache.Shared.Clear();
    try
    {
        var player = new TestPlayer();
        SelectionChoiceCache.Shared.CaptureRelicSelect(
            player,
            new object?[]
            {
                new TestRelic { Id = new TestModelId { Entry = "relic_a" } },
                new TestRelic { Id = new TestModelId { Entry = "relic_b" } }
            },
            "test.relic_select.opened");
        SelectionChoiceCache.Shared.CaptureBundleSelect(
            player,
            new object?[]
            {
                new object?[] { new TestCard { Id = new TestModelId { Entry = "bundle_card_a" } } }
            },
            "test.bundle_select.opened");

        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("relic_select", "relic-context-state"),
            TestSnapshotWithHash("bundle_select", "bundle-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-selection-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordDecisionContextIfCurrentSurface(
                "test.relic_select.context",
                "test.relic_select.context",
                new[] { "relic_select" });
            recorder.RecordPatchedUiSignal(
                "player_choice_synchronizer.player_choice_received",
                null,
                new object?[]
                {
                    player,
                    1u,
                    new TestNetPlayerChoiceResult { type = "Index", indexes = new[] { 1 } }
                });

            recorder.RecordDecisionContextIfCurrentSurface(
                "test.bundle_select.context",
                "test.bundle_select.context",
                new[] { "bundle_select" });
            recorder.RecordPatchedUiSignal(
                "player_choice_synchronizer.player_choice_received",
                null,
                new object?[]
                {
                    player,
                    2u,
                    new TestNetPlayerChoiceResult { type = "Index", indexes = new[] { 0 } }
                });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "selection context and signal record count");
            JsonElement relicSignal = records[1].RootElement;
            JsonElement relicMetadata = relicSignal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("choose_relic_select", relicMetadata.GetProperty("action_type").GetString(),
                "relic player choice action type");
            AssertEqual("relic_select",
                relicSignal.GetProperty("decision_context").GetProperty("state_type").GetString(),
                "relic player choice context surface");
            AssertEqual(true,
                relicSignal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "relic player choice should match legal action");

            JsonElement bundleSignal = records[3].RootElement;
            JsonElement bundleMetadata = bundleSignal.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("choose_card_bundle", bundleMetadata.GetProperty("action_type").GetString(),
                "bundle player choice action type");
            AssertEqual("bundle_select",
                bundleSignal.GetProperty("decision_context").GetProperty("state_type").GetString(),
                "bundle player choice context surface");
            AssertEqual(true,
                bundleSignal.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "bundle player choice should match legal action");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SelectionChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CardRewardSignalWithoutSettledContextExposesLiveGap()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    RewardChoiceCache.Shared.Clear();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, _) =>
            throw new InvalidOperationException($"card reward UI signal should not schedule settled context from {source}"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-card-reward-live-gap");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "ui.card_reward.cards_selected",
                new ThrowingProjectionObject(),
                Array.Empty<object?>());
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(1, records.Length, "card reward signal without settled context record count");
            JsonElement signal = records[0].RootElement;
            AssertRecordType("decision/ui_signal", records[0], "card reward live gap record type");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                signal.GetProperty("capture_policy").GetString(),
                "card reward live gap capture policy");
            JsonElement decisionContext = signal.GetProperty("decision_context");
            AssertEqual("card_reward", decisionContext.GetProperty("state_type").GetString(),
                "card reward live gap should still classify the intended surface");
            AssertEqual("missing_latest_context_for_surface",
                decisionContext.GetProperty("context_reference_status").GetString(),
                "card reward live gap should explain missing settled context");
            AssertEqual("latest_context_for_surface_missing_or_stale",
                decisionContext.GetProperty("context_reference_reason").GetString(),
                "card reward live gap reason");
            JsonElement match = decisionContext.GetProperty("selected_action_match");
            AssertEqual(false, match.GetProperty("matched").GetBoolean(),
                "card reward live gap should not claim a context match");
            AssertEqual("latest_context_for_surface_missing_or_stale",
                match.GetProperty("reason").GetString(),
                "card reward live gap should explain missing settled context in match reason");
            AssertMissingProperty(signal, "pre_state", "card reward live gap signal should not capture pre-state");
            AssertMissingProperty(signal, "post_state", "card reward live gap signal should not capture post-state");
            AssertMissingProperty(signal, "legal_actions", "card reward live gap signal should not build legal actions");
            AssertMissingProperty(signal, "selected_action", "card reward live gap signal should not include selected action");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        RewardChoiceCache.Shared.Clear();
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void EventSignalWithUnavailableContextReportsPlaceholderReason()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["EventSynchronizer"] = new TestEventSynchronizer
            {
                LocalEvent = new TestEventModel
                {
                    Id = new TestModelId { Entry = "MISSING_OPTIONS" },
                    CurrentOptions = null
                }
            }
        });
        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("event", "event-unavailable-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, builder);
            EnableCapturingForTest(recorder, "run-event-unavailable-context");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption",
                null,
                new object?[] { 0 });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "event unavailable context record count");
            JsonElement context = records[1].RootElement;
            JsonElement unavailable = context.GetProperty("legal_actions").GetProperty("actions")[0];
            AssertEqual("event_typed_builder_unavailable", unavailable.GetProperty("action_type").GetString(),
                "event context should expose unavailable marker when typed options are missing");

            JsonElement decisionContext = records[2].RootElement.GetProperty("decision_context");
            AssertEqual("latest_context_by_surface_resolved",
                decisionContext.GetProperty("context_reference_status").GetString(),
                "signal should still reference the latest event context");
            AssertEqual("latest_context_legal_actions_unavailable_or_placeholder_only",
                decisionContext.GetProperty("context_reference_reason").GetString(),
                "signal should explain that the latest context is marker-only");

            JsonElement match = decisionContext.GetProperty("selected_action_match");
            AssertEqual(false, match.GetProperty("matched").GetBoolean(),
                "marker-only event context should not match as trainable");
            AssertEqual("latest_context_legal_actions_unavailable_or_placeholder_only",
                match.GetProperty("reason").GetString(),
                "marker-only event context reason");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void EventDecisionContextRetryRequiresUsableLegalActions()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var eventModel = new TestEventModel
        {
            Id = new TestModelId { Entry = "THE_ARCHITECT" },
            CurrentOptions = null
        };
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["EventSynchronizer"] = new TestEventSynchronizer
            {
                LocalEvent = eventModel
            }
        });
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("event", "event-retry-unavailable"),
            TestSnapshotWithHash("event", "event-retry-available"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, builder);
            EnableCapturingForTest(recorder, "run-event-retry");

            bool firstRecordedUsable = recorder.RecordDecisionContextIfCurrentSurface(
                contextSource: "runtime.event.options_settled_retry",
                source: "runtime.event.options_settled_retry.event_context_refreshed",
                allowedStateTypes: new[] { "event" },
                requireUsableLegalActions: true);
            eventModel.CurrentOptions = new object?[]
            {
                new TestEventOption { TextKey = "THE_ARCHITECT.dialogue.0" },
                new TestEventOption { TextKey = "PROCEED", IsProceed = true }
            };
            bool secondRecordedUsable = recorder.RecordDecisionContextIfCurrentSurface(
                contextSource: "runtime.event.options_settled_retry",
                source: "runtime.event.options_settled_retry.event_context_refreshed.retry_2",
                allowedStateTypes: new[] { "event" },
                requireUsableLegalActions: true);

            AssertEqual(false, firstRecordedUsable, "unavailable event context should request retry");
            AssertEqual(true, secondRecordedUsable, "usable event context should stop retry");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "event retry should write placeholder then usable context");
            JsonElement firstAction = records[0].RootElement.GetProperty("legal_actions").GetProperty("actions")[0];
            AssertEqual("event_typed_builder_unavailable", firstAction.GetProperty("action_type").GetString(),
                "first event retry should record unavailable marker");

            JsonElement secondActions = records[1].RootElement.GetProperty("legal_actions").GetProperty("actions");
            AssertEqual(2, secondActions.GetArrayLength(), "second event retry should capture current options");
            AssertEqual("choose_event_option", secondActions[0].GetProperty("action_type").GetString(),
                "second event retry action type");
            AssertEqual("latest_context_legal_actions_available",
                records[1].RootElement.GetProperty("non_combat_closure").GetProperty("context_reference_reason").GetString(),
                "second event retry closure readiness");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void EventUiSignalReferencesSettledDecisionContextWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["EventSynchronizer"] = new TestEventSynchronizer
            {
                LocalEvent = new TestEventModel
                {
                    Id = new TestModelId { Entry = "NEOW" },
                    CurrentOptions = new object?[]
                    {
                        new TestEventOption { TextKey = "GAIN_RELIC" },
                        new TestEventOption { TextKey = "LEAVE", IsProceed = true }
                    }
                }
            }
        });
        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("event", "event-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, builder);
            EnableCapturingForTest(recorder, "run-event-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption",
                null,
                new object?[] { 1 });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "event context plus signal record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "event lifecycle record");
            AssertRecordType("decision/context", records[1], "event decision context");
            AssertRecordType("decision/ui_signal", records[2], "event signal record");

            JsonElement context = records[1].RootElement;
            string contextId = context.GetProperty("decision_context_id").GetString()
                ?? throw new InvalidOperationException("context id missing");
            JsonElement firstAction = context.GetProperty("legal_actions").GetProperty("actions").EnumerateArray().First();
            AssertEqual("run_start", firstAction.GetProperty("event_source").GetString(), "Neow-like event context source");

            JsonElement signal = records[2].RootElement;
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                signal.GetProperty("capture_policy").GetString(),
                "event signal capture policy");
            AssertMissingProperty(signal, "pre_state", "event signal should not capture pre-state");
            AssertMissingProperty(signal, "post_state", "event signal should not capture post-state");
            AssertMissingProperty(signal, "legal_actions", "event signal should not build legal actions");
            AssertMissingProperty(signal, "selected_action", "event signal should not include selected action");

            JsonElement reference = signal.GetProperty("decision_context");
            AssertEqual(contextId, reference.GetProperty("decision_context_id").GetString(),
                "event signal should reference prior context");
            AssertEqual("event", reference.GetProperty("state_type").GetString(), "event context reference state type");
            AssertEqual("event-context-state", reference.GetProperty("canonical_state_hash").GetString(),
                "event context reference hash");
            AssertEqual("normalized_typed_action_key_subset_match",
                reference.GetProperty("match_policy").GetString(),
                "event context match policy");
            JsonElement match = reference.GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(), "event selected action should match context legal action");
            AssertEqual(1, match.GetProperty("matched_legal_action_index").GetInt32(), "event matched option index");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopUiSignalReferencesSettledDecisionContextWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var contextEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "card-a", Title = "Useful Card" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var signalEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "card-a", Title = "Useful Card" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    CardEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            PatchCallbacks.AfterShopPurchaseCompleted(new object[] { signalEntry });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "shop context plus signal record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "shop lifecycle record");
            AssertRecordType("decision/context", records[1], "shop decision context");
            AssertRecordType("decision/ui_signal", records[2], "shop signal record");

            string contextId = records[1].RootElement.GetProperty("decision_context_id").GetString()
                ?? throw new InvalidOperationException("context id missing");
            JsonElement signal = records[2].RootElement;
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                signal.GetProperty("capture_policy").GetString(),
                "shop signal capture policy");
            AssertMissingProperty(signal, "pre_state", "shop signal should not capture pre-state");
            AssertMissingProperty(signal, "post_state", "shop signal should not capture post-state");
            AssertMissingProperty(signal, "legal_actions", "shop signal should not build legal actions");
            AssertMissingProperty(signal, "selected_action", "shop signal should not include selected action");

            JsonElement reference = signal.GetProperty("decision_context");
            AssertEqual(contextId, reference.GetProperty("decision_context_id").GetString(),
                "shop signal should reference prior context");
            AssertEqual("shop", reference.GetProperty("state_type").GetString(), "shop context reference state type");
            JsonElement match = reference.GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(), "shop selected action should match context legal action");
            AssertEqual("buy_shop_card", match.GetProperty("matched_action_type").GetString(), "shop matched action type");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopCardSignalMatchesTypedCardLegalCategoryByCardId()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var contextEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var signalEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    ColorlessCardEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-card-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-card-category-match");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            recorder.RecordPatchedUiSignal(
                "ui.shop.on_try_purchase",
                signalEntry,
                new object?[] { new TestMerchantInventory(), false });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "shop card category match record count");
            AssertRecordType("decision/context", records[1], "shop decision context");
            AssertRecordType("decision/ui_signal", records[2], "shop attempt signal");

            JsonElement legalAction = records[1].RootElement
                .GetProperty("legal_actions")
                .GetProperty("actions")
                .EnumerateArray()
                .Single();
            AssertEqual("colorless_card", legalAction.GetProperty("category").GetString(),
                "context should keep typed shop card category");
            AssertEqual("THINKING_AHEAD", legalAction.GetProperty("card_id").GetString(),
                "context legal action card id");

            JsonElement metadata = records[2].RootElement.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("card", metadata.GetProperty("category").GetString(),
                "typed signal should remain generic merchant card category");
            AssertEqual("THINKING_AHEAD", metadata.GetProperty("card_id").GetString(),
                "typed signal card id");

            AssertMissingProperty(records[2].RootElement, "decision_context",
                "shop attempt callbacks should remain raw evidence and not close trainable choices");
            AssertMissingProperty(records[2].RootElement, "non_combat_closure",
                "shop attempt callbacks should not produce closure metadata");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopCardCompletionInheritsIdentityFromPriorAttempt()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var contextEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var attemptEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var completedEntry = new TestMerchantCardEntry
        {
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    ColorlessCardEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-card-completion-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-card-completion-enrichment");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            recorder.RecordPatchedUiSignal(
                "ui.shop.on_try_purchase",
                attemptEntry,
                new object?[] { new TestMerchantInventory(), false });
            recorder.RecordPatchedUiSignal(
                "runtime.shop.purchase_completed",
                completedEntry,
                new object?[] { completedEntry });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "shop card completion enrichment record count");
            AssertRecordType("decision/ui_signal", records[2], "shop attempt signal");
            AssertRecordType("decision/ui_signal", records[3], "shop completion signal");

            JsonElement completion = records[3].RootElement;
            JsonElement metadata = completion.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("buy_shop_card", metadata.GetProperty("action_type").GetString(),
                "completion should still project shop card purchase");
            AssertEqual("completed", metadata.GetProperty("purchase_status").GetString(),
                "completion status");
            AssertEqual("TestMerchantCardEntry", metadata.GetProperty("shop_entry_runtime_type_name").GetString(),
                "completion should be a card-entry-shaped runtime object");
            AssertEqual("THINKING_AHEAD", metadata.GetProperty("card_id").GetString(),
                "completion should inherit card id from prior attempt");
            AssertEqual("prior_typed_attempt",
                metadata.GetProperty("shop_completion_identity_enrichment").GetString(),
                "completion enrichment source marker");
            AssertEqual("ui.shop.on_try_purchase",
                metadata.GetProperty("shop_completion_identity_enrichment_source").GetString(),
                "completion enrichment should name prior attempt source");
            AssertEqual("completed_entry_lost_item_identity_after_purchase",
                metadata.GetProperty("shop_completion_identity_enrichment_reason").GetString(),
                "completion enrichment reason");

            JsonElement normalizedKey = metadata.GetProperty("normalized_typed_action_key");
            AssertEqual("buy_shop_card", normalizedKey.GetProperty("action_type").GetString(),
                "enriched completion normalized action type");
            AssertEqual("THINKING_AHEAD", normalizedKey.GetProperty("card_id").GetString(),
                "enriched completion normalized card id");

            JsonElement reference = completion.GetProperty("decision_context");
            AssertEqual("shop_signal_identity_match",
                reference.GetProperty("match_policy").GetString(),
                "enriched completion should match typed legal card category by id");
            JsonElement match = reference.GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(),
                "enriched completion should match latest shop context");
            AssertEqual(0, match.GetProperty("matched_legal_action_index").GetInt32(),
                "enriched completion matched legal action index");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopCardCompletionDoesNotUseStaleAttemptAfterUnrelatedSignal()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var contextEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var attemptEntry = new TestMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var completedEntry = new TestMerchantCardEntry
        {
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    ColorlessCardEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-card-stale-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-card-stale-correlation");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            recorder.RecordPatchedUiSignal(
                "ui.shop.on_try_purchase",
                attemptEntry,
                new object?[] { new TestMerchantInventory(), false });
            recorder.RecordPatchedUiSignal("ui.debug.unrelated", null, Array.Empty<object?>());
            recorder.RecordPatchedUiSignal(
                "runtime.shop.purchase_completed",
                completedEntry,
                new object?[] { completedEntry });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(5, records.Length, "shop card stale correlation record count");
            JsonElement completion = records[4].RootElement;
            JsonElement metadata = completion.GetProperty("ui_signal").GetProperty("metadata");
            AssertMissingProperty(metadata, "shop_completion_identity_enrichment",
                "unrelated signal should clear pending shop card enrichment");
            AssertEqual(JsonValueKind.Null, metadata.GetProperty("card_id").ValueKind,
                "stale completion should not inherit card id");

            JsonElement normalizedKey = metadata.GetProperty("normalized_typed_action_key");
            AssertMissingProperty(normalizedKey, "card_id",
                "stale completion normalized key should not inherit card id");

            JsonElement match = completion
                .GetProperty("decision_context")
                .GetProperty("selected_action_match");
            AssertEqual(false, match.GetProperty("matched").GetBoolean(),
                "stale completion without card id should not match prior shop card legal action");
            AssertEqual("selected_action_normalized_typed_action_key_unavailable",
                match.GetProperty("reason").GetString(),
                "stale completion should report an explicit no-match reason");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopRelicCompletionInheritsIdentityFromPriorAttempt()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var player = new TestPlayer { Gold = 200 };
        var contextEntry = new TestMerchantEntry
        {
            Relic = new TestShopItem { Id = "RAZOR_TOOTH", Title = "Razor Tooth" },
            Cost = 150,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var attemptEntry = new TestMerchantEntry
        {
            Relic = new TestShopItem { Id = "RAZOR_TOOTH", Title = "Razor Tooth" },
            Cost = 150,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var completedEntry = new TestMerchantRelicEntry
        {
            Cost = 150,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    RelicEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-relic-completion-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-relic-completion-enrichment");

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            recorder.RecordPatchedUiSignal(
                "ui.shop.on_try_purchase",
                attemptEntry,
                new object?[] { new TestMerchantInventory(), false });
            recorder.RecordPatchedUiSignal(
                "runtime.shop.purchase_completed",
                completedEntry,
                new object?[] { completedEntry });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "shop relic completion enrichment record count");
            JsonElement completion = records[3].RootElement;
            JsonElement metadata = completion.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("buy_shop_relic", metadata.GetProperty("action_type").GetString(),
                "completion should still project shop relic purchase");
            AssertEqual("completed", metadata.GetProperty("purchase_status").GetString(),
                "completion status");
            AssertEqual("TestMerchantRelicEntry", metadata.GetProperty("shop_entry_runtime_type_name").GetString(),
                "completion should remain a relic-entry-shaped runtime object");
            AssertEqual("RAZOR_TOOTH", metadata.GetProperty("relic_id").GetString(),
                "completion should inherit relic id from prior attempt");
            AssertEqual("prior_typed_attempt",
                metadata.GetProperty("shop_completion_identity_enrichment").GetString(),
                "completion enrichment source marker");

            JsonElement normalizedKey = metadata.GetProperty("normalized_typed_action_key");
            AssertEqual("buy_shop_relic", normalizedKey.GetProperty("action_type").GetString(),
                "enriched completion normalized action type");
            AssertEqual("RAZOR_TOOTH", normalizedKey.GetProperty("relic_id").GetString(),
                "enriched completion normalized relic id");

            JsonElement match = completion
                .GetProperty("decision_context")
                .GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(),
                "enriched relic completion should match latest shop context");
            AssertEqual(0, match.GetProperty("matched_legal_action_index").GetInt32(),
                "enriched relic completion matched legal action index");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ShopCompletionCoalescesDuplicateCallbacksAndRefreshesContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = null;
    var scheduled = new List<(string Source, Action Callback)>();
    try
    {
        originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((source, callback) =>
        {
            scheduled.Add((source, callback));
            return true;
        });

        var player = new TestPlayer { Gold = 160 };
        var cardEntry = new TestMutableMerchantEntry
        {
            Card = new TestShopItem { Id = "THINKING_AHEAD", Title = "Thinking Ahead" },
            Cost = 42,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var potionEntry = new TestMutableMerchantEntry
        {
            Potion = new TestShopItem { Id = "SWIFT_POTION", Title = "Swift Potion" },
            Cost = 45,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    CardEntries = new object?[] { cardEntry },
                    PotionEntries = new object?[] { potionEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-before-purchase"),
            TestSnapshotWithHash("shop", "shop-after-purchase"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-coalesce");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            cardEntry.Used = true;
            PatchCallbacks.AfterShopPurchaseCompleted(new object[] { cardEntry });
            PatchCallbacks.AfterShopInventoryPurchaseCompleted(new object[]
            {
                PurchaseStatus.Success,
                new TestMerchantInventory(),
                cardEntry
            });

            AssertEqual(1, scheduled.Count, "only the semantic completed transaction should schedule a refreshed shop context");
            scheduled[0].Callback();
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            JsonElement[] signals = records
                .Select(record => record.RootElement)
                .Where(root => root.GetProperty("record_type").GetString() == "decision/ui_signal")
                .ToArray();
            AssertEqual(2, signals.Length, "raw duplicate callback evidence should be retained");
            AssertEqual("completed_transaction",
                signals[0].GetProperty("ui_signal").GetProperty("metadata").GetProperty("shop_transaction_status").GetString(),
                "first completion transaction status");
            AssertTrue(signals[0].TryGetProperty("decision_context", out _),
                "first completion should close the latest shop context");
            AssertEqual("duplicate_completion_callback",
                signals[1].GetProperty("ui_signal").GetProperty("metadata").GetProperty("shop_transaction_status").GetString(),
                "second callback should be duplicate raw evidence");
            AssertMissingProperty(signals[1], "decision_context",
                "duplicate shop completion callback should not be trainable");

            JsonElement refreshed = records
                .Select(record => record.RootElement)
                .Where(root => root.GetProperty("record_type").GetString() == "decision/context")
                .Last();
            JsonElement refreshedActions = refreshed.GetProperty("legal_actions").GetProperty("actions");
            AssertTrue(!refreshedActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "buy_shop_card"),
                "refreshed context should remove the completed card purchase from legal actions");
            AssertTrue(refreshedActions.EnumerateArray().Any(action =>
                    action.GetProperty("action_type").GetString() == "buy_shop_potion"
                    && action.GetProperty("potion_id").GetString() == "SWIFT_POTION"),
                "refreshed context should keep later available shop actions");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void NonCombatClosureSummaryMarksTrainableMatchedSignals()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["EventSynchronizer"] = new TestEventSynchronizer
            {
                LocalEvent = new TestEventModel
                {
                    Id = new TestModelId { Entry = "NEOW" },
                    CurrentOptions = new object?[]
                    {
                        new TestEventOption { TextKey = "GAIN_RELIC" },
                        new TestEventOption { TextKey = "LEAVE", IsProceed = true }
                    }
                }
            }
        });
        var snapshotBuilder = new QueuedSnapshotBuilder(TestSnapshotWithHash("event", "event-closure-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, builder);
            EnableCapturingForTest(recorder, "run-event-closure");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption",
                null,
                new object?[] { 1 });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "event closure record count");
            JsonElement contextClosure = records[1].RootElement.GetProperty("non_combat_closure");
            string contextId = records[1].RootElement.GetProperty("decision_context_id").GetString()
                ?? throw new InvalidOperationException("context id missing");
            AssertEqual("event", contextClosure.GetProperty("surface").GetString(), "context closure surface");
            AssertEqual(contextId, contextClosure.GetProperty("decision_context_id").GetString(),
                "context closure id");
            AssertEqual("context_open_awaiting_signal",
                contextClosure.GetProperty("context_reference_status").GetString(),
                "context closure status");
            AssertEqual(false,
                contextClosure.GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "open context alone should not be trainable");

            JsonElement signalClosure = records[2].RootElement.GetProperty("non_combat_closure");
            AssertEqual("event", signalClosure.GetProperty("surface").GetString(), "signal closure surface");
            AssertEqual(contextId, signalClosure.GetProperty("decision_context_id").GetString(),
                "signal closure id");
            AssertEqual("matched",
                signalClosure.GetProperty("selected_action_match_status").GetString(),
                "signal closure match status");
            AssertEqual("normalized_typed_action_key_subset_match",
                signalClosure.GetProperty("match_policy").GetString(),
                "signal closure match policy");
            AssertEqual(1,
                signalClosure.GetProperty("matched_legal_action_index").GetInt32(),
                "signal closure matched index");
            AssertEqual("choose_event_option",
                signalClosure.GetProperty("matched_action_type").GetString(),
                "signal closure matched type");
            AssertEqual(true,
                signalClosure.GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "matched non-combat signal should be trainable");

            TelemetryUploadRunStatus run = TelemetryUploadStatusReader.Build(directory, maxRuns: 10).Runs.Single();
            TelemetryNonCombatMatchQuality quality = run.NonCombatMatchQuality.Single();
            AssertEqual("event", quality.Surface, "status aggregate surface");
            AssertEqual(1, quality.ContextRecords, "status aggregate context count");
            AssertEqual(1, quality.SignalRecords, "status aggregate signal count");
            AssertEqual(1, quality.MatchedSignals, "status aggregate matched count");
            AssertEqual(0, quality.UnmatchedSignals, "status aggregate unmatched count");
            AssertEqual(1, quality.TrainableClosedChoices, "status aggregate trainable count");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusExcludesDiagnosticCardRewardCardsSelectedNoIdentitySignals()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "logical-run-card-reward-status", "segments", "segment-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            CardRewardStatusSignalLine(
                seq: 1,
                source: "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen.SelectCard",
                actionType: "choose_reward_card",
                selectionIdentityStatus: "matched_card_reward_card_id",
                matchStatus: "matched",
                trainable: true),
            CardRewardStatusSignalLine(
                seq: 2,
                source: "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen.CardsSelected",
                actionType: "card_reward_selection_unavailable",
                selectionIdentityStatus: "selected_card_identity_missing_from_signal",
                matchStatus: "unmatched",
                trainable: false)
        });

        TelemetryUploadRunStatus run = TelemetryUploadStatusReader.Build(directory, maxRuns: 10).Runs.Single();
        TelemetryNonCombatMatchQuality quality = run.NonCombatMatchQuality.Single(item => item.Surface == "card_reward");
        AssertEqual(1, quality.SignalRecords, "diagnostic no-identity CardsSelected should not inflate card_reward denominator");
        AssertEqual(1, quality.MatchedSignals, "matched SelectCard signal count");
        AssertEqual(0, quality.UnmatchedSignals, "diagnostic no-identity CardsSelected should not count as actionable unmatched");
        AssertEqual(1, quality.TrainableClosedChoices, "trainable card reward closure count");
        AssertEqual(2, File.ReadAllLines(runPath).Length, "raw diagnostic CardsSelected record should remain in JSONL");

        string rendered = TelemetryUploadStatusRenderer.RenderPlainText(new TelemetryUploadStatusView
        {
            Runs = new[] { run }
        });
        AssertTrue(rendered.Contains("card_reward 1/1", StringComparison.Ordinal),
            "rendered status should show trainable card_reward quality");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusCountsUnmatchedActionableCardRewardSignals()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "logical-run-card-reward-gap", "segments", "segment-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            CardRewardStatusSignalLine(
                seq: 1,
                source: "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen.SelectCard",
                actionType: "card_reward_selection_unavailable",
                selectionIdentityStatus: "selected_card_identity_not_matched_to_cached_card_reward",
                matchStatus: "unmatched",
                trainable: false)
        });

        TelemetryUploadRunStatus run = TelemetryUploadStatusReader.Build(directory, maxRuns: 10).Runs.Single();
        TelemetryNonCombatMatchQuality quality = run.NonCombatMatchQuality.Single(item => item.Surface == "card_reward");
        AssertEqual(1, quality.SignalRecords, "unmatched actionable card_reward signal denominator");
        AssertEqual(0, quality.MatchedSignals, "unmatched actionable card_reward matched count");
        AssertEqual(1, quality.UnmatchedSignals, "unmatched actionable card_reward gap count");
        AssertEqual(0, quality.TrainableClosedChoices, "unmatched actionable card_reward trainable count");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static string CardRewardStatusSignalLine(
    int seq,
    string source,
    string actionType,
    string selectionIdentityStatus,
    string matchStatus,
    bool trainable)
    => JsonSerializer.Serialize(
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = "sts2.telemetry.local.v1",
            ["record_type"] = "decision/ui_signal",
            ["installation_id"] = "inst-test",
            ["run_id"] = "run-card-reward-status",
            ["logical_run_id"] = "logical-run-card-reward-status",
            ["segment_id"] = "segment-a",
            ["local_sequence"] = seq,
            ["recorded_at_utc"] = $"2026-05-10T00:00:{seq:00}Z",
            ["source"] = source,
            ["ui_signal"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["metadata"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = source,
                    ["state_type"] = "card_reward",
                    ["action_type"] = actionType,
                    ["selection_identity_status"] = selectionIdentityStatus
                }
            },
            ["non_combat_closure"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["surface"] = "card_reward",
                ["selected_action_match_status"] = matchStatus,
                ["trainable_closed_non_combat_choice"] = trainable
            }
        },
        TelemetryJson.Options);

static void NativeSavePrivacyScrubRemovesLocalIdentity()
{
    using JsonDocument document = JsonDocument.Parse("""
        {
          "seed": "20VBG069DW",
          "card_id": "strike",
          "relic_id": "anchor",
          "net_id": "net-123",
          "player_id": "player-123",
          "unique_id": "unique-123",
          "steam_user_id": "steam-123",
          "profile_name": "local-profile",
          "local_path": "/home/test/.steam/userdata/123",
          "nested": {
            "account_id": "acct-1",
            "event_id": "fake_merchant",
            "room_id": "room-4"
          }
        }
        """);

    object? scrubbed = NativeSavePrivacyScrubber.Scrub(document.RootElement);
    string json = JsonSerializer.Serialize(scrubbed, TelemetryJson.Options);
    using JsonDocument scrubbedDocument = JsonDocument.Parse(json);
    JsonElement root = scrubbedDocument.RootElement;

    AssertEqual("20VBG069DW", root.GetProperty("seed").GetString(), "seed should be preserved");
    AssertEqual("strike", root.GetProperty("card_id").GetString(), "card id should be preserved");
    AssertEqual("anchor", root.GetProperty("relic_id").GetString(), "relic id should be preserved");
    AssertEqual("[scrubbed]", root.GetProperty("net_id").GetString(), "net id should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("player_id").GetString(), "player id should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("unique_id").GetString(), "unique id should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("steam_user_id").GetString(), "steam id should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("profile_name").GetString(), "profile identifier should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("local_path").GetString(), "local path key should be scrubbed");
    AssertEqual("[scrubbed]", root.GetProperty("nested").GetProperty("account_id").GetString(),
        "account id should be scrubbed");
    AssertEqual("fake_merchant", root.GetProperty("nested").GetProperty("event_id").GetString(),
        "event id should be preserved");
    AssertEqual("room-4", root.GetProperty("nested").GetProperty("room_id").GetString(),
        "room id should be preserved");
}

static void NativeSaveCaptureDedupesManifestEntries()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    string saveRoot = Path.Combine(directory, "SlayTheSpire2");
    string telemetryDirectory = Path.Combine(saveRoot, "sts2-telemetry");
    try
    {
        Directory.CreateDirectory(Path.Combine(saveRoot, "history"));
        string saveJson = """
            {
              "schema_version": "save.v1",
              "build_id": "0.103.2",
              "seed": "20VBG069DW",
              "start_time": "1778339433",
              "current_act_index": 2,
              "floor": 30,
              "steam_id": "steam-123",
              "card_id": "strike",
              "visited_coords": [{"x":1,"y":2}]
            }
            """;
        File.WriteAllText(Path.Combine(saveRoot, "current_run.save"), saveJson);
        File.WriteAllText(Path.Combine(saveRoot, "current_run.save.backup"), saveJson);

        var capture = new NativeSaveCapture(
            _ => new[] { saveRoot },
            () => new DateTimeOffset(2026, 5, 10, 1, 2, 3, TimeSpan.Zero));
        NativeSaveCaptureResult result = capture.CaptureRecent(telemetryDirectory);
        IReadOnlyList<NativeSaveCaptureRef> refs = result.Refs;

        AssertEqual(1, refs.Count, "duplicate native saves should collapse by scrubbed sha");
        AssertEqual(1, result.NewCaptures.Count, "only one new uploadable payload should be returned per sha");
        NativeSaveCaptureRef captureRef = refs.Single();
        AssertEqual("current_run_save", captureRef.FileKind, "file kind");
        AssertEqual("20VBG069DW", captureRef.Metadata["seed"], "metadata seed");
        AssertEqual("0.103.2", captureRef.Metadata["build_id"], "metadata build id");
        AssertEqual(30, captureRef.Metadata["floor"], "metadata floor");

        string objectPath = Path.Combine(telemetryDirectory, "native_saves", "objects", $"{captureRef.Sha256}.json");
        AssertTrue(File.Exists(objectPath), "content-addressed scrubbed save object should exist");
        string stored = File.ReadAllText(objectPath);
        AssertTrue(!stored.Contains("steam-123", StringComparison.Ordinal),
            "stored native save object should not contain steam id");
        AssertTrue(stored.Contains("strike", StringComparison.Ordinal),
            "stored native save object should preserve gameplay card id");

        string manifestPath = Path.Combine(telemetryDirectory, "native_saves", "manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement captures = manifest.RootElement.GetProperty("captures");
        AssertEqual(1, captures.GetArrayLength(), "manifest should dedupe by sha");
        JsonElement entry = captures[0];
        AssertEqual(captureRef.Sha256, entry.GetProperty("sha256").GetString(), "manifest sha");
        AssertEqual("current_run/current_run.save",
            entry.GetProperty("original_category").GetString(),
            "manifest category should be relative-ish");
        AssertTrue(!entry.GetProperty("object_path").GetString()!.StartsWith("/", StringComparison.Ordinal),
            "manifest object path should be relative");
        AssertTrue(entry.TryGetProperty("captured_at", out _), "manifest should include captured_at");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void NativeSaveCaptureDiscoversFlatpakProfileSaves()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    string saveRoot = Path.Combine(directory, "SlayTheSpire2");
    string telemetryDirectory = Path.Combine(saveRoot, "sts2-telemetry");
    string profileSaves = Path.Combine(saveRoot, "steam", "76561198000000000", "modded", "profile1", "saves");
    try
    {
        Directory.CreateDirectory(profileSaves);
        Directory.CreateDirectory(Path.Combine(telemetryDirectory, "native_saves", "saves"));
        File.WriteAllText(
            Path.Combine(profileSaves, "current_run.save"),
            """
            {
              "schema_version": "save.v1",
              "seed": "20VBG069DW",
              "floor": 30,
              "steam_id": "steam-123",
              "event_id": "fake_merchant"
            }
            """);
        File.WriteAllText(
            Path.Combine(telemetryDirectory, "native_saves", "saves", "current_run.save"),
            "{\"seed\":\"should-not-be-read\"}");

        var capture = new NativeSaveCapture(
            _ => new[] { saveRoot },
            () => new DateTimeOffset(2026, 5, 10, 1, 2, 3, TimeSpan.Zero));
        NativeSaveCaptureResult result = capture.CaptureRecent(telemetryDirectory);

        AssertEqual(1, result.Refs.Count, "deep Flatpak-style profile save should be discovered");
        NativeSaveCaptureRef captureRef = result.Refs.Single();
        AssertEqual("20VBG069DW", captureRef.Metadata["seed"], "deep save metadata seed");
        AssertEqual("current_run_save", captureRef.FileKind, "deep save file kind");
        AssertEqual(1, result.NewCaptures.Count, "deep save should produce one uploadable payload");
        string payloadJson = JsonSerializer.Serialize(result.NewCaptures.Single().Payload, TelemetryJson.Options);
        AssertTrue(!payloadJson.Contains("steam-123", StringComparison.Ordinal),
            "deep save payload should scrub steam id");
        AssertTrue(payloadJson.Contains("fake_merchant", StringComparison.Ordinal),
            "deep save payload should preserve gameplay event id");
        AssertTrue(!payloadJson.Contains("should-not-be-read", StringComparison.Ordinal),
            "discovery should avoid telemetry/native_saves output dirs");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void NativeSaveRootResolverIncludesCrossPlatformRoots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string home = Path.Combine(directory, "home", "player");
        string localAppData = Path.Combine(directory, "win", "LocalAppData");
        string appData = Path.Combine(directory, "win", "Roaming");
        string programFiles = Path.Combine(directory, "Program Files (x86)");
        string telemetryDirectory = Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "SlayTheSpire2", "sts2-telemetry");
        string protonUser = Path.Combine(home, ".steam", "steam", "steamapps", "compatdata", "123456", "pfx", "drive_c", "users", "steamuser");
        Directory.CreateDirectory(protonUser);

        IReadOnlyList<string> roots = NativeSaveRootResolver.Resolve(
            telemetryDirectory,
            home,
            localAppData,
            appData,
            programFiles);

        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include Linux Flatpak SlayTheSpire2 root");
        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(home, ".local", "share", "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include native Linux app-data root");
        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(localAppData, "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include Windows LocalAppData root");
        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(home, "AppData", "LocalLow", "Mega Crit", "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include Windows LocalLow root");
        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(home, "Library", "Application Support", "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include macOS Application Support root");
        AssertTrue(roots.Contains(Path.GetFullPath(Path.Combine(protonUser, "AppData", "LocalLow", "Mega Crit", "SlayTheSpire2")), StringComparer.Ordinal),
            "resolver should include Proton LocalLow root");
        AssertTrue(!roots.Any(root => root.Contains("76561198000000000", StringComparison.Ordinal)),
            "resolver should not hardcode user-specific Steam ids");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void NativeSaveCapturePrunesLocalObjectCache()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    string saveRoot = Path.Combine(directory, "SlayTheSpire2");
    string telemetryDirectory = Path.Combine(saveRoot, "sts2-telemetry");
    try
    {
        string objectsDirectory = Path.Combine(telemetryDirectory, "native_saves", "objects");
        Directory.CreateDirectory(objectsDirectory);
        File.WriteAllText(Path.Combine(objectsDirectory, "stale.json"), "{}");
        File.WriteAllText(
            Path.Combine(telemetryDirectory, "native_saves", "manifest.json"),
            """
            {
              "schema_version": "sts2.telemetry.native_save_manifest.v1",
              "captures": [
                {
                  "sha256": "stale",
                  "bytes": 2,
                  "file_kind": "current_run_save",
                  "original_category": "current_run/current_run.save",
                  "captured_at": "2026-05-08T00:00:00Z",
                  "object_path": "objects/stale.json",
                  "metadata": {"seed":"old"}
                }
              ]
            }
            """);

        var capture = new NativeSaveCapture(
            _ => new[] { saveRoot },
            () => new DateTimeOffset(2026, 5, 10, 1, 2, 3, TimeSpan.Zero));
        NativeSaveCaptureResult result = capture.CaptureRecent(telemetryDirectory);

        AssertEqual(0, result.Refs.Count, "no current saves should be captured in prune-only pass");
        AssertTrue(!File.Exists(Path.Combine(objectsDirectory, "stale.json")),
            "stale native save object should be pruned");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(telemetryDirectory, "native_saves", "manifest.json")));
        AssertEqual(0, manifest.RootElement.GetProperty("captures").GetArrayLength(),
            "stale manifest entries should be pruned");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void SaveObservedEmitsUploadableNativeSaveCaptureRecord()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    string saveRoot = Path.Combine(directory, "SlayTheSpire2");
    string telemetryDirectory = Path.Combine(saveRoot, "sts2-telemetry");
    string profileSaves = Path.Combine(saveRoot, "steam", "76561198000000000", "profile1", "saves");
    try
    {
        Directory.CreateDirectory(profileSaves);
        File.WriteAllText(
            Path.Combine(profileSaves, "current_run.save"),
            """
            {
              "schema_version": "save.v1",
              "seed": "20VBG069DW",
              "floor": 30,
              "steam_id": "steam-123",
              "card_id": "strike"
            }
            """);

        using (var writer = new JsonlTelemetryWriter(telemetryDirectory))
        {
            var nativeSaveCapture = new NativeSaveCapture(
                _ => new[] { saveRoot },
                () => new DateTimeOffset(2026, 5, 10, 1, 2, 3, TimeSpan.Zero));
            var recorder = new TelemetryRecorder(
                writer,
                new ThrowingSnapshotBuilder(),
                new LegalActionBuilder(),
                nativeSaveCapture);
            EnableCapturingForTest(recorder, "run-native-save-uploadable");
            recorder.RecordSaveObserved("save_run.postfix");
            recorder.RecordSaveObserved("run_save_manager.saved_event");
        }

        JsonDocument[] records = ReadAllRunRecords(telemetryDirectory);
        try
        {
            AssertEqual(3, records.Length, "one uploadable native save plus two save observed records");
            JsonElement captureRecord = records[0].RootElement;
            AssertRecordType("native_save/capture", records[0], "native save uploadable record type");
            AssertEqual("read_only_native_save_scrubbed_payload",
                captureRecord.GetProperty("capture_policy").GetString(),
                "native save capture policy");
            AssertMissingProperty(captureRecord, "pre_state", "native save capture should not capture pre-state");
            AssertMissingProperty(captureRecord, "post_state", "native save capture should not capture post-state");
            AssertMissingProperty(captureRecord, "legal_actions", "native save capture should not build legal actions");

            JsonElement nativeSave = captureRecord.GetProperty("native_save");
            JsonElement payload = nativeSave.GetProperty("payload");
            string payloadJson = payload.GetRawText();
            AssertTrue(!payloadJson.Contains("steam-123", StringComparison.Ordinal),
                "uploadable native save payload should scrub steam id");
            AssertTrue(payloadJson.Contains("strike", StringComparison.Ordinal),
                "uploadable native save payload should preserve gameplay card id");
            AssertEqual("20VBG069DW",
                nativeSave.GetProperty("metadata").GetProperty("seed").GetString(),
                "uploadable native save metadata");

            JsonElement firstSaveObserved = records[1].RootElement;
            AssertRecordType("lifecycle/save_observed", records[1], "first save observed");
            AssertEqual(1,
                firstSaveObserved.GetProperty("details").GetProperty("native_save_payload_records").GetInt32(),
                "first save observed should report one new payload record");
            AssertEqual(1,
                firstSaveObserved.GetProperty("native_save_refs").GetArrayLength(),
                "first save observed should reference captured save");

            JsonElement secondSaveObserved = records[2].RootElement;
            AssertRecordType("lifecycle/save_observed", records[2], "second save observed");
            AssertEqual(0,
                secondSaveObserved.GetProperty("details").GetProperty("native_save_payload_records").GetInt32(),
                "duplicate sha should not emit another payload record");
            AssertEqual(1,
                secondSaveObserved.GetProperty("native_save_refs").GetArrayLength(),
                "duplicate save observed can still reference the existing save");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void DecisionContextIncludesRecentNativeSaveRef()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["EventSynchronizer"] = new TestEventSynchronizer
            {
                LocalEvent = new TestEventModel
                {
                    Id = new TestModelId { Entry = "NEOW" },
                    CurrentOptions = new object?[] { new TestEventOption { TextKey = "LEAVE", IsProceed = true } }
                }
            }
        });
        var saveRef = new NativeSaveCaptureRef(
            "abc123",
            42,
            "current_run_save",
            "current_run/current_run.save",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?> { ["seed"] = "20VBG069DW" });

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new QueuedSnapshotBuilder(TestSnapshotWithHash("event", "event-native-save-state")), builder);
            EnableCapturingForTest(recorder, "run-event-native-save");
            recorder.SetRecentNativeSaveRefsForTests(new[] { saveRef });
            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "native save context record count");
            JsonElement context = records[1].RootElement;
            JsonElement nativeSaveRef = context.GetProperty("native_save_ref");
            AssertEqual("abc123", nativeSaveRef.GetProperty("sha256").GetString(), "native save ref sha");
            AssertEqual("current_run_save", nativeSaveRef.GetProperty("file_kind").GetString(), "native save ref kind");
            AssertEqual("current_run/current_run.save",
                nativeSaveRef.GetProperty("original_category").GetString(),
                "native save ref category");
            AssertEqual("20VBG069DW",
                nativeSaveRef.GetProperty("metadata").GetProperty("seed").GetString(),
                "native save ref metadata");
            AssertEqual(1, context.GetProperty("native_save_refs").GetArrayLength(),
                "native save refs array count");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderExplicitRunLoadMatchesKnownStateBeforeLaterFork()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-c"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            var firstAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(firstAction);
            recorder.CompleteActionExecutorDecision(firstAction);

            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");

            var divergingAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(divergingAction);
            recorder.CompleteActionExecutorDecision(divergingAction);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(6, records.Length, "explicit load then divergent decision record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "known edge decision frame");
            AssertRecordType("lifecycle/run_loaded", records[2], "explicit run load");
            AssertRecordType("lifecycle/branch_matched", records[3], "explicit branch match");
            AssertRecordType("lifecycle/branch_forked", records[4], "later divergent decision fork");
            AssertRecordType("decision/frame", records[5], "divergent decision frame");

            JsonElement loadMatch = records[2].RootElement.GetProperty("branch_match");
            AssertEqual(true, loadMatch.GetProperty("matched").GetBoolean(), "explicit load should match state-a");
            AssertEqual(true, loadMatch.GetProperty("pending_divergence").GetBoolean(),
                "matching a state with known children should wait for decision-edge comparison");
            AssertEqual("branch-0001", loadMatch.GetProperty("branch_id").GetString(), "matched branch id");
            AssertEqual("state-a", loadMatch.GetProperty("parent_canonical_state_hash").GetString(),
                "matched canonical state hash");
            AssertEqual("matched_state_has_existing_children_pending_divergence",
                loadMatch.GetProperty("reason").GetString(),
                "matched resume reason");

            JsonElement matched = records[3].RootElement;
            AssertEqual("run_manager.set_up_saved_single_player",
                matched.GetProperty("source").GetString(),
                "branch match should keep the load source");
            AssertEqual("state-a",
                matched.GetProperty("state").GetProperty("canonical_state_hash").GetString(),
                "branch match state hash");
            AssertEqual(true,
                matched.GetProperty("branch_match").GetProperty("pending_divergence").GetBoolean(),
                "branch match should report pending divergence");

            JsonElement forkDecision = records[4].RootElement.GetProperty("branch_decision");
            AssertEqual(true, forkDecision.GetProperty("forked").GetBoolean(),
                "only the later divergent decision should fork");
            AssertEqual("state-a", forkDecision.GetProperty("parent_canonical_state_hash").GetString(),
                "fork parent hash");
            AssertEqual("state-c", forkDecision.GetProperty("post_canonical_state_hash").GetString(),
                "fork post hash");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderDecisionFramesMarkReplayedKnownEdges()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "state-c"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            var firstAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(firstAction);
            recorder.CompleteActionExecutorDecision(firstAction);

            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");

            var replayedAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(replayedAction);
            recorder.CompleteActionExecutorDecision(replayedAction);

            var continuationAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(continuationAction);
            recorder.CompleteActionExecutorDecision(continuationAction);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(6, records.Length, "replayed edge then continuation record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "original decision frame");
            AssertRecordType("lifecycle/run_loaded", records[2], "explicit run load");
            AssertRecordType("lifecycle/branch_matched", records[3], "explicit branch match");
            AssertRecordType("decision/frame", records[4], "replayed decision frame");
            AssertRecordType("decision/frame", records[5], "new continuation decision frame");

            string originalDecisionFrameId = records[1].RootElement.GetProperty("decision_frame_id").GetString()
                ?? throw new InvalidOperationException("original decision id missing");
            JsonElement originalDecision = records[1].RootElement.GetProperty("branch_decision");
            AssertEqual(false, originalDecision.GetProperty("trajectory_replayed").GetBoolean(),
                "original decision should not be marked replayed");
            AssertEqual(JsonValueKind.Null,
                originalDecision.GetProperty("matched_decision_frame_id").ValueKind,
                "original decision should not point at a matched edge");
            AssertEqual("attempt-0001",
                records[1].RootElement.GetProperty("branch").GetProperty("attempt_id").GetString(),
                "original decision attempt id");

            AssertEqual("attempt-0002",
                records[2].RootElement.GetProperty("branch").GetProperty("attempt_id").GetString(),
                "run_loaded should allocate a replay attempt");
            AssertEqual("branch-0001",
                records[2].RootElement.GetProperty("branch").GetProperty("branch_id").GetString(),
                "run_loaded should keep matched branch id");

            JsonElement replayedDecision = records[4].RootElement.GetProperty("branch_decision");
            AssertEqual(true, replayedDecision.GetProperty("trajectory_replayed").GetBoolean(),
                "replayed known edge should be explicit on the decision frame");
            AssertEqual(originalDecisionFrameId,
                replayedDecision.GetProperty("matched_decision_frame_id").GetString(),
                "replayed edge should point to the original decision frame");
            AssertEqual("node-000002",
                replayedDecision.GetProperty("matched_child_node_id").GetString(),
                "replayed edge should point to the original child node");
            AssertEqual(false, replayedDecision.GetProperty("forked").GetBoolean(),
                "replayed edge should not fork");
            AssertEqual(false, replayedDecision.GetProperty("divergence_unknown").GetBoolean(),
                "replayed edge should not claim unknown divergence");
            AssertEqual("attempt-0002",
                records[4].RootElement.GetProperty("branch").GetProperty("attempt_id").GetString(),
                "replayed decision should remain in the replay attempt");

            JsonElement continuationDecision = records[5].RootElement.GetProperty("branch_decision");
            AssertEqual(false, continuationDecision.GetProperty("trajectory_replayed").GetBoolean(),
                "new continuation edge should not be marked replayed");
            AssertEqual(JsonValueKind.Null,
                continuationDecision.GetProperty("matched_decision_frame_id").ValueKind,
                "new continuation should not point at a matched decision");
            AssertEqual(JsonValueKind.Null,
                continuationDecision.GetProperty("matched_child_node_id").ValueKind,
                "new continuation should not point at a matched child");
            AssertEqual("new_child_from_leaf",
                continuationDecision.GetProperty("reason").GetString(),
                "new continuation reason");
            AssertEqual("attempt-0002",
                records[5].RootElement.GetProperty("branch").GetProperty("attempt_id").GetString(),
                "new continuation should remain in the replay attempt");

            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "replayed prefix plus new leaf continuation should not fork");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderPendingTransitionMarkersOmitBranchDecision()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            var action = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(action);
            recorder.FlushPendingAsTransitionMarkers("room_entered_before_post_state");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "pending marker record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "pending transition marker");

            JsonElement marker = records[1].RootElement;
            AssertMissingProperty(marker, "branch_decision",
                "pending transition markers should not invent completed decision branch metadata");
            AssertEqual("pending",
                marker.GetProperty("post_state").GetProperty("status").GetString(),
                "pending marker post-state status");
            AssertEqual("pending_transition",
                marker.GetProperty("post_state").GetProperty("visibility").GetString(),
                "pending marker post-state visibility");
            AssertEqual("room_entered_before_post_state",
                marker.GetProperty("post_state").GetProperty("reason").GetString(),
                "pending marker reason");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderSaveObservedSignalOnlyDoesNotCaptureOrSeedBranchMatch()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-b"),
            TestSnapshotWithHash("combat", "state-unmatched"),
            TestSnapshotWithHash("combat", "state-after-new"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(
                writer,
                snapshotBuilder,
                new LegalActionBuilder(),
                NoopNativeSaveCapture.Instance);
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            var firstAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(firstAction);
            recorder.CompleteActionExecutorDecision(firstAction);

            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.RecordSaveObserved("save_manager.save_run");

            var resumedAction = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(resumedAction);
            recorder.CompleteActionExecutorDecision(resumedAction);
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(5, records.Length, "signal-only save observed then unmatched decision record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("decision/frame", records[1], "known edge decision frame");
            AssertRecordType("lifecycle/save_observed", records[2], "signal-only save observed");
            AssertRecordType("lifecycle/run_started", records[3], "unmatched decision pre-state starts new run");
            AssertRecordType("decision/frame", records[4], "decision frame after new run");

            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                records[2].RootElement.GetProperty("capture_policy").GetString(),
                "save_observed should be signal-only");
            AssertEqual("save_observed_signal_only_transition_safety",
                records[2].RootElement.GetProperty("details").GetProperty("reason").GetString(),
                "save_observed should explain why state capture is disabled");
            AssertEqual("disabled",
                records[2].RootElement.GetProperty("details").GetProperty("state_capture").GetString(),
                "save_observed should not capture state");
            AssertEqual("disabled",
                records[2].RootElement.GetProperty("details").GetProperty("branch_index_update").GetString(),
                "save_observed should not seed branch index");
            AssertEqual("branch-0001",
                records[2].RootElement.GetProperty("branch").GetProperty("branch_id").GetString(),
                "pending non-decision record should not allocate a new branch");
            AssertEqual(2,
                records[2].RootElement.GetProperty("branch").GetProperty("known_state_count").GetInt32(),
                "save_observed signal should not index a save state");
            AssertMissingProperty(records[2].RootElement, "state", "save_observed should not capture lifecycle state");
            AssertMissingProperty(records[2].RootElement, "pre_state", "save_observed should not capture pre-state");
            AssertMissingProperty(records[2].RootElement, "post_state", "save_observed should not capture post-state");
            AssertMissingProperty(records[2].RootElement, "legal_actions", "save_observed should not build legal actions");

            string originalRunId = records[0].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("original run id missing");
            AssertEqual(originalRunId,
                records[2].RootElement.GetProperty("run_id").GetString(),
                "signal-only save_observed should stay in the original capture run");
            string newRunId = records[3].RootElement.GetProperty("run_id").GetString()
                ?? throw new InvalidOperationException("new run id missing");
            AssertNotEqual(originalRunId, newRunId, "unmatched decision pre-state should reset run id");
            AssertEqual(newRunId,
                records[4].RootElement.GetProperty("run_id").GetString(),
                "decision frame should use the new run id");

            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/run_loaded"),
                "unmatched non-decision state should not seed a run_loaded match");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_matched"),
                "unmatched non-decision state should not seed branch_matched");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "unmatched classification should not fork");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderExplicitUnmatchedLoadDoesNotFabricateBranchMatch()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithHash("combat", "state-a"),
            TestSnapshotWithHash("combat", "state-from-unknown-parent"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");
            recorder.OnRunLoaded(new TestRunState(), "run_manager.set_up_saved_single_player");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "explicit unmatched load record count");
            AssertRecordType("lifecycle/run_started", records[0], "initial run started");
            AssertRecordType("lifecycle/run_loaded", records[1], "explicit unmatched run load");

            JsonElement branchMatch = records[1].RootElement.GetProperty("branch_match");
            AssertEqual(false, branchMatch.GetProperty("matched").GetBoolean(), "unmatched load should be explicit");
            AssertEqual(false, branchMatch.GetProperty("pending_divergence").GetBoolean(),
                "unmatched load should not claim pending divergence");
            AssertEqual("resume_state_not_found", branchMatch.GetProperty("reason").GetString(),
                "unmatched load reason");
            AssertEqual(JsonValueKind.Null,
                branchMatch.GetProperty("matched_node_id").ValueKind,
                "unmatched load should not fabricate matched node id");
            AssertEqual(JsonValueKind.Null,
                branchMatch.GetProperty("parent_canonical_state_hash").ValueKind,
                "unmatched load should not fabricate parent hash");

            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_matched"),
                "unmatched load should not emit branch_matched");
            AssertTrue(records.All(record =>
                    record.RootElement.GetProperty("record_type").GetString() != "lifecycle/branch_forked"),
                "unmatched load should not emit branch_forked");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void LoadRunSavePatchRecordsSavePreviewOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-save-preview");
            SetStaticRecorderForTest(recorder);

            PatchCallbacks.AfterLoadRunSave();
        }

        string path = Path.Combine(directory, "runs", "run-save-preview", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for save preview");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "LoadRunSave should write exactly one preview record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("lifecycle/save_preview", root.GetProperty("record_type").GetString(), "save preview record type");
        AssertEqual("save_manager.load_run_save", root.GetProperty("source").GetString(), "save preview source");
        AssertEqual(false, root.GetProperty("details").GetProperty("loaded_run_signal").GetBoolean(),
            "LoadRunSave should not be treated as a loaded-run signal");
        AssertMissingProperty(root, "state", "save preview should not capture state");
        AssertTrue(!lines[0].Contains("lifecycle/run_loaded", StringComparison.Ordinal),
            "LoadRunSave should not emit run_loaded");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void SaveRunAndSavedHooksRecordSaveObservedSignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(
                writer,
                new ThrowingSnapshotBuilder(),
                new LegalActionBuilder(),
                NoopNativeSaveCapture.Instance);
            EnableCapturingForTest(recorder, "run-save-observed-signal");
            SetStaticRecorderForTest(recorder);

            PatchCallbacks.AfterSaveRun();
            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnRunSaveSaved");
        }

        string path = Path.Combine(directory, "runs", "run-save-observed-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for save observed signals");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(2, lines.Length, "SaveRun postfix and Saved event should each write one signal record");

        using JsonDocument first = JsonDocument.Parse(lines[0]);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        JsonElement[] roots = { first.RootElement, second.RootElement };
        string[] expectedSources = { "save_run.postfix", "run_save_manager.saved_event" };
        for (int i = 0; i < roots.Length; i++)
        {
            JsonElement root = roots[i];
            AssertEqual("lifecycle/save_observed", root.GetProperty("record_type").GetString(),
                "save observed record type");
            AssertEqual(expectedSources[i], root.GetProperty("source").GetString(), "save observed source");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "save observed capture policy");
            AssertEqual("save_observed_signal_only_transition_safety",
                root.GetProperty("details").GetProperty("reason").GetString(),
                "save observed transition safety reason");
            AssertEqual("disabled",
                root.GetProperty("details").GetProperty("state_capture").GetString(),
                "save observed state capture");
            AssertEqual("disabled",
                root.GetProperty("details").GetProperty("branch_index_update").GetString(),
                "save observed branch index update");
            AssertMissingProperty(root, "state", "save observed should not capture lifecycle state");
            AssertMissingProperty(root, "pre_state", "save observed should not capture pre-state");
            AssertMissingProperty(root, "post_state", "save observed should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "save observed should not build legal actions");
            AssertMissingProperty(root, "selected_action", "save observed should not include selected action");
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RunCleanupAbandonAndEndedHooksRecordSignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-volatile-lifecycle-signal");
            SetStaticRecorderForTest(recorder);

            PatchCallbacks.BeforeRunCleanUp(new object[] { true });
            EnableCapturingForTest(recorder, "run-volatile-lifecycle-signal");
            PatchCallbacks.BeforeRunAbandon();
            EnableCapturingForTest(recorder, "run-volatile-lifecycle-signal");
            PatchCallbacks.BeforeRunEnded(new object[] { true });
        }

        string path = Path.Combine(directory, "runs", "run-volatile-lifecycle-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for volatile lifecycle signals");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(3, lines.Length, "cleanup, abandon, and ended should each write one signal record");

        using JsonDocument cleanup = JsonDocument.Parse(lines[0]);
        using JsonDocument abandon = JsonDocument.Parse(lines[1]);
        using JsonDocument ended = JsonDocument.Parse(lines[2]);
        JsonElement[] roots = { cleanup.RootElement, abandon.RootElement, ended.RootElement };
        string[] expectedTypes = { "lifecycle/run_suspended", "lifecycle/run_suspended", "lifecycle/run_ended" };
        string[] expectedSources = { "run_manager.cleanup", "run_manager.abandon", "run_manager.on_ended" };
        for (int i = 0; i < roots.Length; i++)
        {
            JsonElement root = roots[i];
            AssertEqual(expectedTypes[i], root.GetProperty("record_type").GetString(),
                "volatile lifecycle record type");
            AssertEqual(expectedSources[i], root.GetProperty("source").GetString(),
                "volatile lifecycle source");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "volatile lifecycle capture policy");
            AssertMissingProperty(root, "state", "volatile lifecycle should not capture lifecycle state");
            AssertMissingProperty(root, "pre_state", "volatile lifecycle should not capture pre-state");
            AssertMissingProperty(root, "post_state", "volatile lifecycle should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "volatile lifecycle should not build legal actions");
            AssertMissingProperty(root, "selected_action", "volatile lifecycle should not include selected action");
        }

        AssertEqual(true, ended.RootElement.GetProperty("is_victory").GetBoolean(),
            "run ended should preserve victory flag");
        AssertEqual("run_ended_signal_only_transition_safety",
            ended.RootElement.GetProperty("details").GetProperty("reason").GetString(),
            "run ended should explain signal-only policy");
        AssertEqual("disabled",
            ended.RootElement.GetProperty("details").GetProperty("state_capture").GetString(),
            "run ended should disable state capture");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void PatchedActionMetadataIsShallowForUnsafeRuntimeObjects()
{
    var metadata = ActionMetadata.FromPatchedMethod(
        "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption",
        new ThrowingProjectionObject(),
        new object?[] { new ThrowingProjectionObject(), 7, "choice-id" });

    AssertEqual("choose_event_option", metadata["action_type"], "event action type");
    AssertEqual(typeof(ThrowingProjectionObject).FullName, metadata["runtime_type"], "instance runtime type");
    AssertEqual(3, metadata["argument_count"], "argument count");
    AssertTrue(!metadata.ContainsKey("display"), "unsafe instance display should not be projected");

    var arguments = (IReadOnlyList<object?>)metadata["arguments"]!;
    var first = (IReadOnlyDictionary<string, object?>)arguments[0]!;
    AssertEqual(typeof(ThrowingProjectionObject).FullName, first["type"], "unsafe argument type");
    AssertEqual("type_only", first["projection_policy"], "unsafe argument projection policy");
    AssertTrue(!first.ContainsKey("display"), "unsafe argument display should not be projected");
    AssertTrue(!first.ContainsKey("id"), "unsafe argument id should not be projected");
    AssertTrue(!first.ContainsKey("name"), "unsafe argument name should not be projected");

    var second = (IReadOnlyDictionary<string, object?>)arguments[1]!;
    AssertEqual(7, second["value"], "stable scalar argument value");
}

static void RuntimeActionMetadataIsShallowWhenStringificationIsUnsafe()
{
    var metadata = ActionMetadata.FromRuntimeAction(new GenericHookGameAction(), "action_executor");

    AssertEqual("action_executor", metadata["source"], "runtime action source");
    AssertEqual(typeof(GenericHookGameAction).FullName, metadata["runtime_type"], "runtime action type");
    AssertEqual("generic_hook_action", metadata["action_type"], "stable scalar action type");
    AssertEqual("shallow_runtime_action", metadata["projection_policy"], "runtime action projection policy");
    AssertEqual("generic_hook", metadata["action_family"], "generic hook action family");
    AssertEqual("skipped_generic_hook_runtime_safety",
        metadata["net_action_projection"],
        "GenericHookGameAction ToNetAction should not be called");
    AssertTrue(!metadata.ContainsKey("display"), "unsafe runtime action ToString should not be called");
    AssertEqual("hook.after_act_entered", metadata["hook_id"], "stable hook id");

    var choiceContext = (IReadOnlyDictionary<string, object?>)metadata["choice_context"]!;
    AssertEqual(typeof(ThrowingProjectionObject).FullName, choiceContext["type"], "unsafe action member type");
    AssertEqual("type_only", choiceContext["projection_policy"], "unsafe action member projection policy");
    AssertTrue(!choiceContext.ContainsKey("name"), "unsafe action member name should not be read");

    var card = (IReadOnlyDictionary<string, object?>)metadata["card"]!;
    AssertEqual(typeof(ThrowingProjectionObject).FullName, card["type"], "unsafe card member type");
    AssertEqual("type_only", card["projection_policy"], "unsafe card projection policy");
}

static void ReflectionCachedMemberLookupPreservesNullVersusMissing()
{
    var target = new NullableMemberProbe();

    AssertTrue(
        ReflectionUtil.TryReadMemberValue(target, out object? propertyValue, "NullableProperty"),
        "null property should still count as a found member");
    AssertEqual(null, propertyValue, "null property value");

    AssertTrue(
        ReflectionUtil.TryReadMemberValue(target, out object? fieldValue, "_nullableField"),
        "private null field should still count as a found member");
    AssertEqual(null, fieldValue, "null field value");

    AssertTrue(
        !ReflectionUtil.TryReadMemberValue(target, out object? missingValue, "MissingMember"),
        "missing member should not count as found");
    AssertEqual(null, missingValue, "missing member output value");
}

static void NormalizedTypedPlayCardKeyUsesTrustedFields()
{
    var raw = new Dictionary<string, object?>
    {
        ["source"] = "action_executor",
        ["runtime_type"] = "MegaCrit.Sts2.Core.GameActions.PlayCardAction",
        ["runtime_type_name"] = "PlayCardAction",
        ["action_type"] = "CombatPlayPhaseOnly",
        ["projection_policy"] = "shallow_runtime_action",
        ["card_model_id"] = "STORM_OF_STEEL",
        ["net_combat_card_index"] = 0,
        ["card_index"] = 1,
        ["card_id"] = "STORM_OF_STEEL",
        ["card_name"] = "Storm of Steel",
        ["card_target_type"] = "AnyEnemy",
        ["target_id"] = 7,
        ["target_index"] = 0,
        ["target_index_space"] = "enemies",
        ["target_entity_id"] = "JAW_WORM_0",
        ["target_type"] = "enemy",
        ["target_name"] = "Jaw Worm",
        ["extraction_status"] = new Dictionary<string, object?>
        {
            ["pre_state_card"] = "matched_pre_state_hand_unique_card_id",
            ["target"] = "matched_pre_state_target_candidates"
        }
    };

    IReadOnlyDictionary<string, object?> key = ActionMetadata.BuildNormalizedTypedActionKey(raw);

    AssertEqual("play_card", key["action_type"], "normalized play-card action type");
    AssertEqual("STORM_OF_STEEL", key["card_id"], "card identity should use action-owned card_model_id");
    AssertEqual(1, key["hand_index"], "hand index should come from unique safe pre-state match");
    AssertTrue(!key.ContainsKey("net_combat_card_index"),
        "combat-card runtime id should not be part of canonical action identity");
    AssertTrue(!key.ContainsKey("card_name"), "card display name should not be part of canonical action identity");
    AssertTrue(!key.ContainsKey("extraction_status"), "diagnostic status should not be part of canonical action identity");

    var target = (IReadOnlyDictionary<string, object?>)key["target"]!;
    AssertEqual(7, target["target_id"], "target action id");
    AssertEqual("enemies", target["target_index_space"], "target index space");
    AssertEqual(0, target["target_index"], "target index");
    AssertEqual("JAW_WORM_0", target["target_entity_id"], "target entity id");
    AssertTrue(!target.ContainsKey("target_name"), "target display name should not be part of canonical target identity");

    var noisyRaw = new Dictionary<string, object?>(raw)
    {
        ["net_combat_card_index"] = 99,
        ["card_name"] = "Different Display Name",
        ["target_name"] = "Different Target Name",
        ["projection_policy"] = "changed_debug_policy"
    };

    AssertNotEqual(TelemetryHash.HashCanonical(raw), TelemetryHash.HashCanonical(noisyRaw),
        "raw canonical hash would still move when diagnostic fields change");
    AssertEqual(
        TelemetryHash.HashCanonical(key),
        TelemetryHash.HashCanonical(ActionMetadata.BuildNormalizedTypedActionKey(noisyRaw)),
        "normalized typed key hash should ignore raw diagnostic fields");
}

static void NormalizedTypedPlayCardKeySuppressesUntrustedFields()
{
    var selfRaw = new Dictionary<string, object?>
    {
        ["runtime_type_name"] = "PlayCardAction",
        ["card_model_id"] = "INFINITE_BLADES",
        ["card_index"] = 0,
        ["card_id"] = "INFINITE_BLADES",
        ["card_target_type"] = "Self",
        ["target_id"] = 7,
        ["target_index"] = 0,
        ["target_index_space"] = "enemies",
        ["target_entity_id"] = "JAW_WORM_0",
        ["extraction_status"] = new Dictionary<string, object?>
        {
            ["pre_state_card"] = "matched_pre_state_hand_unique_card_id",
            ["target"] = "matched_pre_state_target_candidates"
        }
    };

    IReadOnlyDictionary<string, object?> selfKey = ActionMetadata.BuildNormalizedTypedActionKey(selfRaw);

    AssertEqual("play_card", selfKey["action_type"], "self card action type");
    AssertEqual("INFINITE_BLADES", selfKey["card_id"], "self card identity");
    AssertEqual(0, selfKey["hand_index"], "self card safe hand index");
    AssertTrue(!selfKey.ContainsKey("target"),
        "Self/no-explicit-target cards should not hash a copied target even if raw contains one");

    var generatedRaw = new Dictionary<string, object?>
    {
        ["runtime_type_name"] = "PlayCardAction",
        ["card_model_id"] = "SHIV",
        ["net_combat_card_index"] = 42,
        ["target_id"] = 7,
        ["extraction_status"] = new Dictionary<string, object?>
        {
            ["pre_state_card"] = "pre_state_hand_card_id_match_not_found",
            ["target"] = "suppressed_selected_card_target_type_unavailable"
        }
    };

    IReadOnlyDictionary<string, object?> generatedKey = ActionMetadata.BuildNormalizedTypedActionKey(generatedRaw);

    AssertEqual("play_card", generatedKey["action_type"], "generated card action type");
    AssertEqual("SHIV", generatedKey["card_id"], "generated card should still have action-owned card identity");
    AssertTrue(!generatedKey.ContainsKey("hand_index"), "unmatched generated card should not fabricate hand index");
    AssertTrue(!generatedKey.ContainsKey("target"), "unmatched generated card should not hash an untrusted target");
    AssertTrue(!generatedKey.ContainsKey("net_combat_card_index"),
        "generated card runtime instance id should stay out of canonical identity");
}

static void NormalizedTypedUsePotionKeyUsesTrustedFields()
{
    var raw = new Dictionary<string, object?>
    {
        ["runtime_type_name"] = "UsePotionAction",
        ["action_type"] = "CombatPlayPhaseOnly",
        ["slot"] = 1,
        ["potion_index"] = 1,
        ["potion_id"] = "FIRE_POTION",
        ["potion_name"] = "Fire Potion",
        ["potion_target_type"] = "AnyEnemy",
        ["usage"] = "CombatOnly",
        ["was_enqueued_in_combat"] = true,
        ["target_id"] = 7,
        ["target_index"] = 0,
        ["target_index_space"] = "enemies",
        ["target_entity_id"] = "JAW_WORM_0",
        ["target_type"] = "enemy",
        ["target_name"] = "Jaw Worm",
        ["extraction_status"] = new Dictionary<string, object?>
        {
            ["potion"] = "matched_pre_state_potions_slot",
            ["target"] = "matched_pre_state_target_candidates"
        }
    };

    IReadOnlyDictionary<string, object?> key = ActionMetadata.BuildNormalizedTypedActionKey(raw);

    AssertEqual("use_potion", key["action_type"], "normalized use-potion action type");
    AssertEqual("FIRE_POTION", key["potion_id"], "potion identity");
    AssertEqual(1, key["slot"], "potion slot");
    AssertTrue(!key.ContainsKey("potion_name"), "potion display name should not be part of canonical action identity");
    AssertTrue(!key.ContainsKey("was_enqueued_in_combat"),
        "queue/debug flag should not be part of canonical action identity");

    var target = (IReadOnlyDictionary<string, object?>)key["target"]!;
    AssertEqual(7, target["target_id"], "potion target action id");
    AssertEqual("enemies", target["target_index_space"], "potion target index space");
    AssertEqual(0, target["target_index"], "potion target index");
    AssertEqual("JAW_WORM_0", target["target_entity_id"], "potion target entity id");

    var selfPotionRaw = new Dictionary<string, object?>(raw)
    {
        ["potion_id"] = "SPEED_POTION",
        ["potion_target_type"] = "Self"
    };
    IReadOnlyDictionary<string, object?> selfPotionKey = ActionMetadata.BuildNormalizedTypedActionKey(selfPotionRaw);
    AssertEqual("SPEED_POTION", selfPotionKey["potion_id"], "self potion identity");
    AssertTrue(!selfPotionKey.ContainsKey("target"), "Self potion should not hash a selected target");
}

static void NormalizedTypedDiscardPotionKeyUsesMatchedSlot()
{
    var preState = TestSnapshotWithRaw(
        "event",
        new Dictionary<string, object?>
        {
            ["state_type"] = "event",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["potions"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["slot"] = 1,
                        ["potion_id"] = "FIRE_POTION",
                        ["potion_name"] = "Fire Potion",
                        ["target_type"] = "AnyEnemy",
                        ["usage"] = "CombatOnly"
                    }
                }
            }
        });
    var metadata = ActionMetadata.FromRuntimeAction(
        new FakeDiscardPotionGameAction(1, wasEnqueuedInCombat: false),
        "action_executor",
        preState);

    AssertEqual(1, metadata["slot"], "discard potion slot");
    AssertEqual("FIRE_POTION", metadata["potion_id"], "matched potion id");
    AssertEqual(false, metadata["was_enqueued_in_combat"], "non-combat discard flag");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("matched_pre_state_potions_slot", status["potion"], "discard potion match status");

    IReadOnlyDictionary<string, object?> key = ActionMetadata.BuildNormalizedTypedActionKey(metadata);
    AssertEqual("discard_potion", key["action_type"], "normalized discard action type");
    AssertEqual(1, key["slot"], "discard key slot");
    AssertEqual("FIRE_POTION", key["potion_id"], "discard key potion id");
    AssertTrue(!key.ContainsKey("potion_name"), "discard key should not include display name");
}

static void NormalizedTypedTreasureRelicKeyUsesPickOrSkip()
{
    var synchronizer = new TestTreasureRoomRelicSynchronizer
    {
        CurrentRelics = new[]
        {
            new TestRelic
            {
                Id = new TestModelId { Entry = "BAG_OF_PREP" },
                Rarity = "Rare"
            }
        }
    };
    var pickMetadata = ActionMetadata.FromRuntimeAction(
        new FakePickRelicAction(0, synchronizer),
        "action_executor");

    AssertEqual(0, pickMetadata["relic_index"], "picked relic index");
    AssertEqual("choose_treasure_relic", pickMetadata["selection_kind"], "pick selection kind");
    AssertEqual("BAG_OF_PREP", pickMetadata["relic_id"], "picked relic id");
    var pickStatus = (IReadOnlyDictionary<string, object?>)pickMetadata["extraction_status"]!;
    AssertEqual("matched_current_treasure_relics_index", pickStatus["relic"], "picked relic match status");

    IReadOnlyDictionary<string, object?> pickKey = ActionMetadata.BuildNormalizedTypedActionKey(pickMetadata);
    AssertEqual("choose_treasure_relic", pickKey["action_type"], "pick key action type");
    AssertEqual(0, pickKey["relic_index"], "pick key relic index");
    AssertEqual("BAG_OF_PREP", pickKey["relic_id"], "pick key relic id");

    var skipMetadata = ActionMetadata.FromRuntimeAction(
        new FakePickRelicAction(null, synchronizer),
        "action_executor");
    IReadOnlyDictionary<string, object?> skipKey = ActionMetadata.BuildNormalizedTypedActionKey(skipMetadata);
    AssertEqual("skip_treasure_relic", skipKey["action_type"], "skip key action type");
    AssertTrue(skipKey.ContainsKey("relic_index"), "skip key should explicitly carry null relic index");
    AssertEqual(null, skipKey["relic_index"], "skip key relic index");
}

static void RuntimePlayCardMetadataIgnoresNetCombatCardIndexForHandMatch()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "INFINITE_BLADES",
                        ["card_name"] = "Infinite Blades",
                        ["card_type"] = "Power",
                        ["target_type"] = "Self"
                    },
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 1,
                        ["card_id"] = "STORM_OF_STEEL",
                        ["card_name"] = "Storm of Steel",
                        ["card_type"] = "Attack",
                        ["target_type"] = "AnyEnemy"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["target_candidates"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target_index_space"] = "enemies",
                        ["target_index"] = 0,
                        ["target_type"] = "enemy",
                        ["entity_id"] = "JAW_WORM_0",
                        ["combat_id"] = 7,
                        ["name"] = "Jaw Worm"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "STORM_OF_STEEL" },
        NetCombatCard = new FakeNetCombatCard { CombatCardIndex = 0 },
        TargetId = 7
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual("CombatPlayPhaseOnly", metadata["action_type"], "play-card action type");
    AssertEqual("STORM_OF_STEEL", metadata["card_model_id"], "card model id");
    AssertEqual(0, metadata["net_combat_card_index"], "net combat card index");
    AssertEqual(1, metadata["card_index"], "pre-state hand card index");
    AssertEqual("STORM_OF_STEEL", metadata["card_id"], "matched card id");
    AssertEqual("Storm of Steel", metadata["card_name"], "matched card name");
    AssertEqual("AnyEnemy", metadata["card_target_type"], "matched card target type");
    AssertEqual(7, metadata["target_id"], "target id");
    AssertEqual(0, metadata["target_index"], "target index");
    AssertEqual("enemies", metadata["target_index_space"], "target index space");
    AssertEqual("enemy", metadata["target_type"], "selected target type");
    AssertEqual("JAW_WORM_0", metadata["target_entity_id"], "target entity id");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("matched_pre_state_hand_unique_card_id", status["card"], "card extraction status");
    AssertEqual("skipped_net_combat_card_index_runtime_identity",
        status["card_index_match"],
        "card index extraction status");
    AssertEqual("matched_pre_state_hand_unique_card_id", status["pre_state_card"], "pre-state card status");
    AssertEqual("matched_pre_state_target_candidates", status["target"], "target extraction status");
}

static void RuntimePlayCardMetadataFallsBackToUniqueCardId()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "DEFEND",
                        ["card_name"] = "Defend",
                        ["card_type"] = "Skill"
                    },
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 1,
                        ["card_id"] = "STRIKE",
                        ["card_name"] = "Strike",
                        ["card_type"] = "Attack",
                        ["target_type"] = "AnyEnemy"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "STRIKE" }
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual(1, metadata["card_index"], "fallback card index");
    AssertEqual("STRIKE", metadata["card_id"], "fallback card id");
    AssertEqual("Strike", metadata["card_name"], "fallback card name");
    AssertEqual("AnyEnemy", metadata["card_target_type"], "fallback card target type");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("net_combat_card_index_unavailable", status["card_index_match"], "missing index status");
    AssertEqual("matched_pre_state_hand_unique_card_id", status["card"], "unique id fallback status");
    AssertEqual("no_target", status["target"], "targetless fallback status");
}

static void RuntimePlayCardMetadataPreservesGeneratedActionIdentity()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "DEFEND",
                        ["card_name"] = "Defend",
                        ["card_type"] = "Skill",
                        ["target_type"] = "Self"
                    },
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 1,
                        ["card_id"] = "STRIKE",
                        ["card_name"] = "Strike",
                        ["card_type"] = "Attack",
                        ["target_type"] = "AnyEnemy"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["target_candidates"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target_index_space"] = "enemies",
                        ["target_index"] = 0,
                        ["target_type"] = "enemy",
                        ["entity_id"] = "JAW_WORM_0",
                        ["combat_id"] = 7,
                        ["name"] = "Jaw Worm"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "SHIV" },
        NetCombatCard = new FakeNetCombatCard { CombatCardIndex = 42 },
        TargetId = 7
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual("SHIV", metadata["card_model_id"], "generated card model id retained");
    AssertEqual(42, metadata["net_combat_card_index"], "generated card combat id retained");
    AssertEqual(7, metadata["target_id"], "target id retained");
    AssertTrue(!metadata.ContainsKey("card_id"), "unmatched generated card should not fabricate card_id");
    AssertTrue(!metadata.ContainsKey("card_index"), "unmatched generated card should not fabricate card_index");
    AssertTrue(!metadata.ContainsKey("card_name"), "unmatched generated card should not copy unrelated card name");
    AssertTrue(!metadata.ContainsKey("card_target_type"), "unmatched generated card should not copy unrelated target type");
    AssertEqual(0, metadata["target_index"], "unmatched generated card should still recover target index from target id");
    AssertEqual("JAW_WORM_0", metadata["target_entity_id"], "unmatched generated card should still recover target entity id");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("extracted", status["card_model_id"], "card model id status");
    AssertEqual("extracted", status["net_combat_card_index"], "net combat card status");
    AssertEqual("extracted", status["target_id"], "target id status");
    AssertEqual("pre_state_hand_card_id_match_not_found", status["card"], "generated card join status");
    AssertEqual("pre_state_hand_card_id_match_not_found", status["pre_state_card"], "pre-state join status");
    AssertEqual("skipped_net_combat_card_index_runtime_identity",
        status["card_index_match"],
        "combat card index should remain runtime identity only");
    AssertEqual("matched_pre_state_target_candidates_without_card_target_type", status["target"],
        "target metadata should fall back to pre-state target id without a safe selected card target type");
}

static void RuntimePlayCardMetadataFallsBackToPreStateTargetId()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "MYSTERY_ATTACK",
                        ["card_name"] = "Mystery Attack",
                        ["card_type"] = "Attack"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["enemies"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["combat_id"] = 77,
                        ["enemy_id"] = "CULTIST",
                        ["name"] = "Cultist"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "MYSTERY_ATTACK" },
        TargetId = 77
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);
    AssertEqual(77, metadata["target_id"], "runtime target id");
    AssertEqual(0, metadata["target_index"], "fallback target index");
    AssertEqual("enemies", metadata["target_index_space"], "fallback target index space");
    AssertEqual("enemy", metadata["target_type"], "fallback target type");
    AssertEqual("CULTIST", metadata["target_entity_id"], "fallback target entity id");

    IReadOnlyDictionary<string, object?> key = ActionMetadata.BuildNormalizedTypedActionKey(metadata);
    AssertTrue(key.ContainsKey("target"), "normalized key should keep recovered target");
    var target = (IReadOnlyDictionary<string, object?>)key["target"]!;
    AssertEqual(77, target["target_id"], "normalized target id");
    AssertEqual("CULTIST", target["target_entity_id"], "normalized target entity");
}

static void RuntimePlayCardMetadataReportsDuplicateCardIdsAmbiguous()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "STRIKE",
                        ["card_name"] = "Strike A",
                        ["card_type"] = "Attack",
                        ["target_type"] = "AnyEnemy"
                    },
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 1,
                        ["card_id"] = "STRIKE",
                        ["card_name"] = "Strike B",
                        ["card_type"] = "Attack",
                        ["target_type"] = "AnyEnemy"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["target_candidates"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target_index_space"] = "enemies",
                        ["target_index"] = 0,
                        ["target_type"] = "enemy",
                        ["entity_id"] = "JAW_WORM_0",
                        ["combat_id"] = 7,
                        ["name"] = "Jaw Worm"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "STRIKE" },
        NetCombatCard = new FakeNetCombatCard { CombatCardIndex = 1 },
        TargetId = 7
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual("STRIKE", metadata["card_model_id"], "card model id");
    AssertEqual(1, metadata["net_combat_card_index"], "net combat card index");
    AssertEqual(7, metadata["target_id"], "target id retained");
    AssertTrue(!metadata.ContainsKey("card_id"), "ambiguous duplicate card should not fabricate card_id");
    AssertTrue(!metadata.ContainsKey("card_index"), "ambiguous duplicate card should not fabricate card_index");
    AssertTrue(!metadata.ContainsKey("card_name"), "ambiguous duplicate card should not copy one card name");
    AssertTrue(!metadata.ContainsKey("card_target_type"), "ambiguous duplicate card should not copy one target type");
    AssertEqual(0, metadata["target_index"], "ambiguous card should still recover target index from target id");
    AssertEqual("JAW_WORM_0", metadata["target_entity_id"], "ambiguous card should still recover target entity id");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("ambiguous_pre_state_hand_card_id_match", status["card"], "ambiguous card status");
    AssertEqual("ambiguous_pre_state_hand_card_id_match", status["pre_state_card"], "ambiguous pre-state status");
    AssertEqual(2, status["card_match_count"], "ambiguous match count");
    AssertEqual("skipped_net_combat_card_index_runtime_identity",
        status["card_index_match"],
        "combat card index should not be used as hand index");
    AssertEqual("matched_pre_state_target_candidates_without_card_target_type", status["target"],
        "target should fall back to target id without selected card target type");
    AssertEqual("unavailable", status["target_card_target_type"], "fallback target type status");
}

static void RuntimePlayCardMetadataSuppressesInvalidSelectedTarget()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["hand"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["card_index"] = 0,
                        ["card_id"] = "INFINITE_BLADES",
                        ["card_name"] = "Infinite Blades",
                        ["card_type"] = "Power",
                        ["target_type"] = "Self"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["target_candidates"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target_index_space"] = "enemies",
                        ["target_index"] = 0,
                        ["target_type"] = "enemy",
                        ["entity_id"] = "JAW_WORM_0",
                        ["combat_id"] = 7,
                        ["name"] = "Jaw Worm"
                    }
                }
            }
        });

    var action = new FakePlayCardAction
    {
        CardModelId = new TestModelId { Entry = "INFINITE_BLADES" },
        NetCombatCard = new FakeNetCombatCard { CombatCardIndex = 12 },
        TargetId = 7
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual("INFINITE_BLADES", metadata["card_model_id"], "card model id");
    AssertEqual(12, metadata["net_combat_card_index"], "net combat card index");
    AssertEqual(0, metadata["card_index"], "matched card index");
    AssertEqual("INFINITE_BLADES", metadata["card_id"], "matched card id");
    AssertEqual("Self", metadata["card_target_type"], "matched card target type");
    AssertEqual(7, metadata["target_id"], "target id retained");
    AssertTrue(!metadata.ContainsKey("target"), "Self card should not copy target object");
    AssertTrue(!metadata.ContainsKey("target_index"), "Self card should not copy target index");
    AssertTrue(!metadata.ContainsKey("target_index_space"), "Self card should not copy target index space");
    AssertTrue(!metadata.ContainsKey("target_entity_id"), "Self card should not copy target entity id");
    AssertTrue(!metadata.ContainsKey("target_type"), "Self card should not copy selected target type");
    AssertTrue(!metadata.ContainsKey("target_name"), "Self card should not copy target name");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("matched_pre_state_hand_unique_card_id", status["card"], "card extraction status");
    AssertEqual("suppressed_selected_card_target_type", status["target"], "suppressed target status");
    AssertEqual("Self", status["target_card_target_type"], "suppressed card target type");
}

static void RuntimeUsePotionMetadataEnrichesFromCombatPreState()
{
    var preState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["potions"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["slot"] = 0,
                        ["potion_id"] = "SPEED_POTION",
                        ["potion_name"] = "Speed Potion",
                        ["target_type"] = "Self",
                        ["usage"] = "AnyTime"
                    },
                    new Dictionary<string, object?>
                    {
                        ["slot"] = 1,
                        ["potion_id"] = "FIRE_POTION",
                        ["potion_name"] = "Fire Potion",
                        ["target_type"] = "AnyEnemy",
                        ["usage"] = "CombatOnly"
                    }
                }
            },
            ["combat"] = new Dictionary<string, object?>
            {
                ["target_candidates"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["target_index_space"] = "enemies",
                        ["target_index"] = 0,
                        ["target_type"] = "enemy",
                        ["entity_id"] = "JAW_WORM_0",
                        ["combat_id"] = 7,
                        ["name"] = "Jaw Worm"
                    }
                }
            }
        });

    var action = new FakeUsePotionAction
    {
        PotionIndex = 1,
        TargetId = 7,
        WasEnqueuedInCombat = true
    };

    var metadata = ActionMetadata.FromRuntimeAction(action, "action_executor", preState);

    AssertEqual("CombatPlayPhaseOnly", metadata["action_type"], "use-potion action type");
    AssertEqual(1, metadata["slot"], "matched potion slot");
    AssertEqual(1, metadata["potion_index"], "matched potion index");
    AssertEqual("FIRE_POTION", metadata["potion_id"], "matched potion id");
    AssertEqual("Fire Potion", metadata["potion_name"], "matched potion name");
    AssertEqual("AnyEnemy", metadata["potion_target_type"], "matched potion target type");
    AssertEqual("CombatOnly", metadata["usage"], "matched potion usage");
    AssertEqual(true, metadata["was_enqueued_in_combat"], "combat enqueue flag");
    AssertEqual(7, metadata["target_id"], "target id");
    AssertEqual(0, metadata["target_index"], "target index");
    AssertEqual("enemy", metadata["target_type"], "matched target type");
    AssertEqual("JAW_WORM_0", metadata["target_entity_id"], "target entity id");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("matched_pre_state_potions_slot", status["potion"], "potion extraction status");
    AssertEqual("matched_pre_state_target_candidates", status["target"], "target extraction status");
}

static void RuntimeUsePotionMetadataReportsExtractionGaps()
{
    var unavailablePreState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>()
        });
    var unavailable = ActionMetadata.FromRuntimeAction(
        new FakeUsePotionAction { PotionIndex = 0 },
        "action_executor",
        unavailablePreState);
    var unavailableStatus = (IReadOnlyDictionary<string, object?>)unavailable["extraction_status"]!;
    AssertEqual("pre_state_potions_unavailable", unavailableStatus["potion"], "missing potions status");

    var missingSlotPreState = TestSnapshotWithRaw(
        "combat",
        new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["local_player"] = new Dictionary<string, object?>
            {
                ["potions"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["slot"] = 0,
                        ["potion_id"] = "FIRE_POTION"
                    }
                }
            }
        });
    var missing = ActionMetadata.FromRuntimeAction(
        new FakeUsePotionAction { PotionIndex = 3 },
        "action_executor",
        missingSlotPreState);
    var missingStatus = (IReadOnlyDictionary<string, object?>)missing["extraction_status"]!;
    AssertEqual("pre_state_potion_slot_match_not_found", missingStatus["potion"], "missing slot status");

    var noIndex = ActionMetadata.FromRuntimeAction(
        new FakeUsePotionAction(),
        "action_executor",
        missingSlotPreState);
    var noIndexStatus = (IReadOnlyDictionary<string, object?>)noIndex["extraction_status"]!;
    AssertEqual("potion_index_unavailable", noIndexStatus["potion"], "missing potion index status");
}

static void ActionExecutorCapturePolicyMarksApprovedVolatileActionsSignalOnly()
{
    string[] signalOnlyActionTypes =
    {
        "MegaCrit.Sts2.Core.GameActions.GenericHookGameAction",
        "MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction",
        "MegaCrit.Sts2.Core.GameActions.VoteForMapCoordAction",
        "MegaCrit.Sts2.Core.GameActions.VoteToMoveToNextActAction",
        "MegaCrit.Sts2.Core.GameActions.ReadyToBeginEnemyTurnAction",
        "MegaCrit.Sts2.Core.GameActions.UndoEndPlayerTurnAction"
    };

    Assembly gameAssembly = typeof(RunManager).Assembly;
    foreach (string actionTypeName in signalOnlyActionTypes)
    {
        string simpleName = actionTypeName[(actionTypeName.LastIndexOf('.') + 1)..];
        AssertTrue(ActionExecutorCapturePolicy.IsSignalOnlyTypeName(actionTypeName),
            $"{actionTypeName} should be signal-only by full name");
        AssertTrue(ActionExecutorCapturePolicy.IsSignalOnlyTypeName(simpleName),
            $"{simpleName} should be signal-only by simple name");

        Type actionType = gameAssembly.GetType(actionTypeName)
            ?? throw new InvalidOperationException($"missing game action type {actionTypeName}");
        AssertTrue(ActionExecutorCapturePolicy.IsSignalOnlyType(actionType),
            $"{actionTypeName} should be signal-only by reflected game type");
    }

    string[] fullFrameActionTypes =
    {
        "MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction",
        "MegaCrit.Sts2.Core.GameActions.PlayCardAction",
        "MegaCrit.Sts2.Core.GameActions.UsePotionAction",
        "MegaCrit.Sts2.Core.GameActions.DiscardPotionGameAction",
        "MegaCrit.Sts2.Core.GameActions.PickRelicAction",
        "MegaCrit.Sts2.Core.GameActions.NotListedAction"
    };

    foreach (string actionTypeName in fullFrameActionTypes)
    {
        string simpleName = actionTypeName[(actionTypeName.LastIndexOf('.') + 1)..];
        AssertTrue(!ActionExecutorCapturePolicy.IsSignalOnlyTypeName(actionTypeName),
            $"{actionTypeName} should remain full-frame by full name");
        AssertTrue(!ActionExecutorCapturePolicy.IsSignalOnlyTypeName(simpleName),
            $"{simpleName} should remain full-frame by simple name");

        Type? actionType = gameAssembly.GetType(actionTypeName);
        if (actionType != null)
            AssertTrue(!ActionExecutorCapturePolicy.IsSignalOnlyType(actionType),
                $"{actionTypeName} should remain full-frame by reflected game type");
    }

    AssertTrue(!ActionExecutorCapturePolicy.IsSignalOnly(new NotListedAction()),
        "a non-listed runtime action object should remain full-frame");
}

static void ActionExecutorCallbacksRouteSignalOnlyActionsWithoutPendingMissingMarkers()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-action-callback-signal");
            SetStaticRecorderForTest(recorder);

            string[] signalOnlyActionTypes =
            {
                "MegaCrit.Sts2.Core.GameActions.GenericHookGameAction",
                "MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction",
                "MegaCrit.Sts2.Core.GameActions.VoteForMapCoordAction",
                "MegaCrit.Sts2.Core.GameActions.VoteToMoveToNextActAction",
                "MegaCrit.Sts2.Core.GameActions.ReadyToBeginEnemyTurnAction",
                "MegaCrit.Sts2.Core.GameActions.UndoEndPlayerTurnAction"
            };

            foreach (string actionTypeName in signalOnlyActionTypes)
            {
                var action = CreateUninitializedGameAction(actionTypeName);
                InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnBeforeActionExecuted", action);
                AssertEqual(0, GetPendingDecisionCount(recorder),
                    $"{actionTypeName} before signal should not create pending decisions");

                InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnAfterActionExecuted", action);
                AssertEqual(0, GetPendingDecisionCount(recorder),
                    $"{actionTypeName} after signal should not create pending decisions");
            }
        }

        string path = Path.Combine(directory, "runs", "run-action-callback-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for ActionExecutor callback signals");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(12, lines.Length, "each signal-only action should write before and after action signals");

        int beforeCount = 0;
        int afterCount = 0;
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            AssertEqual("decision/action_signal", root.GetProperty("record_type").GetString(), "callback signal record type");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "callback signal capture policy");
            AssertMissingProperty(root, "decision_frame_id", "callback signal should not have a decision frame id");
            AssertMissingProperty(root, "state", "callback signal should not capture lifecycle state");
            AssertMissingProperty(root, "pre_state", "callback signal should not capture pre-state");
            AssertMissingProperty(root, "post_state", "callback signal should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "callback signal should not build legal actions");
            AssertMissingProperty(root, "selected_action", "callback signal should not be a full selected-action frame");

            string? phase = root.GetProperty("phase").GetString();
            if (phase == "before_action_executed")
                beforeCount++;
            else if (phase == "after_action_executed")
                afterCount++;
            else
                throw new InvalidOperationException($"unexpected signal phase {phase}");
        }

        AssertEqual(6, beforeCount, "six before-action signals");
        AssertEqual(6, afterCount, "six after-action signals");
        AssertTrue(!lines.Any(line => line.Contains("lifecycle/pending_decision_missing", StringComparison.Ordinal)),
            "after-action signals should not create pending-missing markers");
        AssertTrue(!Directory.Exists(Path.Combine(directory, "operational")),
            "signal-only callbacks should not be routed through telemetry callback failures");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ActionExecutorCallbacksKeepNormalActionsFullFrame()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var snapshotBuilder = new StaticSnapshotBuilder("event");
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-action-full-frame");
            SetStaticRecorderForTest(recorder);

            var action = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            AssertTrue(!ActionExecutorCapturePolicy.IsSignalOnly(action),
                "EndPlayerTurnAction should remain eligible for full decision frames");

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnBeforeActionExecuted", action);
            AssertEqual(1, GetPendingDecisionCount(recorder), "normal action before callback should create a pending decision");
            AssertEqual(1, snapshotBuilder.CaptureCount, "normal action before callback should capture pre-state");
            AssertEqual(2, snapshotBuilder.SafeRunStateCount, "normal action before callback should read run state and refresh stable relic observation");
            AssertEqual(2, snapshotBuilder.GetLocalPlayerCount, "normal action before callback should read local player and refresh stable relic observation");

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnAfterActionExecuted", action);
            AssertEqual(0, GetPendingDecisionCount(recorder), "normal action after callback should complete the pending decision");
            AssertEqual(2, snapshotBuilder.CaptureCount, "normal action after callback should capture post-state");
        }

        string path = Path.Combine(directory, "runs", "run-action-full-frame", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for normal ActionExecutor frame");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "normal ActionExecutor before/after pair should write one full decision frame");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("decision/frame", root.GetProperty("record_type").GetString(), "normal action record type");
        AssertEqual("action_executor", root.GetProperty("decision_source").GetString(), "normal action decision source");
        _ = root.GetProperty("decision_frame_id");
        _ = root.GetProperty("pre_state");
        _ = root.GetProperty("legal_actions");
        JsonElement selectedAction = root.GetProperty("selected_action");
        _ = root.GetProperty("post_state");
        JsonElement normalizedKey = selectedAction.GetProperty("normalized_typed_action_key");
        AssertEqual("end_turn", normalizedKey.GetProperty("action_type").GetString(),
            "normal frame should include normalized selected-action key");
        AssertMissingProperty(normalizedKey, "runtime_type_name",
            "normalized selected-action key should not include raw runtime metadata");
        AssertEqual(
            TelemetryHash.HashCanonical(new Dictionary<string, object?> { ["action_type"] = "end_turn" }),
            selectedAction.GetProperty("canonical_action_hash").GetString(),
            "canonical action hash should be computed from normalized typed key");
        AssertNotEqual(
            selectedAction.GetProperty("raw_action_hash").GetString(),
            selectedAction.GetProperty("canonical_action_hash").GetString(),
            "raw action hash should remain separate from normalized canonical action hash");
        AssertMissingProperty(root, "capture_policy", "full decision frames should not use signal-only capture policy");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ActionExecutorSignalOnlyRecordingDoesNotCaptureSnapshotsOrPendingDecisions()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-action-signal");

            object[] signalOnlyActions =
            {
                new GenericHookGameAction(),
                new MoveToMapCoordAction(),
                new VoteForMapCoordAction(),
                new VoteToMoveToNextActAction(),
                new ReadyToBeginEnemyTurnAction(),
                new UndoEndPlayerTurnAction()
            };

            foreach (object action in signalOnlyActions)
            {
                AssertTrue(ActionExecutorCapturePolicy.IsSignalOnly(action),
                    $"{action.GetType().Name} should be classified signal-only");
                recorder.RecordActionExecutorSignal(action, "before_action_executed");
                AssertEqual(0, GetPendingDecisionCount(recorder),
                    $"{action.GetType().Name} signal should not create pending decisions");
            }
        }

        string path = Path.Combine(directory, "runs", "run-action-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for ActionExecutor signals");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(6, lines.Length, "each signal-only action should write one action signal");

        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            AssertEqual("decision/action_signal", root.GetProperty("record_type").GetString(), "action signal record type");
            AssertEqual("action_executor", root.GetProperty("source").GetString(), "action signal source");
            AssertEqual("before_action_executed", root.GetProperty("phase").GetString(), "action signal phase");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "action signal capture policy");
            AssertMissingProperty(root, "decision_frame_id", "action signal should not have a decision frame id");
            AssertMissingProperty(root, "state", "action signal should not capture lifecycle state");
            AssertMissingProperty(root, "pre_state", "action signal should not capture pre-state");
            AssertMissingProperty(root, "post_state", "action signal should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "action signal should not build legal actions");
            AssertMissingProperty(root, "selected_action", "action signal should not be a full selected-action frame");

            JsonElement metadata = root.GetProperty("action_signal").GetProperty("metadata");
            AssertEqual("action_executor", metadata.GetProperty("source").GetString(), "metadata source");
            AssertEqual("before_action_executed", metadata.GetProperty("phase").GetString(), "metadata phase");
            AssertEqual("type_only_action_executor_signal",
                metadata.GetProperty("projection_policy").GetString(),
                "signal metadata should stay type-only");
            AssertEqual("skipped_signal_only_runtime_safety",
                metadata.GetProperty("net_action_projection").GetString(),
                "signal metadata should not call ToNetAction");
            AssertTrue(metadata.GetProperty("runtime_type_name").GetString()?.EndsWith("Action", StringComparison.Ordinal) == true,
                "signal metadata should include the runtime action type");
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ActionExecutorMapVoteSignalClosesTypedMapContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var destination = new TestMapPoint
        {
            coord = new TestMapCoord { col = 4, row = 1 },
            PointType = "Monster",
            mapGenerationCount = 7
        };
        var otherDestination = new TestMapPoint
        {
            coord = new TestMapCoord { col = 5, row = 1 },
            PointType = "Shop",
            mapGenerationCount = 7
        };
        var source = new TestMapPoint
        {
            coord = new TestMapCoord { col = 3, row = 0 },
            Children = new object?[] { destination, otherDestination }
        };
        var runState = new TestRunState
        {
            Map = new TestMap(),
            CurrentMapPoint = source,
            CurrentMapCoord = source.coord,
            CurrentActIndex = 0
        };
        var builder = TestLegalActionBuilder(new Dictionary<string, object?>
        {
            ["MapSelectionSynchronizer"] = new TestMapSelectionSynchronizer { MapGenerationCount = 7 }
        });
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            localPlayer: null,
            TestSnapshotWithHash("room/previous", "map-vote-before-context"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, builder);
            EnableCapturingForTest(recorder, "run-map-vote");

            recorder.RecordActionExecutorSignal(
                new VoteForMapCoordAction
                {
                    Source = source,
                    Destination = destination
                },
                "before_action_executed");
            recorder.RecordActionExecutorSignal(
                new VoteForMapCoordAction
                {
                    Source = source,
                    Destination = destination
                },
                "after_action_executed");
            recorder.RecordActionExecutorSignal(
                new MoveToMapCoordAction { Destination = destination },
                "before_action_executed");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(4, records.Length, "map vote should write one context plus three raw signals");
            JsonElement context = records[0].RootElement;
            AssertRecordType("decision/context", records[0], "map vote context");
            AssertEqual("map", context.GetProperty("non_combat_closure").GetProperty("surface").GetString(),
                "forced map context surface");
            JsonElement actions = context.GetProperty("legal_actions").GetProperty("actions");
            AssertEqual(2, actions.GetArrayLength(), "map context should expose current destination choices");

            JsonElement voteBefore = records[1].RootElement;
            AssertRecordType("decision/action_signal", records[1], "map vote before signal");
            AssertMissingProperty(voteBefore, "pre_state", "map signal should remain signal-only");
            AssertMissingProperty(voteBefore, "legal_actions", "map signal should not carry legal actions");
            JsonElement metadata = voteBefore.GetProperty("action_signal").GetProperty("metadata");
            AssertEqual("choose_map_node", metadata.GetProperty("action_type").GetString(),
                "map vote should normalize to choose_map_node");
            AssertEqual(true,
                voteBefore.GetProperty("decision_context").GetProperty("selected_action_match").GetProperty("matched").GetBoolean(),
                "map vote should match typed map context");
            AssertEqual(true,
                voteBefore.GetProperty("non_combat_closure").GetProperty("trainable_closed_non_combat_choice").GetBoolean(),
                "map vote before signal should be trainable");

            AssertMissingProperty(records[2].RootElement, "non_combat_closure",
                "map vote after signal should not inflate trainable closure count");
            AssertMissingProperty(records[3].RootElement, "non_combat_closure",
                "map move signal should remain raw transition evidence only");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void PatchedUiCallbackRecordsSignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new StateSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-ui-signal");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom.AfterSelectingOption",
                new ThrowingProjectionObject(),
                new object?[] { new ThrowingProjectionObject(), 7, "rest-option" });
        }

        string path = Path.Combine(directory, "runs", "run-ui-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for UI signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "UI callback should write exactly one signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("decision/ui_signal", root.GetProperty("record_type").GetString(), "UI signal record type");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "UI signal capture policy");
        AssertMissingProperty(root, "decision_frame_id", "UI signal should not have a decision frame id");
        AssertMissingProperty(root, "pre_state", "UI signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "UI signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "UI signal should not build legal actions");

        JsonElement metadata = root.GetProperty("ui_signal").GetProperty("metadata");
        AssertEqual("choose_rest_option", metadata.GetProperty("action_type").GetString(), "rest site action type");
        AssertEqual(typeof(ThrowingProjectionObject).FullName,
            metadata.GetProperty("runtime_type").GetString(),
            "unsafe instance runtime type");
        AssertEqual(3, metadata.GetProperty("argument_count").GetInt32(), "argument count");
        AssertMissingProperty(metadata, "display", "unsafe instance display should not be projected");

        JsonElement[] arguments = metadata.GetProperty("arguments").EnumerateArray().ToArray();
        AssertEqual(3, arguments.Length, "projected argument count");
        AssertEqual(typeof(ThrowingProjectionObject).FullName,
            arguments[0].GetProperty("type").GetString(),
            "unsafe argument type");
        AssertEqual("type_only",
            arguments[0].GetProperty("projection_policy").GetString(),
            "unsafe argument projection policy");
        AssertEqual(7, arguments[1].GetProperty("value").GetInt32(), "stable scalar value");
        AssertEqual("rest-option", arguments[2].GetProperty("value").GetString(), "stable string value");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void EventOptionUiSignalRecordsScalarOptionIndexOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-event-signal");
            SetStaticRecorderForTest(recorder);

            Sts2TelemetryMod.OnUiDecisionFromPatch(
                "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption",
                new ThrowingProjectionObject(),
                new object?[] { 2 });
        }

        string path = Path.Combine(directory, "runs", "run-event-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for event option signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "event option callback should write exactly one signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("decision/ui_signal", root.GetProperty("record_type").GetString(), "event option signal record type");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "event option capture policy");
        AssertMissingProperty(root, "pre_state", "event option signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "event option signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "event option signal should not build legal actions");

        JsonElement metadata = root.GetProperty("ui_signal").GetProperty("metadata");
        AssertEqual("choose_event_option", metadata.GetProperty("action_type").GetString(), "event option action type");
        AssertEqual(1, metadata.GetProperty("argument_count").GetInt32(), "event option argument count");

        JsonElement[] arguments = metadata.GetProperty("arguments").EnumerateArray().ToArray();
        AssertEqual(1, arguments.Length, "event option projected argument count");
        AssertEqual(0, arguments[0].GetProperty("index").GetInt32(), "event option index argument position");
        AssertEqual(typeof(int).FullName, arguments[0].GetProperty("type").GetString(), "event option index type");
        AssertEqual(2, arguments[0].GetProperty("value").GetInt32(), "event option scalar index value");
        AssertEqual(2, metadata.GetProperty("selected_option_index").GetInt32(), "event selected option index");

        JsonElement normalizedKey = metadata.GetProperty("normalized_typed_action_key");
        AssertEqual("choose_event_option", normalizedKey.GetProperty("action_type").GetString(), "event normalized action type");
        AssertEqual(2, normalizedKey.GetProperty("option_index").GetInt32(), "event normalized option index");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RuntimeRestOptionSignalRecordsScalarOptionIndexOnly()
{
    var metadata = ActionMetadata.FromPatchedMethod(
        "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer.ChooseLocalOption",
        new ThrowingProjectionObject(),
        new object?[] { 1 });

    AssertEqual("choose_rest_option", metadata["action_type"], "rest runtime action type");
    AssertEqual(1, metadata["argument_count"], "rest option argument count");
    AssertEqual(1, metadata["selected_option_index"], "rest selected option index");

    var normalizedKey = (IReadOnlyDictionary<string, object?>)metadata["normalized_typed_action_key"]!;
    AssertEqual("choose_rest_option", normalizedKey["action_type"], "rest normalized action type");
    AssertEqual(1, normalizedKey["option_index"], "rest normalized option index");

    var status = (IReadOnlyDictionary<string, object?>)metadata["extraction_status"]!;
    AssertEqual("current_rest_option_unavailable", status["rest_option"],
        "no-game-launch rest signal should mark typed lookup unavailable without UI traversal");
}

static void ShopSignalMetadataRecordsNormalizedTypedKey()
{
    var cardEntry = new TestMerchantEntry
    {
        Card = new TestShopItem { Id = "card-a", Title = "Useful Card" },
        Cost = 42,
        IsStocked = true,
        EnoughGold = true,
        Used = false
    };

    var cardMetadata = ActionMetadata.FromPatchedMethod(
        "ui.shop.on_try_purchase",
        cardEntry,
        new object?[] { new object(), false });

    AssertEqual("buy_shop_card", cardMetadata["action_type"], "shop card signal action type");
    AssertEqual("attempted", cardMetadata["purchase_status"], "shop card signal status");
    AssertEqual("card", cardMetadata["category"], "shop card signal category");
    AssertEqual("card-a", cardMetadata["card_id"], "shop card signal card id");
    var cardKey = (IReadOnlyDictionary<string, object?>)cardMetadata["normalized_typed_action_key"]!;
    AssertEqual("buy_shop_card", cardKey["action_type"], "shop card normalized action type");
    AssertEqual("card-a", cardKey["card_id"], "shop card normalized card id");
    AssertEqual("card", cardKey["category"], "shop card normalized category");

    var removalMetadata = ActionMetadata.FromPatchedMethod(
        "ui.shop.card_removal.on_try_purchase",
        new TestMerchantEntry
        {
            Name = "Card Removal",
            Cost = 75,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        },
        new object?[] { new object(), false, true });

    AssertEqual("remove_card_at_shop", removalMetadata["action_type"], "shop removal signal action type");
    AssertEqual("card_removal", removalMetadata["category"], "shop removal signal category");
    AssertEqual("card_removal", removalMetadata["removal_id"], "shop removal signal id");
    var removalKey = (IReadOnlyDictionary<string, object?>)removalMetadata["normalized_typed_action_key"]!;
    AssertEqual("remove_card_at_shop", removalKey["action_type"], "shop removal normalized action type");
    AssertEqual("card_removal", removalKey["removal_id"], "shop removal normalized id");

    var relicMetadata = ActionMetadata.FromPatchedMethod(
        "runtime.shop.purchase_completed",
        null,
        new object?[]
        {
            new TestMerchantEntry
            {
                Relic = new TestShopItem { Id = "relic-a", Title = "Useful Relic" },
                Cost = 120,
                IsStocked = true,
                EnoughGold = true,
                Used = true
            }
        });

    AssertEqual("buy_shop_relic", relicMetadata["action_type"], "shop completed signal action type");
    AssertEqual("completed", relicMetadata["purchase_status"], "shop completed signal status");
    AssertEqual("relic-a", relicMetadata["relic_id"], "shop completed relic id");
    var relicKey = (IReadOnlyDictionary<string, object?>)relicMetadata["normalized_typed_action_key"]!;
    AssertEqual("buy_shop_relic", relicKey["action_type"], "shop completed normalized action type");
    AssertEqual("relic-a", relicKey["relic_id"], "shop completed normalized relic id");

    var inventoryCompletedMetadata = ActionMetadata.FromPatchedMethod(
        "runtime.shop.inventory_purchase_completed",
        null,
        new object?[]
        {
            PurchaseStatus.Success,
            new TestMerchantEntry
            {
                Card = new TestShopItem { Id = "card-b", Title = "Useful Card" },
                Cost = 80,
                IsStocked = true,
                EnoughGold = true,
                Used = true
            }
        });

    AssertEqual("buy_shop_card", inventoryCompletedMetadata["action_type"],
        "shop inventory completed signal action type");
    AssertEqual("completed", inventoryCompletedMetadata["purchase_status"],
        "shop inventory completed status should normalize PurchaseStatus.Success");
    AssertEqual("card-b", inventoryCompletedMetadata["card_id"], "shop inventory completed card id");
    var inventoryCompletedKey =
        (IReadOnlyDictionary<string, object?>)inventoryCompletedMetadata["normalized_typed_action_key"]!;
    AssertEqual("buy_shop_card", inventoryCompletedKey["action_type"],
        "shop inventory completed normalized action type");
    AssertEqual("card-b", inventoryCompletedKey["card_id"],
        "shop inventory completed normalized card id");
}

static void PlayerChoiceSignalProjectsShopCardRemovalFromTypedRuntimeContext()
{
    var player = new TestPlayer
    {
        Deck = new TestPile
        {
            Cards = new object?[]
            {
                new TestCard { Id = new TestModelId { Entry = "strike" } },
                new TestCard { Id = new TestModelId { Entry = "defend" } }
            }
        },
        RunState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    CardRemovalEntry = new TestMerchantEntry
                    {
                        Name = "Card Removal",
                        Cost = 75,
                        IsStocked = true,
                        EnoughGold = true,
                        Used = false
                    }
                }
            }
        }
    };

    var metadata = ActionMetadata.FromPatchedMethod(
        "player_choice_synchronizer.player_choice_received",
        null,
        new object?[]
        {
            player,
            12u,
            new TestNetPlayerChoiceResult
            {
                type = "Index",
                indexes = new[] { 1 }
            }
        });

    AssertEqual("remove_card_at_shop", metadata["action_type"], "player choice shop removal action type");
    AssertEqual("card_removal", metadata["category"], "player choice shop removal category");
    AssertEqual("selected_card", metadata["purchase_status"], "player choice shop removal selection status");
    AssertEqual(12, metadata["choice_id"], "player choice id");
    AssertEqual(1, metadata["selected_card_index"], "selected deck index");
    AssertEqual("defend", metadata["removed_card_id"], "selected deck card id");

    var choiceResult = (IReadOnlyDictionary<string, object?>)metadata["player_choice_result"]!;
    AssertEqual("Index", choiceResult["choice_type"], "choice result type");
    AssertEqual(1, choiceResult["selected_index"], "choice selected index");

    var normalizedKey = (IReadOnlyDictionary<string, object?>)metadata["normalized_typed_action_key"]!;
    AssertEqual("remove_card_at_shop", normalizedKey["action_type"],
        "player choice shop removal normalized action type");
    AssertEqual("card_removal", normalizedKey["removal_id"],
        "player choice shop removal normalized id");
}

static void ShopInventoryPotionCompletionMatchesSettledContext()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var player = new TestPlayer { Gold = 160 };
        var contextEntry = new TestMerchantPotionEntry
        {
            Potion = new TestShopItem { Id = "SWIFT_POTION", Title = "Swift Potion" },
            Cost = 45,
            IsStocked = true,
            EnoughGold = true,
            Used = false
        };
        var signalEntry = new TestMerchantPotionEntry
        {
            Potion = new TestShopItem { Id = "SWIFT_POTION", Title = "Swift Potion" },
            Cost = 45,
            IsStocked = true,
            EnoughGold = true,
            Used = true
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory
                {
                    PotionEntries = new object?[] { contextEntry }
                },
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "shop-potion-context-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-potion-context-signal");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/room_entered_settled", "run_manager");
            PatchCallbacks.AfterShopInventoryPurchaseCompleted(new object[]
            {
                PurchaseStatus.Success,
                new TestMerchantInventory(),
                signalEntry
            });
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(3, records.Length, "shop potion context plus signal record count");
            AssertRecordType("lifecycle/room_entered_settled", records[0], "shop lifecycle record");
            AssertRecordType("decision/context", records[1], "shop decision context");
            AssertRecordType("decision/ui_signal", records[2], "shop completion signal");

            JsonElement metadata = records[2].RootElement.GetProperty("ui_signal").GetProperty("metadata");
            AssertEqual("buy_shop_potion", metadata.GetProperty("action_type").GetString(),
                "shop inventory completion should project potion action");
            AssertEqual("completed", metadata.GetProperty("purchase_status").GetString(),
                "shop inventory completion status");
            AssertEqual("SWIFT_POTION", metadata.GetProperty("potion_id").GetString(),
                "shop inventory completion potion id");
            AssertEqual("TestMerchantPotionEntry", metadata.GetProperty("shop_entry_runtime_type_name").GetString(),
                "shop inventory completion should select the merchant entry argument, not status or inventory");
            AssertMissingProperty(metadata, "shop_entry_extraction",
                "shop inventory completion should not degrade to entry_unavailable");

            JsonElement reference = records[2].RootElement.GetProperty("decision_context");
            AssertEqual("shop", reference.GetProperty("state_type").GetString(), "shop context reference state type");
            JsonElement match = reference.GetProperty("selected_action_match");
            AssertEqual(true, match.GetProperty("matched").GetBoolean(),
                "shop potion completion should match the latest shop legal-action context");
            AssertEqual(0, match.GetProperty("matched_legal_action_index").GetInt32(),
                "shop potion completion matched legal action index");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UiDecisionPatchTargetsKeepMapSelectionOnActionExecutor()
{
    IReadOnlyList<Sts2HookPatchInstaller.UiDecisionPatchTarget> targets =
        Sts2HookPatchInstaller.UiDecisionPatchTargetsForTests();

    AssertTrue(!targets.Any(target =>
            target.TypeName == "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen"
            && target.MethodName == "OnMapPointSelectedLocally"),
        "map node selections must stay on ActionExecutor decision frames, not the Godot NMapScreen UI prefix");

    Sts2HookPatchInstaller.UiDecisionPatchTarget eventTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer"
        && target.MethodName == "ChooseLocalOption");

    AssertEqual("runtime.event.choose_local_option", eventTarget.Source, "event option source");
    AssertTrue(eventTarget.ParameterTypes is { Length: 1 }
        && eventTarget.ParameterTypes[0] == typeof(int), "event option hook must remain typed to ChooseLocalOption(int)");

    Sts2HookPatchInstaller.UiDecisionPatchTarget restTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer"
        && target.MethodName == "ChooseLocalOption");

    AssertEqual("runtime.rest.choose_local_option", restTarget.Source, "rest option source");
    AssertTrue(restTarget.ParameterTypes is { Length: 1 }
        && restTarget.ParameterTypes[0] == typeof(int), "rest option hook must remain typed to ChooseLocalOption(int)");

    Sts2HookPatchInstaller.UiDecisionPatchTarget cardRewardSelectTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen"
        && target.MethodName == "SelectCard");

    AssertEqual("runtime.card_reward.select_card", cardRewardSelectTarget.Source, "card reward selected-card source");
    AssertTrue(cardRewardSelectTarget.ParameterTypes is { Length: 1 }
        && cardRewardSelectTarget.ParameterTypes[0] == typeof(NCardHolder),
        "card reward selected-card hook must target SelectCard(NCardHolder)");
}

static void RemovedRewardWrapperHookIsOptionalWhileTypedCoverageRemainsRequired()
{
    IReadOnlyList<Sts2HookPatchInstaller.UiDecisionPatchTarget> uiTargets =
        Sts2HookPatchInstaller.UiDecisionPatchTargetsForTests();

    Sts2HookPatchInstaller.UiDecisionPatchTarget rewardWrapperTarget = uiTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Rewards.Reward"
        && target.MethodName == "OnSelectWrapper");
    AssertEqual("ui.reward.on_select_wrapper", rewardWrapperTarget.Source,
        "reward wrapper source remains a signal-only optional hook when present");
    AssertTrue(rewardWrapperTarget.ParameterTypes == null,
        "optional reward wrapper hook may match any surviving wrapper overload");

    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> runtimeTargets =
        Sts2HookPatchInstaller.RuntimeSignalPatchTargetsForTests();
    Sts2HookPatchInstaller.RuntimeSignalPatchTarget rewardsTarget = runtimeTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Rewards.RewardsSet"
        && target.MethodName == "GenerateWithoutOffering");
    AssertEqual("runtime.rewards_set.generate_without_offering", rewardsTarget.Source,
        "typed reward context coverage source");
    AssertEqual(nameof(PatchCallbacks.AfterRewardsGenerated), rewardsTarget.CallbackName,
        "typed reward context callback");
    AssertEqual(0, rewardsTarget.ParameterTypes.Length,
        "typed reward context hook must remain required coverage");

    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> openingTargets =
        Sts2HookPatchInstaller.RuntimeOpeningPatchTargetsForTests();
    Sts2HookPatchInstaller.RuntimeSignalPatchTarget cardRewardTarget = openingTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Rewards.CardReward"
        && target.MethodName == "OnSelect");
    AssertEqual("runtime.card_reward.on_select", cardRewardTarget.Source,
        "typed card reward opening source");
    AssertEqual(nameof(PatchCallbacks.BeforeCardRewardOpened), cardRewardTarget.CallbackName,
        "typed card reward opening callback");
    AssertEqual(0, cardRewardTarget.ParameterTypes.Length,
        "typed card reward opening hook must remain required coverage");

    Sts2HookPatchInstaller.PatchInstallResult missingWrapper =
        Sts2HookPatchInstaller.PatchInstallResult.MissingMethod(
            "MegaCrit.Sts2.Core.Rewards.Reward",
            "OnSelectWrapper",
            "ui.reward.on_select_wrapper",
            nameof(PatchCallbacks.BeforeUiDecision),
            null,
            null);
    IReadOnlyDictionary<string, object?> record = missingWrapper.ToRecord();
    AssertEqual("method_missing", record["status"], "missing optional reward wrapper status");
    AssertEqual("ui.reward.on_select_wrapper", record["source"], "missing optional reward wrapper source");
}

static void ShopHookTargetsIncludeRemovalAndPurchaseCompletion()
{
    IReadOnlyList<Sts2HookPatchInstaller.UiDecisionPatchTarget> uiTargets =
        Sts2HookPatchInstaller.UiDecisionPatchTargetsForTests();

    Sts2HookPatchInstaller.UiDecisionPatchTarget merchantTarget = uiTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry"
        && target.MethodName == "OnTryPurchaseWrapper");
    AssertEqual("ui.shop.on_try_purchase", merchantTarget.Source, "merchant purchase attempt source");
    AssertTrue(merchantTarget.ParameterTypes is { Length: 2 }
        && merchantTarget.ParameterTypes[0] == typeof(MerchantInventory)
        && merchantTarget.ParameterTypes[1] == typeof(bool),
        "merchant purchase attempt hook must target OnTryPurchaseWrapper(MerchantInventory, bool)");

    Sts2HookPatchInstaller.UiDecisionPatchTarget removalTarget = uiTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry"
        && target.MethodName == "OnTryPurchaseWrapper");
    AssertEqual("ui.shop.card_removal.on_try_purchase", removalTarget.Source, "card-removal attempt source");
    AssertTrue(removalTarget.ParameterTypes is { Length: 3 }
        && removalTarget.ParameterTypes[0] == typeof(MerchantInventory)
        && removalTarget.ParameterTypes[1] == typeof(bool)
        && removalTarget.ParameterTypes[2] == typeof(bool),
        "card-removal hook must target OnTryPurchaseWrapper(MerchantInventory, bool, bool)");

    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> runtimeTargets =
        Sts2HookPatchInstaller.RuntimeSignalPatchTargetsForTests();
    Sts2HookPatchInstaller.RuntimeSignalPatchTarget completedTarget = runtimeTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry"
        && target.MethodName == "InvokePurchaseCompleted");
    AssertEqual("runtime.shop.purchase_completed", completedTarget.Source, "shop completion source");
    AssertEqual(nameof(PatchCallbacks.AfterShopPurchaseCompleted), completedTarget.CallbackName, "shop completion callback");
    AssertTrue(completedTarget.ParameterTypes is { Length: 1 }
        && completedTarget.ParameterTypes[0] == typeof(MerchantEntry),
        "shop completion hook must target InvokePurchaseCompleted(MerchantEntry)");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget inventoryCompletedTarget = runtimeTargets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory"
        && target.MethodName == "OnPurchaseCompleted");
    AssertEqual("runtime.shop.inventory_purchase_completed", inventoryCompletedTarget.Source,
        "shop inventory completion source");
    AssertEqual(nameof(PatchCallbacks.AfterShopInventoryPurchaseCompleted), inventoryCompletedTarget.CallbackName,
        "shop inventory completion callback");
    AssertTrue(inventoryCompletedTarget.ParameterTypes is { Length: 2 }
        && inventoryCompletedTarget.ParameterTypes[0] == typeof(PurchaseStatus)
        && inventoryCompletedTarget.ParameterTypes[1] == typeof(MerchantEntry),
        "shop inventory completion hook must target OnPurchaseCompleted(PurchaseStatus, MerchantEntry)");
}

static void RuntimeSignalPatchTargetsIncludeRelicAndShopCompletion()
{
    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> targets =
        Sts2HookPatchInstaller.RuntimeSignalPatchTargetsForTests();

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget rewardsTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Rewards.RewardsSet"
        && target.MethodName == "GenerateWithoutOffering");

    AssertEqual("runtime.rewards_set.generate_without_offering", rewardsTarget.Source,
        "rewards generated cache source");
    AssertEqual(nameof(PatchCallbacks.AfterRewardsGenerated), rewardsTarget.CallbackName,
        "rewards generated cache callback");
    AssertEqual(0, rewardsTarget.ParameterTypes.Length,
        "rewards generated hook should target GenerateWithoutOffering()");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget relicNoArgsTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Models.RelicModel"
        && target.MethodName == "Flash"
        && target.ParameterTypes.Length == 0);

    AssertEqual("runtime.relic_model.flash_no_args", relicNoArgsTarget.Source, "relic no-args trigger source");
    AssertEqual(nameof(PatchCallbacks.AfterRelicFlashNoArgs), relicNoArgsTarget.CallbackName,
        "relic no-args trigger callback");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget relicTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Models.RelicModel"
        && target.MethodName == "Flash"
        && target.ParameterTypes.Length == 1);

    AssertEqual("runtime.relic_model.flash", relicTarget.Source, "relic trigger source");
    AssertEqual(nameof(PatchCallbacks.AfterRelicFlash), relicTarget.CallbackName, "relic trigger callback");
    AssertTrue(relicTarget.ParameterTypes is { Length: 1 }
        && relicTarget.ParameterTypes[0] == typeof(IEnumerable<Creature>),
        "relic trigger hook must patch only Flash(IEnumerable<Creature>)");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget shopTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry"
        && target.MethodName == "InvokePurchaseCompleted");

    AssertEqual("runtime.shop.purchase_completed", shopTarget.Source, "shop completion source");
    AssertEqual(nameof(PatchCallbacks.AfterShopPurchaseCompleted), shopTarget.CallbackName, "shop completion callback");
    AssertTrue(shopTarget.ParameterTypes is { Length: 1 }
        && shopTarget.ParameterTypes[0] == typeof(MerchantEntry),
        "shop completion hook must patch only InvokePurchaseCompleted(MerchantEntry)");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget inventoryShopTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory"
        && target.MethodName == "OnPurchaseCompleted");

    AssertEqual("runtime.shop.inventory_purchase_completed", inventoryShopTarget.Source,
        "shop inventory completion source");
    AssertEqual(nameof(PatchCallbacks.AfterShopInventoryPurchaseCompleted), inventoryShopTarget.CallbackName,
        "shop inventory completion callback");
    AssertTrue(inventoryShopTarget.ParameterTypes is { Length: 2 }
        && inventoryShopTarget.ParameterTypes[0] == typeof(PurchaseStatus)
        && inventoryShopTarget.ParameterTypes[1] == typeof(MerchantEntry),
        "shop inventory completion hook must patch OnPurchaseCompleted(PurchaseStatus, MerchantEntry)");
}

static void RuntimeOpeningPatchTargetsIncludeCardRewardRelicAndBundle()
{
    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> targets =
        Sts2HookPatchInstaller.RuntimeOpeningPatchTargetsForTests();

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget cardRewardTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Rewards.CardReward"
        && target.MethodName == "OnSelect");
    AssertEqual("runtime.card_reward.on_select", cardRewardTarget.Source, "card reward opening source");
    AssertEqual(nameof(PatchCallbacks.BeforeCardRewardOpened), cardRewardTarget.CallbackName,
        "card reward opening callback");
    AssertEqual(0, cardRewardTarget.ParameterTypes.Length,
        "card reward opening hook must target protected OnSelect()");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget relicTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Commands.RelicSelectCmd"
        && target.MethodName == "FromChooseARelicScreen");
    AssertEqual("runtime.relic_select.choose_a_relic", relicTarget.Source, "relic select opening source");
    AssertEqual(nameof(PatchCallbacks.BeforeRelicSelectOpened), relicTarget.CallbackName,
        "relic select opening callback");
    AssertTrue(relicTarget.ParameterTypes is { Length: 2 }
        && relicTarget.ParameterTypes[0] == typeof(Player)
        && relicTarget.ParameterTypes[1] == typeof(IReadOnlyList<RelicModel>),
        "relic select hook must target FromChooseARelicScreen(Player, IReadOnlyList<RelicModel>)");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget bundleTarget = targets.Single(target =>
        target.TypeName == "MegaCrit.Sts2.Core.Commands.CardSelectCmd"
        && target.MethodName == "FromChooseABundleScreen");
    AssertEqual("runtime.bundle_select.choose_a_bundle", bundleTarget.Source, "bundle select opening source");
    AssertEqual(nameof(PatchCallbacks.BeforeBundleSelectOpened), bundleTarget.CallbackName,
        "bundle select opening callback");
    AssertTrue(bundleTarget.ParameterTypes is { Length: 2 }
        && bundleTarget.ParameterTypes[0] == typeof(Player)
        && bundleTarget.ParameterTypes[1] == typeof(IReadOnlyList<IReadOnlyList<CardModel>>),
        "bundle select hook must target FromChooseABundleScreen(Player, IReadOnlyList<IReadOnlyList<CardModel>>)");
}

static void MainMenuUploadUiPatchTargetsAreIsolated()
{
    IReadOnlyList<Sts2HookPatchInstaller.RuntimeSignalPatchTarget> targets =
        Sts2HookPatchInstaller.MainMenuUiPatchTargetsForTests();

    AssertEqual(2, targets.Count, "main menu UI hook count");
    Sts2HookPatchInstaller.RuntimeSignalPatchTarget ready = targets.Single(target => target.MethodName == "_Ready");
    AssertEqual("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu", ready.TypeName, "main menu ready type");
    AssertEqual("main_menu.ready", ready.Source, "main menu ready source");
    AssertEqual(nameof(PatchCallbacks.AfterMainMenuReady), ready.CallbackName, "main menu ready callback");
    AssertEqual(0, ready.ParameterTypes.Length, "main menu ready hook should be parameterless");

    Sts2HookPatchInstaller.RuntimeSignalPatchTarget exit = targets.Single(target => target.MethodName == "_ExitTree");
    AssertEqual("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu", exit.TypeName, "main menu exit type");
    AssertEqual("main_menu.exit_tree", exit.Source, "main menu exit source");
    AssertEqual(nameof(PatchCallbacks.AfterMainMenuExit), exit.CallbackName, "main menu exit callback");
    AssertEqual(0, exit.ParameterTypes.Length, "main menu exit hook should be parameterless");

    AssertTrue(!targets.Any(target => target.MethodName.Contains("Button", StringComparison.OrdinalIgnoreCase)
            || target.MethodName.Contains("RenderSelectionMenu", StringComparison.Ordinal)),
        "main menu UI must not patch or reorder native menu buttons");
}

static void HarmonyNativeDependencyPreloadIsAvailableOnLinux()
{
    MethodInfo method = typeof(Sts2TelemetryMod)
        .GetMethod("TryPreloadHarmonyNativeDependencies", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing native dependency preload method");
    var status = (IReadOnlyDictionary<string, object?>)(method.Invoke(null, null)
        ?? throw new InvalidOperationException("native dependency preload returned null"));

    AssertEqual("harmony.native_dependency_preload", status["source"], "native preload source");
    AssertTrue(status.ContainsKey("status"), "native preload should report status");
    if (OperatingSystem.IsLinux())
        AssertEqual("loaded", status["status"], "Linux native preload should load libgcc_s globally");
    else
        AssertEqual("skipped_non_linux", status["status"], "non-Linux native preload should be skipped");
}

static void ActEnteredLifecycleCallbackRecordsSignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-act-signal");
            SetStaticRecorderForTest(recorder);

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnActEntered");
        }

        string path = Path.Combine(directory, "runs", "run-act-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for act-entered signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "act-entered should write exactly one lifecycle signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("lifecycle/act_entered", root.GetProperty("record_type").GetString(), "act-entered record type");
        AssertEqual("run_manager", root.GetProperty("source").GetString(), "act-entered source");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "act-entered capture policy");
        AssertEqual("branch-0001",
            root.GetProperty("branch").GetProperty("branch_id").GetString(),
            "act-entered branch id");
        AssertMissingProperty(root, "state", "act-entered signal should not capture lifecycle state");
        AssertMissingProperty(root, "pre_state", "act-entered signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "act-entered signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "act-entered signal should not build legal actions");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RoomEnteredLifecycleCallbackRecordsSignalOnly()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    Func<string, Action, bool>? originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((_, _) => false);
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-room-entered-signal");
            SetStaticRecorderForTest(recorder);

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnRoomEntered");
        }

        string path = Path.Combine(directory, "runs", "run-room-entered-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for room-entered signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "immediate room-entered should write exactly one lifecycle signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("lifecycle/room_entered", root.GetProperty("record_type").GetString(), "room-entered record type");
        AssertEqual("run_manager", root.GetProperty("source").GetString(), "room-entered source");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "room-entered capture policy");
        AssertEqual("lifecycle/room_entered_settled",
            root.GetProperty("details").GetProperty("settled_snapshot_record_type").GetString(),
            "room-entered settled record type");
        AssertMissingProperty(root, "state", "room-entered signal should not capture lifecycle state");
        AssertMissingProperty(root, "pre_state", "room-entered signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "room-entered signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "room-entered signal should not build legal actions");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RoomExitedPublicEventSubscriptionIsDisabled()
{
    MethodInfo method = typeof(Sts2TelemetryMod)
        .GetMethod("ShouldSubscribeRoomExitedPublicEvent", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing room-exited subscription policy");
    bool enabled = (bool)(method.Invoke(null, null)
        ?? throw new InvalidOperationException("room-exited subscription policy returned null"));
    AssertEqual(false, enabled, "RunManager.RoomExited subscription should stay disabled for transition stability");
}

static void RoomExitedLifecycleCallbackRecordsSignalOnlyWithoutSettledSnapshot()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    int scheduledCount = 0;
    Func<string, Action, bool>? originalScheduler = Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests((_, _) =>
    {
        scheduledCount++;
        return false;
    });
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-room-exited-signal");
            SetStaticRecorderForTest(recorder);

            InvokeStaticNonPublic(typeof(Sts2TelemetryMod), "OnRoomExited");
        }

        string path = Path.Combine(directory, "runs", "run-room-exited-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for room-exited signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "immediate room-exited should write exactly one lifecycle signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("lifecycle/room_exited", root.GetProperty("record_type").GetString(), "room-exited record type");
        AssertEqual("run_manager", root.GetProperty("source").GetString(), "room-exited source");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "room-exited capture policy");
        AssertEqual("lifecycle/room_exited_settled",
            root.GetProperty("details").GetProperty("settled_snapshot_record_type").GetString(),
            "room-exited settled record type");
        AssertEqual("disabled",
            root.GetProperty("details").GetProperty("settled_snapshot_schedule").GetString(),
            "room-exited settled snapshot schedule");
        AssertEqual(false,
            root.GetProperty("details").GetProperty("settled_snapshot_scheduled").GetBoolean(),
            "room-exited settled snapshot should not be scheduled");
        AssertEqual("room_exit_transition_can_cross_act_or_scene_boundary",
            root.GetProperty("details").GetProperty("settled_snapshot_disabled_reason").GetString(),
            "room-exited settled snapshot disabled reason");
        AssertEqual(0, scheduledCount, "room-exited callback must not schedule a next-frame snapshot");
        AssertMissingProperty(root, "state", "room-exited signal should not capture lifecycle state");
        AssertMissingProperty(root, "pre_state", "room-exited signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "room-exited signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "room-exited signal should not build legal actions");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        Sts2TelemetryMod.ReplaceNextFrameSchedulerForTests(originalScheduler);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ActionExecutorSnapshotsUseRuntimeOnlyScreenPolicy()
{
    AssertTrue(StateSnapshotBuilder.UsesRuntimeOnlyScreenPolicyForTests("action_executor:pre"),
        "ActionExecutor pre snapshots should avoid volatile screen singleton probing");
    AssertTrue(StateSnapshotBuilder.UsesRuntimeOnlyScreenPolicyForTests("action_executor_after:post"),
        "ActionExecutor post snapshots should avoid volatile screen singleton probing");
    AssertTrue(!StateSnapshotBuilder.UsesRuntimeOnlyScreenPolicyForTests("lifecycle/room_entered_settled"),
        "settled lifecycle snapshots may use the normal bounded screen metadata path");
}

static void LegalActionBuilderReturnsPendingTypedBuilderPlaceholder()
{
    var actions = new LegalActionBuilder().Build(TestSnapshot("overlay/test_modal"), runState: null, localPlayer: null);

    AssertEqual(1, actions.Count, "one pending builder action");
    AssertEqual("typed_builder_pending", actions[0]["action_type"], "pending builder action type");
    AssertEqual("legal_action_builder", actions[0]["source"], "pending builder source");
    AssertEqual("typed_builder_pending", actions[0]["availability"], "pending builder availability");
    AssertEqual("overlay/test_modal", actions[0]["state_type"], "pending builder state type");
}

static void LegalActionBuilderExtractsTypedMapActions()
{
    var left = new TestMapPoint { coord = new TestMapCoord { col = 1, row = 1 }, PointType = "Monster" };
    var right = new TestMapPoint { coord = new TestMapCoord { col = 2, row = 1 }, PointType = "Shop" };
    var current = new TestMapPoint
    {
        coord = new TestMapCoord { col = 1, row = 0 },
        PointType = "RestSite",
        Children = new object?[] { left, right }
    };
    var runState = new TestRunState
    {
        Map = new TestMap(),
        CurrentMapPoint = current,
        CurrentMapCoord = current.coord,
        CurrentActIndex = 0
    };
    var builder = TestLegalActionBuilder(new Dictionary<string, object?>
    {
        ["MapSelectionSynchronizer"] = new TestMapSelectionSynchronizer { MapGenerationCount = 3 }
    });

    var actions = builder.Build(TestSnapshot("map"), runState, localPlayer: null);

    AssertEqual(2, actions.Count, "map should expose current point children");
    AssertTrue(actions.All(action => Equals(action["action_type"], "choose_map_node")),
        "map actions should be typed choose_map_node");
    AssertEqual(0, actions[0]["index"], "first map action index");
    AssertEqual("Monster", actions[0]["point_type"], "first map point type");
    AssertEqual(1, actions[0]["coord_col"], "first map col");
    AssertEqual(1, actions[0]["coord_row"], "first map row");
    AssertEqual(3, actions[0]["map_generation_count"], "map generation count");
    var sourceCoord = (IReadOnlyDictionary<string, object?>)actions[0]["source_coord"]!;
    AssertEqual(1, sourceCoord["col"], "source coord col");
    AssertEqual(0, sourceCoord["row"], "source coord row");
    var candidates = (IReadOnlyList<Dictionary<string, object?>>)actions[0]["candidate_paths"]!;
    AssertEqual(2, candidates.Count, "candidate path count");
}

static void LegalActionBuilderExtractsActStartMapActions()
{
    var start = new TestMapPoint
    {
        coord = new TestMapCoord { col = 3, row = 0 },
        PointType = "Monster",
        mapGenerationCount = 2
    };
    var child = new TestMapPoint
    {
        coord = new TestMapCoord { col = 5, row = 1 },
        PointType = "Monster",
        mapGenerationCount = 2
    };
    var syntheticRoot = new TestMapPoint
    {
        Children = new object?[] { child }
    };
    var runState = new TestRunState
    {
        Map = new TestMap
        {
            startMapPoints = new object?[] { start }
        },
        CurrentMapPoint = syntheticRoot,
        CurrentActIndex = 2
    };
    var builder = TestLegalActionBuilder(new Dictionary<string, object?>
    {
        ["MapSelectionSynchronizer"] = new TestMapSelectionSynchronizer { MapGenerationCount = 2 }
    });

    var actions = builder.Build(TestSnapshot("map"), runState, localPlayer: null);

    AssertEqual(1, actions.Count, "act-start map should expose start map points");
    AssertEqual("choose_map_node", actions[0]["action_type"], "act-start map action type");
    AssertEqual(3, actions[0]["coord_col"], "act-start first coord col");
    AssertEqual(0, actions[0]["coord_row"], "act-start first coord row");
    AssertEqual(null, actions[0]["source_coord"], "act-start source coord should remain null");
    AssertEqual(2, actions[0]["map_generation_count"], "act-start map generation count");
}

static void LegalActionBuilderExtractsTypedEventRestAndTreasureActions()
{
    var builder = TestLegalActionBuilder(new Dictionary<string, object?>
    {
        ["EventSynchronizer"] = new TestEventSynchronizer
        {
            IsShared = false,
            LocalEvent = new TestEventModel
            {
                Id = new TestModelId { Entry = "NEOW" },
                CurrentOptions = new object?[]
                {
                    new TestEventOption { TextKey = "GAIN_RELIC", IsLocked = false, Relic = new TestRelic { Id = "starter_relic" } },
                    new TestEventOption { TextKey = "LOCKED", IsLocked = true, IsProceed = true }
                }
            }
        },
        ["RestSiteSynchronizer"] = new TestRestSiteSynchronizer
        {
            LocalOptions = new object?[]
            {
                new TestRestSiteOption { OptionId = "HEAL", IsEnabled = true },
                new TestRestSiteOption { OptionId = "SMITH", IsEnabled = false, SmithCount = 2 }
            }
        },
        ["TreasureRoomRelicSynchronizer"] = new TestTreasureRoomRelicSynchronizer
        {
            CurrentRelics = new object?[]
            {
                new TestRelic { Id = new TestModelId { Entry = "BAG_OF_PREP" }, Rarity = "Rare" }
            }
        }
    });

    var eventActions = builder.Build(TestSnapshot("event"), runState: null, localPlayer: null);
    AssertEqual(2, eventActions.Count, "event option count");
    AssertEqual("choose_event_option", eventActions[0]["action_type"], "event action type");
    AssertEqual("run_start", eventActions[0]["event_source"], "Neow-like event source");
    AssertEqual("NEOW", eventActions[0]["event_id"], "event id");
    AssertEqual("GAIN_RELIC", eventActions[0]["option_text_key"], "event option text key");
    AssertEqual("available", eventActions[0]["availability"], "event option availability");
    AssertEqual("locked", eventActions[1]["availability"], "locked event option availability");
    AssertEqual("text_suppressed_locstring_runtime_safety", eventActions[0]["text_status"], "event text suppression");

    var restActions = builder.Build(TestSnapshot("rest_site"), runState: null, localPlayer: null);
    AssertEqual(2, restActions.Count, "rest option count");
    AssertEqual("choose_rest_option", restActions[0]["action_type"], "rest action type");
    AssertEqual("HEAL", restActions[0]["option_id"], "rest option id");
    AssertEqual("disabled", restActions[1]["availability"], "disabled rest option availability");
    AssertEqual(2, restActions[1]["smith_count"], "smith count");

    var treasureActions = builder.Build(TestSnapshot("treasure"), runState: null, localPlayer: null);
    AssertEqual(2, treasureActions.Count, "treasure relic plus skip action");
    AssertEqual("choose_treasure_relic", treasureActions[0]["action_type"], "treasure choose action type");
    AssertEqual("BAG_OF_PREP", treasureActions[0]["relic_id"], "treasure relic id");
    AssertEqual("skip_treasure_relic", treasureActions[1]["action_type"], "treasure skip action type");
    AssertEqual(null, treasureActions[1]["relic_index"], "skip relic index");
}

static void LegalActionBuilderExtractsNonCombatDiscardPotionActions()
{
    var builder = TestLegalActionBuilder(new Dictionary<string, object?>
    {
        ["EventSynchronizer"] = new TestEventSynchronizer
        {
            LocalEvent = new TestEventModel
            {
                Id = "event-a",
                CurrentOptions = new object?[] { new TestEventOption { TextKey = "LEAVE" } }
            }
        }
    });
    var player = new TestPlayer
    {
        CanRemovePotions = true,
        PotionSlots = new object?[]
        {
            null,
            new TestPotion
            {
                Id = new TestModelId { Entry = "FIRE_POTION" },
                Title = "Fire Potion",
                TargetType = "AnyEnemy",
                Usage = "CombatOnly"
            }
        }
    };

    var actions = builder.Build(TestSnapshot("event"), runState: null, player);
    var discard = actions.Single(action => Equals(action["action_type"], "discard_potion"));

    AssertEqual("local_player_potion_slots", discard["source"], "discard source");
    AssertEqual(1, discard["slot"], "discard potion slot");
    AssertEqual("FIRE_POTION", discard["potion_id"], "discard potion id");
    AssertTrue(!discard.ContainsKey("potion_name"), "discard legal actions should not format potion display names");
    AssertEqual("name_suppressed_locstring_runtime_safety", discard["potion_name_status"], "discard potion name suppression");
    AssertEqual(true, discard["can_discard"], "discard can select");
    AssertEqual("available", discard["availability"], "discard availability");

    var rewardActions = builder.Build(TestSnapshot("card_reward"), runState: null, player);
    AssertTrue(!rewardActions.Any(action => Equals(action["action_type"], "discard_potion")),
        "card reward should suppress non-combat discard legal actions until a typed safe surface exists");
}

static void LegalActionBuilderReturnsSurfaceSpecificUnavailableMarkers()
{
    RewardChoiceCache.Shared.Clear();
    SelectionChoiceCache.Shared.Clear();
    var shopOffers = new LegalActionBuilder().BuildShopOffers(TestSnapshot("shop"), runState: null, localPlayer: null);
    AssertEqual("shop_offers_typed_builder_unavailable", shopOffers[0]["action_type"],
        "shop offers should use a surface-specific unavailable marker");
    AssertEqual("merchant_inventory_not_found", shopOffers[0]["availability"],
        "shop offers should explain that typed merchant inventory is unavailable");

    var actions = new LegalActionBuilder().Build(TestSnapshot("card_reward"), runState: null, localPlayer: null);

    AssertTrue(actions.Any(action => Equals(action["action_type"], "card_reward_typed_builder_unavailable")),
        "card reward should use a surface-specific unavailable marker");
    AssertTrue(actions.Any(action => Equals(action["availability"], "typed_reward_runtime_cache_not_populated")),
        "card reward should explain that the typed reward cache is unavailable");
    AssertTrue(actions.Any(action => Equals(action["action_type"], "potion_replace_typed_builder_unavailable")),
        "card reward should expose potion replacement unavailable marker");
    AssertTrue(actions.Any(action => Equals(action["action_type"], "potion_full_slot_skip_unavailable")),
        "card reward should expose potion full-slot skip unavailable marker");
    AssertTrue(!actions.Any(action => Equals(action["action_type"], "typed_builder_pending")),
        "UI-heavy known surfaces should not use generic typed_builder_pending");

    var rewardActions = new LegalActionBuilder().Build(TestSnapshot("rewards"), runState: null, localPlayer: null);
    AssertTrue(rewardActions.Any(action => Equals(action["action_type"], "potion_replace_typed_builder_unavailable")),
        "reward screen should expose potion replacement unavailable marker");
    AssertTrue(rewardActions.Any(action => Equals(action["action_type"], "potion_full_slot_skip_unavailable")),
        "reward screen should expose potion full-slot skip unavailable marker");

    var bundleActions = new LegalActionBuilder().Build(TestSnapshot("bundle_select"), runState: null, localPlayer: null);
    AssertEqual("bundle_select_typed_builder_unavailable", bundleActions[0]["action_type"],
        "bundle select should use a surface-specific unavailable marker");
    var relicActions = new LegalActionBuilder().Build(TestSnapshot("relic_select"), runState: null, localPlayer: null);
    AssertEqual("relic_select_typed_builder_unavailable", relicActions[0]["action_type"],
        "relic select should use a surface-specific unavailable marker when the typed opening cache is empty");
    var crystalSphereActions = new LegalActionBuilder().Build(TestSnapshot("crystal_sphere"), runState: null, localPlayer: null);
    AssertEqual("crystal_sphere_typed_builder_unavailable", crystalSphereActions[0]["action_type"],
        "crystal sphere should keep a surface-specific unavailable marker");
    var packActions = new LegalActionBuilder().Build(TestSnapshot("pack_select"), runState: null, localPlayer: null);
    AssertEqual("pack_select_typed_builder_unavailable", packActions[0]["action_type"],
        "pack selection should keep a surface-specific unavailable marker");
    var specialActions = new LegalActionBuilder().Build(TestSnapshot("special_select"), runState: null, localPlayer: null);
    AssertEqual("special_select_typed_builder_unavailable", specialActions[0]["action_type"],
        "special selection should keep a surface-specific unavailable marker");
}

static void LegalActionBuilderMarksCombatUnavailableOutsidePlayPhase()
{
    var builder = new LegalActionBuilder(() => new LegalActionBuilder.CombatAvailability(
        CanBuild: false,
        Availability: "combat_not_in_play_phase",
        IsInProgress: true,
        IsPlayPhase: false,
        PlayerActionsDisabled: false));

    var actions = builder.Build(TestSnapshot("combat"), runState: null, localPlayer: null);

    AssertEqual(1, actions.Count, "one combat availability marker");
    AssertEqual("combat_typed_builder_unavailable", actions[0]["action_type"], "combat marker action type");
    AssertEqual("combat_manager", actions[0]["source"], "combat marker source");
    AssertEqual("combat_not_in_play_phase", actions[0]["availability"], "combat marker reason");
    AssertEqual(true, actions[0]["is_in_progress"], "combat marker in progress");
    AssertEqual(false, actions[0]["is_play_phase"], "combat marker play phase");
}

static void LegalActionBuilderMarksCombatRuntimeUnavailableWithoutLocalPlayer()
{
    var builder = new LegalActionBuilder(() => new LegalActionBuilder.CombatAvailability(
        CanBuild: true,
        Availability: "available",
        IsInProgress: true,
        IsPlayPhase: true,
        PlayerActionsDisabled: false));

    var actions = builder.Build(TestSnapshot("combat"), runState: null, localPlayer: null);

    AssertEqual(1, actions.Count, "one combat runtime availability marker");
    AssertEqual("combat_typed_builder_unavailable", actions[0]["action_type"], "combat runtime marker action type");
    AssertEqual("combat_runtime", actions[0]["source"], "combat runtime marker source");
    AssertEqual("local_player_unavailable", actions[0]["availability"], "combat runtime marker reason");
    AssertTrue(!actions.Any(action => Equals(action.GetValueOrDefault("action_type"), "end_turn")),
        "runtime-unavailable combat should not advertise end turn as available");
}

static void LegalActionBuilderEnrichesCombatActions()
{
    var enemy = TestEnemy(7, "JAW_WORM", "Jaw Worm", hp: 31);
    var combatState = new TestCombatState
    {
        Enemies = new[] { enemy }
    };
    var player = TestCombatPlayer(combatState);

    var builder = new LegalActionBuilder(() => new LegalActionBuilder.CombatAvailability(
        CanBuild: true,
        Availability: "available",
        IsInProgress: true,
        IsPlayPhase: true,
        PlayerActionsDisabled: false));

    var actions = builder.Build(TestSnapshot("combat"), runState: null, player);

    var cardAction = actions.Single(action => Equals(action["action_type"], "play_card"));
    AssertEqual("combat_hand", cardAction["source"], "card action source");
    AssertEqual(0, cardAction["card_index"], "card index");
    AssertEqual("STRIKE", cardAction["card_id"], "card id");
    AssertEqual("Strike", cardAction["card_name"], "card name");
    AssertEqual("Attack", cardAction["card_type"], "card type");
    AssertEqual("AnyEnemy", cardAction["target_type"], "card target type");
    AssertEqual(true, cardAction["requires_target"], "targeted card requires target");
    AssertEqual("enemies", cardAction["target_index_space"], "card target index space");
    AssertEqual(true, cardAction["can_play"], "card can play");
    AssertMissingKey(cardAction, "target_candidates",
        "combat legal card actions should not repeat full target candidates");
    var cardTargetIndices = ((IEnumerable<object?>)cardAction["valid_target_indices"]!).ToArray();
    AssertEqual(1, cardTargetIndices.Length, "card valid target count");
    AssertEqual(0, cardTargetIndices[0], "card valid target index");

    var potionAction = actions.Single(action => Equals(action["action_type"], "use_potion"));
    AssertEqual(0, potionAction["slot"], "potion slot");
    AssertEqual("FIRE_POTION", potionAction["potion_id"], "potion id");
    AssertEqual("AnyEnemy", potionAction["target_type"], "potion target type");
    AssertEqual("enemies", potionAction["target_index_space"], "potion target index space");
    AssertEqual(true, potionAction["requires_target"], "potion requires target");
    AssertEqual(true, potionAction["can_use"], "potion can use");
    AssertEqual("available", potionAction["availability"], "potion availability");
    AssertMissingKey(potionAction, "target_candidates",
        "combat legal potion actions should not repeat full target candidates");
    var potionTargetIndices = ((IEnumerable<object?>)potionAction["valid_target_indices"]!).ToArray();
    AssertEqual(1, potionTargetIndices.Length, "potion valid target count");
    AssertEqual(0, potionTargetIndices[0], "potion valid target index");
    AssertTrue(actions.Any(action => Equals(action["action_type"], "end_turn")
        && Equals(action["availability"], "available")), "end-turn action should stay available");
}

static void CombatDecisionFrameDeduplicatesTargetCandidates()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var targetCandidates = new[]
        {
            new Dictionary<string, object?>
            {
                ["target_index_space"] = "enemies",
                ["target_index"] = 0,
                ["target_type"] = "enemy",
                ["entity_id"] = "JAW_WORM_0",
                ["combat_id"] = 7,
                ["enemy_id"] = "JAW_WORM",
                ["name"] = "Jaw Worm",
                ["hp"] = 31,
                ["max_hp"] = 41,
                ["block"] = 3,
                ["is_alive"] = true,
                ["is_hittable"] = true
            }
        };
        var preRaw = new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["combat"] = new Dictionary<string, object?>
            {
                ["round"] = 5,
                ["current_side"] = "Player",
                ["is_play_phase"] = true,
                ["process"] = new Dictionary<string, object?>
                {
                    ["turn_index"] = 5,
                    ["turn_side"] = "Player",
                    ["phase"] = "main_phase",
                    ["action_step"] = "choose_action",
                    ["action_index"] = 11
                },
                ["target_candidates"] = targetCandidates
            }
        };
        var postRaw = new Dictionary<string, object?>
        {
            ["state_type"] = "combat",
            ["combat"] = new Dictionary<string, object?>
            {
                ["round"] = 5,
                ["current_side"] = "Enemy",
                ["is_play_phase"] = false,
                ["process"] = new Dictionary<string, object?>
                {
                    ["turn_index"] = 5,
                    ["turn_side"] = "Enemy",
                    ["phase"] = "resolution_phase",
                    ["action_step"] = "resolve_action",
                    ["action_index"] = 12
                },
                ["target_candidates"] = targetCandidates
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            TestSnapshotWithRaw("combat", preRaw),
            TestSnapshotWithRaw("combat", postRaw));
        var legalActionBuilder = new LegalActionBuilder(() => new LegalActionBuilder.CombatAvailability(
            CanBuild: true,
            Availability: "available",
            IsInProgress: true,
            IsPlayPhase: true,
            PlayerActionsDisabled: false));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, legalActionBuilder);
            EnableCapturingForTest(recorder, "run-combat-sidecar");

            var action = CreateUninitializedGameAction("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            recorder.BeginActionExecutorDecision(action);
            recorder.CompleteActionExecutorDecision(action);
        }

        string path = Path.Combine(directory, "runs", "run-combat-sidecar", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for combat sidecar frame");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "combat before/after pair should write one frame");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        JsonElement legalActions = root.GetProperty("legal_actions");
        JsonElement sidecarTargets = legalActions.GetProperty("target_candidates");
        AssertEqual(1, sidecarTargets.GetArrayLength(), "frame legal action target sidecar count");
        AssertEqual("JAW_WORM_0",
            sidecarTargets[0].GetProperty("entity_id").GetString(),
            "frame legal action target sidecar entity");

        foreach (JsonElement legalAction in legalActions.GetProperty("actions").EnumerateArray())
        {
            AssertTrue(!legalAction.TryGetProperty("target_candidates", out _),
                "combat legal action entries should not repeat full target candidates");
        }

        JsonElement timing = root.GetProperty("operational_metadata").GetProperty("decision_timing");
        AssertTimingDuration(timing, "pre_snapshot_us");
        AssertTimingDuration(timing, "run_player_lookup_us");
        AssertTimingDuration(timing, "legal_action_build_us");
        AssertTimingDuration(timing, "selected_action_build_us");
        AssertTimingDuration(timing, "normalized_typed_action_key_build_us");
        AssertTimingDuration(timing, "selected_action_hash_us");
        AssertTimingDuration(timing, "post_snapshot_us");
        AssertTimingDuration(timing, "branch_edge_recording_us");
        AssertTimingDuration(timing, "decision_frame_build_enqueue_us");
        AssertEqual("microseconds", timing.GetProperty("unit").GetString(), "decision timing unit");

        JsonElement selectedAction = root.GetProperty("selected_action");
        AssertEqual(
            TelemetryHash.HashCanonical(new Dictionary<string, object?> { ["action_type"] = "end_turn" }),
            selectedAction.GetProperty("canonical_action_hash").GetString(),
            "decision timing metadata should not affect selected-action canonical hash");

        JsonElement combatProcess = root.GetProperty("combat_process");
        AssertEqual("action_executor", combatProcess.GetProperty("decision_source").GetString(),
            "combat process should preserve decision source");
        AssertEqual("end_turn", combatProcess.GetProperty("selected_action_type").GetString(),
            "combat process should preserve selected action type");
        JsonElement markerStatus = combatProcess.GetProperty("marker_status");
        AssertEqual("present", markerStatus.GetProperty("turn").GetString(), "combat process turn marker status");
        AssertEqual("present", markerStatus.GetProperty("phase").GetString(), "combat process phase marker status");
        AssertEqual("present", markerStatus.GetProperty("action_step").GetString(), "combat process action-step marker status");
        AssertEqual("main_phase", combatProcess.GetProperty("pre").GetProperty("phase").GetString(),
            "combat process pre phase");
        AssertEqual("resolution_phase", combatProcess.GetProperty("post").GetProperty("phase").GetString(),
            "combat process post phase");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void LegalActionBuilderMarksPotionAvailabilityAndTargetSemantics()
{
    var combatState = new TestCombatState
    {
        Enemies = Array.Empty<object?>()
    };
    var potions = new object?[]
    {
        new TestPotion
        {
            Id = new TestModelId { Entry = "FIRE_POTION" },
            Title = "Fire Potion",
            TargetType = "AnyEnemy",
            Usage = "CombatOnly",
            PassesCustomUsabilityCheck = true
        },
        new TestPotion
        {
            Id = new TestModelId { Entry = "FAIRY_POTION" },
            Title = "Fairy Potion",
            TargetType = "AnyPlayer",
            Usage = "AnyTime",
            PassesCustomUsabilityCheck = true
        },
        new TestPotion
        {
            Id = new TestModelId { Entry = "LOCKED_POTION" },
            Title = "Locked Potion",
            TargetType = "AnyEnemy",
            Usage = "CombatOnly",
            PassesCustomUsabilityCheck = false
        },
        new TestPotion
        {
            Id = new TestModelId { Entry = "SMOKE_BOMB" },
            Title = "Smoke Bomb",
            TargetType = "TargetedNoCreature",
            Usage = "CombatOnly",
            PassesCustomUsabilityCheck = true
        }
    };
    var player = TestCombatPlayer(combatState, potionSlots: potions);

    var builder = new LegalActionBuilder(() => new LegalActionBuilder.CombatAvailability(
        CanBuild: true,
        Availability: "available",
        IsInProgress: true,
        IsPlayPhase: true,
        PlayerActionsDisabled: false));

    var actions = builder.Build(TestSnapshot("combat"), runState: null, player);
    var firePotion = actions.Single(action =>
        action.TryGetValue("potion_id", out object? potionId) && Equals(potionId, "FIRE_POTION"));
    AssertEqual(true, firePotion["requires_target"], "enemy potion requires target");
    AssertEqual(false, firePotion["can_use"], "enemy potion without targets cannot use");
    AssertEqual("no_valid_targets", firePotion["availability"], "enemy potion target availability");

    var playerPotion = actions.Single(action =>
        action.TryGetValue("potion_id", out object? potionId) && Equals(potionId, "FAIRY_POTION"));
    AssertEqual(false, playerPotion["requires_target"], "single-player AnyPlayer potion should not require indexed target");
    AssertEqual(null, playerPotion["target_index_space"], "single-player AnyPlayer potion target index space");
    AssertEqual(true, playerPotion["can_use"], "single-player AnyPlayer potion can use");
    AssertEqual("available", playerPotion["availability"], "single-player AnyPlayer potion availability");

    var lockedPotion = actions.Single(action =>
        action.TryGetValue("potion_id", out object? potionId) && Equals(potionId, "LOCKED_POTION"));
    AssertEqual(false, lockedPotion["can_use"], "potion failing usability check cannot use");
    AssertEqual("potion_unusable", lockedPotion["availability"], "potion usability availability");

    var noCreaturePotion = actions.Single(action =>
        action.TryGetValue("potion_id", out object? potionId) && Equals(potionId, "SMOKE_BOMB"));
    AssertEqual(false, noCreaturePotion["requires_target"], "TargetedNoCreature potion should not require creature target candidates");
    AssertEqual(true, noCreaturePotion["can_use"], "TargetedNoCreature potion can use without creature target candidates");
    AssertEqual("available", noCreaturePotion["availability"], "TargetedNoCreature potion availability");
}

static void StateSnapshotBuilderEnrichesCombatProjections()
{
    var enemy = TestEnemy(7, "JAW_WORM", "Jaw Worm", hp: 31);
    var combatState = new TestCombatState
    {
        Enemies = new[] { enemy }
    };
    var player = TestCombatPlayer(combatState);

    var playerMetadata = StateSnapshotBuilder.BuildPlayerMetadataForTests(player, includeCombatDetails: true);
    AssertEqual(1, playerMetadata["hand_count"], "hand count");
    AssertEqual(1, playerMetadata["draw_pile_count"], "draw pile count");
    AssertEqual(1, playerMetadata["discard_pile_count"], "discard pile count");
    AssertEqual(1, playerMetadata["exhaust_pile_count"], "exhaust pile count");
    AssertEqual(2, playerMetadata["deck_count"], "deck count");

    var hand = (IReadOnlyList<Dictionary<string, object?>>)playerMetadata["hand"]!;
    AssertEqual("STRIKE", hand[0]["card_id"], "snapshot hand card id");
    AssertEqual(true, hand[0]["can_play"], "snapshot hand can play");

    var potions = (IReadOnlyList<Dictionary<string, object?>>)playerMetadata["potions"]!;
    AssertEqual(0, potions[0]["slot"], "snapshot potion slot");
    AssertEqual("FIRE_POTION", potions[0]["potion_id"], "snapshot potion id");
    AssertEqual("Fire Potion", potions[0]["potion_name"], "snapshot potion name");
    AssertEqual("AnyEnemy", potions[0]["target_type"], "snapshot potion target type");

    var powers = (IReadOnlyList<Dictionary<string, object?>>)playerMetadata["powers"]!;
    AssertEqual("VULNERABLE", powers[0]["power_id"], "player power id");
    AssertEqual(true, powers[0]["is_debuff"], "player power debuff flag");

    var combatMetadata = StateSnapshotBuilder.BuildCombatMetadataForTests(combatState, player);
    var enemies = (IReadOnlyList<Dictionary<string, object?>>)combatMetadata["enemies"]!;
    AssertEqual("JAW_WORM_0", enemies[0]["entity_id"], "enemy entity id");
    AssertEqual(7, enemies[0]["combat_id"], "enemy combat id");
    AssertEqual(31, enemies[0]["hp"], "enemy hp");

    var process = (IReadOnlyDictionary<string, object?>)combatMetadata["process"]!;
    AssertEqual(1, process["turn_index"], "combat process turn index");
    AssertEqual("Player", process["turn_side"], "combat process turn side");
    AssertEqual("main_phase", process["phase"], "combat process phase");
    AssertEqual("choose_action", process["action_step"], "combat process action step");
    AssertEqual(2, process["action_index"], "combat process action index");
    var markerStatus = (IReadOnlyDictionary<string, object?>)process["marker_status"]!;
    AssertEqual("present", markerStatus["turn_index"], "combat process turn marker");
    AssertEqual("present", markerStatus["phase"], "combat process phase marker");
    AssertEqual("present", markerStatus["action_step"], "combat process action-step marker");

    var targetCandidates = (IReadOnlyList<Dictionary<string, object?>>)combatMetadata["target_candidates"]!;
    AssertTrue(targetCandidates.Any(target => Equals(target["entity_id"], "JAW_WORM_0")),
        "combat target candidates should include enemy");
}

static void LegalActionBuilderExtractsTypedShopInventoryActions()
{
    var player = new TestPlayer { Gold = 160 };
    var duplicateCardEntry = new TestMerchantEntry
    {
        Card = new TestShopItem { Id = "card-a", Title = "Useful Card" },
        Cost = 42,
        IsStocked = true,
        EnoughGold = true,
        Used = false
    };
    var inventory = new TestMerchantInventory
    {
        CharacterCardEntries = new[]
        {
            duplicateCardEntry,
            new TestMerchantEntry
            {
                Card = new TestShopItem { Id = "too-expensive", Title = "Too Expensive" },
                Cost = 999,
                IsStocked = true,
                EnoughGold = false,
                Used = false
            }
        },
        CardEntries = new[]
        {
            duplicateCardEntry,
            new TestMerchantEntry
            {
                Card = new TestShopItem { Id = "unstocked", Title = "Unstocked" },
                Cost = 1,
                IsStocked = false,
                EnoughGold = true,
                Used = false
            }
        },
        RelicEntries = new[]
        {
            new TestMerchantEntry
            {
                Relic = new TestShopItem { Id = "relic-a", Title = "Useful Relic" },
                Cost = 120,
                IsStocked = true,
                EnoughGold = true,
                Used = false
            }
        },
        PotionEntries = new[]
        {
            new TestMerchantEntry
            {
                Potion = new TestShopItem { Id = "potion-a", Title = "Useful Potion" },
                Cost = 30,
                IsStocked = true,
                EnoughGold = true,
                Used = false
            }
        },
        CardRemovalEntry = new TestMerchantEntry
        {
            Name = "Card Removal",
            Cost = 75,
            EnoughGold = true,
            Used = false
        }
    };
    var runState = new TestRunState
    {
        CurrentRoom = new MerchantRoom
        {
            Inventory = inventory,
            Player = player
        }
    };

    var actions = new LegalActionBuilder().Build(TestSnapshot("shop"), runState, player);

    AssertEqual(4, actions.Count, "shop should expose purchasable card, relic, potion, and removal actions");
    var cardAction = actions.Single(action => Equals(action["action_type"], "buy_shop_card"));
    AssertEqual("character_card", cardAction["category"], "card category");
    AssertEqual(0, cardAction["index"], "card index");
    AssertEqual("card-a", cardAction["id"], "card id");
    AssertEqual("Useful Card", cardAction["name"], "card name");
    AssertEqual(42, cardAction["price"], "card price");
    AssertEqual(true, cardAction["is_stocked"], "card stocked");
    AssertEqual(true, cardAction["enough_gold"], "card enough gold");
    AssertEqual(false, cardAction["used"], "card used");
    AssertEqual(true, cardAction["can_buy"], "card can buy");
    var matchKey = (IReadOnlyDictionary<string, object?>)cardAction["match_key"]!;
    AssertEqual("buy_shop_card", matchKey["action_type"], "shop match action type");
    AssertEqual("character_card", matchKey["category"], "shop match category");
    AssertEqual(0, matchKey["index"], "shop match index");
    AssertEqual("card-a", matchKey["id"], "shop match id");

    AssertTrue(actions.Any(action => Equals(action["action_type"], "buy_shop_relic")
        && Equals(action["id"], "relic-a")), "relic action should be present");
    AssertTrue(actions.Any(action => Equals(action["action_type"], "buy_shop_potion")
        && Equals(action["id"], "potion-a")), "potion action should be present");
    AssertTrue(actions.Any(action => Equals(action["action_type"], "remove_card_at_shop")
        && Equals(action["id"], "card_removal")), "card removal action should be present");
    AssertTrue(!actions.Any(action => Equals(action["id"], "too-expensive")), "unaffordable entries are not legal actions");
    AssertTrue(!actions.Any(action => Equals(action["id"], "unstocked")), "unstocked entries are not legal actions");
}

static void LocalGameAssemblyExposesRequiredHookTargets()
{
    Assembly gameAssembly = typeof(RunManager).Assembly;

    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Runs.RunManager", "SetUpSavedSinglePlayer");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Runs.RunManager", "CleanUp");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Runs.RunManager", "Abandon");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Runs.RunManager", "OnEnded");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Saves.SaveManager", "SaveRun");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Saves.SaveManager", "LoadRunSave");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer", "ChooseLocalOption", typeof(int));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer", "ChooseLocalOption", typeof(int));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom", "AfterSelectingOption");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry", "OnTryPurchaseWrapper",
        typeof(MerchantInventory), typeof(bool));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry", "OnTryPurchaseWrapper",
        typeof(MerchantInventory), typeof(bool), typeof(bool));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry", "InvokePurchaseCompleted",
        typeof(MerchantEntry));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory", "OnPurchaseCompleted",
        typeof(PurchaseStatus), typeof(MerchantEntry));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", "CardsSelected");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen", "SelectCard",
        typeof(NCardHolder));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChoiceSelectionSkipButton", "OnPress");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Rewards.RewardsSet", "GenerateWithoutOffering");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Rewards.CardReward", "OnSelect");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Commands.RelicSelectCmd", "FromChooseARelicScreen",
        typeof(Player), typeof(IReadOnlyList<RelicModel>));
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Commands.CardSelectCmd", "FromChooseABundleScreen",
        typeof(Player), typeof(IReadOnlyList<IReadOnlyList<CardModel>>));
    AssertHasExactMethod(gameAssembly, "MegaCrit.Sts2.Core.Models.RelicModel", "Flash");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Models.RelicModel", "Flash", typeof(IEnumerable<Creature>));
    AssertHasEventOrDelegateField(gameAssembly, "MegaCrit.Sts2.Core.Models.RelicModel", "Flashed");
    AssertEventOrDelegateFieldParameterCount(gameAssembly, "MegaCrit.Sts2.Core.Models.RelicModel", "Flashed", 2);
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu", "_Ready");
    AssertHasMethod(gameAssembly, "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu", "_ExitTree");
}

static void JsonlWriterPersistsRecords()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            writer.Enqueue("run-test", new Dictionary<string, object?>
            {
                ["schema_version"] = TelemetryRecorder.SchemaVersion,
                ["record_type"] = "test/record",
                ["value"] = 7
            });
        }

        string path = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "one record line");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        AssertEqual("test/record", document.RootElement.GetProperty("record_type").GetString(), "record_type");
        AssertEqual(7, document.RootElement.GetProperty("value").GetInt32(), "value");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void JsonlWriterRoutesLogicalRunRecordsToSegmentFiles()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            writer.Enqueue("run-segment-a", new Dictionary<string, object?>
            {
                ["schema_version"] = TelemetryRecorder.SchemaVersion,
                ["record_type"] = "test/record",
                ["run_id"] = "run-segment-a",
                ["logical_run_id"] = "abc123",
                ["segment_id"] = "run-segment-a",
                ["value"] = 11
            });
            writer.Enqueue("run-segment-b", new Dictionary<string, object?>
            {
                ["schema_version"] = TelemetryRecorder.SchemaVersion,
                ["record_type"] = "test/prefixed-record",
                ["run_id"] = "run-segment-b",
                ["logical_run_id"] = "logical-run-def456",
                ["segment_id"] = "run-segment-b",
                ["value"] = 12
            });
        }

        string logicalPath = Path.Combine(
            directory,
            "runs",
            "logical-run-abc123",
            "segments",
            "run-segment-a.jsonl");
        string prefixedLogicalPath = Path.Combine(
            directory,
            "runs",
            "logical-run-def456",
            "segments",
            "run-segment-b.jsonl");
        string doublePrefixedLogicalPath = Path.Combine(
            directory,
            "runs",
            "logical-run-logical-run-def456",
            "segments",
            "run-segment-b.jsonl");
        string legacyPath = Path.Combine(directory, "runs", "run-segment-a", "telemetry.jsonl");
        AssertTrue(File.Exists(logicalPath), "logical segment JSONL should exist");
        AssertTrue(File.Exists(prefixedLogicalPath), "prefixed logical_run_id should use a single logical-run directory prefix");
        AssertTrue(!File.Exists(doublePrefixedLogicalPath), "prefixed logical_run_id should not be double-prefixed");
        AssertTrue(!File.Exists(legacyPath), "identity-known records should use the logical segment path");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllLines(logicalPath).Single());
        AssertEqual("test/record", document.RootElement.GetProperty("record_type").GetString(), "logical record_type");
        AssertEqual("abc123", document.RootElement.GetProperty("logical_run_id").GetString(), "logical_run_id");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderEnvelopeIncludesCaptureSessionAndSegmentIdentity()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-capture-session");
            SetPrivateField(recorder, "_logicalRunIdentity", new Dictionary<string, object?>
            {
                ["status"] = "complete",
                ["logical_run_id"] = "logical-run-session-test",
                ["logical_run_key"] = "seed|ironclad|0"
            });

            recorder.RecordPatchedUiSignal("test.signal", null, Array.Empty<object?>());
        }

        string path = Path.Combine(
            directory,
            "runs",
            "logical-run-session-test",
            "segments",
            "run-capture-session.jsonl");
        AssertTrue(File.Exists(path), "recorder record should be routed to logical segment path");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllLines(path).Single());
        JsonElement root = document.RootElement;
        AssertEqual("run-capture-session", root.GetProperty("run_id").GetString(), "run_id remains capture session");
        AssertEqual("run-capture-session", root.GetProperty("capture_session_id").GetString(), "capture_session_id");
        AssertEqual("run-capture-session", root.GetProperty("segment_id").GetString(), "segment_id");
        AssertEqual("logical-run-session-test", root.GetProperty("logical_run_id").GetString(), "logical_run_id");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void JsonlWriterPersistsNonFiniteNumbers()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var payload = new Dictionary<string, object?>
        {
            ["nan"] = double.NaN,
            ["positive_infinity"] = double.PositiveInfinity,
            ["negative_infinity"] = float.NegativeInfinity,
            ["nested"] = new Dictionary<string, object?>
            {
                ["finite"] = 1.5f,
                ["nan"] = float.NaN
            }
        };

        AssertEqual(64, TelemetryHash.HashRaw(payload).Length, "raw hash length");
        AssertEqual(64, TelemetryHash.HashCanonical(payload).Length, "canonical hash length");

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            writer.Enqueue("run-nonfinite", payload);
        }

        string path = Path.Combine(directory, "runs", "run-nonfinite", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for non-finite values");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "one non-finite record line");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        AssertEqual("NaN", document.RootElement.GetProperty("nan").GetString(), "NaN should be represented as a JSON string");
        AssertEqual("Infinity", document.RootElement.GetProperty("positive_infinity").GetString(), "positive infinity string");
        AssertEqual("-Infinity", document.RootElement.GetProperty("negative_infinity").GetString(), "negative infinity string");
        AssertEqual(1.5, document.RootElement.GetProperty("nested").GetProperty("finite").GetDouble(), "finite value remains numeric");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void TelemetryDirectoryResolverKeepsRuntimeDataOutsideModDirectory()
{
    string? originalOverride = Environment.GetEnvironmentVariable(TelemetryDirectoryResolver.EnvironmentVariableName);
    try
    {
        string overrideDirectory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-override-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(TelemetryDirectoryResolver.EnvironmentVariableName, overrideDirectory);
        AssertEqual(
            Path.GetFullPath(overrideDirectory),
            TelemetryDirectoryResolver.ResolveForMod(() => "/ignored/userdata", () => "/ignored/fallback"),
            "explicit telemetry directory override");

        Environment.SetEnvironmentVariable(TelemetryDirectoryResolver.EnvironmentVariableName, null);
        string userDataDirectory = Path.Combine(Path.GetTempPath(), $"sts2-userdata-{Guid.NewGuid():N}");
        AssertEqual(
            Path.Combine(userDataDirectory, TelemetryDirectoryResolver.TelemetryDirectoryName),
            TelemetryDirectoryResolver.ResolveForMod(() => userDataDirectory, () => "/ignored/fallback"),
            "Godot user data directory should own telemetry by default");

        string fallbackRoot = Path.Combine(Path.GetTempPath(), $"sts2-fallback-{Guid.NewGuid():N}");
        AssertEqual(
            Path.Combine(fallbackRoot, "SlayTheSpire2", TelemetryDirectoryResolver.TelemetryDirectoryName),
            TelemetryDirectoryResolver.ResolveForMod(() => "", () => fallbackRoot),
            "fallback user data directory should stay outside the mod install directory");
    }
    finally
    {
        Environment.SetEnvironmentVariable(TelemetryDirectoryResolver.EnvironmentVariableName, originalOverride);
    }
}

static void UploadSettingsDefaultEnabledAndDisableMarkerWorks()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory);
        AssertTrue(settings.Enabled, "upload should default to enabled");
        AssertEqual("staging", settings.ActiveEndpoint, "default active endpoint");
        AssertEqual(TelemetryUploadSettings.DefaultEndpointUrl, settings.EffectiveEndpointUrl, "default staging endpoint");

        bool firstNotice = TelemetryUploadNotice.Ensure(directory, settings);
        AssertTrue(firstNotice, "first notice should be emitted before acknowledgement");
        string noticePath = Path.Combine(
            directory,
            TelemetryUploadSettings.UploadDirectoryName,
            TelemetryUploadSettings.NoticeFileName);
        AssertTrue(File.Exists(noticePath), "notice file should exist");
        using (JsonDocument notice = JsonDocument.Parse(File.ReadAllText(noticePath)))
        {
            AssertEqual("enabled", notice.RootElement.GetProperty("upload_default").GetString(), "upload default notice");
            AssertTrue(
                notice.RootElement.GetProperty("collected_data_categories").EnumerateArray().Any(category =>
                    category.GetString() == "scrubbed native save payloads from current run and history files"),
                "upload notice should name scrubbed native save payloads");
            AssertTrue(
                notice.RootElement.GetProperty("excluded_data_categories").EnumerateArray().Any(category =>
                    category.GetString() == "local filesystem paths"),
                "upload notice should exclude local filesystem paths");
            AssertEqual(
                $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.DisableUploadRequestFileName}",
                notice.RootElement.GetProperty("disable_path").GetString(),
                "notice direct disable path");
        }

        string disablePath = Path.Combine(
            directory,
            TelemetryUploadSettings.UploadDirectoryName,
            TelemetryUploadSettings.DisableUploadRequestFileName);
        File.WriteAllText(disablePath, "disable");
        TelemetryUploadSettings disabled = settings.ApplyDisableRequest(directory);
        AssertTrue(!disabled.Enabled, "disable marker should disable uploads");
        AssertTrue(!File.Exists(disablePath), "disable marker should be consumed");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UpdatePlannerAutoAuthorizesPatchAndGatesMinor()
{
    var patchManifest = new TelemetryModReleaseManifest
    {
        LatestVersion = "0.1.1",
        MinSupportedVersion = "0.1.1",
        RequiresConfirmation = false,
        Artifacts = new[]
        {
            new TelemetryModReleaseArtifact
            {
                Platform = "linux-x64",
                Kind = "mod_package",
                Url = "https://example.invalid/sts2-telemetry-0.1.1-linux-x64.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 123
            }
        }
    };

    TelemetryUpdatePlan patch = TelemetryUpdatePlanner.Plan("0.1.0", patchManifest, "linux-x64");
    AssertEqual(TelemetryUpdateStates.AutoInstallReady, patch.State, "patch update state");
    AssertEqual("patch_update_auto_authorized", patch.Reason, "patch update reason");
    AssertEqual(TelemetryUpdateKinds.Patch, patch.UpdateKind, "patch update kind");
    AssertEqual(TelemetryUpdateAuthorization.AutomaticPatch, patch.Authorization, "patch authorization");
    AssertTrue(patch.Artifact != null, "patch artifact should be selected");

    var minorManifest = patchManifest with { LatestVersion = "0.2.0" };
    TelemetryUpdatePlan minor = TelemetryUpdatePlanner.Plan("0.1.0", minorManifest, "linux-x64");
    AssertEqual(TelemetryUpdateStates.UpdateAvailable, minor.State, "minor update should not auto-install");
    AssertEqual(TelemetryUpdateKinds.Minor, minor.UpdateKind, "minor update kind");
    AssertEqual(TelemetryUpdateAuthorization.RequiresUserConfirmation, minor.Authorization, "minor authorization");

    TelemetryUpdatePlan missingArtifact = TelemetryUpdatePlanner.Plan("0.1.0", patchManifest, "win-x64");
    AssertEqual(TelemetryUpdateStates.UpdateAvailable, missingArtifact.State, "missing platform artifact state");
    AssertEqual("no_platform_artifact", missingArtifact.Reason, "missing platform artifact reason");
}

static void UpdateStoreWritesStatusAndInstallRequestUnderUpdateDirectory()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var store = new TelemetryUpdateStore(directory);
        store.WriteStatus(new TelemetryUpdateStatus
        {
            State = TelemetryUpdateStates.InstallRequested,
            CurrentVersion = "0.1.0",
            TargetVersion = "0.1.1",
            Authorization = TelemetryUpdateAuthorization.AutomaticPatch
        });
        store.WriteInstallRequest(new TelemetryUpdateInstallRequest
        {
            RequestId = "request-test",
            CurrentVersion = "0.1.0",
            TargetVersion = "0.1.1",
            PackagePath = "/tmp/package.zip",
            PackageSha256 = new string('b', 64),
            TargetModDirectory = "/tmp/game/mods/telemetry",
            TelemetryBaseDirectory = directory,
            ResultPath = store.HelperResultPath
        });

        AssertEqual(
            Path.Combine(directory, TelemetryUpdateSettings.UpdateDirectoryName, TelemetryUpdateSettings.StatusFileName),
            store.StatusPath,
            "update status path");
        AssertTrue(File.Exists(store.StatusPath), "update status should exist");
        AssertTrue(File.Exists(store.InstallRequestPath), "install request should exist");

        TelemetryUpdateStatus status = store.ReadStatus()!;
        AssertEqual(TelemetryUpdateStates.InstallRequested, status.State, "stored update status");
        AssertEqual("0.1.1", status.TargetVersion, "stored target version");

        TelemetryUpdateInstallRequest request = store.ReadInstallRequest()!;
        AssertEqual("request-test", request.RequestId, "stored request id");
        AssertEqual(store.HelperResultPath, request.ResultPath, "stored helper result path");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UpdateInstallerRejectsBadHashAndAppliesValidPackage()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string targetDirectory = Path.Combine(directory, "game", "mods", "telemetry");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "Sts2Telemetry.dll"), "old dll");
        File.WriteAllText(Path.Combine(targetDirectory, "Sts2Telemetry.json"), "{\"version\":\"0.1.0\"}");

        string packagePath = Path.Combine(directory, "update.zip");
        CreateUpdatePackage(packagePath, "new dll", "{\"version\":\"0.1.1\"}");
        string packageSha = TelemetryUpdateHash.Sha256HexFile(packagePath);
        var store = new TelemetryUpdateStore(directory);

        var badRequest = new TelemetryUpdateInstallRequest
        {
            RequestId = "request-bad",
            CurrentVersion = "0.1.0",
            TargetVersion = "0.1.1",
            PackagePath = packagePath,
            PackageSha256 = new string('0', 64),
            TargetModDirectory = targetDirectory,
            TelemetryBaseDirectory = directory,
            ResultPath = store.HelperResultPath
        };
        TelemetryUpdateInstallResult badResult = TelemetryUpdateInstaller.Apply(badRequest);
        AssertEqual("failed", badResult.State, "bad hash should fail");
        AssertEqual("old dll", File.ReadAllText(Path.Combine(targetDirectory, "Sts2Telemetry.dll")), "bad hash keeps old dll");

        var goodRequest = badRequest with
        {
            RequestId = "request-good",
            PackageSha256 = packageSha
        };
        TelemetryUpdateInstallResult goodResult = TelemetryUpdateInstaller.Apply(goodRequest);
        AssertEqual("installed", goodResult.State, "valid package should install");
        AssertEqual("new dll", File.ReadAllText(Path.Combine(targetDirectory, "Sts2Telemetry.dll")), "dll replaced");
        AssertEqual("{\"version\":\"0.1.1\"}", File.ReadAllText(Path.Combine(targetDirectory, "Sts2Telemetry.json")),
            "manifest replaced");
        AssertTrue(
            File.Exists(Path.Combine(directory, "update", "backups", "request-good", "Sts2Telemetry.dll")),
            "backup dll should exist");
        AssertEqual("installed", store.ReadHelperResult()!.State, "helper result persisted");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void CreateUpdatePackage(string path, string dllContent, string manifestContent)
{
    using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
    AddZipEntry(archive, "Sts2Telemetry.dll", dllContent);
    AddZipEntry(archive, "Sts2Telemetry.json", manifestContent);
}

static void AddZipEntry(ZipArchive archive, string name, string content)
{
    ZipArchiveEntry entry = archive.CreateEntry(name);
    using Stream stream = entry.Open();
    using var writer = new StreamWriter(stream, Encoding.UTF8);
    writer.Write(content);
}

static void UploadQueuePackagesJsonlSegmentAsGzipManifest()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string segmentPath = Path.Combine(directory, "runs", "logical-run-test", "segments", "segment-001.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(segmentPath)!);
        File.WriteAllLines(segmentPath, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_started\",\"record_id\":\"rec-1\",\"envelope_id\":\"env-1\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-001\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\",\"operational_metadata\":{\"mod_version\":\"0.1.0\"},\"state\":{\"raw_snapshot\":{\"game\":{\"game_version\":\"v0.103-test\",\"mod_version\":\"0.1.0\"},\"run\":{\"floor\":4}}}}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_ended\",\"record_id\":\"rec-2\",\"envelope_id\":\"env-2\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-001\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:01:00Z\",\"is_victory\":true}"
        });

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 };
        IReadOnlyList<TelemetryUploadQueueItem> items = queue.PackagePendingSources(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            force: true);

        AssertEqual(1, items.Count, "one bundle should be queued");
        TelemetryUploadQueueItem item = items[0];
        AssertTrue(File.Exists(item.BundlePath), "gzip bundle should exist");
        AssertTrue(File.Exists(item.ManifestPath), "manifest should exist");

        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(item.ManifestPath)))
        {
            JsonElement root = manifest.RootElement;
            AssertEqual("inst-test", root.GetProperty("installation_id").GetString(), "manifest installation id");
            AssertEqual("run-test", root.GetProperty("run_id").GetString(), "manifest run id");
            AssertEqual("logical-run-test", root.GetProperty("logical_run_id").GetString(), "manifest logical run id");
            AssertEqual("segment-001", root.GetProperty("segment_id").GetString(), "manifest segment id");
            AssertEqual("gzip", root.GetProperty("compression").GetString(), "manifest compression");
            AssertEqual(2, root.GetProperty("record_count").GetInt32(), "manifest record count");
            AssertEqual(1L, root.GetProperty("first_local_sequence").GetInt64(), "first sequence");
            AssertEqual(2L, root.GetProperty("last_local_sequence").GetInt64(), "last sequence");
            AssertEqual("v0.103-test", root.GetProperty("game_version").GetString(), "game version");
            AssertEqual("0.1.0", root.GetProperty("mod_version").GetString(), "mod version");
            AssertEqual(64, root.GetProperty("sha256").GetString()!.Length, "bundle sha length");
        }

        using var input = File.OpenRead(item.BundlePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string jsonl = reader.ReadToEnd();
        AssertTrue(jsonl.Contains("\"record_type\":\"lifecycle/run_started\"", StringComparison.Ordinal),
            "bundle should preserve first JSONL record");
        AssertTrue(jsonl.Contains("\"record_type\":\"lifecycle/run_ended\"", StringComparison.Ordinal),
            "bundle should preserve second JSONL record");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueueBundlesNativeSaveCapturePayload()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    string saveRoot = Path.Combine(directory, "SlayTheSpire2");
    string telemetryDirectory = Path.Combine(saveRoot, "sts2-telemetry");
    string profileSaves = Path.Combine(saveRoot, "steam", "76561198000000000", "profile1", "saves");
    try
    {
        Directory.CreateDirectory(profileSaves);
        File.WriteAllText(
            Path.Combine(profileSaves, "current_run.save"),
            """
            {
              "schema_version": "save.v1",
              "seed": "20VBG069DW",
              "floor": 30,
              "steam_id": "steam-123",
              "local_path": "/home/player/.local/share/SlayTheSpire2/current_run.save",
              "card_id": "strike"
            }
            """);

        using (var writer = new JsonlTelemetryWriter(telemetryDirectory))
        {
            var nativeSaveCapture = new NativeSaveCapture(
                _ => new[] { saveRoot },
                () => new DateTimeOffset(2026, 5, 10, 1, 2, 3, TimeSpan.Zero));
            var recorder = new TelemetryRecorder(
                writer,
                new ThrowingSnapshotBuilder(),
                new LegalActionBuilder(),
                nativeSaveCapture);
            EnableCapturingForTest(recorder, "run-native-save-bundled");
            recorder.RecordSaveObserved("save_run.postfix");
        }

        var queue = new TelemetryUploadQueue(
            telemetryDirectory,
            "inst-test",
            () => DateTimeOffset.Parse("2026-05-10T01:04:00Z"));
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(telemetryDirectory) with
        {
            StableSourceSeconds = 0
        };
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();

        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(item.ManifestPath)))
            AssertEqual(2, manifest.RootElement.GetProperty("record_count").GetInt32(),
                "bundle manifest should count native save capture plus save observed");

        using var input = File.OpenRead(item.BundlePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string jsonl = reader.ReadToEnd();
        string[] lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(2, lines.Length, "bundle should contain both source JSONL records");
        using JsonDocument captureDocument = JsonDocument.Parse(lines.Single(line =>
            line.Contains("\"record_type\":\"native_save/capture\"", StringComparison.Ordinal)));
        JsonElement captureRecord = captureDocument.RootElement;
        AssertEqual("native_save/capture",
            captureRecord.GetProperty("record_type").GetString(),
            "bundle should include native save capture record");
        AssertEqual("read_only_native_save_scrubbed_payload",
            captureRecord.GetProperty("capture_policy").GetString(),
            "bundled native save capture policy");
        JsonElement payload = captureRecord.GetProperty("native_save").GetProperty("payload");
        string payloadJson = payload.GetRawText();
        AssertTrue(payloadJson.Contains("strike", StringComparison.Ordinal),
            "bundled native save payload should preserve gameplay card id");
        AssertTrue(!payloadJson.Contains("steam-123", StringComparison.Ordinal),
            "bundled native save payload should scrub steam id");
        AssertTrue(!payloadJson.Contains("/home/player", StringComparison.Ordinal),
            "bundled native save payload should scrub local paths");
        AssertTrue(!jsonl.Contains(profileSaves, StringComparison.Ordinal),
            "bundled native save records should not include local save directory paths");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueueSkipsDuplicateSourceDigest()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_started\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_ended\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:01:00Z\"}"
        });

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 };
        TelemetryUploadQueueItem item = queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        queue.MarkUploaded(item);

        string sourceStatePath = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(directory),
            TelemetryUploadSettings.SourceStateFileName);
        File.Delete(sourceStatePath);

        IReadOnlyList<TelemetryUploadQueueItem> secondPass = queue.PackagePendingSources(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            force: true);

        AssertEqual(0, secondPass.Count, "unchanged source digest should not enqueue a duplicate bundle");
        TelemetryUploadQueueItem uploaded = new TelemetryUploadQueue(directory, "inst-test").Items().Single();
        AssertEqual(64, uploaded.Status.SourceSha256?.Length ?? 0, "uploaded status should preserve source digest");
        AssertEqual(TelemetryRunQuality.Complete, uploaded.Status.RunQuality, "uploaded status should preserve run quality");

        string duplicateDirectory = Path.Combine(Path.GetDirectoryName(uploaded.Directory)!, "bundle_duplicate_source");
        Directory.CreateDirectory(duplicateDirectory);
        var duplicateStatus = uploaded.Status with
        {
            BundleId = "bundle_duplicate_source",
            CreatedAtUtc = uploaded.Status.CreatedAtUtc.AddMinutes(1),
            UpdatedAtUtc = uploaded.Status.UpdatedAtUtc.AddMinutes(1)
        };
        File.WriteAllText(
            Path.Combine(duplicateDirectory, "status.json"),
            JsonSerializer.Serialize(duplicateStatus, TelemetryJson.Options));

        TelemetryUploadSummary summary = queue.BuildSummary(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            hasToken: true,
            lastSyncState: "synced");
        AssertEqual(2, summary.UploadedBundles, "uploaded bundle count keeps historical duplicate statuses");
        AssertEqual(1, summary.UploadedSourceCount, "unique uploaded source count");
        AssertEqual(1, summary.DuplicateUploadedSourceCount, "duplicate uploaded source count");

        TelemetryUploadRunStatus run = TelemetryUploadStatusReader.Build(directory, maxRuns: 10).Runs.Single();
        AssertEqual(2, run.UploadedBundles, "view uploaded bundle count");
        AssertEqual(1, run.UploadedSourceCount, "view uploaded source count");
        AssertEqual(1, run.DuplicateUploadedSourceCount, "view duplicate source count");
        string rendered = TelemetryUploadStatusRenderer.RenderPlainText(new TelemetryUploadStatusView
        {
            UpdatedAtUtc = DateTimeOffset.Parse("2026-05-08T00:03:00Z"),
            Runs = new[] { run }
        });
        AssertTrue(rendered.Contains("重复源1", StringComparison.Ordinal), "renderer should show duplicate source count");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueuePackagesNextChunkForDuplicateSourceDigest()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:01:00Z\"}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_ended\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":3,\"recorded_at_utc\":\"2026-05-08T00:02:00Z\"}"
        });

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:03:00Z"));
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with
        {
            StableSourceSeconds = 0,
            MaxRecordsPerBundle = 2
        };
        TelemetryUploadQueueItem first = queue.PackagePendingSources(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();
        AssertEqual(2, first.Status.RecordCount, "first bundle should stop at max records");
        AssertEqual(2L, first.Status.LastLocalSequence, "first bundle last sequence");

        string sourceStatePath = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(directory),
            TelemetryUploadSettings.SourceStateFileName);
        File.Delete(sourceStatePath);

        TelemetryUploadQueueItem second = queue.PackagePendingSources(
            settings,
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();
        AssertEqual(1, second.Status.RecordCount, "second bundle should package remaining record");
        AssertEqual(3L, second.Status.FirstLocalSequence, "second bundle first sequence");
        AssertEqual(3L, second.Status.LastLocalSequence, "second bundle last sequence");
        AssertEqual(first.Status.SourceSha256, second.Status.SourceSha256, "partial bundles should track the same exact source digest");
        AssertEqual(TelemetryRunQuality.Complete, second.Status.RunQuality, "remaining chunk should preserve run quality");

        using var input = File.OpenRead(second.BundlePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string jsonl = reader.ReadToEnd();
        string[] lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(1, lines.Length, "second bundle should contain one JSONL record");
        AssertTrue(lines[0].Contains("\"local_sequence\":3", StringComparison.Ordinal),
            "second bundle should contain the next unqueued sequence");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueueBoundsDropOldestBundleWithReason()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllText(
            runPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with
        {
            StableSourceSeconds = 0,
            MaxQueueBytes = 1
        };
        TelemetryUploadQueueItem item = queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();

        TelemetryUploadQueueItem reloaded = TelemetryUploadQueueItem.TryLoad(item.Directory)!;
        AssertEqual("dropped", reloaded.Status.State, "oversized local queue item should be dropped");
        AssertEqual("queue_max_bytes_exceeded", reloaded.Status.DropReason, "drop reason");
        AssertTrue(!File.Exists(reloaded.BundlePath), "dropped bundle bytes should be removed");
        AssertTrue(!File.Exists(reloaded.ManifestPath), "dropped manifest should be removed");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueueMarkUploadedRemovesPayloadButKeepsStatusCoverage()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:01:00Z\"}"
        });

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();

        queue.MarkUploaded(item);

        TelemetryUploadQueueItem reloaded = TelemetryUploadQueueItem.TryLoad(item.Directory)!;
        AssertEqual("uploaded", reloaded.Status.State, "uploaded state");
        AssertEqual(1L, reloaded.Status.FirstLocalSequence, "uploaded first sequence");
        AssertEqual(2L, reloaded.Status.LastLocalSequence, "uploaded last sequence");
        AssertTrue(File.Exists(reloaded.StatusPath), "uploaded status should remain");
        AssertTrue(!File.Exists(reloaded.BundlePath), "uploaded bundle bytes should be removed");
        AssertTrue(!File.Exists(reloaded.ManifestPath), "uploaded manifest should be removed");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueuePrunesOnlyOldFullyUploadedRunSources()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-05-16T00:00:00Z");
        var queue = new TelemetryUploadQueue(directory, "inst-test", () => now);
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with
        {
            StableSourceSeconds = 0,
            MaxRunHistoryDays = 7
        };

        string oldUploaded = Path.Combine(directory, "runs", "run-old-uploaded", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(oldUploaded)!);
        File.WriteAllLines(oldUploaded, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-old-uploaded\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-01T00:00:00Z\"}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-old-uploaded\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-01T00:01:00Z\"}"
        });
        TelemetryUploadQueueItem oldItem = queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        queue.MarkUploaded(oldItem);
        File.SetLastWriteTimeUtc(oldUploaded, now.AddDays(-8).UtcDateTime);

        string recentUploaded = Path.Combine(directory, "runs", "run-recent-uploaded", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(recentUploaded)!);
        File.WriteAllText(
            recentUploaded,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-recent-uploaded\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-15T00:00:00Z\"}\n");
        TelemetryUploadQueueItem recentItem = queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        queue.MarkUploaded(recentItem);
        File.SetLastWriteTimeUtc(recentUploaded, now.AddDays(-1).UtcDateTime);

        queue.PruneUploadedRunSources(settings);

        AssertTrue(!File.Exists(oldUploaded), "old fully uploaded source should be pruned");
        AssertTrue(File.Exists(recentUploaded), "recent uploaded source should be retained");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadQueueRetainsOldRunSourcesWithUnsuccessfulEvidence()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-05-16T00:00:00Z");
        var queue = new TelemetryUploadQueue(directory, "inst-test", () => now);
        TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(directory) with
        {
            StableSourceSeconds = 0,
            MaxRunHistoryDays = 7
        };

        string pendingPath = Path.Combine(directory, "runs", "run-pending", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        File.WriteAllText(
            pendingPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-pending\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-01T00:00:00Z\"}\n");
        queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        File.SetLastWriteTimeUtc(pendingPath, now.AddDays(-8).UtcDateTime);

        string failedPath = Path.Combine(directory, "runs", "run-failed", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(failedPath)!);
        File.WriteAllText(
            failedPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-failed\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-01T00:00:00Z\"}\n");
        TelemetryUploadQueueItem failedItem = queue.PackagePendingSources(settings, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        queue.MarkFailed(failedItem, "test_failed", "test failure", retryAfterSeconds: null);
        File.SetLastWriteTimeUtc(failedPath, now.AddDays(-8).UtcDateTime);

        string droppedPath = Path.Combine(directory, "runs", "run-dropped", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(droppedPath)!);
        File.WriteAllText(
            droppedPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-dropped\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-01T00:00:00Z\"}\n");
        queue.PackagePendingSources(settings with { MaxQueueBytes = 1 }, TelemetryUploadPolicy.LocalDefault, force: true).Single();
        File.SetLastWriteTimeUtc(droppedPath, now.AddDays(-8).UtcDateTime);

        queue.PruneUploadedRunSources(settings);

        AssertTrue(File.Exists(pendingPath), "old pending source should be retained");
        AssertTrue(File.Exists(failedPath), "old failed source should be retained");
        AssertTrue(File.Exists(droppedPath), "old dropped source should be retained");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadSigningMaterialMatchesServerOrder()
{
    string material = TelemetryUploadCrypto.SigningMaterial(
        "inst-test",
        "tok-test",
        "2026-05-08T00:00:00Z",
        "nonce-test",
        "POST",
        "/v1/bundles",
        "manifest-sha",
        "bundle-sha");
    AssertEqual(
        "inst-test\ntok-test\n2026-05-08T00:00:00Z\nnonce-test\nPOST\n/v1/bundles\nmanifest-sha\nbundle-sha",
        material,
        "server signing material order");

    string signature = TelemetryUploadCrypto.SignatureHex("secret", material);
    AssertEqual(64, signature.Length, "hmac sha256 hex length");
}

static void UploadClientSendsSignedMultipartBundleShape()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllText(
            runPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();

        var handler = new RecordingUploadHandler();
        var client = new TelemetryUploadClient(
            new HttpClient(handler),
            () => DateTimeOffset.Parse("2026-05-08T00:03:00Z"));
        var token = new TelemetryUploadToken
        {
            InstallationId = "inst-test",
            UploadTokenId = "tok-test",
            UploadSecret = "secret-test"
        };

        client.UploadAsync(new Uri("http://127.0.0.1:8080"), token, item, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual("POST", handler.Method, "upload method");
        AssertEqual("/v1/bundles", handler.Path, "upload path");
        AssertTrue(handler.ContentType?.StartsWith("multipart/form-data", StringComparison.Ordinal) == true,
            "upload content type");
        AssertEqual("inst-test", handler.Headers["X-STS2-Installation-ID"], "installation header");
        AssertEqual("tok-test", handler.Headers["X-STS2-Upload-Token-ID"], "token header");
        AssertEqual(TelemetryUploadCrypto.Sha256Hex(item.ManifestBytes()), handler.Headers["X-STS2-Manifest-SHA256"],
            "manifest hash header");
        using (FileStream bundle = item.OpenBundle())
        {
            AssertEqual(TelemetryUploadCrypto.Sha256Hex(bundle), handler.Headers["X-STS2-Bundle-SHA256"],
                "bundle hash header");
        }

        string expectedMaterial = TelemetryUploadCrypto.SigningMaterial(
            "inst-test",
            "tok-test",
            handler.Headers["X-STS2-Timestamp"],
            handler.Headers["X-STS2-Nonce"],
            "POST",
            "/v1/bundles",
            handler.Headers["X-STS2-Manifest-SHA256"],
            handler.Headers["X-STS2-Bundle-SHA256"]);
        AssertEqual(
            TelemetryUploadCrypto.SignatureHex("secret-test", expectedMaterial),
            handler.Headers["X-STS2-Signature"],
            "upload signature");
        AssertTrue(handler.Body.Contains("name=manifest", StringComparison.Ordinal), "multipart manifest part");
        AssertTrue(handler.Body.Contains("name=bundle", StringComparison.Ordinal), "multipart bundle part");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadClientRetrievesSignedRewardStatus()
{
    var handler = new RecordingRewardHandler();
    var client = new TelemetryUploadClient(
        new HttpClient(handler),
        () => DateTimeOffset.Parse("2026-05-09T00:03:00Z"));
    var token = new TelemetryUploadToken
    {
        InstallationId = "inst-test",
        UploadTokenId = "tok-test",
        UploadSecret = "secret-test"
    };

    TelemetryUploadRewardStatus reward = client.GetRunRewardAsync(
            new Uri("http://127.0.0.1:8080"),
            token,
            "run-test",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("GET", handler.Method, "reward status method");
    AssertEqual("/v1/rewards/runs/run-test", handler.Path, "reward status path");
    AssertEqual("inst-test", handler.Headers["X-STS2-Installation-ID"], "installation header");
    AssertEqual("tok-test", handler.Headers["X-STS2-Upload-Token-ID"], "token header");
    string expectedMaterial = TelemetryUploadCrypto.RewardStatusSigningMaterial(
        "inst-test",
        "tok-test",
        handler.Headers["X-STS2-Timestamp"],
        handler.Headers["X-STS2-Nonce"],
        "GET",
        "/v1/rewards/runs/run-test");
    AssertEqual(
        TelemetryUploadCrypto.SignatureHex("secret-test", expectedMaterial),
        handler.Headers["X-STS2-Signature"],
        "reward status signature");
    AssertEqual("generated", reward.Status, "reward status");
    AssertEqual("RCODE", reward.RedeemCode, "redeem code");
    AssertEqual(123, reward.AmountCents, "reward amount cents");
    AssertEqual("1.23", reward.Amount, "reward amount dollars");
}

static void UploadServiceRefreshesRewardStatusByLogicalRunId()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string segmentPath = Path.Combine(directory, "runs", "logical-run-test", "segments", "segment-a.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(segmentPath)!);
        File.WriteAllText(
            segmentPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-capture\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-a\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();
        queue.MarkUploaded(item);
        TelemetryUploadTokenStore.Save(directory, new TelemetryUploadToken
        {
            InstallationId = "inst-test",
            UploadTokenId = "tok-test",
            UploadSecret = "secret-test"
        });

        var handler = new RewardRefreshServiceHandler();
        using var service = new TelemetryUploadService(
            directory,
            "inst-test",
            new HttpClient(handler));

        service.RunSyncCycleForTests(UploadServiceTestSettings(directory), forcePackaging: false, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(0, handler.UploadRequests, "reward refresh should not upload another bundle");
        AssertEqual(1, handler.RewardRequests.Count, "reward refresh request count");
        AssertEqual("/v1/rewards/runs/logical-run-test", handler.RewardRequests[0], "reward refresh path");

        TelemetryUploadQueueItem refreshed = TelemetryUploadQueueItem.TryLoad(item.Directory)
            ?? throw new InvalidOperationException("refreshed queue item missing");
        AssertEqual("run-capture", refreshed.Status.RunId, "stored queue capture run id");
        AssertEqual("logical-run-test", refreshed.Status.LogicalRunId, "stored queue logical run id");
        AssertEqual("logical-run-test", refreshed.Status.Reward?.RunId, "persisted reward response run id");
        AssertEqual("generated", refreshed.Status.Reward?.Status, "persisted reward status");
        AssertEqual("RCODE", refreshed.Status.Reward?.RedeemCode, "persisted reward code");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadServiceRefreshesRejectedUploadTokenAndRetriesOnce()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        WriteUploadTestRun(directory);
        TelemetryUploadTokenStore.Save(directory, new TelemetryUploadToken
        {
            InstallationId = "inst-test",
            UploadTokenId = "tok-old",
            UploadSecret = "secret-old"
        });

        var handler = new TokenRefreshUploadHandler("unknown_upload_token");
        using var service = new TelemetryUploadService(
            directory,
            "inst-test",
            new HttpClient(handler));

        service.RunSyncCycleForTests(UploadServiceTestSettings(directory), forcePackaging: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        TelemetryUploadQueueItem item = new TelemetryUploadQueue(directory, "inst-test").Items().Single();
        AssertEqual("uploaded", item.Status.State, "refreshed retry should mark bundle uploaded");
        AssertTrue(!File.Exists(item.BundlePath), "uploaded retry should remove bundle bytes");
        AssertTrue(!File.Exists(item.ManifestPath), "uploaded retry should remove manifest bytes");
        AssertEqual(2, handler.UploadRequests, "upload should be attempted once plus one retry");
        AssertEqual(1, handler.RegisterRequests, "token refresh should register once");
        AssertEqual(2, handler.UploadTokenIds.Count, "both upload attempts should include token id");
        AssertEqual("tok-old", handler.UploadTokenIds[0], "first upload should use existing token");
        AssertEqual("tok-fresh", handler.UploadTokenIds[1], "retry should use fresh token");

        TelemetryUploadToken refreshed = TelemetryUploadTokenStore.Load(directory, "inst-test")
            ?? throw new InvalidOperationException("refreshed token missing");
        AssertEqual("tok-fresh", refreshed.UploadTokenId, "fresh upload token should be saved");

        TelemetryUploadSummary summary = ReadUploadSummary(directory);
        AssertEqual(true, summary.HasUploadToken, "summary should report saved upload token");
        AssertEqual("synced", summary.LastSyncState, "sync state");
        AssertEqual(1, summary.UploadedBundles, "uploaded bundle count");
        AssertEqual(0, summary.FailedBundles, "failed bundle count");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadServiceMarksRegistrationFailureAfterRejectedUploadToken()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        WriteUploadTestRun(directory);
        TelemetryUploadTokenStore.Save(directory, new TelemetryUploadToken
        {
            InstallationId = "inst-test",
            UploadTokenId = "tok-old",
            UploadSecret = "secret-old"
        });

        var handler = new TokenRefreshUploadHandler(
            "upload_token_inactive",
            registrationFailureCode: "registration_unavailable");
        using var service = new TelemetryUploadService(
            directory,
            "inst-test",
            new HttpClient(handler));

        service.RunSyncCycleForTests(UploadServiceTestSettings(directory), forcePackaging: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        TelemetryUploadQueueItem item = new TelemetryUploadQueue(directory, "inst-test").Items().Single();
        AssertEqual("failed", item.Status.State, "registration failure should mark bundle failed");
        AssertEqual("registration_unavailable", item.Status.LastErrorCode, "registration failure code");
        AssertEqual(1, item.Status.AttemptCount, "registration failure should count one failed cycle");
        AssertEqual(1, handler.UploadRequests, "registration failure should not retry upload");
        AssertEqual(1, handler.RegisterRequests, "registration should be attempted once");

        TelemetryUploadToken stored = TelemetryUploadTokenStore.Load(directory, "inst-test")
            ?? throw new InvalidOperationException("old token missing");
        AssertEqual("tok-old", stored.UploadTokenId, "failed refresh should not overwrite old token");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadServiceMarksRetryFailureAfterTokenRefresh()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        WriteUploadTestRun(directory);
        TelemetryUploadTokenStore.Save(directory, new TelemetryUploadToken
        {
            InstallationId = "inst-test",
            UploadTokenId = "tok-old",
            UploadSecret = "secret-old"
        });

        var handler = new TokenRefreshUploadHandler(
            "invalid_signature",
            retryFailureCode: "server_busy",
            registrationRetryAfterSeconds: 17);
        using var service = new TelemetryUploadService(
            directory,
            "inst-test",
            new HttpClient(handler));

        service.RunSyncCycleForTests(UploadServiceTestSettings(directory), forcePackaging: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        TelemetryUploadQueueItem item = new TelemetryUploadQueue(directory, "inst-test").Items().Single();
        AssertEqual("failed", item.Status.State, "retry failure should mark bundle failed");
        AssertEqual("server_busy", item.Status.LastErrorCode, "retry failure code");
        AssertEqual(1, item.Status.AttemptCount, "retry failure should count one failed cycle");
        AssertTrue(item.Status.NextAttemptAtUtc != null, "retry failure should schedule a next attempt");
        AssertEqual(2, handler.UploadRequests, "retry failure should attempt upload exactly twice");
        AssertEqual(1, handler.RegisterRequests, "token refresh should register once");

        TelemetryUploadToken refreshed = TelemetryUploadTokenStore.Load(directory, "inst-test")
            ?? throw new InvalidOperationException("refreshed token missing");
        AssertEqual("tok-fresh", refreshed.UploadTokenId, "fresh token should remain saved after retry failure");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static TelemetryUploadSettings UploadServiceTestSettings(string directory)
    => TelemetryUploadSettings.LoadOrCreate(directory) with
    {
        StagingEndpointUrl = "http://127.0.0.1:8080",
        StableSourceSeconds = 0
    };

static void WriteUploadTestRun(string directory)
{
    string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
    File.WriteAllText(
        runPath,
        "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");
}

static TelemetryUploadSummary ReadUploadSummary(string directory)
{
    string path = Path.Combine(
        TelemetryUploadSettings.UploadDirectory(directory),
        TelemetryUploadSettings.StatusFileName);
    return JsonSerializer.Deserialize<TelemetryUploadSummary>(File.ReadAllText(path), TelemetryJson.Options)
        ?? throw new InvalidOperationException("upload summary missing");
}

static void UploadSummaryPersistsRetrievedRewardStatus()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-test", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllText(
            runPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"test\",\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();
        queue.MarkUploaded(item);

        TelemetryUploadQueueItem uploaded = TelemetryUploadQueueItem.TryLoad(item.Directory)!;
        uploaded.SaveReward(new TelemetryUploadRewardStatus
        {
            InstallationId = "inst-test",
            RunId = "run-test",
            FormulaVersion = "formula-test",
            Status = "generated",
            AmountCents = 123,
            Amount = "1.23",
            RedeemCode = "RCODE",
            UpdatedAtUtc = DateTimeOffset.Parse("2026-05-09T00:00:00Z")
        });

        TelemetryUploadSummary summary = queue.BuildSummary(
            TelemetryUploadSettings.LoadOrCreate(directory),
            TelemetryUploadPolicy.LocalDefault,
            hasToken: true,
            lastSyncState: "synced");

        AssertEqual(1, summary.Rewards.Length, "summary reward count");
        AssertEqual("generated", summary.Rewards[0].Status, "summary reward status");
        AssertEqual("RCODE", summary.Rewards[0].RedeemCode, "summary redeem code");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusViewGroupsLogicalRunsAndRewards()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string segmentA = Path.Combine(directory, "runs", "logical-run-test", "segments", "segment-a.jsonl");
        string segmentB = Path.Combine(directory, "runs", "logical-run-test", "segments", "segment-b.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(segmentA)!);
        File.WriteAllLines(segmentA, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_started\",\"installation_id\":\"inst-test\",\"run_id\":\"run-a\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-a\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\",\"state\":{\"raw_snapshot\":{\"run\":{\"floor\":4,\"ascension\":2}}}}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_ended\",\"installation_id\":\"inst-test\",\"run_id\":\"run-a\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-a\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:10:00Z\",\"is_victory\":true}"
        });
        File.WriteAllLines(segmentB, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"decision/frame\",\"installation_id\":\"inst-test\",\"run_id\":\"run-b\",\"logical_run_id\":\"logical-run-test\",\"segment_id\":\"segment-b\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:20:00Z\",\"pre_state\":{\"raw_snapshot\":{\"run\":{\"floor\":10,\"ascension\":2,\"character\":\"IRONCLAD\"}}}}"
        });

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:30:00Z"));
        TelemetryUploadQueueItem[] items = queue.PackagePendingSources(
                TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
                TelemetryUploadPolicy.LocalDefault,
                force: true)
            .ToArray();
        AssertEqual(2, items.Length, "two logical-run segment bundles should be queued");
        TelemetryUploadQueueItem uploaded = items.Single(item => item.Status.SourcePath.EndsWith("segment-a.jsonl", StringComparison.Ordinal));
        queue.MarkUploaded(uploaded);
        queue.WriteSummary(new TelemetryUploadSummary
        {
            Enabled = true,
            HasUploadToken = true,
            Rewards = new[]
            {
                new TelemetryUploadRewardStatus
                {
                    InstallationId = "inst-test",
                    RunId = "run-a",
                    FormulaVersion = "formula-test",
                    Status = "generated",
                    Amount = "1.23",
                    RedeemCode = "RCODE",
                FloorReached = 5,
                Ascension = 2,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-05-09T00:00:00Z")
            }
            }
        });

        TelemetryUploadStatusView view = TelemetryUploadStatusReader.Build(
            directory,
            maxRuns: 10,
            now: () => DateTimeOffset.Parse("2026-05-09T00:01:00Z"));

        AssertEqual(1, view.Runs.Length, "logical segments should group into one run row");
        TelemetryUploadRunStatus run = view.Runs[0];
        AssertEqual("logical-run-test", run.LogicalRunId, "logical run id");
        AssertEqual("completed", run.RunState, "run state");
        AssertEqual(TelemetryRunQuality.Complete, run.RunQuality, "run quality");
        AssertEqual("partial", run.UploadState, "mixed uploaded and queued bundles should be partial");
        AssertEqual("generated", run.RewardState, "reward state");
        AssertEqual("1.23", run.RewardAmount, "reward amount");
        AssertEqual("RCODE", run.RedeemCode, "redeem code");
        AssertEqual(10, run.FloorReached, "floor reached from direct pre_state raw_snapshot");
        AssertEqual(2, run.Ascension, "ascension");
        AssertEqual("IRONCLAD", run.Character, "character");
        AssertTrue(run.Source.Contains("(+1 segments)", StringComparison.Ordinal),
            "source should summarize grouped segment files");
        AssertEqual(2, run.SourceCount, "source segment count");
        AssertEqual(1, run.UploadedSourceCount, "uploaded unique source count");
        AssertEqual(0, run.DuplicateUploadedSourceCount, "duplicate source count");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusViewMarksLoadOnlyRunQuality()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "logical-run-load-only", "segments", "run-load.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllLines(runPath, new[]
        {
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_loaded\",\"installation_id\":\"inst-test\",\"run_id\":\"run-load-a\",\"logical_run_id\":\"logical-run-load-only\",\"segment_id\":\"run-load\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\",\"state\":{\"raw_snapshot\":{\"run\":{\"floor\":21,\"ascension\":0,\"character\":\"SILENT\"}}}}",
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_loaded\",\"installation_id\":\"inst-test\",\"run_id\":\"run-load-a\",\"logical_run_id\":\"logical-run-load-only\",\"segment_id\":\"run-load\",\"local_sequence\":2,\"recorded_at_utc\":\"2026-05-08T00:01:00Z\",\"state\":{\"raw_snapshot\":{\"run\":{\"floor\":25,\"ascension\":0,\"character\":\"SILENT\"}}}}"
        });

        TelemetryUploadStatusView view = TelemetryUploadStatusReader.Build(
            directory,
            maxRuns: 10,
            now: () => DateTimeOffset.Parse("2026-05-09T00:01:00Z"));

        TelemetryUploadRunStatus run = view.Runs.Single();
        AssertEqual("logical-run-load-only", run.LogicalRunId, "logical run id");
        AssertEqual(TelemetryRunQuality.LoadOnly, run.RunQuality, "load-only run quality");
        AssertEqual(25, run.FloorReached, "load-only floor reached");

        string rendered = TelemetryUploadStatusRenderer.RenderPlainText(view);
        AssertTrue(rendered.Contains("状态：仅加载片段", StringComparison.Ordinal),
            "renderer should distinguish load-only fragments");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusViewMarksRewardsDisabledFromLocalSummary()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        string runPath = Path.Combine(directory, "runs", "run-disabled", "telemetry.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(runPath)!);
        File.WriteAllText(
            runPath,
            "{\"schema_version\":\"sts2.telemetry.local.v1\",\"record_type\":\"lifecycle/run_started\",\"installation_id\":\"inst-test\",\"run_id\":\"run-disabled\",\"local_sequence\":1,\"recorded_at_utc\":\"2026-05-08T00:00:00Z\"}\n");

        var queue = new TelemetryUploadQueue(directory, "inst-test", () => DateTimeOffset.Parse("2026-05-08T00:02:00Z"));
        TelemetryUploadQueueItem item = queue.PackagePendingSources(
            TelemetryUploadSettings.LoadOrCreate(directory) with { StableSourceSeconds = 0 },
            TelemetryUploadPolicy.LocalDefault,
            force: true).Single();
        queue.MarkUploaded(item);
        queue.WriteSummary(new TelemetryUploadSummary
        {
            Enabled = false,
            HasUploadToken = true,
            Policy = TelemetryUploadPolicy.LocalDefault,
            UpdatedAtUtc = DateTimeOffset.Parse("2026-05-09T00:00:00Z")
        });

        TelemetryUploadStatusView view = TelemetryUploadStatusReader.Build(
            directory,
            maxRuns: 10,
            now: () => DateTimeOffset.Parse("2026-05-09T00:01:00Z"));

        AssertEqual(1, view.Runs.Length, "disabled summary run count");
        AssertEqual("uploaded", view.Runs[0].UploadState, "disabled summary upload state");
        AssertEqual("disabled", view.Runs[0].RewardState, "disabled summary reward state");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void UploadStatusRendererShowsPanelText()
{
    var view = new TelemetryUploadStatusView
    {
        UpdatedAtUtc = DateTimeOffset.Parse("2026-05-09T00:01:00Z"),
        TelemetryBaseDirectory = "/tmp/sts2-telemetry",
        UploadStatusPath = "/tmp/sts2-telemetry/upload/status.json",
        Runs = new[]
        {
            new TelemetryUploadRunStatus
            {
                GroupKey = "logical:logical-run-test",
                Source = "runs/logical-run-test/segments/segment-a.jsonl",
                RunId = "run-a",
                LogicalRunId = "logical-run-test",
                Character = "IRONCLAD",
                RunState = "completed",
                UploadState = "uploaded",
                RewardState = "generated",
                RewardAmount = "1.23",
                RedeemCode = "RCODE",
                FloorReached = 5,
                Ascension = 2,
                LastLocalSequence = 10,
                BundleCount = 1,
                UploadedBundles = 1
            }
        }
    };

    string rendered = TelemetryUploadStatusRenderer.RenderPlainText(view);

    AssertTrue(rendered.Contains("遥测上传 / 奖励", StringComparison.Ordinal), "renderer title");
    AssertTrue(rendered.Contains("更新：2026-05-09", StringComparison.Ordinal), "renderer updated time");
    AssertTrue(rendered.Contains("铁甲战士 进阶2 第5层", StringComparison.Ordinal), "renderer compact run identity");
    AssertTrue(rendered.Contains("记录：runs/logical-run-test/segments/segment-a.jsonl", StringComparison.Ordinal),
        "renderer source");
    AssertTrue(rendered.Contains("兑换码：RCODE", StringComparison.Ordinal), "renderer redeem code");
    AssertTrue(rendered.Contains("状态：已获得兑换码", StringComparison.Ordinal),
        "renderer current status");
    AssertTrue(!rendered.Contains("状态：运行已完成 / 上传已上传 / 奖励已生成", StringComparison.Ordinal),
        "renderer should not show slash-separated status summary");
    AssertTrue(!rendered.Contains("run-a", StringComparison.Ordinal), "renderer should hide raw run id");
    AssertTrue(!rendered.Contains("Bundle", StringComparison.OrdinalIgnoreCase), "renderer should hide bundle counts");
    AssertTrue(!rendered.Contains("seq", StringComparison.OrdinalIgnoreCase), "renderer should hide local sequence");
    AssertEqual("RCODE", TelemetryUploadStatusRenderer.LatestGeneratedRedeemCode(view), "latest code");
}

static void UploadStatusRendererChoosesCurrentStatusByPrecedence()
{
    var cases = new[]
    {
        (Run: BuildUploadStatusRun("completed", "failed", "generated", redeemCode: "RCODE"), Expected: "上传失败", Name: "failed upload"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "generated", redeemCode: "RCODE"), Expected: "已获得兑换码", Name: "generated code"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "generated"), Expected: "兑换码已生成", Name: "generated without code"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "processing"), Expected: "等待兑换码", Name: "processing reward"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "ineligible"), Expected: "无奖励", Name: "ineligible reward"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "disabled"), Expected: "奖励已禁用", Name: "disabled reward"),
        (Run: BuildUploadStatusRun("completed", "partial", "not_applicable"), Expected: "部分上传", Name: "partial upload"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "not_applicable"), Expected: "已上传", Name: "uploaded"),
        (Run: BuildUploadStatusRun("in_progress", "queued", "not_applicable"), Expected: "进行中", Name: "queued in-progress run"),
        (Run: BuildUploadStatusRun("completed", "not_queued", "not_applicable"), Expected: "已完成", Name: "completed run"),
        (Run: BuildUploadStatusRun("suspended", "not_queued", "not_applicable"), Expected: "已暂停", Name: "suspended run"),
        (Run: BuildUploadStatusRun("abandoned", "not_queued", "not_applicable"), Expected: "已放弃", Name: "abandoned run"),
        (Run: BuildUploadStatusRun("unsupported", "not_queued", "not_applicable"), Expected: "不支持", Name: "fallback run state"),
        (Run: BuildUploadStatusRun("completed", "uploaded", "generated", redeemCode: "RCODE", lastErrorCode: "server_error"),
            Expected: "上传失败", Name: "last error wins")
    };

    foreach (var testCase in cases)
        AssertEqual($"状态：{testCase.Expected}", RenderUploadStatusLine(testCase.Run), testCase.Name);
}

static string RenderUploadStatusLine(TelemetryUploadRunStatus run)
{
    string rendered = TelemetryUploadStatusRenderer.RenderPlainText(new TelemetryUploadStatusView
    {
        UpdatedAtUtc = DateTimeOffset.Parse("2026-05-09T00:01:00Z"),
        Runs = new[] { run }
    });

    return rendered.Split('\n')
        .Single(line => line.TrimStart().StartsWith("状态：", StringComparison.Ordinal))
        .Trim();
}

static TelemetryUploadRunStatus BuildUploadStatusRun(
    string runState,
    string uploadState,
    string rewardState,
    string? redeemCode = null,
    string? lastErrorCode = null)
    => new()
    {
        GroupKey = $"test:{runState}:{uploadState}:{rewardState}",
        Source = "runs/test/telemetry.jsonl",
        RunId = "run-test",
        Character = "IRONCLAD",
        RunState = runState,
        UploadState = uploadState,
        RewardState = rewardState,
        RedeemCode = redeemCode,
        LastErrorCode = lastErrorCode,
        FloorReached = 1,
        Ascension = 0
    };

static async Task<int> RunStagingUploadE2e()
{
    string endpointUrl = Environment.GetEnvironmentVariable("STS2_STAGING_API_URL")?.Trim().TrimEnd('/') ?? "";
    if (string.IsNullOrWhiteSpace(endpointUrl))
    {
        Console.Error.WriteLine("FAIL set STS2_STAGING_API_URL to run staging upload E2E");
        return 2;
    }
    if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpoint)
        || endpoint.Scheme is not ("http" or "https"))
    {
        Console.Error.WriteLine("FAIL staging endpoint must be an absolute http or https URL");
        return 2;
    }

    string telemetryDirectory = EnvOrDefault("STS2_STAGING_TELEMETRY_DIR", DefaultStagingTelemetryDirectory());
    if (!Directory.Exists(telemetryDirectory))
    {
        Console.Error.WriteLine($"FAIL telemetry directory does not exist: {telemetryDirectory}");
        return 2;
    }

    if (!Directory.Exists(Path.Combine(telemetryDirectory, JsonlTelemetryWriter.RunsDirectoryName))
        || !Directory.EnumerateFiles(
                Path.Combine(telemetryDirectory, JsonlTelemetryWriter.RunsDirectoryName),
                "*.jsonl",
                SearchOption.AllDirectories)
            .Any())
    {
        Console.Error.WriteLine($"FAIL no telemetry JSONL files found under {telemetryDirectory}");
        return 2;
    }

    string installationId = InstallationIdentity.LoadOrCreate(telemetryDirectory);
    using HttpClient httpClient = BuildStagingHttpClient();
    var client = new TelemetryUploadClient(httpClient);
    var queue = new TelemetryUploadQueue(telemetryDirectory, installationId);
    TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(telemetryDirectory) with
    {
        ActiveEndpoint = "staging",
        StagingEndpointUrl = endpointUrl,
        StableSourceSeconds = 0
    };

    TelemetryUploadPolicy policy;
    try
    {
        policy = await client.GetPolicyAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL staging policy fetch failed: {DescribeUploadException(ex)}");
        return 1;
    }

    if (policy.UploadDisabled)
    {
        Console.Error.WriteLine("FAIL staging policy reports upload_disabled=true");
        return 1;
    }

    TelemetryUploadToken token = await LoadOrRegisterStagingToken(
            client,
            endpoint,
            telemetryDirectory,
            installationId,
            CancellationToken.None)
        .ConfigureAwait(false);

    queue.PackagePendingSources(settings, policy, force: true);
    IReadOnlyList<TelemetryUploadQueueItem> uploadableItems = UploadableItems(queue);
    if (uploadableItems.Count == 0)
    {
        Console.Error.WriteLine("FAIL no pending or failed upload queue item with bundle and manifest files is available");
        return 2;
    }

    TelemetryUploadQueueItem? firstUploaded = null;
    TelemetryUploadQueueItem? lastUploaded = null;
    int uploadedBundles = 0;
    foreach (TelemetryUploadQueueItem item in uploadableItems)
    {
        try
        {
            policy = await client.UploadAsync(endpoint, token, item, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TelemetryUploadHttpException ex) when (IsRefreshableTokenFailure(ex))
        {
            Console.Error.WriteLine($"WARN existing upload token was rejected ({ex.Code}); registering a fresh staging token and retrying once");
            token = await RegisterAndSaveStagingToken(client, endpoint, telemetryDirectory, installationId, CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                policy = await client.UploadAsync(endpoint, token, item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                queue.MarkFailed(item, UploadErrorCode(retryEx), retryEx.Message, policy.RetryAfterSeconds);
                Console.Error.WriteLine($"FAIL staging bundle upload retry failed: {DescribeUploadException(retryEx)}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            queue.MarkFailed(item, UploadErrorCode(ex), ex.Message, policy.RetryAfterSeconds);
            Console.Error.WriteLine($"FAIL staging bundle upload failed: {DescribeUploadException(ex)}");
            return 1;
        }

        queue.MarkUploaded(item);
        firstUploaded ??= item;
        lastUploaded = item;
        uploadedBundles++;
    }

    queue.WriteSummary(queue.BuildSummary(settings, policy, hasToken: true, lastSyncState: "staging_e2e_uploaded"));

    Console.WriteLine("PASS staging upload e2e accepted");
    Console.WriteLine($"STAGING_E2E_ENDPOINT={endpointUrl}");
    Console.WriteLine($"STAGING_E2E_INSTALLATION_ID={installationId}");
    Console.WriteLine($"STAGING_E2E_BUNDLE_ID={firstUploaded!.Status.BundleId}");
    Console.WriteLine($"STAGING_E2E_LAST_BUNDLE_ID={lastUploaded!.Status.BundleId}");
    Console.WriteLine($"STAGING_E2E_UPLOADED_BUNDLES={uploadedBundles}");
    Console.WriteLine($"STAGING_E2E_MANIFEST_PATH={firstUploaded.ManifestPath}");
    Console.WriteLine($"STAGING_E2E_STATUS_PATH={firstUploaded.StatusPath}");
    Console.WriteLine($"STAGING_E2E_SOURCE_PATH={firstUploaded.Status.SourcePath}");
    Console.WriteLine($"STAGING_E2E_RECORD_COUNT={firstUploaded.Status.RecordCount}");
    return 0;
}

static async Task<TelemetryUploadToken> LoadOrRegisterStagingToken(
    TelemetryUploadClient client,
    Uri endpoint,
    string telemetryDirectory,
    string installationId,
    CancellationToken cancellationToken)
{
    bool forceRefresh = string.Equals(
        Environment.GetEnvironmentVariable("STS2_STAGING_REFRESH_TOKEN")?.Trim(),
        "1",
        StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            Environment.GetEnvironmentVariable("STS2_STAGING_REFRESH_TOKEN")?.Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);
    TelemetryUploadToken? existing = forceRefresh
        ? null
        : TelemetryUploadTokenStore.Load(telemetryDirectory, installationId);
    return existing ?? await RegisterAndSaveStagingToken(client, endpoint, telemetryDirectory, installationId, cancellationToken)
        .ConfigureAwait(false);
}

static async Task<TelemetryUploadToken> RegisterAndSaveStagingToken(
    TelemetryUploadClient client,
    Uri endpoint,
    string telemetryDirectory,
    string installationId,
    CancellationToken cancellationToken)
{
    (TelemetryUploadToken token, _) = await client.RegisterAsync(endpoint, installationId, cancellationToken)
        .ConfigureAwait(false);
    TelemetryUploadTokenStore.Save(telemetryDirectory, token);
    return token;
}

static IReadOnlyList<TelemetryUploadQueueItem> UploadableItems(TelemetryUploadQueue queue)
    => queue.Items()
        .Where(HasUploadBytes)
        .Where(item => item.Status.IsUploadable(DateTimeOffset.UtcNow))
        .ToArray();

static bool HasUploadBytes(TelemetryUploadQueueItem item)
    => File.Exists(item.BundlePath) && File.Exists(item.ManifestPath);

static HttpClient BuildStagingHttpClient()
{
    var handler = new SocketsHttpHandler();
    string resolveIp = Environment.GetEnvironmentVariable("STS2_STAGING_RESOLVE_IP")?.Trim() ?? "";
    if (!string.IsNullOrWhiteSpace(resolveIp))
    {
        IPAddress address = IPAddress.Parse(resolveIp);
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    return new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
}

static string DefaultStagingTelemetryDirectory()
{
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(
        home,
        ".var",
        "app",
        "com.valvesoftware.Steam",
        "data",
        "SlayTheSpire2",
        TelemetryDirectoryResolver.TelemetryDirectoryName);
}

static string EnvOrDefault(string name, string fallback)
{
    string value = Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static bool IsRefreshableTokenFailure(TelemetryUploadHttpException ex)
    => ex.Code is "unknown_upload_token" or "upload_token_inactive" or "invalid_signature";

static string UploadErrorCode(Exception ex)
    => ex is TelemetryUploadHttpException http ? http.Code : "staging_upload_e2e_failed";

static string DescribeUploadException(Exception ex)
{
    if (ex is TelemetryUploadHttpException http)
        return $"{http.Code}: {http.Message}";
    return $"{ex.GetType().Name}: {ex.Message}";
}

static void ShopPurchaseCompletedSignalRecordsTypedMetadataWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-shop-completed-signal");
            SetStaticRecorderForTest(recorder);

            var entry = new TestMerchantEntry
            {
                Potion = new TestShopItem { Id = "potion-a", Title = "Useful Potion" },
                Cost = 30,
                IsStocked = true,
                EnoughGold = true,
                Used = true
            };

            PatchCallbacks.AfterShopPurchaseCompleted(new object[] { entry });
        }

        string path = Path.Combine(directory, "runs", "run-shop-completed-signal", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for shop completion signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "shop completion should write exactly one signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("decision/ui_signal", root.GetProperty("record_type").GetString(), "shop signal record type");
        AssertEqual("runtime.shop.purchase_completed", root.GetProperty("decision_source").GetString(), "shop signal source");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "shop signal capture policy");
        AssertMissingProperty(root, "decision_frame_id", "shop signal should not have decision frame id");
        AssertMissingProperty(root, "state", "shop signal should not capture lifecycle state");
        AssertMissingProperty(root, "pre_state", "shop signal should not capture pre-state");
        AssertMissingProperty(root, "post_state", "shop signal should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "shop signal should not build legal actions");
        AssertMissingProperty(root, "selected_action", "shop signal should not include selected action");

        JsonElement metadata = root.GetProperty("ui_signal").GetProperty("metadata");
        AssertEqual("buy_shop_potion", metadata.GetProperty("action_type").GetString(), "shop potion action type");
        AssertEqual("completed", metadata.GetProperty("purchase_status").GetString(), "shop completion status");
        AssertEqual("potion-a", metadata.GetProperty("potion_id").GetString(), "shop completed potion id");
        JsonElement normalizedKey = metadata.GetProperty("normalized_typed_action_key");
        AssertEqual("buy_shop_potion", normalizedKey.GetProperty("action_type").GetString(),
            "shop completion normalized action type");
        AssertEqual("potion-a", normalizedKey.GetProperty("potion_id").GetString(),
            "shop completion normalized potion id");
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RelicTriggerSignalRecordsTypedAttributionWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new ThrowingSnapshotBuilder(), new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-relic-trigger");

            var relic = new TestRelic
            {
                Id = new TestModelId { Entry = "BAG_OF_PREP" },
                Rarity = "Uncommon",
                Status = "Active",
                DisplayAmount = 2,
                StackCount = 1,
                ShowCounter = true,
                ShouldFlashOnPlayer = true
            };
            var enemy = TestEnemy(17, "CULTIST", "Cultist", 30);

            recorder.RecordRelicTriggerSignal("runtime.relic_model.flash", relic, new object?[] { enemy });
        }

        string path = Path.Combine(directory, "runs", "run-relic-trigger", "telemetry.jsonl");
        AssertTrue(File.Exists(path), "telemetry.jsonl should exist for relic trigger signal");
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "relic trigger should write exactly one signal record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement root = document.RootElement;
        AssertEqual("effect/relic_trigger", root.GetProperty("record_type").GetString(), "relic trigger record type");
        AssertEqual("runtime.relic_model.flash", root.GetProperty("source").GetString(), "relic trigger source");
        AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
            root.GetProperty("capture_policy").GetString(),
            "relic trigger capture policy");
        AssertEqual("BAG_OF_PREP", root.GetProperty("relic_id").GetString(), "top-level relic id");
        AssertEqual("visual_flash_observed",
            root.GetProperty("trigger_attribution").GetString(),
            "trigger attribution");
        AssertEqual("effect_delta_not_computed",
            root.GetProperty("effect_attribution").GetString(),
            "effect attribution");
        AssertEqual(1, root.GetProperty("target_count").GetInt32(), "target count");
        AssertMissingProperty(root, "decision_frame_id", "relic trigger should not have decision frame id");
        AssertMissingProperty(root, "state", "relic trigger should not capture lifecycle state");
        AssertMissingProperty(root, "pre_state", "relic trigger should not capture pre-state");
        AssertMissingProperty(root, "post_state", "relic trigger should not capture post-state");
        AssertMissingProperty(root, "legal_actions", "relic trigger should not build legal actions");
        AssertMissingProperty(root, "selected_action", "relic trigger should not include selected action");

        JsonElement trigger = root.GetProperty("relic_trigger");
        AssertEqual("observed_relic_trigger", trigger.GetProperty("role").GetString(), "trigger role");
        AssertEqual(64, trigger.GetProperty("raw_trigger_hash").GetString()?.Length, "raw trigger hash length");
        AssertEqual(64, trigger.GetProperty("canonical_trigger_hash").GetString()?.Length, "canonical trigger hash length");

        JsonElement metadata = trigger.GetProperty("metadata");
        AssertEqual("typed_relic_flash_signal", metadata.GetProperty("projection_policy").GetString(), "projection policy");
        AssertEqual("BAG_OF_PREP", metadata.GetProperty("relic_id").GetString(), "metadata relic id");
        AssertEqual("effect_summary_unavailable",
            metadata.GetProperty("effect_summary_status").GetString(),
            "effect summary status");

        JsonElement relicMetadata = metadata.GetProperty("relic");
        AssertEqual("Active", relicMetadata.GetProperty("status").GetString(), "relic status");
        AssertEqual(2, relicMetadata.GetProperty("display_amount").GetInt32(), "display amount");
        AssertEqual(true, relicMetadata.GetProperty("show_counter").GetBoolean(), "show counter");

        JsonElement target = metadata.GetProperty("targets").EnumerateArray().Single();
        AssertEqual("enemies", target.GetProperty("target_index_space").GetString(), "target index space");
        AssertEqual("enemy", target.GetProperty("target_type").GetString(), "target type");
        AssertEqual(17, target.GetProperty("combat_id").GetInt32(), "target combat id");
        AssertEqual("CULTIST", target.GetProperty("enemy_id").GetString(), "target enemy id");
        AssertEqual("extracted", metadata.GetProperty("target_extraction").GetString(), "target extraction status");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RelicObserverDiagnosticsReportNoPlayerAndRelicGaps()
{
    var observer = new RelicFlashedSignalObserver((_, _, _) =>
        throw new InvalidOperationException("observer should not fire without relic flash"));

    observer.ObservePlayerRelics(null);
    RelicFlashedSignalObserver.Diagnostics noPlayer = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, noPlayer.ObservePlayerRelicsCount, "no-player observation count");
    AssertEqual(1, noPlayer.PlayerUnavailableCount, "no-player diagnostic count");

    observer.ObservePlayerRelics(new TestPlayer());
    RelicFlashedSignalObserver.Diagnostics noRelicCollection = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, noRelicCollection.PlayerRelicsUnavailableCount,
        "missing relic collection diagnostic count");

    observer.ObservePlayerRelics(new TestPlayer { Relics = Array.Empty<object?>() });
    RelicFlashedSignalObserver.Diagnostics emptyRelics = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, emptyRelics.PlayerRelicsEmptyCount, "empty relic collection diagnostic count");
    AssertEqual(2, emptyRelics.PlayerRelicObtainedSubscribedCount,
        "players with RelicObtained fields should still subscribe for future relics");
    AssertEqual(2, emptyRelics.ActivePlayerSubscriptionCount, "active player subscription count");
    AssertEqual(0, emptyRelics.ActiveRelicSubscriptionCount, "no active relic subscriptions");
}

static void RecorderRelicDiagnosticsReportNoPlayerFromRuntimeBridge()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        var snapshotBuilder = new StaticSnapshotBuilder("combat");
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            recorder.OnRunStarted(new TestRunState(), "run_manager.run_started");

            RelicFlashedSignalObserver.Diagnostics diagnostics = GetRelicSignalDiagnostics(recorder);
            AssertEqual(1, diagnostics.ObservePlayerRelicsCount,
                "runtime bridge should still ask observer to diagnose missing player");
            AssertEqual(1, diagnostics.PlayerUnavailableCount,
                "runtime bridge should surface no-current-player diagnostics");
            AssertEqual(0, diagnostics.PlayerRelicObtainedSubscribedCount,
                "missing player should not create player subscriptions");
            AssertEqual(0, diagnostics.RelicFlashedSubscribedCount,
                "missing player should not create relic subscriptions");
        }
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RelicObserverDiagnosticsReportMissingFlashedSignal()
{
    var observer = new RelicFlashedSignalObserver((_, _, _) =>
        throw new InvalidOperationException("observer should not fire when Flashed is missing"));
    var relic = new TestRelic();
    var player = new TestPlayer
    {
        Relics = new object?[] { relic }
    };

    observer.ObservePlayerRelics(player);
    RelicFlashedSignalObserver.Diagnostics diagnostics = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, diagnostics.PlayerRelicObtainedSubscribedCount,
        "player RelicObtained field should subscribe even when relic Flashed is missing");
    AssertEqual(1, diagnostics.RelicFlashedMissingCount, "missing Flashed diagnostic count");
    AssertEqual(0, diagnostics.RelicFlashedSubscribedCount, "missing Flashed should not subscribe");
    AssertEqual(0, diagnostics.RelicFlashedFiredCount, "missing Flashed should not fire");

    observer.ObservePlayerRelics(player);
    RelicFlashedSignalObserver.Diagnostics repeatedDiagnostics = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, repeatedDiagnostics.RelicFlashedMissingCount,
        "repeated observation of the same missing Flashed surface should not inflate the gap count");
}

static void RelicObserverDiagnosticsReportFieldBackedFlashedSubscription()
{
    var observedSignals = new List<(string Source, object? Relic, object? Targets)>();
    var observer = new RelicFlashedSignalObserver((source, relic, targets) =>
        observedSignals.Add((source, relic, targets)));
    var relic = new TestFieldObservableRelic();
    var player = new TestPlayer
    {
        Relics = new object?[] { relic }
    };

    observer.ObservePlayerRelics(player);
    RelicFlashedSignalObserver.Diagnostics subscribed = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, subscribed.PlayerRelicObtainedSubscribedCount,
        "player RelicObtained field subscription diagnostic count");
    AssertEqual(1, subscribed.RelicFlashedSubscribedCount,
        "field-backed Flashed subscription diagnostic count");
    AssertEqual(0, subscribed.RelicFlashedMissingCount, "field-backed Flashed should not report missing");
    AssertEqual(0, subscribed.RelicFlashedFiredCount,
        "subscribed-but-not-fired state should be visible before the signal fires");
    AssertEqual(1, subscribed.ActiveRelicSubscriptionCount, "active relic subscription count");

    var targets = Array.Empty<object?>();
    relic.RaiseFlashed(targets);
    RelicFlashedSignalObserver.Diagnostics fired = observer.GetDiagnosticsSnapshot();
    AssertEqual(1, fired.RelicFlashedFiredCount, "field-backed Flashed fired diagnostic count");
    AssertEqual(1, observedSignals.Count, "field-backed Flashed should emit one observed signal");
    AssertEqual("runtime.relic_model.flashed_event", observedSignals[0].Source, "observed signal source");
    AssertTrue(ReferenceEquals(relic, observedSignals[0].Relic), "observed signal should keep relic identity");
    AssertTrue(ReferenceEquals(targets, observedSignals[0].Targets), "observed signal should keep target identity");

    observer.Reset();
    relic.RaiseFlashed(Array.Empty<object?>());
    RelicFlashedSignalObserver.Diagnostics reset = observer.GetDiagnosticsSnapshot();
    AssertEqual(0, reset.RelicFlashedSubscribedCount, "reset should clear subscription diagnostics");
    AssertEqual(0, reset.RelicFlashedFiredCount, "reset should clear fired diagnostics");
    AssertEqual(0, reset.ActiveRelicSubscriptionCount, "reset should clear active relic subscription count");
    AssertEqual(1, observedSignals.Count, "reset should remove the field-backed Flashed handler");
}

static void ObservedRelicFlashedEventRecordsTypedAttributionWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var relic = new TestObservableRelic
        {
            Id = new TestModelId { Entry = "RAZOR_TOOTH" },
            Rarity = "Common",
            Status = "Active",
            DisplayAmount = 1,
            StackCount = 1,
            ShowCounter = true,
            ShouldFlashOnPlayer = true
        };
        var player = new TestPlayer
        {
            Relics = new object?[] { relic }
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory(),
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "relic-flashed-observer-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-relic-observed-event");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/run_started", "run_manager");
            int captureCountAfterObservation = snapshotBuilder.CaptureCount;
            int safeRunStateCountAfterObservation = snapshotBuilder.SafeRunStateCount;
            int localPlayerCountAfterObservation = snapshotBuilder.GetLocalPlayerCount;

            var enemy = TestEnemy(19, "CULTIST", "Cultist", 32);
            relic.RaiseFlashed(new object?[] { enemy });

            AssertEqual(captureCountAfterObservation, snapshotBuilder.CaptureCount,
                "observed relic flashed callback should not capture snapshots");
            AssertEqual(safeRunStateCountAfterObservation, snapshotBuilder.SafeRunStateCount,
                "observed relic flashed callback should not re-read run state");
            AssertEqual(localPlayerCountAfterObservation, snapshotBuilder.GetLocalPlayerCount,
                "observed relic flashed callback should not re-read local player");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "observed relic flashed event record count");
            JsonElement root = records[1].RootElement;
            AssertEqual("effect/relic_trigger", root.GetProperty("record_type").GetString(), "relic trigger record type");
            AssertEqual("runtime.relic_model.flashed_event", root.GetProperty("source").GetString(), "relic trigger source");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "observed relic flashed event capture policy");
            AssertEqual("RAZOR_TOOTH", root.GetProperty("relic_id").GetString(), "top-level relic id");
            AssertMissingProperty(root, "pre_state", "observed relic flashed event should not capture pre-state");
            AssertMissingProperty(root, "post_state", "observed relic flashed event should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "observed relic flashed event should not capture legal actions");

            JsonElement metadata = root.GetProperty("relic_trigger").GetProperty("metadata");
            AssertEqual("RAZOR_TOOTH", metadata.GetProperty("relic_id").GetString(), "metadata relic id");
            AssertEqual(1, metadata.GetProperty("target_count").GetInt32(), "metadata target count");
            AssertEqual("effect_summary_unavailable",
                metadata.GetProperty("effect_summary_status").GetString(),
                "effect summary status");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void ObservedRelicFlashedFieldRecordsTypedAttributionWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var relic = new TestFieldObservableRelic
        {
            Id = new TestModelId { Entry = "SNECKO_SKULL" },
            Rarity = "Uncommon",
            Status = "Active",
            DisplayAmount = 3,
            StackCount = 1,
            ShowCounter = true,
            ShouldFlashOnPlayer = true
        };
        var player = new TestPlayer
        {
            Relics = new object?[] { relic }
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory(),
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "relic-flashed-field-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-relic-observed-field");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/run_started", "run_manager");
            int captureCountAfterObservation = snapshotBuilder.CaptureCount;
            int safeRunStateCountAfterObservation = snapshotBuilder.SafeRunStateCount;
            int localPlayerCountAfterObservation = snapshotBuilder.GetLocalPlayerCount;

            var enemy = TestEnemy(23, "JAW_WORM", "Jaw Worm", 44);
            relic.RaiseFlashed(new object?[] { enemy });

            AssertEqual(captureCountAfterObservation, snapshotBuilder.CaptureCount,
                "observed relic flashed field callback should not capture snapshots");
            AssertEqual(safeRunStateCountAfterObservation, snapshotBuilder.SafeRunStateCount,
                "observed relic flashed field callback should not re-read run state");
            AssertEqual(localPlayerCountAfterObservation, snapshotBuilder.GetLocalPlayerCount,
                "observed relic flashed field callback should not re-read local player");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "observed relic flashed field record count");
            JsonElement root = records[1].RootElement;
            AssertEqual("effect/relic_trigger", root.GetProperty("record_type").GetString(), "relic trigger record type");
            AssertEqual("runtime.relic_model.flashed_event", root.GetProperty("source").GetString(), "relic trigger source");
            AssertEqual("signal_only_no_state_snapshot_no_legal_actions",
                root.GetProperty("capture_policy").GetString(),
                "observed relic flashed field capture policy");
            AssertEqual("SNECKO_SKULL", root.GetProperty("relic_id").GetString(), "top-level relic id");
            AssertMissingProperty(root, "pre_state", "observed relic flashed field should not capture pre-state");
            AssertMissingProperty(root, "post_state", "observed relic flashed field should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "observed relic flashed field should not capture legal actions");

            JsonElement metadata = root.GetProperty("relic_trigger").GetProperty("metadata");
            AssertEqual("SNECKO_SKULL", metadata.GetProperty("relic_id").GetString(), "metadata relic id");
            AssertEqual(1, metadata.GetProperty("target_count").GetInt32(), "metadata target count");
            AssertEqual("effect_summary_unavailable",
                metadata.GetProperty("effect_summary_status").GetString(),
                "effect summary status");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void NewlyObtainedRelicFlashedFieldRecordsTypedAttributionWithoutSnapshots()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    TelemetryRecorder originalRecorder = Sts2TelemetryMod.Recorder;
    try
    {
        var relic = new TestFieldObservableRelic
        {
            Id = new TestModelId { Entry = "RAZOR_TOOTH" },
            Rarity = "Common",
            Status = "Active",
            DisplayAmount = 1,
            StackCount = 1,
            ShowCounter = true,
            ShouldFlashOnPlayer = true
        };
        var player = new TestPlayer
        {
            Relics = Array.Empty<object?>()
        };
        var runState = new TestRunState
        {
            CurrentRoom = new MerchantRoom
            {
                Inventory = new TestMerchantInventory(),
                Player = player
            }
        };
        var snapshotBuilder = new QueuedSnapshotBuilder(
            runState,
            player,
            TestSnapshotWithHash("shop", "newly-obtained-relic-flashed-state"));

        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, snapshotBuilder, new LegalActionBuilder());
            EnableCapturingForTest(recorder, "run-newly-obtained-relic-observed");
            SetStaticRecorderForTest(recorder);

            recorder.RecordLifecycle("lifecycle/run_started", "run_manager");
            int captureCountAfterObservation = snapshotBuilder.CaptureCount;
            int safeRunStateCountAfterObservation = snapshotBuilder.SafeRunStateCount;
            int localPlayerCountAfterObservation = snapshotBuilder.GetLocalPlayerCount;

            var enemy = TestEnemy(29, "FUNGO_BEAST", "Fungo Beast", 50);
            player.RaiseRelicObtained(relic);
            relic.RaiseFlashed(new object?[] { enemy });

            AssertEqual(captureCountAfterObservation, snapshotBuilder.CaptureCount,
                "newly obtained relic flashed callback should not capture snapshots");
            AssertEqual(safeRunStateCountAfterObservation, snapshotBuilder.SafeRunStateCount,
                "newly obtained relic flashed callback should not re-read run state");
            AssertEqual(localPlayerCountAfterObservation, snapshotBuilder.GetLocalPlayerCount,
                "newly obtained relic flashed callback should not re-read local player");
        }

        JsonDocument[] records = ReadAllRunRecords(directory);
        try
        {
            AssertEqual(2, records.Length, "newly obtained relic flashed field record count");
            JsonElement root = records[1].RootElement;
            AssertEqual("effect/relic_trigger", root.GetProperty("record_type").GetString(), "relic trigger record type");
            AssertEqual("runtime.relic_model.flashed_event", root.GetProperty("source").GetString(), "relic trigger source");
            AssertEqual("RAZOR_TOOTH", root.GetProperty("relic_id").GetString(), "top-level relic id");
            AssertMissingProperty(root, "pre_state", "newly obtained relic flashed should not capture pre-state");
            AssertMissingProperty(root, "post_state", "newly obtained relic flashed should not capture post-state");
            AssertMissingProperty(root, "legal_actions", "newly obtained relic flashed should not capture legal actions");

            JsonElement metadata = root.GetProperty("relic_trigger").GetProperty("metadata");
            AssertEqual("RAZOR_TOOTH", metadata.GetProperty("relic_id").GetString(), "metadata relic id");
            AssertEqual(1, metadata.GetProperty("target_count").GetInt32(), "metadata target count");
            AssertEqual("effect_summary_unavailable",
                metadata.GetProperty("effect_summary_status").GetString(),
                "effect summary status");
        }
        finally
        {
            foreach (JsonDocument record in records)
                record.Dispose();
        }
    }
    finally
    {
        SetStaticRecorderForTest(originalRecorder);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void RecorderPersistsTelemetryCallbackFailures()
{
    string directory = Path.Combine(Path.GetTempPath(), $"sts2-telemetry-test-{Guid.NewGuid():N}");
    try
    {
        using (var writer = new JsonlTelemetryWriter(directory))
        {
            var recorder = new TelemetryRecorder(writer, new StateSnapshotBuilder(), new LegalActionBuilder());
            recorder.RecordTelemetryError("test.callback", new InvalidOperationException("boom"));
        }

        string operationalDirectory = Path.Combine(directory, "operational");
        string path = Directory.GetFiles(operationalDirectory, "*.jsonl").Single();
        string[] lines = File.ReadAllLines(path);
        AssertEqual(1, lines.Length, "one callback failure record");

        using JsonDocument document = JsonDocument.Parse(lines[0]);
        AssertEqual("lifecycle/telemetry_callback_failed",
            document.RootElement.GetProperty("record_type").GetString(),
            "callback failure record type");
        AssertEqual("test.callback", document.RootElement.GetProperty("source").GetString(), "callback failure source");
        AssertEqual(typeof(InvalidOperationException).FullName,
            document.RootElement.GetProperty("exception").GetProperty("type").GetString(),
            "callback failure exception type");
    }
    finally
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void AssertNotEqual<T>(T expected, T actual, string message)
{
    if (EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: both were {actual}");
}

static void AssertHasMethod(Assembly assembly, string typeName, string methodName, params Type[] parameterTypes)
{
    Type type = assembly.GetType(typeName)
        ?? throw new InvalidOperationException($"missing type {typeName}");
    bool exists = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Any(method => method.Name == methodName
            && !method.IsGenericMethod
            && ParametersMatch(method, parameterTypes));
    AssertTrue(exists, $"missing method {typeName}.{methodName}");
}

static void AssertHasExactMethod(Assembly assembly, string typeName, string methodName, params Type[] parameterTypes)
{
    Type type = assembly.GetType(typeName)
        ?? throw new InvalidOperationException($"missing type {typeName}");
    bool exists = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Any(method => method.Name == methodName
            && !method.IsGenericMethod
            && ParametersMatchExactly(method, parameterTypes));
    AssertTrue(exists, $"missing exact method {typeName}.{methodName}({string.Join(",", parameterTypes.Select(type => type.Name))})");
}

static void AssertHasEventOrDelegateField(Assembly assembly, string typeName, string signalName)
{
    Type type = assembly.GetType(typeName)
        ?? throw new InvalidOperationException($"missing type {typeName}");
    bool exists = FindEventOrDelegateFieldType(type, signalName) != null;
    AssertTrue(exists, $"missing event/delegate field {typeName}.{signalName}");
}

static void AssertEventOrDelegateFieldParameterCount(
    Assembly assembly,
    string typeName,
    string signalName,
    int expectedParameterCount)
{
    Type type = assembly.GetType(typeName)
        ?? throw new InvalidOperationException($"missing type {typeName}");

    Type signalType = FindEventOrDelegateFieldType(type, signalName)
        ?? throw new InvalidOperationException($"missing event/delegate field {typeName}.{signalName}");
    MethodInfo invoke = signalType.GetMethod("Invoke")
        ?? throw new InvalidOperationException($"event/delegate field {typeName}.{signalName} is missing an Invoke method");
    AssertEqual(expectedParameterCount, invoke.GetParameters().Length,
        $"unexpected parameter count for event/delegate field {typeName}.{signalName}");
}

static Type? FindEventOrDelegateFieldType(Type type, string signalName)
{
    for (Type? current = type; current != null; current = current.BaseType)
    {
        EventInfo? eventInfo = current.GetEvents(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(candidate => candidate.Name == signalName);
        if (eventInfo?.EventHandlerType != null)
            return eventInfo.EventHandlerType;

        FieldInfo? fieldInfo = current.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(candidate => candidate.Name == signalName && typeof(Delegate).IsAssignableFrom(candidate.FieldType));
        if (fieldInfo != null)
            return fieldInfo.FieldType;
    }

    return null;
}

static bool ParametersMatch(MethodInfo method, IReadOnlyList<Type> parameterTypes)
{
    if (parameterTypes.Count == 0)
        return true;

    return ParametersMatchExactly(method, parameterTypes);
}

static bool ParametersMatchExactly(MethodInfo method, IReadOnlyList<Type> parameterTypes)
{
    ParameterInfo[] parameters = method.GetParameters();
    if (parameters.Length != parameterTypes.Count)
        return false;

    for (int i = 0; i < parameters.Length; i++)
    {
        if (parameters[i].ParameterType != parameterTypes[i])
            return false;
    }

    return true;
}

static void InvokeStaticNonPublic(Type type, string methodName, params object?[] args)
{
    MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"missing method {type.FullName}.{methodName}");
    method.Invoke(null, args);
}

static MegaCrit.Sts2.Core.GameActions.GameAction CreateUninitializedGameAction(string actionTypeName)
{
    Type actionType = typeof(RunManager).Assembly.GetType(actionTypeName)
        ?? throw new InvalidOperationException($"missing game action type {actionTypeName}");
    return (MegaCrit.Sts2.Core.GameActions.GameAction)RuntimeHelpers.GetUninitializedObject(actionType);
}

static void AssertMissingProperty(JsonElement element, string propertyName, string message)
{
    if (element.TryGetProperty(propertyName, out _))
        throw new InvalidOperationException(message);
}

static void AssertMissingKey(IReadOnlyDictionary<string, object?> dictionary, string key, string message)
{
    if (dictionary.ContainsKey(key))
        throw new InvalidOperationException(message);
}

static void AssertTimingDuration(JsonElement timing, string propertyName)
{
    AssertTrue(timing.TryGetProperty(propertyName, out JsonElement value),
        $"decision timing should include {propertyName}");
    AssertTrue(value.ValueKind == JsonValueKind.Number,
        $"decision timing {propertyName} should be numeric");
    AssertTrue(value.GetInt64() >= 0,
        $"decision timing {propertyName} should be non-negative");
}

static void AssertRecordType(string expected, JsonDocument document, string message)
    => AssertEqual(expected, document.RootElement.GetProperty("record_type").GetString(), message);

static JsonDocument[] ReadAllRunRecords(string directory)
{
    string runsDirectory = Path.Combine(directory, "runs");
    return Directory.GetFiles(runsDirectory, "*.jsonl", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines)
        .Select(line => JsonDocument.Parse(line))
        .OrderBy(document => document.RootElement.GetProperty("recorded_at_utc").GetDateTimeOffset())
        .ThenBy(document => document.RootElement.GetProperty("local_sequence").GetInt64())
        .ToArray();
}

static void EnableCapturingForTest(TelemetryRecorder recorder, string runId)
{
    Type statusType = typeof(TelemetryRecorder).GetNestedType("CaptureStatus", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing CaptureStatus");
    object capturing = Enum.Parse(statusType, "Capturing");
    SetPrivateField(recorder, "_status", capturing);
    SetPrivateField(recorder, "_runId", runId);
}

static void SetStaticRecorderForTest(TelemetryRecorder recorder)
{
    MethodInfo setter = typeof(Sts2TelemetryMod)
        .GetProperty(nameof(Sts2TelemetryMod.Recorder), BindingFlags.Static | BindingFlags.Public)!
        .GetSetMethod(nonPublic: true)
        ?? throw new InvalidOperationException("missing Recorder setter");
    setter.Invoke(null, new object[] { recorder });
}

static void SetPrivateField(object target, string fieldName, object? value)
{
    FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"missing field {fieldName}");
    field.SetValue(target, value);
}

static int GetPendingDecisionCount(TelemetryRecorder recorder)
{
    FieldInfo field = typeof(TelemetryRecorder).GetField("_pendingDecisions", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing pending decisions field");
    var pending = (System.Collections.ICollection)(field.GetValue(recorder)
        ?? throw new InvalidOperationException("pending decisions field was null"));
    return pending.Count;
}

static RelicFlashedSignalObserver.Diagnostics GetRelicSignalDiagnostics(TelemetryRecorder recorder)
{
    FieldInfo field = typeof(TelemetryRecorder).GetField("_relicSignalObserver", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("missing relic signal observer field");
    var observer = (RelicFlashedSignalObserver)(field.GetValue(recorder)
        ?? throw new InvalidOperationException("relic signal observer field was null"));
    return observer.GetDiagnosticsSnapshot();
}

static StateSnapshot TestSnapshot(string stateType)
    => new(
        stateType,
        new Dictionary<string, object?> { ["state_type"] = stateType },
        new Dictionary<string, object?> { ["state_type"] = stateType },
        $"raw-{stateType}",
        $"canonical-{stateType}");

static StateSnapshot TestSnapshotWithRaw(string stateType, IReadOnlyDictionary<string, object?> raw)
    => new(
        stateType,
        raw,
        raw,
        $"raw-{stateType}",
        $"canonical-{stateType}");

static StateSnapshot TestSnapshotWithHash(string stateType, string canonicalStateHash)
{
    var run = new Dictionary<string, object?>
    {
        ["logical_run_identity"] = new Dictionary<string, object?>
        {
            ["status"] = "complete",
            ["logical_run_key"] = "logical-key-test",
            ["logical_run_id"] = "logical-run-test"
        }
    };
    return new StateSnapshot(
        stateType,
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        $"raw-{canonicalStateHash}",
        canonicalStateHash);
}

static StateSnapshot TestSnapshotWithLogicalRun(
    string stateType,
    string canonicalStateHash,
    string logicalRunId,
    string character)
{
    var identity = new Dictionary<string, object?>
    {
        ["status"] = "complete",
        ["logical_run_key"] = logicalRunId + "-key",
        ["logical_run_id"] = logicalRunId,
        ["fields"] = new Dictionary<string, object?>
        {
            ["seed"] = logicalRunId + "-seed",
            ["character"] = character,
            ["ascension"] = 0,
            ["game_mode"] = "normal",
            ["start_time"] = logicalRunId + "-start",
            ["modifiers"] = Array.Empty<string>()
        }
    };
    var run = new Dictionary<string, object?>
    {
        ["character"] = character,
        ["ascension"] = 0,
        ["logical_run_identity"] = identity
    };
    return new StateSnapshot(
        stateType,
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        $"raw-{canonicalStateHash}",
        canonicalStateHash);
}

static StateSnapshot TestSnapshotWithDegradedStartTime(
    string stateType,
    string canonicalStateHash,
    string stableLogicalRunId,
    string character)
{
    var fields = new Dictionary<string, object?>
    {
        ["seed"] = stableLogicalRunId + "-seed",
        ["character"] = character,
        ["ascension"] = 0,
        ["game_mode"] = "normal",
        ["start_time"] = "0",
        ["modifiers"] = Array.Empty<string>()
    };
    var identity = new Dictionary<string, object?>
    {
        ["status"] = "degraded",
        ["identity_quality"] = "degraded",
        ["degraded_reason"] = "start_time_zero_loaded_save_identity",
        ["degraded_fields"] = new[] { "start_time" },
        ["observed_logical_run_key"] = stableLogicalRunId + "-degraded-key",
        ["observed_logical_run_id"] = stableLogicalRunId + "-degraded",
        ["fields"] = fields
    };
    var run = new Dictionary<string, object?>
    {
        ["character"] = character,
        ["ascension"] = 0,
        ["logical_run_identity"] = identity
    };
    return new StateSnapshot(
        stateType,
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["run"] = run
        },
        $"raw-{canonicalStateHash}",
        canonicalStateHash);
}

static LegalActionBuilder TestLegalActionBuilder(IReadOnlyDictionary<string, object?> runManagerMembers)
    => new(
        () => new LegalActionBuilder.CombatAvailability(
            CanBuild: false,
            Availability: "not_in_combat",
            IsInProgress: false,
            IsPlayPhase: false,
            PlayerActionsDisabled: false),
        memberNames => memberNames.Length > 0 && runManagerMembers.TryGetValue(memberNames[0], out object? value)
            ? value
            : null);

static TestCreature TestEnemy(int combatId, string id, string name, int hp)
    => new()
    {
        CombatId = combatId,
        Monster = new TestMonster
        {
            Id = new TestModelId { Entry = id },
            Title = name,
            NextMove = new TestMove { Id = new TestModelId { Entry = "ATTACK" } }
        },
        CurrentHp = hp,
        MaxHp = hp + 10,
        Block = 3,
        IsAlive = true,
        IsHittable = true,
        Powers = new[]
        {
            new TestPower
            {
                Id = new TestModelId { Entry = "WEAK" },
                Title = "Weak",
                Amount = 1,
                Type = "Debuff"
            }
        }
    };

static TestPlayer TestCombatPlayer(TestCombatState combatState, IEnumerable<object?>? potionSlots = null)
{
    var player = new TestPlayer
    {
        NetId = 1,
        Gold = 99,
        Creature = new TestCreature
        {
            CombatState = combatState,
            CurrentHp = 70,
            MaxHp = 80,
            Block = 5,
            IsAlive = true,
            IsHittable = true,
            Powers = new[]
            {
                new TestPower
                {
                    Id = new TestModelId { Entry = "VULNERABLE" },
                    Title = "Vulnerable",
                    Amount = 2,
                    Type = "Debuff"
                }
            }
        },
        PlayerCombatState = new TestPlayerCombatState
        {
            Energy = 3,
            MaxEnergy = 3,
            Hand = new TestPile
            {
                Cards = new[]
                {
                    new TestCard
                    {
                        Id = new TestModelId { Entry = "STRIKE" },
                        Title = "Strike",
                        Type = "Attack",
                        TargetType = "AnyEnemy",
                        Rarity = "Basic",
                        IsUpgraded = false,
                        EnergyCost = new TestEnergyCost { Amount = 1 },
                        CanPlayValue = true,
                        CanPlayReason = "None"
                    }
                }
            },
            DrawPile = new TestPile
            {
                Cards = new[] { new TestCard { Id = new TestModelId { Entry = "DRAW" }, Title = "Draw Card" } }
            },
            DiscardPile = new TestPile
            {
                Cards = new[] { new TestCard { Id = new TestModelId { Entry = "DISCARD" }, Title = "Discard Card" } }
            },
            ExhaustPile = new TestPile
            {
                Cards = new[] { new TestCard { Id = new TestModelId { Entry = "EXHAUST" }, Title = "Exhaust Card" } }
            }
        },
        Deck = new TestPile
        {
            Cards = new[]
            {
                new TestCard { Id = new TestModelId { Entry = "STRIKE" }, Title = "Strike" },
                new TestCard { Id = new TestModelId { Entry = "DEFEND" }, Title = "Defend" }
            }
        },
        PotionSlots = potionSlots ?? new object?[]
        {
            new TestPotion
            {
                Id = new TestModelId { Entry = "FIRE_POTION" },
                Title = "Fire Potion",
                TargetType = "AnyEnemy",
                Usage = "CombatOnly",
                PassesCustomUsabilityCheck = true
            }
        }
    };
    combatState.Players = new[] { player };
    return player;
}

sealed class TestRunState
{
    public int? Floor { get; init; }
    public int? AscensionLevel { get; init; }
    public string? Seed { get; init; }
    public string? GameMode { get; init; }
    public string? StartTime { get; init; }
    public object? Character { get; init; }
    public object? Rng { get; init; }
    public IEnumerable<object?>? Players { get; init; }
    public IEnumerable<string>? Modifiers { get; init; }
    public object? CurrentRoom { get; init; }
    public object? Map { get; init; }
    public object? CurrentMapPoint { get; init; }
    public object? CurrentMapCoord { get; init; }
    public int? CurrentActIndex { get; init; }
}

sealed class TestCharacter
{
    public string? Id { get; init; }
}

sealed class TestRunRng
{
    public string? StringSeed { get; init; }
    public uint Seed { get; init; }
}

sealed class TestRunManager
{
    private readonly long _startTime;

    public TestRunManager(long startTime)
    {
        _startTime = startTime;
    }
}

sealed class ThrowingProjectionObject
{
    public object Id => throw new InvalidOperationException("Id should not be read");

    public string Title => throw new InvalidOperationException("Title should not be read");

    public override string ToString()
        => throw new InvalidOperationException("ToString should not be called");
}

sealed class TestCardRewardSelectionScreen
{
    private readonly object?[] _selectedCards;

    public TestCardRewardSelectionScreen(object?[] selectedCards)
    {
        _selectedCards = selectedCards;
    }

    public IEnumerable<object?> GetSelectedCards()
        => _selectedCards;
}

sealed class TestCardHolder
{
    public TestCardHolder(object? cardModel)
    {
        CardModel = cardModel;
    }

    public object? CardModel { get; }
}

sealed class GenericHookGameAction
{
    public string ActionType => "generic_hook_action";

    public string HookId => "hook.after_act_entered";

    public object ChoiceContext => new ThrowingProjectionObject();

    public object Card => new ThrowingProjectionObject();

    public object ToNetAction()
        => throw new InvalidOperationException("ToNetAction should not be called");

    public override string ToString()
        => throw new InvalidOperationException("ToString should not be called");
}

sealed class MoveToMapCoordAction
{
    public object? Destination { get; init; }
}

sealed class VoteForMapCoordAction
{
    public object? Source { get; init; }
    public object? Destination { get; init; }
}

sealed class VoteToMoveToNextActAction
{
}

sealed class ReadyToBeginEnemyTurnAction
{
}

sealed class UndoEndPlayerTurnAction
{
}

sealed class NotListedAction
{
}

sealed class NullableMemberProbe
{
    private readonly string? _nullableField = null;

    public string? NullableProperty => _nullableField;
}

sealed class FakePlayCardAction
{
    public string ActionType => "CombatPlayPhaseOnly";

    public object? CardModelId { get; init; }

    public object? NetCombatCard { get; init; }

    public int? TargetId { get; init; }
}

sealed class FakeNetCombatCard
{
    public int CombatCardIndex { get; init; }
}

sealed class FakeUsePotionAction
{
    public string ActionType => "CombatPlayPhaseOnly";

    public int? PotionIndex { get; init; }

    public int? TargetId { get; init; }

    public bool? WasEnqueuedInCombat { get; init; }
}

sealed class FakeDiscardPotionGameAction
{
    private readonly uint _potionSlotIndex;

    public FakeDiscardPotionGameAction(uint potionSlotIndex, bool wasEnqueuedInCombat)
    {
        _potionSlotIndex = potionSlotIndex;
        WasEnqueuedInCombat = wasEnqueuedInCombat;
    }

    public string ActionType => WasEnqueuedInCombat ? "CombatPlayPhaseOnly" : "NonCombat";

    public bool WasEnqueuedInCombat { get; }
}

sealed class FakePickRelicAction
{
    private readonly int? _relicIndex;

    public FakePickRelicAction(int? relicIndex, object? synchronizer)
    {
        _relicIndex = relicIndex;
        TestSynchronizer = synchronizer;
    }

    public string ActionType => "NonCombat";

    public object? TestSynchronizer { get; }
}

sealed class ThrowingSnapshotBuilder : IStateSnapshotBuilder
{
    public StateSnapshot Capture(string reason, bool includePlayerMetadata = true)
        => throw new InvalidOperationException("snapshot capture should not be invoked");

    public object? SafeRunState()
        => throw new InvalidOperationException("safe run state should not be read");

    public object? GetLocalPlayer(object? runState)
        => throw new InvalidOperationException("local player should not be read");
}

sealed class StaticSnapshotBuilder : IStateSnapshotBuilder
{
    private readonly string _stateType;

    public StaticSnapshotBuilder(string stateType)
    {
        _stateType = stateType;
    }

    public int CaptureCount { get; private set; }
    public int SafeRunStateCount { get; private set; }
    public int GetLocalPlayerCount { get; private set; }

    public StateSnapshot Capture(string reason, bool includePlayerMetadata = true)
    {
        CaptureCount++;
        return new StateSnapshot(
            _stateType,
            new Dictionary<string, object?> { ["state_type"] = _stateType },
            new Dictionary<string, object?> { ["state_type"] = _stateType },
            $"raw-{_stateType}-{CaptureCount}",
            $"canonical-{_stateType}-{CaptureCount}");
    }

    public object? SafeRunState()
    {
        SafeRunStateCount++;
        return null;
    }

    public object? GetLocalPlayer(object? runState)
    {
        GetLocalPlayerCount++;
        return null;
    }
}

sealed class QueuedSnapshotBuilder : IStateSnapshotBuilder
{
    private readonly Queue<StateSnapshot> _snapshots;
    private readonly object? _runState;
    private readonly object? _localPlayer;

    public QueuedSnapshotBuilder(params StateSnapshot[] snapshots)
        : this(null, null, snapshots)
    {
    }

    public QueuedSnapshotBuilder(object? runState, object? localPlayer, params StateSnapshot[] snapshots)
    {
        _snapshots = new Queue<StateSnapshot>(snapshots);
        _runState = runState;
        _localPlayer = localPlayer;
    }

    public int CaptureCount { get; private set; }

    public int SafeRunStateCount { get; private set; }

    public int GetLocalPlayerCount { get; private set; }

    public StateSnapshot Capture(string reason, bool includePlayerMetadata = true)
    {
        if (_snapshots.Count == 0)
            throw new InvalidOperationException($"no queued snapshot for {reason}");
        CaptureCount++;
        return _snapshots.Dequeue();
    }

    public object? SafeRunState()
    {
        SafeRunStateCount++;
        return _runState ?? new TestRunState();
    }

    public object? GetLocalPlayer(object? runState)
    {
        GetLocalPlayerCount++;
        return _localPlayer;
    }
}

sealed class MerchantRoom
{
    public object? Inventory { get; init; }
    public object? Player { get; init; }
}

sealed class TestRewardsSet
{
    public IEnumerable<object?>? Rewards { get; init; }
    public bool DisallowSkipping { get; init; }
    public object? Room { get; init; }
}

sealed class TestGoldReward
{
    public string RewardType => "Gold";
    public int RewardsSetIndex => 1;
    public int Amount { get; init; }
    public bool IsPopulated => true;
}

sealed class TestPotionReward
{
    public string RewardType => "Potion";
    public int RewardsSetIndex => 2;
    public object? Potion { get; init; }
    public bool IsPopulated => Potion != null;
}

sealed class TestRelicReward
{
    public string RewardType => "Relic";
    public int RewardsSetIndex => 3;
    public object? _relic { get; init; }
    public string? Rarity { get; init; }
    public bool IsPopulated => _relic != null;
}

sealed class TestCardReward
{
    public string RewardType => "Card";
    public int RewardsSetIndex => 5;
    public IEnumerable<object?>? Cards { get; init; }
    public IEnumerable<object?>? SacrificeOptions { get; init; }
    public bool HasPaelsWingSacrifice { get; init; }
    public bool CanSkip { get; init; }
    public bool CanReroll { get; init; }
    public bool IsPopulated => Cards != null;
}

sealed class TestMap
{
    public IEnumerable<object?>? startMapPoints { get; init; }
    public object? StartingMapPoint { get; init; }
}

sealed class TestMapPoint
{
    public object? coord { get; init; }
    public string? PointType { get; init; }
    public IEnumerable<object?>? Children { get; init; }
    public int? mapGenerationCount { get; init; }
}

sealed class TestMapCoord
{
    public int col { get; init; }
    public int row { get; init; }
}

sealed class TestMapSelectionSynchronizer
{
    public int MapGenerationCount { get; init; }
}

sealed class TestEventSynchronizer
{
    public bool IsShared { get; init; }
    public object? LocalEvent { get; init; }

    public object? GetLocalEvent()
        => LocalEvent;
}

sealed class TestEventModel
{
    public object? Id { get; init; }
    public bool IsFinished { get; init; }
    public IEnumerable<object?>? CurrentOptions { get; set; }
}

sealed class TestEventOption
{
    public string? TextKey { get; init; }
    public bool IsLocked { get; init; }
    public bool IsProceed { get; init; }
    public bool WasChosen { get; init; }
    public object? Relic { get; init; }
}

sealed class TestRestSiteSynchronizer
{
    public IEnumerable<object?>? LocalOptions { get; init; }

    public object? GetLocalOptions()
        => LocalOptions;
}

sealed class TestRestSiteOption
{
    public string? OptionId { get; init; }
    public bool IsEnabled { get; init; }
    public int? SmithCount { get; init; }
}

sealed class TestTreasureRoomRelicSynchronizer
{
    public IEnumerable<object?>? CurrentRelics { get; init; }
}

sealed class TestRelic
{
    public object? Id { get; init; }
    public string? Rarity { get; init; }
    public string? Status { get; init; }
    public int? DisplayAmount { get; init; }
    public int? StackCount { get; init; }
    public bool? ShowCounter { get; init; }
    public bool? ShouldFlashOnPlayer { get; init; }
}

sealed class TestObservableRelic
{
    public object? Id { get; init; }
    public string? Rarity { get; init; }
    public string? Status { get; init; }
    public int? DisplayAmount { get; init; }
    public int? StackCount { get; init; }
    public bool? ShowCounter { get; init; }
    public bool? ShouldFlashOnPlayer { get; init; }
    public event Action<object?, object?>? Flashed;

    public void RaiseFlashed(object? targets)
        => Flashed?.Invoke(this, targets);
}

sealed class RecordingUploadHandler : HttpMessageHandler
{
    public string Method { get; private set; } = "";
    public string Path { get; private set; } = "";
    public string? ContentType { get; private set; }
    public string Body { get; private set; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Method = request.Method.Method;
        Path = request.RequestUri?.AbsolutePath ?? "";
        ContentType = request.Content?.Headers.ContentType?.ToString();
        foreach (var header in request.Headers)
            Headers[header.Key] = string.Join(",", header.Value);

        if (request.Content != null)
        {
            byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = Encoding.UTF8.GetString(body);
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
        {
            Content = new StringContent(
                "{\"installation_id\":\"inst-test\",\"bundle_id\":\"bundle-test\",\"status\":\"accepted\",\"validation_status\":\"pending\",\"storage_key\":\"bundles/test\",\"idempotent\":false,\"policy\":{\"max_bundle_bytes\":52428800,\"accepted_schema_versions\":[\"sts2.telemetry.local.v1\"],\"accepted_compression\":[\"gzip\"],\"retry_after_seconds\":null,\"upload_disabled\":false}}",
                Encoding.UTF8,
                "application/json")
        };
    }
}

sealed class RecordingRewardHandler : HttpMessageHandler
{
    public string Method { get; private set; } = "";
    public string Path { get; private set; } = "";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Method = request.Method.Method;
        Path = request.RequestUri?.AbsolutePath ?? "";
        foreach (var header in request.Headers)
            Headers[header.Key] = string.Join(",", header.Value);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"installation_id\":\"inst-test\",\"run_id\":\"run-test\",\"formula_version\":\"formula-test\",\"status\":\"generated\",\"amount_cents\":123,\"amount\":\"1.23\",\"floor_reached\":10,\"ascension\":2,\"redeem_code\":\"RCODE\"}",
                Encoding.UTF8,
                "application/json")
        });
    }
}

sealed class RewardRefreshServiceHandler : HttpMessageHandler
{
    public int PolicyRequests { get; private set; }
    public int UploadRequests { get; private set; }
    public List<string> RewardRequests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        if (path == "/v1/policy")
        {
            PolicyRequests++;
            return JsonResponse(
                HttpStatusCode.OK,
                "{\"max_bundle_bytes\":52428800,\"accepted_schema_versions\":[\"sts2.telemetry.local.v1\"],\"accepted_compression\":[\"gzip\"],\"retry_after_seconds\":null,\"upload_disabled\":false}");
        }

        if (path == "/v1/bundles")
        {
            UploadRequests++;
            if (request.Content != null)
                await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return JsonResponse(
                HttpStatusCode.BadRequest,
                "{\"error\":{\"code\":\"unexpected_upload\",\"message\":\"upload was not expected\"}}");
        }

        if (path.StartsWith(TelemetryUploadCrypto.RunRewardPathPrefix, StringComparison.Ordinal))
        {
            RewardRequests.Add(path);
            return JsonResponse(
                HttpStatusCode.OK,
                "{\"installation_id\":\"inst-test\",\"run_id\":\"logical-run-test\",\"formula_version\":\"formula-test\",\"status\":\"generated\",\"amount_cents\":123,\"amount\":\"1.23\",\"floor_reached\":10,\"ascension\":2,\"redeem_code\":\"RCODE\"}");
        }

        return JsonResponse(
            HttpStatusCode.NotFound,
            "{\"error\":{\"code\":\"not_found\",\"message\":\"not found\"}}");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}

sealed class TokenRefreshUploadHandler : HttpMessageHandler
{
    private readonly string _initialFailureCode;
    private readonly string? _registrationFailureCode;
    private readonly string? _retryFailureCode;
    private readonly int? _registrationRetryAfterSeconds;

    public int PolicyRequests { get; private set; }
    public int RegisterRequests { get; private set; }
    public int UploadRequests { get; private set; }
    public List<string> UploadTokenIds { get; } = new();

    public TokenRefreshUploadHandler(
        string initialFailureCode,
        string? registrationFailureCode = null,
        string? retryFailureCode = null,
        int? registrationRetryAfterSeconds = null)
    {
        _initialFailureCode = initialFailureCode;
        _registrationFailureCode = registrationFailureCode;
        _retryFailureCode = retryFailureCode;
        _registrationRetryAfterSeconds = registrationRetryAfterSeconds;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        if (path == "/v1/policy")
        {
            PolicyRequests++;
            return JsonResponse(HttpStatusCode.OK, PolicyJson());
        }

        if (path == "/v1/installations/register")
        {
            RegisterRequests++;
            if (_registrationFailureCode != null)
                return ErrorResponse(HttpStatusCode.ServiceUnavailable, _registrationFailureCode, "registration unavailable");
            return JsonResponse(
                HttpStatusCode.OK,
                "{\"installation_id\":\"inst-test\",\"upload_token_id\":\"tok-fresh\",\"upload_secret\":\"secret-fresh\",\"policy\":"
                + PolicyJson(_registrationRetryAfterSeconds)
                + "}");
        }

        if (path == "/v1/bundles")
        {
            UploadRequests++;
            if (request.Headers.TryGetValues("X-STS2-Upload-Token-ID", out IEnumerable<string>? tokenIds))
                UploadTokenIds.Add(tokenIds.Single());
            if (request.Content != null)
                await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (UploadRequests == 1)
                return ErrorResponse(HttpStatusCode.Unauthorized, _initialFailureCode, "upload token rejected");
            if (_retryFailureCode != null)
                return ErrorResponse(HttpStatusCode.TooManyRequests, _retryFailureCode, "retry rejected");
            return JsonResponse(
                HttpStatusCode.Accepted,
                "{\"installation_id\":\"inst-test\",\"bundle_id\":\"bundle-test\",\"status\":\"accepted\",\"validation_status\":\"pending\",\"idempotent\":false,\"policy\":"
                + PolicyJson()
                + "}");
        }

        return ErrorResponse(HttpStatusCode.NotFound, "not_found", "not found");
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string code, string message)
        => JsonResponse(statusCode, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}");

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string PolicyJson(int? retryAfterSeconds = null)
        => "{\"max_bundle_bytes\":52428800,\"accepted_schema_versions\":[\"sts2.telemetry.local.v1\"],\"accepted_compression\":[\"gzip\"],\"retry_after_seconds\":"
            + (retryAfterSeconds?.ToString() ?? "null")
            + ",\"upload_disabled\":false}";
}

sealed class NoopNativeSaveCapture : INativeSaveCapture
{
    public static NoopNativeSaveCapture Instance { get; } = new();

    public NativeSaveCaptureResult CaptureRecent(string telemetryBaseDirectory)
        => NativeSaveCaptureResult.Empty;
}

sealed class TestFieldObservableRelic
{
    public object? Id { get; init; }
    public string? Rarity { get; init; }
    public string? Status { get; init; }
    public int? DisplayAmount { get; init; }
    public int? StackCount { get; init; }
    public bool? ShowCounter { get; init; }
    public bool? ShouldFlashOnPlayer { get; init; }
    public Action<object?, object?>? Flashed = null;

    public void RaiseFlashed(object? targets)
        => Flashed?.Invoke(this, targets);
}

sealed class TestMerchantInventory
{
    public IEnumerable<object?>? CharacterCardEntries { get; init; }
    public IEnumerable<object?>? ColorlessCardEntries { get; init; }
    public IEnumerable<object?>? CardEntries { get; init; }
    public IEnumerable<object?>? RelicEntries { get; init; }
    public IEnumerable<object?>? PotionEntries { get; init; }
    public object? CardRemovalEntry { get; init; }
}

sealed class TestMerchantEntry
{
    public object? Card { get; init; }
    public object? Relic { get; init; }
    public object? Potion { get; init; }
    public string? Name { get; init; }
    public int? Cost { get; init; }
    public bool? IsStocked { get; init; }
    public bool? EnoughGold { get; init; }
    public bool? Used { get; init; }
}

sealed class TestMutableMerchantEntry
{
    public object? Card { get; init; }
    public object? Relic { get; init; }
    public object? Potion { get; init; }
    public int? Cost { get; init; }
    public bool? IsStocked { get; init; }
    public bool? EnoughGold { get; init; }
    public bool? Used { get; set; }
}

sealed class TestMerchantPotionEntry
{
    public object? Potion { get; init; }
    public int? Cost { get; init; }
    public bool? IsStocked { get; init; }
    public bool? EnoughGold { get; init; }
    public bool? Used { get; init; }
}

sealed class TestMerchantCardEntry
{
    public int? Cost { get; init; }
    public bool? IsStocked { get; init; }
    public bool? EnoughGold { get; init; }
    public bool? Used { get; init; }
}

sealed class TestMerchantRelicEntry
{
    public int? Cost { get; init; }
    public bool? IsStocked { get; init; }
    public bool? EnoughGold { get; init; }
    public bool? Used { get; init; }
}

sealed class TestShopItem
{
    public string? Id { get; init; }
    public string? Title { get; init; }
}

sealed class TestPlayer
{
    public ulong NetId { get; init; }
    public int Gold { get; init; }
    public bool CanRemovePotions { get; init; }
    public object? RunState { get; init; }
    public object? Character { get; init; }
    public object? Creature { get; init; }
    public object? PlayerCombatState { get; init; }
    public object? Deck { get; init; }
    public IEnumerable<object?>? Relics { get; init; }
    public IEnumerable<object?>? PotionSlots { get; init; }
    public Action<object?>? RelicObtained = null;

    public void RaiseRelicObtained(object? relic)
        => RelicObtained?.Invoke(relic);
}

sealed class TestNetPlayerChoiceResult
{
    public string? type;
    public IEnumerable<int>? indexes;
}

sealed class TestPlayerCombatState
{
    public int Energy { get; init; }
    public int MaxEnergy { get; init; }
    public object? Hand { get; init; }
    public object? DrawPile { get; init; }
    public object? DiscardPile { get; init; }
    public object? ExhaustPile { get; init; }
}

sealed class TestCombatState
{
    public int RoundNumber { get; init; } = 1;
    public string CurrentSide { get; init; } = "Player";
    public string Phase { get; init; } = "main_phase";
    public string ActionStep { get; init; } = "choose_action";
    public int ActionIndex { get; init; } = 2;
    public IEnumerable<object?>? Enemies { get; init; }
    public IEnumerable<object?>? Players { get; set; }
}

sealed class TestCreature
{
    public int CombatId { get; init; }
    public object? Monster { get; init; }
    public object? Player { get; init; }
    public object? CombatState { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public int Block { get; init; }
    public bool IsAlive { get; init; }
    public bool IsHittable { get; init; }
    public IEnumerable<object?>? Powers { get; init; }
}

sealed class TestMonster
{
    public object? Id { get; init; }
    public string? Title { get; init; }
    public object? NextMove { get; init; }
}

sealed class TestMove
{
    public object? Id { get; init; }
}

sealed class TestPower
{
    public object? Id { get; init; }
    public string? Title { get; init; }
    public int Amount { get; init; }
    public string? Type { get; init; }
}

sealed class TestModelId
{
    public string? Category { get; init; }
    public string? Entry { get; init; }
}

sealed class TestPile
{
    public IEnumerable<object?>? Cards { get; init; }
}

sealed class TestCard
{
    public object? Id { get; init; }
    public string? Title { get; init; }
    public string? Type { get; init; }
    public string? TargetType { get; init; }
    public string? Rarity { get; init; }
    public bool IsUpgraded { get; init; }
    public object? EnergyCost { get; init; }
    public int CurrentStarCost { get; init; }
    public bool HasStarCostX { get; init; }
    public bool CanPlayValue { get; init; }
    public string? CanPlayReason { get; init; }

    public bool CanPlay(out string? reason, out object? model)
    {
        reason = CanPlayReason;
        model = null;
        return CanPlayValue;
    }
}

sealed class TestEnergyCost
{
    public bool CostsX { get; init; }
    public int Amount { get; init; }

    public int GetAmountToSpend()
        => Amount;
}

sealed class TestPotion
{
    public object? Id { get; init; }
    public string? Title { get; init; }
    public string? TargetType { get; init; }
    public string? Usage { get; init; }
    public bool IsQueued { get; init; }
    public bool PassesCustomUsabilityCheck { get; init; }
}
