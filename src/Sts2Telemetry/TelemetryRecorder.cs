using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.GameActions;

namespace Sts2Telemetry;

public sealed class TelemetryRecorder
{
    public const string SchemaVersion = "sts2.telemetry.local.v1";
    private const string SignalOnlyCapturePolicy =
        ActionExecutorCapturePolicy.SignalOnlyNoStateSnapshotNoLegalActions;
    private const long PendingShopCardAttemptMaxAgeSequences = 4;
    private const int PendingShopCardAttemptMaxCompletionSignals = 2;
    private const int MaxRecentStableLogicalRunIdentities = 8;
    private static readonly string[] CardRewardContextStateTypes = { "card_reward" };

    private readonly JsonlTelemetryWriter _writer;
    private readonly IStateSnapshotBuilder _snapshotBuilder;
    private readonly LegalActionBuilder _legalActionBuilder;
    private readonly INativeSaveCapture _nativeSaveCapture;
    private readonly RelicFlashedSignalObserver _relicSignalObserver;
    private readonly Dictionary<string, PendingDecision> _pendingDecisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DecisionContextReference> _latestDecisionContextByStateType = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyDictionary<string, object?>> _recentStableLogicalRunIdentities = new();
    private readonly List<NativeSaveCaptureRef> _recentNativeSaveRefs = new();
    private readonly object _gate = new();
    private BranchTracker _branchTracker = new();
    private PendingShopCardAttempt? _pendingShopCardAttempt;
    private RecentShopCompletion? _recentShopCompletion;

    private CaptureStatus _status = CaptureStatus.NoRun;
    private string? _runId;
    private long _localSequence;
    private long _envelopeSequence;
    private bool _resumeObservedForCurrentRun;
    private PendingRunStart? _pendingRunStart;
    private bool _pendingExplicitRunLoadSawRunStarted;
    private IReadOnlyDictionary<string, object?>? _logicalRunIdentity;
    private bool _preserveBranchTrackerForNextLoad;

    public TelemetryRecorder(
        JsonlTelemetryWriter writer,
        IStateSnapshotBuilder snapshotBuilder,
        LegalActionBuilder legalActionBuilder)
        : this(writer, snapshotBuilder, legalActionBuilder, NativeSaveCapture.CreateDefault())
    {
    }

    internal TelemetryRecorder(
        JsonlTelemetryWriter writer,
        IStateSnapshotBuilder snapshotBuilder,
        LegalActionBuilder legalActionBuilder,
        INativeSaveCapture nativeSaveCapture)
    {
        _writer = writer;
        _snapshotBuilder = snapshotBuilder;
        _legalActionBuilder = legalActionBuilder;
        _nativeSaveCapture = nativeSaveCapture;
        _relicSignalObserver = new RelicFlashedSignalObserver(Sts2TelemetryMod.OnRelicTriggeredFromObservedRuntimeSignal);
    }

    public static TelemetryRecorder CreateDefault()
        => new(JsonlTelemetryWriter.CreateForMod(), new StateSnapshotBuilder(), new LegalActionBuilder());

    internal string TelemetryBaseDirectory => _writer.BaseDirectory;
    internal string InstallationId => _writer.InstallationId;
    internal string? CurrentRunId
    {
        get
        {
            lock (_gate)
                return string.IsNullOrWhiteSpace(_runId) ? null : _runId;
        }
    }

    public void RecordModInitialized()
    {
        WriteOperational("lifecycle/mod_initialized", new Dictionary<string, object?>
        {
            ["mod"] = new Dictionary<string, object?>
            {
                ["id"] = Sts2TelemetryMod.ModId,
                ["version"] = Sts2TelemetryMod.Version,
                ["schema_version"] = SchemaVersion,
                ["network_upload"] = "background_upload_enabled_by_default",
                ["compression"] = "gzip_fallback"
            }
        });
    }

    public void RecordTelemetryError(string source, Exception exception)
    {
        WriteOperational("lifecycle/telemetry_callback_failed", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["role"] = "operational_metadata",
            ["visibility"] = "operational_metadata",
            ["exception"] = ExceptionToRecord(exception)
        });
    }

    public void RecordHarmonyPatchStatus(IReadOnlyDictionary<string, object?> patchStatus)
        => WriteOperational("lifecycle/harmony_patch_status", patchStatus);

    public void RecordHarmonyNativeDependencyStatus(IReadOnlyDictionary<string, object?> dependencyStatus)
        => WriteOperational("lifecycle/harmony_native_dependency_status", dependencyStatus);

    public void OnRunStarted(object? runState, string source)
    {
        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:entered");
        bool preserveLoadedRun;
        PendingRunStart? pendingRunStart;
        lock (_gate)
        {
            pendingRunStart = _pendingRunStart;
            preserveLoadedRun = _status == CaptureStatus.Capturing && _resumeObservedForCurrentRun;
            if (preserveLoadedRun)
            {
                _resumeObservedForCurrentRun = false;
                if (pendingRunStart?.Kind != PendingRunStartKind.ExplicitRunLoad)
                {
                    _pendingRunStart = null;
                    _pendingExplicitRunLoadSawRunStarted = false;
                }
                else
                {
                    _pendingExplicitRunLoadSawRunStarted = true;
                }
            }
        }

        if (preserveLoadedRun)
        {
            Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:preserve_loaded_run:capture:start");
            var loadedSnapshot = _snapshotBuilder.Capture("run_started_after_load");
            Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:preserve_loaded_run:capture:complete");
            if (TryRotateRunForChangedLogicalIdentity(loadedSnapshot, source))
                return;

            UpdateLogicalRunIdentity(loadedSnapshot);
            WriteLifecycle("lifecycle/run_started_after_load", new Dictionary<string, object?>
            {
                ["source"] = source,
                ["state"] = BuildStateReference(loadedSnapshot, includeSnapshot: false),
                ["branch"] = _branchTracker.BuildMetadata()
            });
            return;
        }

        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:support:start");
        var support = RunSupportDetector.Inspect(runState);
        Sts2TelemetryMod.TraceDiagnostic($"recorder.on_run_started:support:complete:{support.IsSupported}");

        bool preserveBranchTrackerForRunStart = false;
        lock (_gate)
        {
            if (_status == CaptureStatus.Capturing)
            {
                _pendingRunStart = new PendingRunStart(source, support, PendingRunStartKind.DelayedRunStart);
                _resumeObservedForCurrentRun = false;
                _pendingExplicitRunLoadSawRunStarted = false;
                _preserveBranchTrackerForNextLoad = false;
                ClearDecisionContextsUnderLock();
                Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:delayed_until_stable_snapshot");
                return;
            }

            _pendingDecisions.Clear();
            _localSequence = 0;
            _envelopeSequence = 0;
            _resumeObservedForCurrentRun = false;
            _pendingRunStart = null;
            _pendingExplicitRunLoadSawRunStarted = false;
            preserveBranchTrackerForRunStart = _preserveBranchTrackerForNextLoad;
            _preserveBranchTrackerForNextLoad = false;
            if (!preserveBranchTrackerForRunStart)
                _branchTracker = new BranchTracker();
            ClearDecisionContextsUnderLock();
            _logicalRunIdentity = null;

            if (!support.IsSupported)
            {
                _status = CaptureStatus.UnsupportedRun;
                _runId = CreateRunId("unsupported");
                Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:unsupported:write:start");
                WriteUnsupportedRun(support, source);
                Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:unsupported:write:complete");
                return;
            }

            _status = CaptureStatus.Capturing;
            _runId = CreateRunId("run");
        }

        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:capture:start");
        var snapshot = _snapshotBuilder.Capture("run_started");
        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:capture:complete");
        ObserveRelicSignalsForCurrentPlayer(runState);
        UpdateLogicalRunIdentity(snapshot);
        if (preserveBranchTrackerForRunStart)
        {
            BranchResumeResult preview = _branchTracker.PreviewResume(snapshot.CanonicalStateHash);
            if (preview.Matched)
            {
                BranchResumeResult resume = _branchTracker.ObserveResume(snapshot.CanonicalStateHash);
                WriteRunLoaded(source, snapshot, resume, classificationSource: "run_started_after_preserved_boundary");
                return;
            }

            lock (_gate)
                _branchTracker = new BranchTracker();
        }

        _branchTracker.ObserveState(snapshot.CanonicalStateHash, "run_start");
        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:write:start");
        WriteLifecycle("lifecycle/run_started", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["support"] = support.ToRecord(),
            ["state"] = BuildStateReference(snapshot, includeSnapshot: true),
            ["branch"] = _branchTracker.BuildMetadata()
        });
        Sts2TelemetryMod.TraceDiagnostic("recorder.on_run_started:write:complete");
    }

    public void OnRunLoaded(object? runState, string source)
    {
        if (!CanCaptureLifecycleForRun(runState, source, out var snapshot))
            return;

        ObserveRelicSignalsForCurrentPlayer(runState);
        BranchResumeResult preview = _branchTracker.PreviewResume(snapshot.CanonicalStateHash);
        if (!preview.Matched && ShouldDelayExplicitRunLoadUntilStableSnapshot(snapshot))
        {
            lock (_gate)
            {
                _resumeObservedForCurrentRun = true;
                _pendingExplicitRunLoadSawRunStarted = false;
                _pendingRunStart = new PendingRunStart(
                    source,
                    RunSupportResult.Supported(new Dictionary<string, object?>
                    {
                        ["resume_signal"] = "explicit_run_load",
                        ["mode_assumption"] = "existing_supported_capture"
                    }),
                    PendingRunStartKind.ExplicitRunLoad);
                ClearDecisionContextsUnderLock();
            }

            return;
        }

        BranchResumeResult resume = _branchTracker.ObserveResume(snapshot.CanonicalStateHash);
        lock (_gate)
        {
            _resumeObservedForCurrentRun = true;
            _pendingRunStart = null;
            _pendingExplicitRunLoadSawRunStarted = false;
            ClearDecisionContextsUnderLock();
        }

        WriteRunLoaded(source, snapshot, resume, classificationSource: null);
    }

    public void OnRunSuspendedOrCleanedUp(string source, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (!IsCapturing)
            return;

        bool abandoned = IsAbandonSource(source, details);
        ResetObservedRelicSignals();
        _branchTracker.MarkCurrentBranchStatus(abandoned ? "abandoned" : "unknown");
        ClearDecisionContexts();
        WriteLifecycle("lifecycle/run_suspended", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["details"] = details ?? new Dictionary<string, object?>(),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["capture_policy"] = SignalOnlyCapturePolicy
        });

        lock (_gate)
            TransitionToNoRunAfterBoundaryUnderLock(preserveBranchTrackerForNextLoad: !abandoned);
    }

    public void OnRunEnded(string source, bool? isVictory = null)
    {
        if (!IsCapturing)
            return;

        ResetObservedRelicSignals();
        _branchTracker.MarkCurrentBranchStatus(isVictory == null ? "final" : isVictory.Value ? "final/victory" : "final/loss");
        WriteLifecycle("lifecycle/run_ended", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["is_victory"] = isVictory,
            ["details"] = new Dictionary<string, object?>
            {
                ["state_capture"] = "disabled",
                ["reason"] = "run_ended_signal_only_transition_safety"
            },
            ["branch"] = _branchTracker.BuildMetadata(),
            ["capture_policy"] = SignalOnlyCapturePolicy
        });

        lock (_gate)
            TransitionToNoRunAfterBoundaryUnderLock(preserveBranchTrackerForNextLoad: false);
    }

    public string? RecordLifecycle(string recordType, string source, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (!IsCapturing)
            return null;

        StateSnapshot snapshot = _snapshotBuilder.Capture(
            recordType,
            includePlayerMetadata: ShouldCaptureLifecyclePlayerMetadata(recordType));
        ObserveRelicSignalsForCurrentPlayer();
        StableSnapshotResolution resolution = PrepareStableSnapshotForRecord(snapshot, recordType);
        if (resolution == StableSnapshotResolution.SuppressRecord)
            return null;

        var recordDetails = details == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(details);
        AddPendingResumeClassificationDetails(recordDetails, resolution, recordType);
        if (resolution == StableSnapshotResolution.Continue
            && ShouldIndexStableSnapshotForResumeMatching(recordType, snapshot))
        {
            _branchTracker.ObserveState(snapshot.CanonicalStateHash, recordType);
        }

        WriteLifecycle(recordType, new Dictionary<string, object?>
        {
            ["source"] = source,
            ["details"] = recordDetails,
            ["state"] = BuildStateReference(snapshot, includeSnapshot: false),
            ["branch"] = _branchTracker.BuildMetadata()
        });

        if (ShouldEmitDecisionContext(recordType, snapshot))
            WriteDecisionContext(recordType, source, snapshot);

        return snapshot.StateType;
    }

    public void RecordLifecycleSignal(string recordType, string source, IReadOnlyDictionary<string, object?>? details = null)
    {
        if (!IsCapturing)
            return;

        if (ShouldClearDecisionContextOnLifecycleSignal(recordType))
            ClearDecisionContexts();

        WriteLifecycle(recordType, new Dictionary<string, object?>
        {
            ["source"] = source,
            ["details"] = details ?? new Dictionary<string, object?>(),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["capture_policy"] = SignalOnlyCapturePolicy
        });
    }

    public bool RecordDecisionContextIfCurrentSurface(
        string contextSource,
        string source,
        IReadOnlyCollection<string> allowedStateTypes,
        IReadOnlyDictionary<string, object?>? details = null,
        bool requireUsableLegalActions = false)
    {
        if (!IsCapturing)
            return false;

        StateSnapshot snapshot = _snapshotBuilder.Capture(contextSource, includePlayerMetadata: false);
        ObserveRelicSignalsForCurrentPlayer();
        StableSnapshotResolution resolution = PrepareStableSnapshotForRecord(snapshot, contextSource);
        if (resolution == StableSnapshotResolution.SuppressRecord)
            return false;

        if (!allowedStateTypes.Contains(snapshot.StateType))
            return false;

        var recordDetails = details == null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(details);
        AddPendingResumeClassificationDetails(recordDetails, resolution, contextSource);
        bool hasUsableLegalActions = WriteDecisionContext(contextSource, source, snapshot, recordDetails);
        return !requireUsableLegalActions || hasUsableLegalActions;
    }

    public bool LatestDecisionContextHasUsableLegalActions(string stateType)
    {
        lock (_gate)
        {
            return _latestDecisionContextByStateType.TryGetValue(stateType, out DecisionContextReference? context)
                && context.HasUsableLegalActions;
        }
    }

    internal void ClearDecisionContextForSurface(string stateType)
    {
        lock (_gate)
            _latestDecisionContextByStateType.Remove(stateType);
    }

    internal bool EnsureCardRewardDecisionContextForSelectionSignal(string source)
    {
        if (!IsCapturing)
            return false;

        IReadOnlyList<Dictionary<string, object?>>? cachedActions = RewardChoiceCache.Shared.BuildLegalActions("card_reward");
        if (cachedActions == null || cachedActions.Count == 0)
            return false;

        var legalActions = cachedActions
            .Select(action => new Dictionary<string, object?>(action, StringComparer.Ordinal))
            .ToArray();
        if (LatestDecisionContextMatchesLegalActions("card_reward", legalActions))
            return true;

        StateSnapshot snapshot = BuildCachedDecisionContextSnapshot("card_reward", legalActions);
        WriteDecisionContextRecord(
            contextSource: "runtime.card_reward.selection_signal_immediate",
            source: $"{source}.card_reward_context_immediate",
            preState: snapshot,
            legalActions: legalActions,
            shopOffers: null,
            details: new Dictionary<string, object?>
            {
                ["trigger_source"] = source,
                ["settled_after_callback"] = false,
                ["settled_snapshot_schedule"] = "not_required_cached_runtime_context",
                ["decision_context_trigger"] = "card_reward_selection_signal_before_scheduled_context",
                ["context_race_fix"] = "card_reward_selection_signal_requires_context_before_recording",
                ["context_material_source"] = "reward_runtime_cache"
            },
            capturePolicy: "cached_runtime_context_no_selected_action_no_post_state");
        return true;
    }

    public void RecordSavePreview(string source)
    {
        if (!IsCapturing)
            return;

        WriteLifecycle("lifecycle/save_preview", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["details"] = new Dictionary<string, object?>
            {
                ["preview_only"] = true,
                ["loaded_run_signal"] = false
            },
            ["branch"] = _branchTracker.BuildMetadata(),
            ["capture_policy"] = SignalOnlyCapturePolicy
        });
    }

    public void RecordSaveObserved(string source)
    {
        if (!IsCapturing)
            return;

        NativeSaveCaptureResult nativeSaveCapture = CaptureNativeSavesForSaveObserved(source);
        IReadOnlyList<NativeSaveCaptureRef> nativeSaveRefs = nativeSaveCapture.Refs;

        WriteLifecycle("lifecycle/save_observed", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["details"] = new Dictionary<string, object?>
            {
                ["reason"] = "save_observed_signal_only_transition_safety",
                ["state_capture"] = "disabled",
                ["branch_index_update"] = "disabled",
                ["decision_data_role"] = "lifecycle_signal_only",
                ["native_save_capture"] = nativeSaveRefs.Count > 0
                    ? "captured_read_only_scrubbed_refs"
                    : "not_available_or_no_candidate_files",
                ["native_save_payload_records"] = nativeSaveCapture.NewCaptures.Count
            },
            ["native_save_refs"] = nativeSaveRefs.Select(save => save.ToRecord()).ToArray(),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["capture_policy"] = SignalOnlyCapturePolicy
        });
    }

    public void BeginActionExecutorDecision(GameAction action)
    {
        BeginDecision(
            pendingKey: ActionKey(action),
            source: "action_executor",
            selectedActionFactory: preState => ActionMetadata.FromGameAction(action, preState));
    }

    public void CompleteActionExecutorDecision(GameAction action)
    {
        CompleteDecision(
            pendingKey: ActionKey(action),
            source: "action_executor_after",
            pendingMarkerIfMissing: false);
    }

    public void RecordActionExecutorSignal(object action, string phase, string? capturePolicy = null)
    {
        if (!IsCapturing)
            return;

        var signal = ActionMetadata.FromActionExecutorSignal(action, phase);
        EnsureDecisionContextForActionSignal(signal);
        var record = BuildEnvelope("decision/action_signal");
        ClearPendingShopCardAttemptForUnrelatedSignal(signal, LocalSequence(record));
        record["source"] = "action_executor";
        record["phase"] = phase;
        record["action_type"] = signal["action_type"];
        record["action_runtime_type"] = signal["runtime_type"];
        record["action_runtime_type_name"] = signal["runtime_type_name"];
        record["decision_source"] = "action_executor";
        record["signal_source"] = "action_executor";
        record["signal_phase"] = phase;
        record["signal_role"] = "observed_action_executor_signal";
        record["capture_policy"] = capturePolicy ?? SignalOnlyCapturePolicy;
        record["branch"] = _branchTracker.BuildMetadata();
        record["action_signal"] = new Dictionary<string, object?>
        {
            ["role"] = "observed_action_executor_signal",
            ["visibility"] = "observed_runtime_action",
            ["metadata"] = signal,
            ["raw_signal_hash"] = TelemetryHash.HashRaw(signal),
            ["canonical_signal_hash"] = TelemetryHash.HashCanonical(signal)
        };
        var decisionContext = ShouldAttachDecisionContextToSignal(signal)
            ? BuildDecisionContextReferenceForSignal(signal)
            : null;
        if (decisionContext != null)
        {
            record["decision_context"] = decisionContext;
            record["non_combat_closure"] = BuildNonCombatClosureForSignal(decisionContext);
        }
        record["role_visibility"] = new[]
        {
            "observed_action_executor_signal",
            "operational_metadata"
        };

        Enqueue(record);
    }

    private void EnsureDecisionContextForActionSignal(IReadOnlyDictionary<string, object?> signal)
    {
        if (!ShouldCreateMapDecisionContextForSignal(signal))
            return;

        const string contextSource = "action_executor.map_context";
        StateSnapshot snapshot = _snapshotBuilder.Capture(contextSource, includePlayerMetadata: false);
        StableSnapshotResolution resolution = PrepareStableSnapshotForRecord(snapshot, contextSource);
        if (resolution == StableSnapshotResolution.SuppressRecord)
            return;

        var details = new Dictionary<string, object?>
        {
            ["decision_context_trigger"] = "action_executor_signal_before_trainable_map_vote",
            ["signal_action_type"] = StringValue(signal, "action_type"),
            ["signal_runtime_type_name"] = StringValue(signal, "runtime_type_name"),
            ["signal_phase"] = StringValue(signal, "phase"),
            ["context_surface_forced"] = !string.Equals(snapshot.StateType, "map", StringComparison.Ordinal)
        };
        if (signal.TryGetValue("destination_coord", out object? destinationCoord))
            details["signal_destination_coord"] = destinationCoord;
        AddPendingResumeClassificationDetails(details, resolution, contextSource);

        StateSnapshot mapSnapshot = string.Equals(snapshot.StateType, "map", StringComparison.Ordinal)
            ? snapshot
            : RetagSnapshotForDecisionSurface(snapshot, "map");
        WriteDecisionContext(contextSource, contextSource, mapSnapshot, details);
    }

    public bool RecordPatchedUiSignal(string source, object? instance, object?[] args)
    {
        if (!IsCapturing)
            return false;

        var signal = new Dictionary<string, object?>(
            ActionMetadata.FromPatchedMethod(source, instance, args),
            StringComparer.Ordinal);
        var record = BuildEnvelope("decision/ui_signal");
        ApplyShopCardAttemptCorrelation(signal, LocalSequence(record));
        record["decision_source"] = source;
        record["signal_source"] = source;
        record["signal_role"] = "observed_ui_signal";
        record["capture_policy"] = SignalOnlyCapturePolicy;
        record["ui_signal"] = new Dictionary<string, object?>
        {
            ["role"] = "observed_ui_signal",
            ["visibility"] = "observed_player_choice",
            ["metadata"] = signal,
            ["raw_signal_hash"] = TelemetryHash.HashRaw(signal),
            ["canonical_signal_hash"] = TelemetryHash.HashCanonical(signal)
        };
        bool canAttachDecisionContext = ShouldAttachDecisionContextToSignal(signal);
        var decisionContext = canAttachDecisionContext
            ? BuildDecisionContextReferenceForSignal(signal)
            : null;
        if (decisionContext != null)
        {
            record["decision_context"] = decisionContext;
            record["non_combat_closure"] = BuildNonCombatClosureForSignal(decisionContext);
        }
        record["role_visibility"] = new[]
        {
            "observed_ui_signal",
            "operational_metadata"
        };

        Enqueue(record);
        return canAttachDecisionContext && IsCompletedShopCardSignal(signal);
    }

    public void RecordRelicTriggerSignal(string source, object? relic, object? targets)
    {
        if (!IsCapturing)
            return;

        var signal = RelicTriggerMetadata.FromRelicFlash(source, relic, targets);
        var record = BuildEnvelope("effect/relic_trigger");
        record["source"] = source;
        record["signal_source"] = source;
        record["signal_role"] = "observed_relic_trigger";
        record["capture_policy"] = SignalOnlyCapturePolicy;
        record["branch"] = _branchTracker.BuildMetadata();
        CopyIfPresent(record, signal, "relic_id");
        CopyIfPresent(record, signal, "relic_runtime_type");
        CopyIfPresent(record, signal, "relic_runtime_type_name");
        CopyIfPresent(record, signal, "relic_rarity");
        record["trigger_attribution"] = signal["trigger_attribution"];
        record["effect_attribution"] = signal["effect_attribution"];
        record["target_count"] = signal["target_count"];
        record["relic_trigger"] = new Dictionary<string, object?>
        {
            ["role"] = "observed_relic_trigger",
            ["visibility"] = "observed_runtime_effect_signal",
            ["metadata"] = signal,
            ["raw_trigger_hash"] = TelemetryHash.HashRaw(signal),
            ["canonical_trigger_hash"] = TelemetryHash.HashCanonical(signal)
        };
        record["role_visibility"] = new[]
        {
            "observed_relic_trigger",
            "operational_metadata"
        };

        Enqueue(record);
    }

    private void BeginDecision(
        string pendingKey,
        string source,
        Func<StateSnapshot, IReadOnlyDictionary<string, object?>> selectedActionFactory)
    {
        if (!IsCapturing)
            return;

        var timing = new DecisionTiming();
        long stepStart = Stopwatch.GetTimestamp();
        StateSnapshot preState = _snapshotBuilder.Capture($"{source}:pre");
        timing.RecordElapsedMicroseconds("pre_snapshot_us", stepStart);
        if (PrepareStableSnapshotForRecord(preState, $"{source}:pre") == StableSnapshotResolution.SuppressRecord)
            return;

        stepStart = Stopwatch.GetTimestamp();
        object? runState = _snapshotBuilder.SafeRunState();
        object? player = _snapshotBuilder.GetLocalPlayer(runState);
        timing.RecordElapsedMicroseconds("run_player_lookup_us", stepStart);
        ObserveRelicSignalsForCurrentPlayer(runState, player);

        stepStart = Stopwatch.GetTimestamp();
        var legalActions = _legalActionBuilder.Build(preState, runState, player);
        timing.RecordElapsedMicroseconds("legal_action_build_us", stepStart);

        stepStart = Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, object?> selectedAction = selectedActionFactory(preState);
        timing.RecordElapsedMicroseconds("selected_action_build_us", stepStart);

        stepStart = Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, object?> normalizedTypedActionKey =
            ActionMetadata.BuildNormalizedTypedActionKey(selectedAction);
        timing.RecordElapsedMicroseconds("normalized_typed_action_key_build_us", stepStart);

        stepStart = Stopwatch.GetTimestamp();
        string selectedActionRawHash = TelemetryHash.HashRaw(selectedAction);
        string selectedActionCanonicalHash = TelemetryHash.HashCanonical(normalizedTypedActionKey);
        timing.RecordElapsedMicroseconds("selected_action_hash_us", stepStart);

        string decisionFrameId = NextId("decision");

        var pending = new PendingDecision(
            PendingKey: pendingKey,
            DecisionFrameId: decisionFrameId,
            Source: source,
            PreState: preState,
            LegalActions: legalActions,
            SelectedAction: selectedAction,
            NormalizedTypedActionKey: normalizedTypedActionKey,
            SelectedActionRawHash: selectedActionRawHash,
            SelectedActionCanonicalHash: selectedActionCanonicalHash,
            Timing: timing);

        lock (_gate)
        {
            _pendingDecisions[pendingKey] = pending;
        }
    }

    private void CompleteDecision(string pendingKey, string source, bool pendingMarkerIfMissing)
    {
        if (!IsCapturing)
            return;

        PendingDecision? pending;
        lock (_gate)
        {
            if (!_pendingDecisions.Remove(pendingKey, out pending))
                pending = null;
        }

        if (pending == null)
        {
            if (pendingMarkerIfMissing)
            {
                WriteLifecycle("lifecycle/pending_decision_missing", new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["pending_key_hash"] = TelemetryHash.HashRaw(pendingKey)
                });
            }
            return;
        }

        long stepStart = Stopwatch.GetTimestamp();
        StateSnapshot postState = _snapshotBuilder.Capture($"{source}:post");
        pending.Timing.RecordElapsedMicroseconds("post_snapshot_us", stepStart);
        StableSnapshotResolution resolution = PrepareStableSnapshotForRecord(postState, $"{source}:post");
        if (resolution == StableSnapshotResolution.SuppressRecord || resolution == StableSnapshotResolution.StartedNewRun)
            return;

        stepStart = Stopwatch.GetTimestamp();
        BranchDecisionResult branchDecision = _branchTracker.RecordDecisionEdge(
            pending.PreState.CanonicalStateHash,
            postState.CanonicalStateHash,
            pending.DecisionFrameId,
            pending.SelectedActionCanonicalHash);
        pending.Timing.RecordElapsedMicroseconds("branch_edge_recording_us", stepStart);

        if (branchDecision.Forked)
            WriteBranchDecisionLifecycle("lifecycle/branch_forked", pending, postState, branchDecision);
        else if (branchDecision.DivergenceUnknown)
            WriteBranchDecisionLifecycle("lifecycle/branch_divergence_unknown", pending, postState, branchDecision);

        WriteDecisionFrame(pending, postState, pendingMarker: false, branchDecision: branchDecision);
    }

    public void FlushPendingAsTransitionMarkers(string reason)
    {
        List<PendingDecision> pending;
        lock (_gate)
        {
            pending = _pendingDecisions.Values.ToList();
            _pendingDecisions.Clear();
        }

        foreach (PendingDecision decision in pending)
            WriteDecisionFrame(decision, postState: null, pendingMarker: true, markerReason: reason);
    }

    private void WriteDecisionFrame(
        PendingDecision pending,
        StateSnapshot? postState,
        bool pendingMarker,
        BranchDecisionResult? branchDecision = null,
        string? markerReason = null)
    {
        long buildStart = Stopwatch.GetTimestamp();
        var record = BuildEnvelope("decision/frame");
        record["decision_frame_id"] = pending.DecisionFrameId;
        record["decision_source"] = pending.Source;
        record["branch"] = _branchTracker.BuildMetadata();
        record["pre_state"] = BuildStateRole("visible_pre_decision", pending.PreState, includeSnapshot: true);
        var legalActions = new Dictionary<string, object?>
        {
            ["role"] = "visible_pre_decision",
            ["visibility"] = "player_visible",
            ["action_count"] = pending.LegalActions.Count,
            ["actions"] = pending.LegalActions
        };
        AddCombatTargetCandidateSidecar(legalActions, pending.PreState);
        record["legal_actions"] = legalActions;
        record["selected_action"] = new Dictionary<string, object?>
        {
            ["role"] = "selected_action",
            ["visibility"] = "observed_player_choice",
            ["raw"] = pending.SelectedAction,
            ["normalized_typed_action_key"] = pending.NormalizedTypedActionKey,
            ["raw_action_hash"] = pending.SelectedActionRawHash,
            ["canonical_action_hash"] = pending.SelectedActionCanonicalHash
        };
        Dictionary<string, object?>? combatProcess = BuildCombatProcessRecord(pending, postState);
        if (combatProcess != null)
            record["combat_process"] = combatProcess;

        if (branchDecision != null)
            record["branch_decision"] = BranchDecisionToRecord(branchDecision);

        if (postState != null)
        {
            record["post_state"] = BuildStateRole("post_action_observed", postState, includeSnapshot: true);
        }
        else
        {
            record["post_state"] = new Dictionary<string, object?>
            {
                ["role"] = "post_action_observed",
                ["visibility"] = "pending_transition",
                ["status"] = "pending",
                ["reason"] = markerReason ?? "post_state_not_observed"
            };
        }

        record["hashes"] = new Dictionary<string, object?>
        {
            ["pre_raw_state_hash"] = pending.PreState.RawStateHash,
            ["pre_canonical_state_hash"] = pending.PreState.CanonicalStateHash,
            ["post_raw_state_hash"] = postState?.RawStateHash,
            ["post_canonical_state_hash"] = postState?.CanonicalStateHash
        };
        record["role_visibility"] = new[]
        {
            "visible_pre_decision",
            "selected_action",
            "post_action_observed",
            combatProcess == null ? null : "combat_process",
            "operational_metadata"
        }.Where(value => value != null).Cast<string>().ToArray();

        pending.Timing.RecordElapsedMicroseconds("decision_frame_build_enqueue_us", buildStart);
        AddDecisionTimingMetadata(record, pending.Timing);
        Enqueue(record);
    }

    private bool WriteDecisionContext(
        string contextSource,
        string source,
        StateSnapshot preState,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        object? runState = _snapshotBuilder.SafeRunState();
        object? player = _snapshotBuilder.GetLocalPlayer(runState);
        ObserveRelicSignalsForCurrentPlayer(runState, player);
        IReadOnlyList<Dictionary<string, object?>> legalActions = _legalActionBuilder.Build(preState, runState, player);
        IReadOnlyList<Dictionary<string, object?>>? shopOffers = preState.StateType == "shop"
            ? _legalActionBuilder.BuildShopOffers(preState, runState, player)
            : null;
        return WriteDecisionContextRecord(
            contextSource,
            source,
            preState,
            legalActions,
            shopOffers,
            details,
            capturePolicy: "stable_snapshot_context_no_selected_action_no_post_state");
    }

    private bool WriteDecisionContextRecord(
        string contextSource,
        string source,
        StateSnapshot preState,
        IReadOnlyList<Dictionary<string, object?>> legalActions,
        IReadOnlyList<Dictionary<string, object?>>? shopOffers,
        IReadOnlyDictionary<string, object?>? details,
        string capturePolicy)
    {
        string decisionContextId = NextId("decision-context");
        IReadOnlyList<LegalActionReference> legalActionReferences = BuildLegalActionReferences(legalActions);
        (bool hasUsableLegalActions, string legalActionReadiness) = DescribeLegalActionReadiness(legalActions);

        var legalActionsPayload = new Dictionary<string, object?>
        {
            ["role"] = "visible_pre_decision",
            ["visibility"] = "player_visible",
            ["action_count"] = legalActions.Count,
            ["actions"] = legalActions
        };

        Dictionary<string, object?>? shopOffersPayload = null;
        if (shopOffers != null)
        {
            shopOffersPayload = new Dictionary<string, object?>
            {
                ["role"] = "visible_pre_decision",
                ["visibility"] = "player_visible",
                ["offer_count"] = shopOffers.Count,
                ["offers"] = shopOffers
            };
        }

        var record = BuildEnvelope("decision/context");
        long contextLocalSequence = LocalSequence(record);
        record["decision_context_id"] = decisionContextId;
        record["source"] = source;
        record["decision_source"] = contextSource;
        record["context_source"] = contextSource;
        record["capture_policy"] = capturePolicy;
        if (details is { Count: > 0 })
            record["details"] = details;
        record["branch"] = _branchTracker.BuildMetadata();
        record["pre_state"] = BuildStateRole("visible_pre_decision", preState, includeSnapshot: true);
        record["legal_actions"] = legalActionsPayload;
        if (shopOffersPayload != null)
            record["shop_offers"] = shopOffersPayload;
        record["non_combat_closure"] = BuildNonCombatClosureForContext(
            preState.StateType,
            decisionContextId,
            hasUsableLegalActions,
            legalActionReadiness);
        IReadOnlyList<NativeSaveCaptureRef> nativeSaveRefs = RecentNativeSaveRefsForContext(preState.StateType);
        if (nativeSaveRefs.Count > 0)
        {
            record["native_save_ref"] = nativeSaveRefs[0].ToRecord();
            record["native_save_refs"] = nativeSaveRefs.Select(save => save.ToRecord()).ToArray();
        }
        if (preState.StateType == "card_reward"
            && TryBuildCardRewardParentContextLink(decisionContextId, out var parentLink))
        {
            record["parent_decision_context"] = parentLink;
        }
        var hashes = new Dictionary<string, object?>
        {
            ["pre_raw_state_hash"] = preState.RawStateHash,
            ["pre_canonical_state_hash"] = preState.CanonicalStateHash,
            ["legal_actions_raw_hash"] = TelemetryHash.HashRaw(legalActions),
            ["legal_actions_canonical_hash"] = TelemetryHash.HashCanonical(legalActions)
        };
        if (shopOffers != null)
        {
            hashes["shop_offers_raw_hash"] = TelemetryHash.HashRaw(shopOffers);
            hashes["shop_offers_canonical_hash"] = TelemetryHash.HashCanonical(shopOffers);
        }

        record["hashes"] = hashes;
        var roleVisibility = new List<string>
        {
            "visible_pre_decision",
            "legal_actions"
        };
        if (shopOffers != null)
            roleVisibility.Add("shop_offers");
        roleVisibility.Add("operational_metadata");
        record["role_visibility"] = roleVisibility.ToArray();

        var reference = new DecisionContextReference(
            DecisionContextId: decisionContextId,
            ContextSource: contextSource,
            Source: source,
            StateType: preState.StateType,
            RawStateHash: preState.RawStateHash,
            CanonicalStateHash: preState.CanonicalStateHash,
            LegalActions: legalActionReferences,
            LocalSequence: contextLocalSequence,
            HasUsableLegalActions: hasUsableLegalActions,
            LegalActionReadiness: legalActionReadiness);

        lock (_gate)
        {
            _latestDecisionContextByStateType[preState.StateType] = reference;
        }

        Enqueue(record);
        return hasUsableLegalActions;
    }

    private bool LatestDecisionContextMatchesLegalActions(
        string stateType,
        IReadOnlyList<Dictionary<string, object?>> legalActions)
    {
        DecisionContextReference? context;
        lock (_gate)
        {
            _latestDecisionContextByStateType.TryGetValue(stateType, out context);
        }

        if (context == null || !context.HasUsableLegalActions)
            return false;

        IReadOnlyList<LegalActionReference> latestActions = BuildLegalActionReferences(legalActions);
        if (context.LegalActions.Count != latestActions.Count)
            return false;

        for (int i = 0; i < latestActions.Count; i++)
        {
            if (!string.Equals(
                    context.LegalActions[i].CanonicalActionHash,
                    latestActions[i].CanonicalActionHash,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static StateSnapshot BuildCachedDecisionContextSnapshot(
        string stateType,
        IReadOnlyList<Dictionary<string, object?>> legalActions)
    {
        var actionKeys = legalActions
            .Select(ActionMetadata.BuildNormalizedTypedActionKey)
            .ToArray();
        var raw = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state_type"] = stateType,
            ["surface"] = stateType,
            ["context_material_source"] = "reward_runtime_cache",
            ["context_capture_policy"] = "cached_runtime_context",
            ["legal_action_count"] = legalActions.Count,
            ["legal_action_keys"] = actionKeys
        };
        var canonical = TelemetryHash.Canonicalize(raw) as IReadOnlyDictionary<string, object?>
            ?? raw;

        return new StateSnapshot(
            stateType,
            raw,
            canonical,
            TelemetryHash.HashRaw(raw),
            TelemetryHash.HashCanonicalPayload(canonical));
    }

    private Dictionary<string, object?>? BuildDecisionContextReferenceForSignal(
        IReadOnlyDictionary<string, object?> signal)
    {
        string? stateType = StateTypeForSignal(signal);
        if (stateType == null)
            return null;

        IReadOnlyDictionary<string, object?> selectedActionKey = NormalizedSignalActionKey(signal);
        bool hasUsableSelectedActionKey = HasUsableSelectedActionKey(stateType, selectedActionKey);

        DecisionContextReference? context;
        lock (_gate)
        {
            _latestDecisionContextByStateType.TryGetValue(stateType, out context);
        }

        if (context == null)
        {
            return new Dictionary<string, object?>
            {
                ["state_type"] = stateType,
                ["match_policy"] = "latest_context_by_surface",
                ["context_reference_status"] = "missing_latest_context_for_surface",
                ["context_reference_reason"] = "latest_context_for_surface_missing_or_stale",
                ["selected_action_match"] = DecisionContextMatch.Unmatched(
                    "latest_context_for_surface_missing_or_stale",
                    hasUsableSelectedActionKey ? TelemetryHash.HashCanonical(selectedActionKey) : null).ToRecord()
            };
        }

        DecisionContextMatch match = MatchDecisionContext(context, selectedActionKey, hasUsableSelectedActionKey);
        var reference = new Dictionary<string, object?>
        {
            ["decision_context_id"] = context.DecisionContextId,
            ["context_source"] = context.ContextSource,
            ["source"] = context.Source,
            ["state_type"] = context.StateType,
            ["raw_state_hash"] = context.RawStateHash,
            ["canonical_state_hash"] = context.CanonicalStateHash,
            ["match_policy"] = match.MatchPolicy,
            ["context_local_sequence"] = context.LocalSequence,
            ["context_reference_status"] = "latest_context_by_surface_resolved",
            ["context_reference_reason"] = context.HasUsableLegalActions
                ? "latest_context_by_surface"
                : context.LegalActionReadiness,
            ["selected_action_match"] = match.ToRecord()
        };

        return reference;
    }

    private NativeSaveCaptureResult CaptureNativeSavesForSaveObserved(string source)
    {
        NativeSaveCaptureResult result;
        try
        {
            result = _nativeSaveCapture.CaptureRecent(_writer.BaseDirectory);
        }
        catch
        {
            result = NativeSaveCaptureResult.Empty;
        }

        foreach (NativeSaveCapturedPayload capture in result.NewCaptures)
            WriteNativeSaveCaptureRecord(source, capture);

        if (result.Refs.Count > 0)
        {
            lock (_gate)
            {
                _recentNativeSaveRefs.Clear();
                _recentNativeSaveRefs.AddRange(result.Refs.Take(4));
            }
        }

        return result;
    }

    private void WriteNativeSaveCaptureRecord(string source, NativeSaveCapturedPayload capture)
    {
        var record = BuildEnvelope("native_save/capture");
        record["source"] = source;
        record["capture_policy"] = "read_only_native_save_scrubbed_payload";
        record["native_save_ref"] = capture.Ref.ToRecord();
        record["native_save"] = new Dictionary<string, object?>
        {
            ["role"] = "native_save_payload",
            ["visibility"] = "offline_training_evidence",
            ["metadata"] = capture.Ref.Metadata,
            ["payload"] = capture.Payload
        };
        record["role_visibility"] = new[]
        {
            "native_save_payload",
            "operational_metadata"
        };
        Enqueue(record);
    }

    internal void SetRecentNativeSaveRefsForTests(IReadOnlyList<NativeSaveCaptureRef> refs)
    {
        lock (_gate)
        {
            _recentNativeSaveRefs.Clear();
            _recentNativeSaveRefs.AddRange(refs.Take(4));
        }
    }

    private IReadOnlyList<NativeSaveCaptureRef> RecentNativeSaveRefsForContext(string stateType)
    {
        if (!IsNonCombatDecisionSurface(stateType))
            return Array.Empty<NativeSaveCaptureRef>();

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        lock (_gate)
        {
            return _recentNativeSaveRefs
                .Where(save => save.CapturedAtUtc >= cutoff)
                .Take(4)
                .ToArray();
        }
    }

    private static StateSnapshot RetagSnapshotForDecisionSurface(StateSnapshot snapshot, string stateType)
    {
        var raw = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in snapshot.RawSnapshot)
            raw[key] = value;
        raw["state_type"] = stateType;

        var canonical = TelemetryHash.Canonicalize(raw) as IReadOnlyDictionary<string, object?>
            ?? new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["state_type"] = stateType
            };

        return new StateSnapshot(
            stateType,
            raw,
            canonical,
            TelemetryHash.HashRaw(raw),
            TelemetryHash.HashCanonicalPayload(canonical));
    }

    private static Dictionary<string, object?> BuildNonCombatClosureForContext(
        string stateType,
        string decisionContextId,
        bool hasUsableLegalActions,
        string legalActionReadiness)
        => new()
        {
            ["state_type"] = stateType,
            ["surface"] = stateType,
            ["decision_context_id"] = decisionContextId,
            ["context_reference_status"] = "context_open_awaiting_signal",
            ["context_reference_reason"] = hasUsableLegalActions
                ? "latest_context_legal_actions_available"
                : legalActionReadiness,
            ["match_policy"] = "latest_context_by_surface",
            ["selected_action_match_status"] = "not_observed_on_context_record",
            ["selected_action_match_reason"] = "context_record_has_no_selected_action",
            ["matched_legal_action_index"] = null,
            ["matched_action_type"] = null,
            ["matched_legal_action_hash"] = null,
            ["trainable_closed_non_combat_choice"] = false
        };

    private bool TryBuildCardRewardParentContextLink(
        string childDecisionContextId,
        out Dictionary<string, object?> parentLink)
    {
        parentLink = new Dictionary<string, object?>();
        DecisionContextReference? rewardsContext;
        lock (_gate)
        {
            _latestDecisionContextByStateType.TryGetValue("rewards", out rewardsContext);
        }

        if (rewardsContext == null)
            return false;

        LegalActionReference? parentAction = rewardsContext.LegalActions.FirstOrDefault(action =>
            string.Equals(action.ActionType, "claim_reward", StringComparison.Ordinal)
            && string.Equals(StringValue(action.NormalizedTypedActionKey, "reward_id"), "card_reward", StringComparison.Ordinal));
        if (parentAction == null)
            return false;

        parentLink = new Dictionary<string, object?>
        {
            ["link_role"] = "parent_reward_claim_opens_child_card_reward_context",
            ["parent_decision_context_id"] = rewardsContext.DecisionContextId,
            ["child_decision_context_id"] = childDecisionContextId,
            ["parent_state_type"] = rewardsContext.StateType,
            ["child_state_type"] = "card_reward",
            ["parent_legal_action_index"] = parentAction.Index,
            ["parent_action_type"] = parentAction.ActionType,
            ["parent_legal_action_hash"] = parentAction.CanonicalActionHash,
            ["parent_normalized_typed_action_key"] = parentAction.NormalizedTypedActionKey,
            ["link_reason"] = "claim_reward_card_reward_opened_child_card_reward_surface"
        };
        return true;
    }

    private static Dictionary<string, object?> BuildNonCombatClosureForSignal(
        IReadOnlyDictionary<string, object?> decisionContext)
    {
        string? stateType = StringValue(decisionContext, "state_type");
        object? selectedActionMatch = decisionContext.TryGetValue("selected_action_match", out object? rawMatch)
            ? rawMatch
            : null;
        TryCoerceDictionary(selectedActionMatch, out IReadOnlyDictionary<string, object?> match);
        bool matched = match.TryGetValue("matched", out object? rawMatched)
            && rawMatched is bool matchedValue
            && matchedValue;
        string status = matched ? "matched" : "unmatched";
        string? contextId = StringValue(decisionContext, "decision_context_id");
        return new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["surface"] = stateType,
            ["decision_context_id"] = contextId,
            ["context_reference_status"] = StringValue(decisionContext, "context_reference_status"),
            ["context_reference_reason"] = StringValue(decisionContext, "context_reference_reason"),
            ["match_policy"] = StringValue(decisionContext, "match_policy")
                ?? StringValue(match, "match_policy")
                ?? "latest_context_by_surface",
            ["selected_action_match_status"] = status,
            ["selected_action_match_reason"] = StringValue(match, "reason"),
            ["matched_legal_action_index"] = match.TryGetValue("matched_legal_action_index", out object? index) ? index : null,
            ["matched_action_type"] = StringValue(match, "matched_action_type"),
            ["matched_legal_action_hash"] = StringValue(match, "matched_legal_action_hash"),
            ["trainable_closed_non_combat_choice"] = matched && IsNonCombatDecisionSurface(stateType)
        };
    }

    private static IReadOnlyList<LegalActionReference> BuildLegalActionReferences(
        IReadOnlyList<Dictionary<string, object?>> legalActions)
    {
        var references = new List<LegalActionReference>(legalActions.Count);
        for (int i = 0; i < legalActions.Count; i++)
        {
            Dictionary<string, object?> action = legalActions[i];
            IReadOnlyDictionary<string, object?> normalizedKey = NormalizedLegalActionKey(action);
            references.Add(new LegalActionReference(
                Index: i,
                ActionType: StringValue(normalizedKey, "action_type") ?? StringValue(action, "action_type") ?? "unknown",
                NormalizedTypedActionKey: normalizedKey,
                CanonicalActionHash: TelemetryHash.HashCanonical(normalizedKey)));
        }

        return references;
    }

    private static IReadOnlyDictionary<string, object?> NormalizedLegalActionKey(
        IReadOnlyDictionary<string, object?> action)
    {
        IReadOnlyDictionary<string, object?> normalizedKey = ActionMetadata.BuildNormalizedTypedActionKey(action);
        if (!string.Equals(StringValue(normalizedKey, "action_type"), "unknown", StringComparison.Ordinal))
            return normalizedKey;

        if (action.TryGetValue("match_key", out object? matchKey)
            && TryCoerceDictionary(matchKey, out var matchKeyDictionary))
        {
            return matchKeyDictionary;
        }

        return normalizedKey;
    }

    private static DecisionContextMatch MatchDecisionContext(
        DecisionContextReference context,
        IReadOnlyDictionary<string, object?> selectedActionKey,
        bool hasUsableSelectedActionKey)
    {
        if (!hasUsableSelectedActionKey)
            return DecisionContextMatch.Unmatched("selected_action_normalized_typed_action_key_unavailable", null);

        string selectedActionHash = TelemetryHash.HashCanonical(selectedActionKey);

        if (!context.HasUsableLegalActions)
        {
            return DecisionContextMatch.Unmatched(
                context.LegalActionReadiness,
                selectedActionHash);
        }

        foreach (LegalActionReference action in context.LegalActions)
        {
            if (string.Equals(action.CanonicalActionHash, selectedActionHash, StringComparison.Ordinal))
            {
                return DecisionContextMatch.CreateMatched(
                    "normalized_typed_action_key_hash_match",
                    selectedActionHash,
                    action);
            }
        }

        foreach (LegalActionReference action in context.LegalActions)
        {
            if (ActionKeyContains(action.NormalizedTypedActionKey, selectedActionKey))
            {
                return DecisionContextMatch.CreateMatched(
                    "normalized_typed_action_key_subset_match",
                    selectedActionHash,
                    action);
            }
        }

        foreach (LegalActionReference action in context.LegalActions)
        {
            if (ShopSignalIdentityMatches(action.NormalizedTypedActionKey, selectedActionKey))
            {
                return DecisionContextMatch.CreateMatched(
                    "shop_signal_identity_match",
                    selectedActionHash,
                    action);
            }
        }

        return DecisionContextMatch.Unmatched(
            "latest_context_by_surface_no_legal_action_match",
            selectedActionHash);
    }

    private static bool HasUsableSelectedActionKey(
        string stateType,
        IReadOnlyDictionary<string, object?> selectedActionKey)
    {
        string? actionType = StringValue(selectedActionKey, "action_type");
        if (IsUnavailableOrPlaceholderActionType(actionType))
            return false;

        if (IsShopSignalActionType(actionType) && ShopSignalIdentityValue(selectedActionKey) == null)
            return false;

        if (actionType is "choose_reward_card" or "sacrifice_reward_card"
            && StringValue(selectedActionKey, "card_id") == null
            && !selectedActionKey.ContainsKey("card_index"))
        {
            return false;
        }

        string? keyStateType = StateTypeForActionType(actionType);
        return string.Equals(keyStateType, stateType, StringComparison.Ordinal);
    }

    private static (bool HasUsableLegalActions, string Reason) DescribeLegalActionReadiness(
        IReadOnlyList<Dictionary<string, object?>> legalActions)
    {
        if (legalActions.Count == 0)
            return (false, "latest_context_legal_actions_unavailable_or_placeholder_only");

        bool hasUsableAction = legalActions.Any(action =>
        {
            string? actionType = StringValue(action, "action_type");
            return !IsUnavailableOrPlaceholderActionType(actionType);
        });

        return hasUsableAction
            ? (true, "latest_context_legal_actions_available")
            : (false, "latest_context_legal_actions_unavailable_or_placeholder_only");
    }

    private static bool IsUnavailableOrPlaceholderActionType(string? actionType)
        => string.IsNullOrWhiteSpace(actionType)
            || string.Equals(actionType, "unknown", StringComparison.Ordinal)
            || actionType.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || actionType.Contains("pending", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldAttachDecisionContextToSignal(IReadOnlyDictionary<string, object?> signal)
    {
        if (IsAttemptedShopCardSignal(signal))
            return false;

        if (string.Equals(StringValue(signal, "shop_transaction_status"), "duplicate_completion_callback", StringComparison.Ordinal))
            return false;

        if (IsMapActionSignal(signal))
            return IsTrainableMapVoteSignal(signal);

        return true;
    }

    private static bool ShouldCreateMapDecisionContextForSignal(IReadOnlyDictionary<string, object?> signal)
        => IsTrainableMapVoteSignal(signal);

    private static bool IsTrainableMapVoteSignal(IReadOnlyDictionary<string, object?> signal)
        => string.Equals(StringValue(signal, "action_type"), "choose_map_node", StringComparison.Ordinal)
            && string.Equals(StringValue(signal, "phase"), "before_action_executed", StringComparison.Ordinal)
            && (StringValue(signal, "runtime_type_name")?.EndsWith("VoteForMapCoordAction", StringComparison.Ordinal) == true)
            && signal.TryGetValue("destination_coord", out object? destinationCoord)
            && destinationCoord != null;

    private static bool IsMapActionSignal(IReadOnlyDictionary<string, object?> signal)
        => string.Equals(StringValue(signal, "state_type"), "map", StringComparison.Ordinal)
            || string.Equals(StateTypeForActionType(StringValue(signal, "action_type")), "map", StringComparison.Ordinal)
            || (StringValue(signal, "runtime_type_name")?.Contains("MapCoordAction", StringComparison.Ordinal) == true);

    private static bool ShopSignalIdentityMatches(
        IReadOnlyDictionary<string, object?> candidate,
        IReadOnlyDictionary<string, object?> selected)
    {
        string? selectedActionType = StringValue(selected, "action_type");
        string? candidateActionType = StringValue(candidate, "action_type");
        if (!IsShopSignalActionType(selectedActionType)
            || !string.Equals(selectedActionType, candidateActionType, StringComparison.Ordinal))
        {
            return false;
        }

        string? selectedIdentity = ShopSignalIdentityValue(selected);
        string? candidateIdentity = ShopSignalIdentityValue(candidate);
        if (string.IsNullOrWhiteSpace(selectedIdentity)
            || !string.Equals(selectedIdentity, candidateIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        string? selectedCategory = StringValue(selected, "category");
        string? candidateCategory = StringValue(candidate, "category");
        if (string.Equals(selectedActionType, "buy_shop_card", StringComparison.Ordinal)
            && string.Equals(selectedCategory, "card", StringComparison.Ordinal)
            && candidateCategory is "character_card" or "colorless_card")
        {
            return true;
        }

        return string.Equals(selectedCategory, candidateCategory, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, object?> NormalizedSignalActionKey(
        IReadOnlyDictionary<string, object?> signal)
    {
        if (signal.TryGetValue("normalized_typed_action_key", out object? normalizedKey)
            && TryCoerceDictionary(normalizedKey, out var normalizedKeyDictionary))
        {
            return normalizedKeyDictionary;
        }

        return ActionMetadata.BuildNormalizedTypedActionKey(signal);
    }

    private void ApplyShopCardAttemptCorrelation(Dictionary<string, object?> signal, long localSequence)
    {
        lock (_gate)
        {
            ExpirePendingShopCardAttemptUnderLock(localSequence);

            if (IsCompletedShopCardSignal(signal))
            {
                PendingShopCardAttempt? pending = _pendingShopCardAttempt;
                if (ShopSignalIdentityKey(signal) == null
                    && pending != null
                    && PendingShopCardAttemptCanEnrich(pending, localSequence))
                {
                    EnrichShopCardCompletionFromPendingAttempt(signal, pending, localSequence);
                }

                RecordShopCardCompletionUnderLock(signal);
                MarkShopCompletionTransactionUnderLock(signal, localSequence);
                return;
            }

            if (IsAttemptedShopCardSignal(signal))
            {
                string? identityKey = ShopSignalIdentityKey(signal);
                if (identityKey == null)
                {
                    ClearPendingShopCardAttemptUnderLock();
                    return;
                }

                _pendingShopCardAttempt = new PendingShopCardAttempt(
                    Source: StringValue(signal, "source") ?? "unknown",
                    LocalSequence: localSequence,
                    ActionType: StringValue(signal, "action_type"),
                    Category: StringValue(signal, "category"),
                    Id: StringValue(signal, "id"),
                    CardId: StringValue(signal, "card_id"),
                    RelicId: StringValue(signal, "relic_id"),
                    PotionId: StringValue(signal, "potion_id"),
                    RemovalId: StringValue(signal, "removal_id"),
                    IdentityKey: identityKey,
                    NormalizedTypedActionKey: CopyActionKey(NormalizedSignalActionKey(signal)),
                    CompletionSignalCount: 0);
                return;
            }

            ClearPendingShopCardAttemptUnderLock();
        }
    }

    private void ClearPendingShopCardAttemptForUnrelatedSignal(
        IReadOnlyDictionary<string, object?> signal,
        long localSequence)
    {
        lock (_gate)
        {
            ExpirePendingShopCardAttemptUnderLock(localSequence);
            if (!IsAttemptedShopCardSignal(signal) && !IsCompletedShopCardSignal(signal))
                ClearPendingShopCardAttemptUnderLock();
        }
    }

    private static void EnrichShopCardCompletionFromPendingAttempt(
        Dictionary<string, object?> signal,
        PendingShopCardAttempt pending,
        long localSequence)
    {
        CopyIfMissing(signal, "id", pending.Id ?? pending.CardId ?? pending.RelicId ?? pending.PotionId ?? pending.RemovalId);
        CopyIfMissing(signal, "card_id", pending.CardId ?? pending.Id);
        CopyIfMissing(signal, "relic_id", pending.RelicId ?? pending.Id);
        CopyIfMissing(signal, "potion_id", pending.PotionId ?? pending.Id);
        CopyIfMissing(signal, "removal_id", pending.RemovalId ?? pending.Id);
        if (pending.Category != null)
            signal["category"] = pending.Category;

        signal["shop_completion_identity_enrichment"] = "prior_typed_attempt";
        signal["shop_completion_identity_enrichment_source"] = pending.Source;
        signal["shop_completion_identity_enrichment_reason"] =
            "completed_entry_lost_item_identity_after_purchase";
        signal["shop_completion_identity_enrichment_attempt_local_sequence"] = pending.LocalSequence;
        signal["shop_completion_identity_enrichment_age_sequences"] =
            Math.Max(0, localSequence - pending.LocalSequence);
        signal["shop_completion_identity_enrichment_attempt_key"] = pending.NormalizedTypedActionKey;
        signal["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(signal);
    }

    private void RecordShopCardCompletionUnderLock(IReadOnlyDictionary<string, object?> signal)
    {
        PendingShopCardAttempt? pending = _pendingShopCardAttempt;
        if (pending == null)
            return;

        string? completionActionType = StringValue(signal, "action_type");
        string? completionIdentityKey = ShopSignalIdentityKey(signal);
        if (completionIdentityKey == null
            || !string.Equals(completionActionType, pending.ActionType, StringComparison.Ordinal)
            || !string.Equals(completionIdentityKey, pending.IdentityKey, StringComparison.Ordinal))
        {
            ClearPendingShopCardAttemptUnderLock();
            return;
        }

        int completionSignalCount = pending.CompletionSignalCount + 1;
        _pendingShopCardAttempt = completionSignalCount >= PendingShopCardAttemptMaxCompletionSignals
            ? null
            : pending with { CompletionSignalCount = completionSignalCount };
    }

    private void MarkShopCompletionTransactionUnderLock(
        Dictionary<string, object?> signal,
        long localSequence)
    {
        string? identityKey = ShopSignalIdentityKey(signal);
        if (identityKey == null)
        {
            signal["shop_transaction_status"] = "completed_identity_unavailable";
            signal["shop_transaction_trainable"] = false;
            return;
        }

        if (_recentShopCompletion is { } recent
            && string.Equals(recent.IdentityKey, identityKey, StringComparison.Ordinal)
            && localSequence - recent.LocalSequence is >= 0 and <= PendingShopCardAttemptMaxCompletionSignals)
        {
            signal["shop_transaction_status"] = "duplicate_completion_callback";
            signal["shop_transaction_trainable"] = false;
            signal["shop_duplicate_of_local_sequence"] = recent.LocalSequence;
            return;
        }

        _recentShopCompletion = new RecentShopCompletion(identityKey, localSequence);
        signal["shop_transaction_status"] = "completed_transaction";
        signal["shop_transaction_trainable"] = true;
    }

    private void ExpirePendingShopCardAttemptUnderLock(long localSequence)
    {
        if (_pendingShopCardAttempt is not { } pending)
            return;

        if (localSequence - pending.LocalSequence > PendingShopCardAttemptMaxAgeSequences)
            ClearPendingShopCardAttemptUnderLock();
    }

    private void ClearPendingShopCardAttemptUnderLock()
        => _pendingShopCardAttempt = null;

    private static bool PendingShopCardAttemptCanEnrich(PendingShopCardAttempt pending, long localSequence)
        => localSequence - pending.LocalSequence is >= 0 and <= PendingShopCardAttemptMaxAgeSequences;

    private static bool IsAttemptedShopCardSignal(IReadOnlyDictionary<string, object?> signal)
        => IsShopSignalActionType(StringValue(signal, "action_type"))
            && string.Equals(StringValue(signal, "purchase_status"), "attempted", StringComparison.Ordinal);

    private static bool IsCompletedShopCardSignal(IReadOnlyDictionary<string, object?> signal)
        => IsShopSignalActionType(StringValue(signal, "action_type"))
            && string.Equals(StringValue(signal, "purchase_status"), "completed", StringComparison.Ordinal);

    private static bool IsShopSignalActionType(string? actionType)
        => actionType is "buy_shop_card" or "buy_shop_relic" or "buy_shop_potion" or "remove_card_at_shop" or "shop_purchase";

    private static string? ShopSignalIdentityKey(IReadOnlyDictionary<string, object?> actionKey)
    {
        string? actionType = StringValue(actionKey, "action_type");
        if (!IsShopSignalActionType(actionType))
            return null;

        string? identity = ShopSignalIdentityValue(actionKey);
        if (identity == null)
            return null;

        return string.Join(
            "|",
            actionType,
            StringValue(actionKey, "category") ?? "",
            identity,
            StringValue(actionKey, "index") ?? "");
    }

    private static string? ShopSignalIdentityValue(IReadOnlyDictionary<string, object?> actionKey)
        => StringValue(actionKey, "card_id")
            ?? StringValue(actionKey, "relic_id")
            ?? StringValue(actionKey, "potion_id")
            ?? StringValue(actionKey, "removal_id")
            ?? StringValue(actionKey, "id");

    private static IReadOnlyDictionary<string, object?> CopyActionKey(
        IReadOnlyDictionary<string, object?> actionKey)
    {
        var copy = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in actionKey)
            copy[key] = value;
        return copy;
    }

    private static bool ActionKeyContains(
        IReadOnlyDictionary<string, object?> candidate,
        IReadOnlyDictionary<string, object?> selected)
    {
        foreach (var (key, selectedValue) in selected)
        {
            if (!candidate.TryGetValue(key, out object? candidateValue))
                return false;

            if (!ActionKeyValuesEqual(candidateValue, selectedValue))
                return false;
        }

        return selected.Count > 0;
    }

    private static bool ActionKeyValuesEqual(object? left, object? right)
    {
        if (left == null || right == null)
            return left == null && right == null;

        if (Equals(left, right))
            return true;

        if (TryCoerceDictionary(left, out var leftDictionary)
            && TryCoerceDictionary(right, out var rightDictionary))
        {
            return string.Equals(
                TelemetryHash.HashCanonical(leftDictionary),
                TelemetryHash.HashCanonical(rightDictionary),
                StringComparison.Ordinal);
        }

        return string.Equals(
            TelemetryHash.HashCanonical(left),
            TelemetryHash.HashCanonical(right),
            StringComparison.Ordinal);
    }

    private bool TryRotateRunForChangedLogicalIdentity(StateSnapshot snapshot, string source)
    {
        IReadOnlyDictionary<string, object?>? nextIdentity = ExtractLogicalRunIdentity(snapshot.RawSnapshot)
            ?? ExtractLogicalRunIdentity(snapshot.CanonicalSnapshot);
        if (!TryGetCompleteLogicalRunIdentity(nextIdentity, out string nextLogicalRunId, out string? nextLogicalRunKey))
            return false;

        string? previousLogicalRunId;
        string? previousLogicalRunKey;
        lock (_gate)
        {
            if (_status != CaptureStatus.Capturing
                || !TryGetCompleteLogicalRunIdentity(_logicalRunIdentity, out previousLogicalRunId, out previousLogicalRunKey)
                || LogicalRunIdentityMatches(previousLogicalRunId, previousLogicalRunKey, nextLogicalRunId, nextLogicalRunKey))
            {
                return false;
            }

            _pendingDecisions.Clear();
            _localSequence = 0;
            _envelopeSequence = 0;
            _resumeObservedForCurrentRun = false;
            _pendingRunStart = null;
            _pendingExplicitRunLoadSawRunStarted = false;
            _preserveBranchTrackerForNextLoad = false;
            _branchTracker = new BranchTracker();
            ClearDecisionContextsUnderLock();
            _logicalRunIdentity = nextIdentity;
            RememberStableLogicalRunIdentityUnderLock(nextIdentity!);
            _status = CaptureStatus.Capturing;
            _runId = CreateRunId("run");
        }

        _branchTracker.ObserveState(snapshot.CanonicalStateHash, "logical_identity_changed");
        WriteLifecycle("lifecycle/run_started", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["state"] = BuildStateReference(snapshot, includeSnapshot: true),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["details"] = new Dictionary<string, object?>
            {
                ["classification"] = "logical_run_identity_changed",
                ["previous_logical_run_id"] = previousLogicalRunId,
                ["current_logical_run_id"] = nextLogicalRunId
            }
        });
        return true;
    }

    private static bool LogicalRunIdentityMatches(
        string previousLogicalRunId,
        string? previousLogicalRunKey,
        string nextLogicalRunId,
        string? nextLogicalRunKey)
        => string.Equals(previousLogicalRunId, nextLogicalRunId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(previousLogicalRunKey)
                && string.Equals(previousLogicalRunKey, nextLogicalRunKey, StringComparison.Ordinal));

    private static bool TryGetCompleteLogicalRunIdentity(
        IReadOnlyDictionary<string, object?>? identity,
        out string logicalRunId,
        out string? logicalRunKey)
    {
        logicalRunId = "";
        logicalRunKey = null;
        if (identity == null)
            return false;

        if (!string.Equals(StringValue(identity, "status"), "complete", StringComparison.Ordinal))
            return false;

        logicalRunId = StringValue(identity, "logical_run_id")?.Trim() ?? "";
        logicalRunKey = StringValue(identity, "logical_run_key")?.Trim();
        return !string.IsNullOrWhiteSpace(logicalRunId);
    }

    private void TransitionToNoRunAfterBoundaryUnderLock(bool preserveBranchTrackerForNextLoad)
    {
        _status = CaptureStatus.NoRun;
        _runId = null;
        _logicalRunIdentity = null;
        _pendingDecisions.Clear();
        _localSequence = 0;
        _envelopeSequence = 0;
        _resumeObservedForCurrentRun = false;
        _pendingRunStart = null;
        _pendingExplicitRunLoadSawRunStarted = false;
        _preserveBranchTrackerForNextLoad = preserveBranchTrackerForNextLoad && _branchTracker.KnownStateCount > 0;
        ClearDecisionContextsUnderLock();
        if (!_preserveBranchTrackerForNextLoad)
            _branchTracker = new BranchTracker();
    }

    private static bool IsAbandonSource(string source, IReadOnlyDictionary<string, object?>? details)
    {
        if (source.Contains("abandon", StringComparison.OrdinalIgnoreCase))
            return true;

        if (details == null)
            return false;

        return details.TryGetValue("reason", out object? reason)
            && reason?.ToString()?.Contains("abandon", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool CanCaptureLifecycleForRun(object? runState, string source, out StateSnapshot snapshot)
    {
        snapshot = _snapshotBuilder.Capture(source);
        TryRotateRunForChangedLogicalIdentity(snapshot, source);
        UpdateLogicalRunIdentity(snapshot);

        if (_status == CaptureStatus.UnsupportedRun)
            return false;

        if (_status == CaptureStatus.NoRun)
        {
            var support = RunSupportDetector.Inspect(runState);
            if (!support.IsSupported)
            {
                lock (_gate)
                {
                    _status = CaptureStatus.UnsupportedRun;
                    _runId = CreateRunId("unsupported");
                    _localSequence = 0;
                    _envelopeSequence = 0;
                    _resumeObservedForCurrentRun = false;
                    _pendingRunStart = null;
                    _pendingExplicitRunLoadSawRunStarted = false;
                    _preserveBranchTrackerForNextLoad = false;
                    _logicalRunIdentity = null;
                    _branchTracker = new BranchTracker();
                    ClearDecisionContextsUnderLock();
                }
                WriteUnsupportedRun(support, source);
                return false;
            }

            lock (_gate)
            {
                bool preserveBranchTracker = _preserveBranchTrackerForNextLoad;
                _status = CaptureStatus.Capturing;
                _runId = CreateRunId("run");
                _localSequence = 0;
                _envelopeSequence = 0;
                _resumeObservedForCurrentRun = false;
                _pendingRunStart = null;
                _pendingExplicitRunLoadSawRunStarted = false;
                _preserveBranchTrackerForNextLoad = false;
                _logicalRunIdentity = null;
                ClearDecisionContextsUnderLock();
                if (!preserveBranchTracker)
                    _branchTracker = new BranchTracker();
            }
        }

        return true;
    }

    private StableSnapshotResolution PrepareStableSnapshotForRecord(StateSnapshot snapshot, string stableRecordSource)
    {
        if (TryRotateRunForChangedLogicalIdentity(snapshot, stableRecordSource))
            return StableSnapshotResolution.StartedNewRun;

        UpdateLogicalRunIdentity(snapshot);

        PendingRunStart? pendingRunStart;
        lock (_gate)
        {
            pendingRunStart = _pendingRunStart;
        }

        if (pendingRunStart == null)
            return StableSnapshotResolution.Continue;

        BranchResumeResult preview = _branchTracker.PreviewResume(snapshot.CanonicalStateHash);
        if (preview.Matched)
        {
            BranchResumeResult resume = _branchTracker.ObserveResume(snapshot.CanonicalStateHash);
            bool preserveFutureRunStarted;
            lock (_gate)
            {
                preserveFutureRunStarted = pendingRunStart.Kind == PendingRunStartKind.ExplicitRunLoad
                    && !_pendingExplicitRunLoadSawRunStarted;
                _pendingRunStart = null;
                _pendingExplicitRunLoadSawRunStarted = false;
                _resumeObservedForCurrentRun = preserveFutureRunStarted;
                _preserveBranchTrackerForNextLoad = false;
                ClearDecisionContextsUnderLock();
            }

            WriteRunLoaded(pendingRunStart.Source, snapshot, resume, stableRecordSource, pendingRunStart.Kind);
            return StableSnapshotResolution.Continue;
        }

        if (!IsBranchComparableStableSnapshotSource(stableRecordSource))
            return StableSnapshotResolution.PendingUnmatchedStableSnapshot;

        if (pendingRunStart.Kind == PendingRunStartKind.ExplicitRunLoad)
        {
            BranchResumeResult resume = _branchTracker.ObserveResume(snapshot.CanonicalStateHash);
            bool preserveFutureRunStarted;
            lock (_gate)
            {
                preserveFutureRunStarted = !_pendingExplicitRunLoadSawRunStarted;
                _pendingRunStart = null;
                _pendingExplicitRunLoadSawRunStarted = false;
                _resumeObservedForCurrentRun = preserveFutureRunStarted;
                _preserveBranchTrackerForNextLoad = false;
                ClearDecisionContextsUnderLock();
            }

            WriteRunLoaded(pendingRunStart.Source, snapshot, resume, stableRecordSource, pendingRunStart.Kind);
            return StableSnapshotResolution.Continue;
        }

        lock (_gate)
        {
            _pendingDecisions.Clear();
            _localSequence = 0;
            _envelopeSequence = 0;
            _resumeObservedForCurrentRun = false;
            _pendingRunStart = null;
            _pendingExplicitRunLoadSawRunStarted = false;
            _preserveBranchTrackerForNextLoad = false;
            _branchTracker = new BranchTracker();
            ClearDecisionContextsUnderLock();

            if (!pendingRunStart.Support.IsSupported)
            {
                _status = CaptureStatus.UnsupportedRun;
                _runId = CreateRunId("unsupported");
            }
            else
            {
                _status = CaptureStatus.Capturing;
                _runId = CreateRunId("run");
            }
        }

        if (!pendingRunStart.Support.IsSupported)
        {
            WriteUnsupportedRun(pendingRunStart.Support, pendingRunStart.Source);
            return StableSnapshotResolution.SuppressRecord;
        }

        _branchTracker.ObserveState(snapshot.CanonicalStateHash, "delayed_run_start");
        WriteLifecycle("lifecycle/run_started", new Dictionary<string, object?>
        {
            ["source"] = pendingRunStart.Source,
            ["support"] = pendingRunStart.Support.ToRecord(),
            ["state"] = BuildStateReference(snapshot, includeSnapshot: true),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["details"] = new Dictionary<string, object?>
            {
                ["classification"] = "delayed_until_stable_snapshot",
                ["stable_record_source"] = stableRecordSource,
                ["resume_match"] = ResumeToRecord(preview)
            }
        });

        return StableSnapshotResolution.StartedNewRun;
    }

    private void WriteRunLoaded(
        string source,
        StateSnapshot snapshot,
        BranchResumeResult resume,
        string? classificationSource,
        PendingRunStartKind? classificationKind = null)
    {
        UpdateLogicalRunIdentity(snapshot);
        var payload = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["state"] = BuildStateReference(snapshot, includeSnapshot: true),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["branch_match"] = ResumeToRecord(resume)
        };

        if (classificationSource != null)
        {
            payload["details"] = new Dictionary<string, object?>
            {
                ["classification"] = classificationKind == PendingRunStartKind.ExplicitRunLoad
                    ? "explicit_run_loaded_delayed_until_stable_snapshot"
                    : "run_started_delayed_until_stable_snapshot",
                ["stable_record_source"] = classificationSource
            };
        }

        WriteLifecycle("lifecycle/run_loaded", payload);

        if (resume.Matched)
        {
            WriteLifecycle("lifecycle/branch_matched", new Dictionary<string, object?>
            {
                ["source"] = source,
                ["state"] = BuildStateReference(snapshot, includeSnapshot: false),
                ["branch"] = _branchTracker.BuildMetadata(),
                ["branch_match"] = ResumeToRecord(resume)
            });
        }
    }

    private void WriteBranchDecisionLifecycle(
        string recordType,
        PendingDecision pending,
        StateSnapshot postState,
        BranchDecisionResult branchDecision)
    {
        WriteLifecycle(recordType, new Dictionary<string, object?>
        {
            ["decision_frame_id"] = pending.DecisionFrameId,
            ["decision_source"] = pending.Source,
            ["pre_state"] = BuildStateReference(pending.PreState, includeSnapshot: false),
            ["post_state"] = BuildStateReference(postState, includeSnapshot: false),
            ["branch"] = _branchTracker.BuildMetadata(),
            ["branch_decision"] = BranchDecisionToRecord(branchDecision)
        });
    }

    private void WriteUnsupportedRun(RunSupportResult support, string source)
    {
        var record = BuildEnvelope("lifecycle/unsupported_run");
        record["source"] = source;
        record["role"] = "operational_metadata";
        record["visibility"] = "operational_metadata";
        record["support"] = support.ToRecord();
        record["capture_policy"] = "stopped_for_this_run";
        record["details"] = new Dictionary<string, object?>
        {
            ["prototype_supported_modes"] = new[] { "single_player_normal" },
            ["records_after_this_one"] = "suppressed_until_next_supported_run"
        };
        Enqueue(record);
    }

    private void WriteLifecycle(string recordType, IReadOnlyDictionary<string, object?> payload)
    {
        var record = BuildEnvelope(recordType);
        foreach (var (key, value) in payload)
            record[key] = value;
        Enqueue(record);
    }

    private void WriteOperational(string recordType, IReadOnlyDictionary<string, object?> payload)
    {
        var record = BuildEnvelope(recordType);
        foreach (var (key, value) in payload)
            record[key] = value;
        _writer.Enqueue(null, record);
    }

    private Dictionary<string, object?> BuildEnvelope(string recordType)
    {
        long localSequence = Interlocked.Increment(ref _localSequence);
        string envelopeId = NextId("env");
        var record = new Dictionary<string, object?>
        {
            ["schema_version"] = SchemaVersion,
            ["record_type"] = recordType,
            ["record_id"] = NextId("record"),
            ["envelope_id"] = envelopeId,
            ["installation_id"] = _writer.InstallationId,
            ["run_id"] = _runId,
            ["local_sequence"] = localSequence,
            ["recorded_at_utc"] = DateTimeOffset.UtcNow,
            ["operational_metadata"] = new Dictionary<string, object?>
            {
                ["mod_id"] = Sts2TelemetryMod.ModId,
                ["mod_version"] = Sts2TelemetryMod.Version,
                ["schema_version"] = SchemaVersion,
                ["storage"] = "local_jsonl",
                ["network_upload"] = "background_upload_enabled_by_default",
                ["compression"] = "gzip_fallback"
            }
        };

        if (!string.IsNullOrWhiteSpace(_runId))
        {
            record["capture_session_id"] = _runId;
            record["segment_id"] = _runId;
        }

        if (_logicalRunIdentity != null)
        {
            record["logical_run_identity"] = _logicalRunIdentity;
            if (_logicalRunIdentity.TryGetValue("logical_run_id", out object? logicalRunId))
                record["logical_run_id"] = logicalRunId;
            if (_logicalRunIdentity.TryGetValue("logical_run_key", out object? logicalRunKey))
                record["logical_run_key"] = logicalRunKey;
        }

        return record;
    }

    private static long LocalSequence(IReadOnlyDictionary<string, object?> record)
        => record.TryGetValue("local_sequence", out object? value) && value is long localSequence
            ? localSequence
            : 0;

    private Dictionary<string, object?> BuildStateRole(string role, StateSnapshot snapshot, bool includeSnapshot)
    {
        var result = BuildStateReference(snapshot, includeSnapshot);
        result["role"] = role;
        result["visibility"] = role switch
        {
            "visible_pre_decision" => "player_visible",
            "post_action_observed" => "post_action_observed",
            _ => "operational_metadata"
        };
        return result;
    }

    private static Dictionary<string, object?> BuildStateReference(StateSnapshot snapshot, bool includeSnapshot)
    {
        var result = new Dictionary<string, object?>
        {
            ["state_type"] = snapshot.StateType,
            ["raw_state_hash"] = snapshot.RawStateHash,
            ["canonical_state_hash"] = snapshot.CanonicalStateHash,
            ["hash_projection_version"] = "canonical.v1"
        };

        if (includeSnapshot)
        {
            result["raw_snapshot"] = snapshot.RawSnapshot;
            result["canonical_snapshot"] = snapshot.CanonicalSnapshot;
        }

        return result;
    }

    private static void AddCombatTargetCandidateSidecar(
        IDictionary<string, object?> legalActions,
        StateSnapshot snapshot)
    {
        if (snapshot.StateType != "combat")
            return;

        if (!TryGetDictionary(snapshot.RawSnapshot, "combat", out var combat)
            || !combat.TryGetValue("target_candidates", out object? targetCandidates)
            || targetCandidates == null)
        {
            return;
        }

        legalActions["target_candidates"] = targetCandidates;
    }

    private static Dictionary<string, object?>? BuildCombatProcessRecord(
        PendingDecision pending,
        StateSnapshot? postState)
    {
        if (pending.PreState.StateType != "combat" && postState?.StateType != "combat")
            return null;

        Dictionary<string, object?> pre = BuildCombatProcessState(pending.PreState);
        Dictionary<string, object?>? post = postState == null ? null : BuildCombatProcessState(postState);

        return new Dictionary<string, object?>
        {
            ["role"] = "combat_decision_process",
            ["visibility"] = "player_visible",
            ["decision_source"] = pending.Source,
            ["selected_action_type"] = StringValue(pending.NormalizedTypedActionKey, "action_type"),
            ["selected_action_runtime_type_name"] = StringValue(pending.SelectedAction, "runtime_type_name"),
            ["marker_status"] = BuildCombatProcessMarkerStatus(pre, post),
            ["pre"] = pre,
            ["post"] = post
        };
    }

    private static Dictionary<string, object?> BuildCombatProcessState(StateSnapshot snapshot)
    {
        var result = new Dictionary<string, object?>();
        if (!TryGetDictionary(snapshot.RawSnapshot, "combat", out var combat))
            return result;

        CopyIfPresent(result, combat, "round");
        CopyIfPresent(result, combat, "current_side");
        CopyIfPresent(result, combat, "is_in_progress");
        CopyIfPresent(result, combat, "is_play_phase");
        CopyIfPresent(result, combat, "player_actions_disabled");

        if (TryGetDictionary(combat, "process", out var process))
        {
            CopyIfPresent(result, process, "turn_index");
            CopyIfPresent(result, process, "turn_side");
            CopyIfPresent(result, process, "phase");
            CopyIfPresent(result, process, "action_step");
            CopyIfPresent(result, process, "action_index");
        }

        return result;
    }

    private static Dictionary<string, object?> BuildCombatProcessMarkerStatus(
        IReadOnlyDictionary<string, object?> pre,
        IReadOnlyDictionary<string, object?>? post)
        => new()
        {
            ["turn"] = HasCombatProcessValue(pre, post, "turn_index", "round") ? "present" : "unavailable",
            ["phase"] = HasCombatProcessValue(pre, post, "phase", "turn_side", "current_side", "is_play_phase")
                ? "present"
                : "unavailable",
            ["action_step"] = HasCombatProcessValue(pre, post, "action_step", "action_index") ? "present" : "unavailable"
        };

    private static bool HasCombatProcessValue(
        IReadOnlyDictionary<string, object?> pre,
        IReadOnlyDictionary<string, object?>? post,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (pre.TryGetValue(key, out object? preValue) && preValue != null)
                return true;

            if (post != null && post.TryGetValue(key, out object? postValue) && postValue != null)
                return true;
        }

        return false;
    }

    private static void AddDecisionTimingMetadata(
        IDictionary<string, object?> record,
        DecisionTiming timing)
    {
        if (!record.TryGetValue("operational_metadata", out object? value)
            || value is not IDictionary<string, object?> operationalMetadata)
        {
            return;
        }

        var decisionTiming = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, elapsed) in timing.ElapsedMicroseconds)
            decisionTiming[key] = elapsed;

        decisionTiming["unit"] = "microseconds";
        decisionTiming["clock"] = "Stopwatch.GetTimestamp";
        decisionTiming["enqueue_mode"] = "background_jsonl_writer_queue_handoff";
        operationalMetadata["decision_timing"] = decisionTiming;
    }

    private static Dictionary<string, object?> ResumeToRecord(BranchResumeResult resume)
        => new()
        {
            ["matched"] = resume.Matched,
            ["forked"] = resume.Forked,
            ["pending_divergence"] = resume.PendingDivergence,
            ["branch_id"] = resume.BranchId,
            ["matched_node_id"] = resume.MatchedNodeId,
            ["parent_canonical_state_hash"] = resume.ParentCanonicalStateHash,
            ["reason"] = resume.Reason
        };

    private static Dictionary<string, object?> BranchDecisionToRecord(BranchDecisionResult decision)
        => new()
        {
            ["forked"] = decision.Forked,
            ["divergence_unknown"] = decision.DivergenceUnknown,
            ["trajectory_replayed"] = decision.TrajectoryReplayed,
            ["branch_id"] = decision.BranchId,
            ["parent_node_id"] = decision.ParentNodeId,
            ["parent_canonical_state_hash"] = decision.ParentCanonicalStateHash,
            ["post_canonical_state_hash"] = decision.PostCanonicalStateHash,
            ["selected_action_canonical_hash"] = decision.SelectedActionCanonicalHash,
            ["matched_decision_frame_id"] = decision.MatchedDecisionFrameId,
            ["matched_child_node_id"] = decision.MatchedChildNodeId,
            ["reason"] = decision.Reason
        };

    private void UpdateLogicalRunIdentity(StateSnapshot snapshot)
    {
        IReadOnlyDictionary<string, object?>? observedIdentity = ExtractLogicalRunIdentity(snapshot.RawSnapshot)
            ?? ExtractLogicalRunIdentity(snapshot.CanonicalSnapshot);

        lock (_gate)
        {
            _logicalRunIdentity = ResolveEffectiveLogicalRunIdentityUnderLock(observedIdentity);
            RememberStableLogicalRunIdentityUnderLock(_logicalRunIdentity);
        }
    }

    private IReadOnlyDictionary<string, object?> ResolveEffectiveLogicalRunIdentityUnderLock(
        IReadOnlyDictionary<string, object?>? observedIdentity)
    {
        if (observedIdentity == null)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "incomplete",
                ["identity_quality"] = "incomplete",
                ["reason"] = "snapshot_missing_logical_run_identity"
            };
        }

        if (!IsDegradedStartTimeIdentity(observedIdentity)
            || !TryFindStableIdentityForDegradedStartTimeUnderLock(observedIdentity, out var stableIdentity))
        {
            return observedIdentity;
        }

        return BuildReconciledLogicalRunIdentity(observedIdentity, stableIdentity);
    }

    private void RememberStableLogicalRunIdentityUnderLock(IReadOnlyDictionary<string, object?> identity)
    {
        if (!IsStableCompleteLogicalRunIdentity(identity)
            || !TryGetCompleteLogicalRunIdentity(identity, out string logicalRunId, out _))
        {
            return;
        }

        _recentStableLogicalRunIdentities.RemoveAll(candidate =>
            TryGetCompleteLogicalRunIdentity(candidate, out string candidateLogicalRunId, out _)
            && string.Equals(candidateLogicalRunId, logicalRunId, StringComparison.Ordinal));
        _recentStableLogicalRunIdentities.Insert(0, identity);
        if (_recentStableLogicalRunIdentities.Count > MaxRecentStableLogicalRunIdentities)
            _recentStableLogicalRunIdentities.RemoveRange(
                MaxRecentStableLogicalRunIdentities,
                _recentStableLogicalRunIdentities.Count - MaxRecentStableLogicalRunIdentities);
    }

    private bool TryFindStableIdentityForDegradedStartTimeUnderLock(
        IReadOnlyDictionary<string, object?> observedIdentity,
        out IReadOnlyDictionary<string, object?> stableIdentity)
    {
        if (_logicalRunIdentity != null
            && IsStableCompleteLogicalRunIdentity(_logicalRunIdentity)
            && LogicalRunIdentityMatchesIgnoringStartTime(observedIdentity, _logicalRunIdentity))
        {
            stableIdentity = _logicalRunIdentity;
            return true;
        }

        foreach (IReadOnlyDictionary<string, object?> candidate in _recentStableLogicalRunIdentities)
        {
            if (LogicalRunIdentityMatchesIgnoringStartTime(observedIdentity, candidate))
            {
                stableIdentity = candidate;
                return true;
            }
        }

        stableIdentity = new Dictionary<string, object?>();
        return false;
    }

    private static bool IsDegradedStartTimeIdentity(IReadOnlyDictionary<string, object?> identity)
        => string.Equals(StringValue(identity, "status"), "degraded", StringComparison.Ordinal)
            && string.Equals(StringValue(identity, "degraded_reason"), "start_time_zero_loaded_save_identity", StringComparison.Ordinal);

    private static bool IsStableCompleteLogicalRunIdentity(IReadOnlyDictionary<string, object?> identity)
        => string.Equals(StringValue(identity, "status"), "complete", StringComparison.Ordinal)
            && !string.Equals(StringValue(identity, "identity_quality"), "reconciled_degraded_start_time", StringComparison.Ordinal)
            && TryGetCompleteLogicalRunIdentity(identity, out _, out _);

    private static bool LogicalRunIdentityMatchesIgnoringStartTime(
        IReadOnlyDictionary<string, object?> observedIdentity,
        IReadOnlyDictionary<string, object?> stableIdentity)
    {
        if (!TryGetIdentityFields(observedIdentity, out var observedFields)
            || !TryGetIdentityFields(stableIdentity, out var stableFields))
        {
            return false;
        }

        foreach (string field in new[] { "seed", "character", "ascension", "game_mode", "modifiers" })
        {
            if (!observedFields.TryGetValue(field, out object? observedValue)
                || !stableFields.TryGetValue(field, out object? stableValue)
                || !IdentityFieldValuesEqual(observedValue, stableValue))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, object?> BuildReconciledLogicalRunIdentity(
        IReadOnlyDictionary<string, object?> observedIdentity,
        IReadOnlyDictionary<string, object?> stableIdentity)
    {
        var effective = new Dictionary<string, object?>(stableIdentity, StringComparer.Ordinal)
        {
            ["status"] = "complete",
            ["identity_quality"] = "reconciled_degraded_start_time",
            ["identity_reconciliation"] = new Dictionary<string, object?>
            {
                ["status"] = "reconciled",
                ["reason"] = "degraded_start_time_zero_matches_recent_stable_identity",
                ["source"] = "recent_stable_logical_run_identity",
                ["matched_fields"] = new[] { "seed", "character", "ascension", "game_mode", "modifiers" },
                ["degraded_fields"] = new[] { "start_time" },
                ["observed_identity"] = observedIdentity,
                ["effective_logical_run_id"] = StringValue(stableIdentity, "logical_run_id"),
                ["effective_logical_run_key"] = StringValue(stableIdentity, "logical_run_key")
            }
        };

        return effective;
    }

    private static bool TryGetIdentityFields(
        IReadOnlyDictionary<string, object?> identity,
        out IReadOnlyDictionary<string, object?> fields)
    {
        if (identity.TryGetValue("fields", out object? value)
            && TryCoerceDictionary(value, out fields))
        {
            return true;
        }

        fields = new Dictionary<string, object?>();
        return false;
    }

    private static bool IdentityFieldValuesEqual(object? left, object? right)
        => string.Equals(
            TelemetryHash.HashCanonical(left),
            TelemetryHash.HashCanonical(right),
            StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, object?>? ExtractLogicalRunIdentity(
        IReadOnlyDictionary<string, object?> snapshot)
    {
        if (!TryGetDictionary(snapshot, "run", out var run))
            return null;
        return TryGetDictionary(run, "logical_run_identity", out var identity)
            ? identity
            : null;
    }

    private static bool TryGetDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key,
        out IReadOnlyDictionary<string, object?> dictionary)
    {
        if (source.TryGetValue(key, out object? value)
            && value is IReadOnlyDictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        dictionary = new Dictionary<string, object?>();
        return false;
    }

    private void ObserveRelicSignalsForCurrentPlayer(object? runState = null, object? player = null)
    {
        if (!IsCapturing)
            return;

        object? effectiveRunState = runState ?? _snapshotBuilder.SafeRunState();
        object? effectivePlayer = player ?? _snapshotBuilder.GetLocalPlayer(effectiveRunState);
        _relicSignalObserver.ObservePlayerRelics(effectivePlayer);
    }

    private void ResetObservedRelicSignals()
        => _relicSignalObserver.Reset();

    private static void CopyIfMissing(IDictionary<string, object?> target, string key, object? value)
    {
        if (value == null)
            return;

        if (target.TryGetValue(key, out object? existing) && existing != null)
            return;

        target[key] = value;
    }

    private void Enqueue(IReadOnlyDictionary<string, object?> record)
        => _writer.Enqueue(_runId, record);

    private string NextId(string prefix)
        => $"{prefix}-{Interlocked.Increment(ref _envelopeSequence):000000000}";

    private static string CreateRunId(string prefix)
        => $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    private static string ActionKey(GameAction action)
        => $"action:{RuntimeHelpers.GetHashCode(action)}:{action.GetType().FullName}";

    private static bool ShouldCaptureLifecyclePlayerMetadata(string recordType)
        => recordType is not (
            "lifecycle/room_entered"
            or "lifecycle/room_exited"
            or "lifecycle/room_entered_settled"
            or "lifecycle/room_exited_settled");

    private static bool IsBranchComparableStableSnapshotSource(string stableRecordSource)
        => stableRecordSource is "action_executor:pre";

    private bool ShouldDelayExplicitRunLoadUntilStableSnapshot(StateSnapshot snapshot)
        => _branchTracker.KnownStateCount > 0
            && snapshot.StateType is not (
                "combat"
                or "event"
                or "shop"
                or "rest_site"
                or "treasure"
                or "map");

    private static bool ShouldIndexStableSnapshotForResumeMatching(string recordType, StateSnapshot snapshot)
        => recordType is "lifecycle/room_entered_settled"
            && snapshot.StateType is "event" or "shop" or "rest_site" or "treasure" or "map";

    private static bool ShouldEmitDecisionContext(string recordType, StateSnapshot snapshot)
        => recordType is "lifecycle/room_entered_settled"
            && snapshot.StateType is "event" or "shop" or "rest_site" or "treasure" or "map" or "rewards" or "card_reward";

    private static bool IsNonCombatDecisionSurface(string? stateType)
        => stateType is "event"
            or "shop"
            or "rest_site"
            or "treasure"
            or "map"
            or "rewards"
            or "card_reward"
            or "relic_select"
            or "bundle_select";

    private static bool ShouldClearDecisionContextOnLifecycleSignal(string recordType)
        => recordType is "lifecycle/room_entered"
            or "lifecycle/room_exited"
            or "lifecycle/act_entered"
            or "lifecycle/run_suspended"
            or "lifecycle/run_ended";

    private void ClearDecisionContexts()
    {
        lock (_gate)
            ClearDecisionContextsUnderLock();
    }

    private void ClearDecisionContextsUnderLock()
    {
        _latestDecisionContextByStateType.Clear();
        ClearPendingShopCardAttemptUnderLock();
        _recentShopCompletion = null;
    }

    private static string? StateTypeForSignal(IReadOnlyDictionary<string, object?> signal)
    {
        string? explicitStateType = StringValue(signal, "state_type");
        if (IsNonCombatDecisionSurface(explicitStateType))
            return explicitStateType;

        string? actionType = StringValue(signal, "action_type");
        string? source = StringValue(signal, "source");
        string? runtimeTypeName = StringValue(signal, "runtime_type_name");

        string? actionStateType = StateTypeForActionType(actionType);
        if (actionStateType != null)
            return actionStateType;

        if (source?.Contains("card_reward", StringComparison.OrdinalIgnoreCase) == true)
            return "card_reward";
        if (source?.Contains("card_selection.skip", StringComparison.OrdinalIgnoreCase) == true)
            return "card_reward";
        if (actionType is "select_card" or "skip_card_selection")
            return "card_select";

        if (source?.Contains("shop", StringComparison.OrdinalIgnoreCase) == true
            || source?.Contains("merchant", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "shop";
        }

        if (source?.Contains("event", StringComparison.OrdinalIgnoreCase) == true)
            return "event";
        if (source?.Contains("rest", StringComparison.OrdinalIgnoreCase) == true)
            return "rest_site";
        if (source?.Contains("map", StringComparison.OrdinalIgnoreCase) == true)
            return "map";
        if (runtimeTypeName?.Contains("MapCoordAction", StringComparison.Ordinal) == true)
            return "map";
        if (source?.Contains("treasure", StringComparison.OrdinalIgnoreCase) == true
            || source?.Contains("relic", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "treasure";
        }
        if (source?.Contains("reward", StringComparison.OrdinalIgnoreCase) == true)
            return "rewards";

        return null;
    }

    private static string? StateTypeForActionType(string? actionType)
        => actionType switch
        {
            "buy_shop_card" or "buy_shop_relic" or "buy_shop_potion" or "remove_card_at_shop" or "shop_purchase" => "shop",
            "choose_event_option" or "proceed_event" => "event",
            "choose_rest_option" => "rest_site",
            "choose_map_node" or "cancel_map_vote" => "map",
            "choose_treasure_relic" or "skip_treasure_relic" => "treasure",
            "choose_relic_select" or "skip_relic_select" => "relic_select",
            "choose_card_bundle" => "bundle_select",
            "claim_reward" or "skip_reward" => "rewards",
            "choose_reward_card" or "skip_card_reward" or "reroll_card_reward" or "sacrifice_reward_card" => "card_reward",
            _ => null
        };

    private static string? StringValue(IReadOnlyDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static bool TryCoerceDictionary(
        object? value,
        out IReadOnlyDictionary<string, object?> dictionary)
    {
        if (value is IReadOnlyDictionary<string, object?> typed)
        {
            dictionary = typed;
            return true;
        }

        if (value is IDictionary<string, object?> mutable)
        {
            dictionary = new Dictionary<string, object?>(mutable, StringComparer.Ordinal);
            return true;
        }

        dictionary = new Dictionary<string, object?>();
        return false;
    }

    private static void AddPendingResumeClassificationDetails(
        IDictionary<string, object?> details,
        StableSnapshotResolution resolution,
        string stableRecordSource)
    {
        if (resolution != StableSnapshotResolution.PendingUnmatchedStableSnapshot)
            return;

        details["resume_classification"] = "pending_unmatched_stable_snapshot";
        details["stable_record_source"] = stableRecordSource;
        details["classification_policy"] =
            "unmatched_non_decision_snapshot_does_not_reset_run_or_branch_tracker";
    }

    private static Dictionary<string, object?> ExceptionToRecord(Exception exception)
        => new()
        {
            ["type"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["stack_trace"] = exception.StackTrace
        };

    private static void CopyIfPresent(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (source.TryGetValue(key, out object? value) && value != null)
            target[key] = value;
    }

    private bool IsCapturing => _status == CaptureStatus.Capturing;

    private enum CaptureStatus
    {
        NoRun,
        Capturing,
        UnsupportedRun
    }

    private enum StableSnapshotResolution
    {
        Continue,
        PendingUnmatchedStableSnapshot,
        StartedNewRun,
        SuppressRecord
    }

    private enum PendingRunStartKind
    {
        DelayedRunStart,
        ExplicitRunLoad
    }

    private sealed record PendingRunStart(
        string Source,
        RunSupportResult Support,
        PendingRunStartKind Kind);

    private sealed record PendingShopCardAttempt(
        string Source,
        long LocalSequence,
        string? ActionType,
        string? Category,
        string? Id,
        string? CardId,
        string? RelicId,
        string? PotionId,
        string? RemovalId,
        string IdentityKey,
        IReadOnlyDictionary<string, object?> NormalizedTypedActionKey,
        int CompletionSignalCount);

    private sealed record RecentShopCompletion(
        string IdentityKey,
        long LocalSequence);

    private sealed record DecisionContextReference(
        string DecisionContextId,
        string ContextSource,
        string Source,
        string StateType,
        string RawStateHash,
        string CanonicalStateHash,
        IReadOnlyList<LegalActionReference> LegalActions,
        long LocalSequence,
        bool HasUsableLegalActions,
        string LegalActionReadiness);

    private sealed record LegalActionReference(
        int Index,
        string ActionType,
        IReadOnlyDictionary<string, object?> NormalizedTypedActionKey,
        string CanonicalActionHash);

    private sealed record DecisionContextMatch(
        bool Matched,
        string MatchPolicy,
        string Reason,
        string? SelectedActionCanonicalHash,
        int? MatchedLegalActionIndex,
        string? MatchedActionType,
        string? MatchedLegalActionHash)
    {
        public static DecisionContextMatch CreateMatched(
            string matchPolicy,
            string selectedActionCanonicalHash,
            LegalActionReference legalAction)
            => new(
                true,
                matchPolicy,
                matchPolicy,
                selectedActionCanonicalHash,
                legalAction.Index,
                legalAction.ActionType,
                legalAction.CanonicalActionHash);

        public static DecisionContextMatch Unmatched(string reason, string? selectedActionCanonicalHash)
            => new(
                false,
                "latest_context_by_surface",
                reason,
                selectedActionCanonicalHash,
                null,
                null,
                null);

        public Dictionary<string, object?> ToRecord()
            => new()
            {
                ["matched"] = Matched,
                ["match_policy"] = MatchPolicy,
                ["reason"] = Reason,
                ["selected_action_canonical_hash"] = SelectedActionCanonicalHash,
                ["matched_legal_action_index"] = MatchedLegalActionIndex,
                ["matched_action_type"] = MatchedActionType,
                ["matched_legal_action_hash"] = MatchedLegalActionHash
            };
    }

    private sealed record PendingDecision(
        string PendingKey,
        string DecisionFrameId,
        string Source,
        StateSnapshot PreState,
        IReadOnlyList<Dictionary<string, object?>> LegalActions,
        IReadOnlyDictionary<string, object?> SelectedAction,
        IReadOnlyDictionary<string, object?> NormalizedTypedActionKey,
        string SelectedActionRawHash,
        string SelectedActionCanonicalHash,
        DecisionTiming Timing
    );

    private sealed class DecisionTiming
    {
        private readonly SortedDictionary<string, object?> _elapsedMicroseconds = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, object?> ElapsedMicroseconds => _elapsedMicroseconds;

        public void RecordElapsedMicroseconds(string key, long startTimestamp)
            => _elapsedMicroseconds[key] = ElapsedMicrosecondsSince(startTimestamp);

        private static long ElapsedMicrosecondsSince(long startTimestamp)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            double elapsed = elapsedTicks * 1_000_000d / Stopwatch.Frequency;
            return Math.Max(0L, (long)Math.Round(elapsed, MidpointRounding.AwayFromZero));
        }
    }
}
