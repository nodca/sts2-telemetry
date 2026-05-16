using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Telemetry;

[ModInitializer(nameof(Initialize))]
public static class Sts2TelemetryMod
{
    public const string ModId = "com.wcn.sts2.telemetry";
    public const string Version = "0.1.17";
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;
    private const int DecisionContextMaxSettleFrames = 3;
    private const int DecisionContextMaxActionExecutorDeferrals = 2;
    private static readonly string[] RewardContextStateTypes = { "rewards" };
    private static readonly string[] CardRewardContextStateTypes = { "card_reward" };
    private static readonly string[] EventContextStateTypes = { "event" };
    private static readonly string[] TreasureContextStateTypes = { "treasure" };
    private static readonly string[] ShopContextStateTypes = { "shop" };
    private static readonly string[] RelicSelectContextStateTypes = { "relic_select" };
    private static readonly string[] BundleSelectContextStateTypes = { "bundle_select" };

    private static readonly object Gate = new();
    private static Harmony? _harmony;
    private static bool _initialized;
    private static bool _actionExecutorSubscribed;
    private static object? _actionExecutor;
    private static int _actionExecutorInFlightCount;
    private static EventInfo? _playerChoiceReceivedEvent;
    private static Delegate? _playerChoiceReceivedHandler;
    private static object? _playerChoiceSynchronizer;
    private static readonly Dictionary<int, RecentRelicSignal> RecentRelicFlashSignals = new();
    private static long _traceSwitchCheckedAtMilliseconds;
    private static bool _traceSwitchCachedValue;
    private static Func<string, Action, bool>? _nextFrameSchedulerForTests;
    private static TelemetryUploadService? _uploadService;
    private static TelemetryUpdateService? _updateService;

    public static TelemetryRecorder Recorder { get; private set; } = TelemetryRecorder.CreateDefault();

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
            _initialized = true;
        }

        RecordCrashBreadcrumb("mod.initialize", "start");
        try
        {
            TraceDiagnostic("initialize:record_mod_initialized:start");
            Recorder.RecordModInitialized();
            TraceDiagnostic("initialize:upload_service:start");
            TryStartUploadService();
            TraceDiagnostic("initialize:update_service:start");
            TryStartUpdateService();
            TraceDiagnostic("initialize:subscribe_public_events:start");
            SubscribePublicEvents();
            RecordCrashBreadcrumb("mod.initialize", "public_event_subscription_complete");
            TraceDiagnostic("initialize:apply_harmony:start");
            TryApplyHarmonyPatches();
            RecordCrashBreadcrumb("mod.initialize", "harmony_patch_install_complete");
            TraceDiagnostic("initialize:complete");
            RecordCrashBreadcrumb("mod.initialize", "complete");
            GD.Print("[STS2 Telemetry] local JSONL recorder initialized; background upload queue enabled by default");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] initialization failed: {ex}");
        }
    }

    internal static void RunNextFrame(Action action)
        => RunNextFrame("next_frame", action);

    internal static void RunNextFrame(string source, Action action)
    {
        if (!TryScheduleNextFrame(source, action))
            GuardTelemetryCallback(source, action);
    }

    private static bool TryScheduleNextFrame(string source, Action action)
    {
        Func<string, Action, bool>? schedulerForTests = _nextFrameSchedulerForTests;
        if (schedulerForTests != null)
            return schedulerForTests(source, action);

        try
        {
            if (!IsLikelyGodotGameProcess())
                return false;

            if (Engine.GetMainLoop() is not SceneTree tree)
                return false;

            Action? callback = null;
            callback = () =>
            {
                if (callback != null)
                    tree.ProcessFrame -= callback;
                GuardTelemetryCallback(source, action);
            };
            tree.ProcessFrame += callback;
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] failed to schedule next-frame telemetry callback ({source}): {ex.Message}");
            return false;
        }
    }

    private static bool IsLikelyGodotGameProcess()
    {
        string processName = Path.GetFileNameWithoutExtension(System.Environment.ProcessPath) ?? "";
        return string.Equals(processName, "SlayTheSpire2", StringComparison.OrdinalIgnoreCase);
    }

    internal static Func<string, Action, bool>? ReplaceNextFrameSchedulerForTests(Func<string, Action, bool>? scheduler)
    {
        Func<string, Action, bool>? previous = _nextFrameSchedulerForTests;
        _nextFrameSchedulerForTests = scheduler;
        return previous;
    }

    internal static void OnSavedRunLoadedFromPatch(string source)
    {
        RecordCrashBreadcrumb(source, "enter");
        GuardTelemetryCallback(source, () =>
        {
            TelemetryMainMenuUploadUi.Hide();
            ClearChoiceCaches();
            object? runState = SafeCurrentRunState();
            RecordCrashBreadcrumb(source, "before_recorder");
            Recorder.OnRunLoaded(runState, source);
            RecordCrashBreadcrumb(source, "after_recorder");
            TrySubscribeActionExecutor();
            TrySubscribePlayerChoiceSynchronizer();
        });
    }

    internal static void OnSaveObservedFromPatch(string source)
        => GuardTelemetryCallback(source, () =>
        {
            Recorder.RecordSaveObserved(source);
            RequestUploadSync();
        });

    internal static void OnSavePreviewFromPatch(string source)
        => GuardTelemetryCallback(source, () => Recorder.RecordSavePreview(source));

    internal static void OnRunEndedFromPatch(string source, bool? isVictory)
        => GuardTelemetryCallback(source, () =>
        {
            Recorder.OnRunEnded(source, isVictory);
            RequestUploadSync();
        });

    internal static void OnRunSuspendedFromPatch(string source, IReadOnlyDictionary<string, object?> details)
        => GuardTelemetryCallback(source, () =>
        {
            Recorder.OnRunSuspendedOrCleanedUp(source, details);
            RequestUploadSync();
        });

    internal static void OnMainMenuReadyFromPatch(string source)
        => GuardTelemetryCallback(source, () =>
        {
            bool shown = TelemetryMainMenuUploadUi.Show(Recorder.TelemetryBaseDirectory);
            if (!shown)
                GD.Print("[STS2 Telemetry] main menu upload status UI unavailable; see upload/status.json under the telemetry data directory");
        });

    internal static void OnMainMenuExitFromPatch(string source)
        => GuardTelemetryCallback(source, TelemetryMainMenuUploadUi.Hide);

    internal static void OnUiDecisionFromPatch(string source, object? instance, object?[] args)
        => GuardTelemetryCallback(source, () =>
        {
            if (IsRewardSelectionSignal(source))
                RewardChoiceCache.Shared.CaptureCardReward(instance, source);
            if (IsCardRewardSelectionSignal(source))
                Recorder.EnsureCardRewardDecisionContextForSelectionSignal(source);
            Recorder.RecordPatchedUiSignal(source, instance, args);
            if (IsEventOptionSelectionSignal(source))
            {
                Recorder.ClearDecisionContextForSurface("event");
                ScheduleEventDecisionContextRefresh(source, attempt: 1);
            }
        });

    internal static void OnRewardsGeneratedFromPatch(string source, object? rewardsSet, object? rewards)
        => GuardTelemetryCallback(source, () =>
        {
            RewardChoiceCache.Shared.CaptureRewards(rewardsSet, rewards, source);
            RewardChoiceCache.Shared.PreserveRewardsAcrossNextRoomEntered();
            ScheduleRewardDecisionContext(source, attempt: 1);
        });

    internal static void OnCardRewardOpenedFromPatch(string source, object? reward)
        => GuardTelemetryCallback(source, () =>
        {
            RewardChoiceCache.Shared.CaptureCardReward(reward, source);
            ScheduleDecisionContext(
                triggerSource: source,
                contextSource: "runtime.card_reward.on_select",
                callbackSourcePrefix: "runtime.card_reward.on_select.card_reward_context_settled",
                allowedStateTypes: CardRewardContextStateTypes,
                attempt: 1);
        });

    internal static void OnRelicSelectOpenedFromPatch(string source, object? player, object? relics)
        => GuardTelemetryCallback(source, () =>
        {
            if (!SelectionChoiceCache.Shared.CaptureRelicSelect(player, relics, source))
                return;

            ScheduleDecisionContext(
                triggerSource: source,
                contextSource: "runtime.relic_select.choose_a_relic",
                callbackSourcePrefix: "runtime.relic_select.choose_a_relic.relic_select_context_settled",
                allowedStateTypes: RelicSelectContextStateTypes,
                attempt: 1);
        });

    internal static void OnBundleSelectOpenedFromPatch(string source, object? player, object? bundles)
        => GuardTelemetryCallback(source, () =>
        {
            if (!SelectionChoiceCache.Shared.CaptureBundleSelect(player, bundles, source))
                return;

            ScheduleDecisionContext(
                triggerSource: source,
                contextSource: "runtime.bundle_select.choose_a_bundle",
                callbackSourcePrefix: "runtime.bundle_select.choose_a_bundle.bundle_select_context_settled",
                allowedStateTypes: BundleSelectContextStateTypes,
                attempt: 1);
        });

    internal static void OnRelicTriggeredFromPatch(string source, object? relic, object? targets)
        => GuardTelemetryCallback(source, () =>
        {
            if (ShouldSuppressRelicSignal(source, relic, targets))
                return;

            Recorder.RecordRelicTriggerSignal(source, relic, targets);
            RememberRelicSignal(source, relic, targets);
        });

    internal static void OnRelicTriggeredFromObservedRuntimeSignal(string source, object? relic, object? targets)
        => GuardTelemetryCallback(source, () =>
        {
            if (ShouldSuppressRelicSignal(source, relic, targets))
                return;

            Recorder.RecordRelicTriggerSignal(source, relic, targets);
            RememberRelicSignal(source, relic, targets);
        });

    internal static void OnShopPurchaseCompletedFromPatch(string source, object? entry, object?[] args)
        => GuardTelemetryCallback(source, () =>
        {
            bool trainableCompletion = Recorder.RecordPatchedUiSignal(source, entry, args);
            if (!trainableCompletion)
                return;

            ScheduleDecisionContext(
                triggerSource: source,
                contextSource: "runtime.shop.purchase_completed.refresh",
                callbackSourcePrefix: "runtime.shop.purchase_completed.shop_context_refreshed",
                allowedStateTypes: ShopContextStateTypes,
                attempt: 1);
        });

    private static bool ShouldSubscribeRoomExitedPublicEvent()
        => false;

    private static void SubscribePublicEvents()
    {
        RunManager.Instance.RunStarted += OnRunStarted;
        RunManager.Instance.ActEntered += OnActEntered;
        RunManager.Instance.RoomEntered += OnRoomEntered;
        if (ShouldSubscribeRoomExitedPublicEvent())
            RunManager.Instance.RoomExited += OnRoomExited;

        TrySubscribeCombatEvents();
        TrySubscribeRunSaveEvents();
        TrySubscribeActionExecutor();
        TrySubscribePlayerChoiceSynchronizer();
    }

    private static void TrySubscribeCombatEvents()
    {
        try
        {
            CombatManager.Instance.CombatSetUp += _ => GuardTelemetryCallback(
                "combat_manager.combat_setup",
                () => Recorder.RecordLifecycle("lifecycle/combat_setup", "combat_manager"));
            CombatManager.Instance.CombatEnded += _ => GuardTelemetryCallback(
                "combat_manager.combat_ended",
                () => Recorder.RecordLifecycle("lifecycle/combat_ended", "combat_manager"));
            CombatManager.Instance.CombatWon += _ => GuardTelemetryCallback(
                "combat_manager.combat_won",
                () => Recorder.RecordLifecycle("lifecycle/combat_won", "combat_manager"));
            CombatManager.Instance.TurnStarted += _ => GuardTelemetryCallback(
                "combat_manager.turn_started",
                () => Recorder.RecordLifecycle("lifecycle/combat_turn_started", "combat_manager"));
            CombatManager.Instance.TurnEnded += _ => GuardTelemetryCallback(
                "combat_manager.turn_ended",
                () => Recorder.RecordLifecycle("lifecycle/combat_turn_ended", "combat_manager"));
            CombatManager.Instance.PlayerActionsDisabledChanged += _ =>
                GuardTelemetryCallback(
                    "combat_manager.player_actions_disabled_changed",
                    () => Recorder.RecordLifecycle("lifecycle/player_actions_disabled_changed", "combat_manager"));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] combat event subscription degraded: {ex.Message}");
        }
    }

    private static void TrySubscribeRunSaveEvents()
    {
        try
        {
            Type? type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager");
            object? instance = ReflectionUtil.GetStaticMemberValue("MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager", "Instance");
            EventInfo? saved = type?.GetEvent("Saved", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo? method = typeof(Sts2TelemetryMod).GetMethod(nameof(OnRunSaveSaved), BindingFlags.Static | BindingFlags.NonPublic);
            if (instance != null && saved?.EventHandlerType != null && method != null)
            {
                Delegate handler = Delegate.CreateDelegate(saved.EventHandlerType, method);
                saved.AddEventHandler(instance, handler);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] save event subscription degraded: {ex.Message}");
        }
    }

    private static void TrySubscribeActionExecutor()
    {
        try
        {
            object? executor = ReflectionUtil.GetMemberValue(RunManager.Instance, "ActionExecutor");
            if (executor == null || ReferenceEquals(executor, _actionExecutor))
                return;

            if (_actionExecutorSubscribed && _actionExecutor is ActionExecutor oldExecutor)
            {
                oldExecutor.BeforeActionExecuted -= OnBeforeActionExecuted;
                oldExecutor.AfterActionExecuted -= OnAfterActionExecuted;
            }

            if (executor is ActionExecutor typedExecutor)
            {
                typedExecutor.BeforeActionExecuted += OnBeforeActionExecuted;
                typedExecutor.AfterActionExecuted += OnAfterActionExecuted;
                _actionExecutor = typedExecutor;
                _actionExecutorSubscribed = true;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] action executor subscription degraded: {ex.Message}");
        }
    }

    private static void TrySubscribePlayerChoiceSynchronizer()
    {
        try
        {
            object? synchronizer = ReflectionUtil.GetMemberValue(RunManager.Instance, "PlayerChoiceSynchronizer")
                ?? ReflectionUtil.GetMemberValue(SafeCurrentRunState(), "PlayerChoiceSynchronizer");
            if (synchronizer == null || ReferenceEquals(synchronizer, _playerChoiceSynchronizer))
                return;

            if (_playerChoiceSynchronizer != null && _playerChoiceReceivedEvent != null && _playerChoiceReceivedHandler != null)
                _playerChoiceReceivedEvent.RemoveEventHandler(_playerChoiceSynchronizer, _playerChoiceReceivedHandler);

            EventInfo? choiceReceived = synchronizer.GetType()
                .GetEvent("PlayerChoiceReceived", BindingFlags.Instance | BindingFlags.Public);
            if (choiceReceived?.EventHandlerType == null)
                return;

            Delegate? handler = BuildPlayerChoiceReceivedHandler(choiceReceived.EventHandlerType);
            if (handler == null)
                return;

            choiceReceived.AddEventHandler(synchronizer, handler);
            _playerChoiceSynchronizer = synchronizer;
            _playerChoiceReceivedEvent = choiceReceived;
            _playerChoiceReceivedHandler = handler;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] player choice subscription degraded: {ex.Message}");
        }
    }

    private static Delegate? BuildPlayerChoiceReceivedHandler(Type eventHandlerType)
    {
        MethodInfo? invoke = eventHandlerType.GetMethod("Invoke");
        ParameterInfo[] parameters = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
        if (parameters.Length != 3)
            return null;

        MethodInfo? genericMethod = typeof(Sts2TelemetryMod)
            .GetMethod(nameof(OnPlayerChoiceReceived), BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo? closedMethod = genericMethod?.MakeGenericMethod(
            parameters[0].ParameterType,
            parameters[1].ParameterType,
            parameters[2].ParameterType);
        return closedMethod == null ? null : Delegate.CreateDelegate(eventHandlerType, closedMethod);
    }

    private static void TryApplyHarmonyPatches()
    {
        try
        {
            if (IsHarmonyDisabledForDiagnostics())
            {
                TraceDiagnostic("harmony:install:skipped:diagnostic_switch");
                GD.Print("[STS2 Telemetry] Harmony patches disabled by local diagnostic switch; public events remain active");
                return;
            }

            IReadOnlyDictionary<string, object?> nativeDependencyStatus = TryPreloadHarmonyNativeDependencies();
            Recorder.RecordHarmonyNativeDependencyStatus(nativeDependencyStatus);
            LogHarmonyNativeDependencyStatus(nativeDependencyStatus);

            _harmony = new Harmony(ModId);
            Sts2HookPatchInstaller.PatchInstallReport report = Sts2HookPatchInstaller.Install(_harmony);
            Recorder.RecordHarmonyPatchStatus(report.ToRecord());
            LogHarmonyPatchStatus(report);
            TraceDiagnostic($"harmony:install:complete:patched={report.PatchedMethodCount}:missing={report.MissingTargetCount}:failed={report.FailedPatchCount}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] Harmony patches degraded; public event hooks remain active: {ex}");
        }
    }

    private static void OnRunStarted(RunState runState)
    {
        RecordCrashBreadcrumb("run_manager.run_started", "enter");
        TraceDiagnostic("run_started:callback_entered");
        GuardTelemetryCallback("run_manager.run_started", () =>
        {
            TelemetryMainMenuUploadUi.Hide();
            ClearChoiceCaches();
            TraceDiagnostic("run_started:recorder:start");
            RecordCrashBreadcrumb("run_manager.run_started", "before_recorder");
            Recorder.OnRunStarted(runState, "run_manager.run_started");
            RecordCrashBreadcrumb("run_manager.run_started", "after_recorder");
            TraceDiagnostic("run_started:recorder:complete");
            TrySubscribeActionExecutor();
            TrySubscribePlayerChoiceSynchronizer();
            TraceDiagnostic("run_started:resubscribe:complete");
            RequestUploadSync();
        });
    }

    private static void OnActEntered()
        => GuardTelemetryCallback("run_manager.act_entered",
            () => Recorder.RecordLifecycleSignal("lifecycle/act_entered", "run_manager"));

    private static void OnRoomEntered()
    {
        RecordCrashBreadcrumb("run_manager.room_entered", "enter");
        GuardTelemetryCallback("run_manager.room_entered", () =>
        {
            ClearChoiceCachesForRoomEntered();
            Recorder.FlushPendingAsTransitionMarkers("room_entered_before_post_state");
            RecordCrashBreadcrumb("run_manager.room_entered", "before_schedule");
            bool settledScheduled = ScheduleSettledRoomLifecycle(
                "lifecycle/room_entered_settled",
                "run_manager.room_entered_settled",
                () =>
                {
                    TrySubscribeActionExecutor();
                    TrySubscribePlayerChoiceSynchronizer();
                    RequestUploadSync();
                });
            RecordCrashBreadcrumb("run_manager.room_entered", "after_schedule");
            Recorder.RecordLifecycleSignal(
                "lifecycle/room_entered",
                "run_manager",
                BuildSettledLifecycleDetails("lifecycle/room_entered_settled", settledScheduled));
            RecordCrashBreadcrumb("run_manager.room_entered", "after_signal");
        });
    }

    private static void OnRoomExited()
    {
        GuardTelemetryCallback("run_manager.room_exited", () =>
        {
            Recorder.FlushPendingAsTransitionMarkers("room_exited_before_post_state");
            Recorder.RecordLifecycleSignal(
                "lifecycle/room_exited",
                "run_manager",
                BuildDisabledSettledLifecycleDetails(
                    "lifecycle/room_exited_settled",
                    "room_exit_transition_can_cross_act_or_scene_boundary"));
        });
    }

    private static bool ScheduleSettledRoomLifecycle(string recordType, string callbackSource, Action? afterRecord = null)
        => TryScheduleNextFrame(callbackSource, () =>
        {
            RecordCrashBreadcrumb(callbackSource, "enter");
            RecordCrashBreadcrumb(callbackSource, "before_record");
            string? settledStateType = Recorder.RecordLifecycle(recordType, "run_manager", new Dictionary<string, object?>
            {
                ["settled_after_callback"] = true,
                ["settled_snapshot_schedule"] = "next_frame"
            });
            if (recordType == "lifecycle/room_entered_settled"
                && settledStateType == "event"
                && !Recorder.LatestDecisionContextHasUsableLegalActions("event"))
            {
                ScheduleEventDecisionContextRefresh(callbackSource, attempt: 1);
            }
            RecordCrashBreadcrumb(callbackSource, "after_record");
            afterRecord?.Invoke();
        });

    private static void ScheduleEventDecisionContextRefresh(string triggerSource, int attempt)
        => ScheduleDecisionContext(
            triggerSource,
            contextSource: "runtime.event.options_settled_retry",
            callbackSourcePrefix: "runtime.event.options_settled_retry.event_context_refreshed",
            allowedStateTypes: EventContextStateTypes,
            attempt,
            requireUsableLegalActions: true);

    private static void ScheduleRewardDecisionContext(string triggerSource, int attempt)
        => ScheduleDecisionContext(
            triggerSource,
            contextSource: "runtime.rewards_set.generate_without_offering",
            callbackSourcePrefix: "runtime.rewards_set.generate_without_offering.reward_context_settled",
            allowedStateTypes: RewardContextStateTypes,
            attempt,
            afterAttempt: (recorded, completedAttempt) =>
            {
                if (recorded || completedAttempt >= DecisionContextMaxSettleFrames)
                    RewardChoiceCache.Shared.CompleteScheduledRewardsContext(recorded);
            });

    private static void ScheduleDecisionContext(
        string triggerSource,
        string contextSource,
        string callbackSourcePrefix,
        IReadOnlyCollection<string> allowedStateTypes,
        int attempt,
        Action<bool, int>? afterAttempt = null,
        bool requireUsableLegalActions = false)
        => ScheduleDecisionContext(
            triggerSource,
            contextSource,
            callbackSourcePrefix,
            allowedStateTypes,
            attempt,
            afterAttempt,
            requireUsableLegalActions,
            actionExecutorDeferrals: 0);

    private static void ScheduleDecisionContext(
        string triggerSource,
        string contextSource,
        string callbackSourcePrefix,
        IReadOnlyCollection<string> allowedStateTypes,
        int attempt,
        Action<bool, int>? afterAttempt,
        bool requireUsableLegalActions,
        int actionExecutorDeferrals)
    {
        string callbackSource = attempt == 1
            ? callbackSourcePrefix
            : $"{callbackSourcePrefix}.retry_{attempt}";

        bool scheduled = TryScheduleNextFrame(callbackSource, () =>
        {
            if (IsActionExecutorInFlight()
                && actionExecutorDeferrals < DecisionContextMaxActionExecutorDeferrals)
            {
                TraceDiagnostic(
                    $"{callbackSourcePrefix}:deferred_action_executor_in_flight:attempt={attempt}:defer={actionExecutorDeferrals + 1}");
                ScheduleDecisionContext(
                    triggerSource,
                    contextSource,
                    callbackSourcePrefix,
                    allowedStateTypes,
                    attempt,
                    afterAttempt,
                    requireUsableLegalActions,
                    actionExecutorDeferrals + 1);
                return;
            }

            bool recorded = Recorder.RecordDecisionContextIfCurrentSurface(
                contextSource: contextSource,
                source: callbackSource,
                allowedStateTypes: allowedStateTypes,
                details: new Dictionary<string, object?>
                {
                    ["trigger_source"] = triggerSource,
                    ["settled_after_callback"] = true,
                    ["settled_snapshot_schedule"] = "next_frame",
                    ["settle_attempt"] = attempt,
                    ["max_settle_attempts"] = DecisionContextMaxSettleFrames,
                    ["require_usable_legal_actions"] = requireUsableLegalActions,
                    ["action_executor_in_flight_deferred"] = actionExecutorDeferrals > 0,
                    ["action_executor_in_flight_deferrals"] = actionExecutorDeferrals,
                    ["action_executor_in_flight_gate"] = "defer_scheduled_context_until_action_boundary"
                },
                requireUsableLegalActions);

            if (!recorded && attempt < DecisionContextMaxSettleFrames)
                ScheduleDecisionContext(
                    triggerSource,
                    contextSource,
                    callbackSourcePrefix,
                    allowedStateTypes,
                    attempt + 1,
                    afterAttempt,
                    requireUsableLegalActions,
                    actionExecutorDeferrals: 0);

            afterAttempt?.Invoke(recorded, attempt);
        });

        if (!scheduled)
        {
            TraceDiagnostic($"{callbackSourcePrefix}:schedule_failed:attempt={attempt}");
            afterAttempt?.Invoke(false, DecisionContextMaxSettleFrames);
        }
    }

    private static void ScheduleTreasureRelicPickSettledContext(string triggerSource, int attempt)
        => ScheduleDecisionContext(
            triggerSource,
            contextSource: "action_executor.treasure_relic_pick.settled_context",
            callbackSourcePrefix: "action_executor.treasure_relic_pick.treasure_context_settled",
            allowedStateTypes: TreasureContextStateTypes,
            attempt);

    private static void ClearChoiceCaches()
    {
        RewardChoiceCache.Shared.Clear();
        SelectionChoiceCache.Shared.Clear();
    }

    private static void ClearChoiceCachesForRoomEntered()
    {
        RewardChoiceCache.Shared.ClearForRoomEntered();
        SelectionChoiceCache.Shared.Clear();
    }

    private static IReadOnlyDictionary<string, object?> BuildSettledLifecycleDetails(
        string settledRecordType,
        bool settledScheduled)
        => new Dictionary<string, object?>
        {
            ["immediate_callback_policy"] = "signal_only",
            ["settled_snapshot_record_type"] = settledRecordType,
            ["settled_snapshot_schedule"] = "next_frame",
            ["settled_snapshot_scheduled"] = settledScheduled
        };

    private static IReadOnlyDictionary<string, object?> BuildDisabledSettledLifecycleDetails(
        string settledRecordType,
        string disabledReason)
        => new Dictionary<string, object?>
        {
            ["immediate_callback_policy"] = "signal_only",
            ["settled_snapshot_record_type"] = settledRecordType,
            ["settled_snapshot_schedule"] = "disabled",
            ["settled_snapshot_scheduled"] = false,
            ["settled_snapshot_disabled_reason"] = disabledReason,
            ["stable_state_capture_continues_at"] = "lifecycle/room_entered_settled"
        };

    internal static int ActionExecutorInFlightCountForTests()
        => Math.Max(0, Volatile.Read(ref _actionExecutorInFlightCount));

    internal static void ResetActionExecutorInFlightForTests()
        => Volatile.Write(ref _actionExecutorInFlightCount, 0);

    private static bool IsActionExecutorInFlight()
        => Volatile.Read(ref _actionExecutorInFlightCount) > 0;

    private static void MarkActionExecutorInFlight()
        => Interlocked.Increment(ref _actionExecutorInFlightCount);

    private static void MarkActionExecutorSettled()
    {
        if (Interlocked.Decrement(ref _actionExecutorInFlightCount) < 0)
            Volatile.Write(ref _actionExecutorInFlightCount, 0);
    }

    private static void OnBeforeActionExecuted(GameAction action)
        => GuardTelemetryCallback("action_executor.before_action_executed", () =>
        {
            MarkActionExecutorInFlight();
            if (ActionExecutorCapturePolicy.IsTreasureRelicPickTransition(action))
            {
                Recorder.RecordActionExecutorSignal(
                    action,
                    "before_action_executed",
                    ActionExecutorCapturePolicy.TreasureRelicPickTransitionSafety);
                return;
            }
            else if (ActionExecutorCapturePolicy.IsSignalOnly(action))
            {
                Recorder.RecordActionExecutorSignal(action, "before_action_executed");
                return;
            }

            Recorder.BeginActionExecutorDecision(action);
        });

    private static void OnAfterActionExecuted(GameAction action)
        => GuardTelemetryCallback("action_executor.after_action_executed", () =>
        {
            try
            {
                if (ActionExecutorCapturePolicy.IsTreasureRelicPickTransition(action))
                {
                    Recorder.RecordActionExecutorSignal(
                        action,
                        "after_action_executed",
                        ActionExecutorCapturePolicy.TreasureRelicPickTransitionSafety);
                    ScheduleTreasureRelicPickSettledContext("action_executor.after_action_executed", attempt: 1);
                }
                else if (ActionExecutorCapturePolicy.IsSignalOnly(action))
                {
                    Recorder.RecordActionExecutorSignal(action, "after_action_executed");
                }
                else
                {
                    Recorder.CompleteActionExecutorDecision(action);
                }
            }
            finally
            {
                MarkActionExecutorSettled();
            }
        });

    private static void OnRunSaveSaved()
        => GuardTelemetryCallback("run_save_manager.saved_event",
            () =>
            {
                Recorder.RecordSaveObserved("run_save_manager.saved_event");
                RequestUploadSync();
            });

    private static void OnPlayerChoiceReceived<TPlayer, TChoiceId, TResult>(
        TPlayer player,
        TChoiceId choiceId,
        TResult result)
    {
        const string source = "player_choice_synchronizer.player_choice_received";
        GuardTelemetryCallback(source, () =>
            Recorder.RecordPatchedUiSignal(source, null, new object?[] { player, choiceId, result }));
    }

    private static bool IsRewardSelectionSignal(string source)
        => string.Equals(source, "ui.reward.on_select_wrapper", StringComparison.Ordinal)
            || source.Contains("Reward.OnSelectWrapper", StringComparison.Ordinal);

    private static bool IsCardRewardSelectionSignal(string source)
        => source.Contains("card_reward", StringComparison.OrdinalIgnoreCase)
            || source.Contains("card_selection.skip", StringComparison.OrdinalIgnoreCase)
            || source.Contains("CardRewardSelection", StringComparison.Ordinal);

    private static bool IsEventOptionSelectionSignal(string source)
        => string.Equals(source, "runtime.event.choose_local_option", StringComparison.Ordinal)
            || source.Contains("EventSynchronizer.ChooseLocalOption", StringComparison.Ordinal);

    private static void GuardTelemetryCallback(string source, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] callback failed ({source}): {ex}");
            try
            {
                Recorder.RecordTelemetryError(source, ex);
            }
            catch (Exception recordEx)
            {
                GD.PrintErr($"[STS2 Telemetry] failed to record callback failure ({source}): {recordEx}");
            }
        }
    }

    private static void TryStartUploadService()
    {
        try
        {
            _uploadService ??= TelemetryUploadService.Start(Recorder.TelemetryBaseDirectory, Recorder.InstallationId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] background upload service unavailable: {ex.Message}");
        }
    }

    private static void TryStartUpdateService()
    {
        try
        {
            string? assemblyLocation = typeof(Sts2TelemetryMod).Assembly.Location;
            string? modDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrWhiteSpace(modDirectory))
                throw new InvalidOperationException("mod directory could not be resolved");
            _updateService ??= TelemetryUpdateService.Start(Recorder.TelemetryBaseDirectory, modDirectory);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] background update service unavailable: {ex.Message}");
        }
    }

    private static void RequestUploadSync()
    {
        try
        {
            _uploadService?.RequestSync();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] failed to request background upload sync: {ex.Message}");
        }
    }

    internal static void TraceDiagnostic(string message)
    {
        if (IsTraceEnabledForDiagnostics())
            GD.Print($"[STS2 Telemetry] trace {message}");
    }

    private static void RecordCrashBreadcrumb(string source, string stage)
    {
        try
        {
            CrashBreadcrumbWriter.Write(Recorder.TelemetryBaseDirectory, source, stage, Recorder.CurrentRunId);
        }
        catch
        {
            // Keep crash-boundary diagnostics strictly side-effect-safe for game callbacks.
        }
    }

    private static void LogHarmonyPatchStatus(Sts2HookPatchInstaller.PatchInstallReport report)
    {
        GD.Print(
            $"[STS2 Telemetry] Harmony patch status: patched={report.PatchedMethodCount}, missing={report.MissingTargetCount}, failed={report.FailedPatchCount}");

        foreach (Sts2HookPatchInstaller.PatchInstallResult result in report.Results)
        {
            if (result.Status == "patched")
                continue;

            string detail =
                $"{result.Status} {result.TypeName}.{result.MethodName}({result.ParameterSignature})"
                + (result.ErrorMessage == null ? "" : $": {result.ErrorMessage}");
            if (result.Status == "patch_failed")
                GD.PrintErr($"[STS2 Telemetry] Harmony patch degraded: {detail}");
            else
                GD.Print($"[STS2 Telemetry] Harmony patch skipped: {detail}");
        }
    }

    private static IReadOnlyDictionary<string, object?> TryPreloadHarmonyNativeDependencies()
    {
        var record = new Dictionary<string, object?>
        {
            ["source"] = "harmony.native_dependency_preload",
            ["platform"] = RuntimeInformation.OSDescription,
            ["required_for"] = "MonoMod.RuntimeDetour native helper",
            ["policy"] = "best_effort_before_harmony_patch"
        };

        if (!OperatingSystem.IsLinux())
        {
            record["status"] = "skipped_non_linux";
            return record;
        }

        string[] candidates =
        {
            "libgcc_s.so.1",
            "/usr/lib/libgcc_s.so.1",
            "/usr/lib/x86_64-linux-gnu/libgcc_s.so.1"
        };

        var attempts = new List<Dictionary<string, object?>>();
        foreach (string candidate in candidates)
        {
            try
            {
                ConsumeDlError();
                IntPtr handle = Dlopen(candidate, RtldNow | RtldGlobal);
                string? error = ConsumeDlError();
                attempts.Add(new Dictionary<string, object?>
                {
                    ["library"] = candidate,
                    ["loaded"] = handle != IntPtr.Zero,
                    ["error"] = handle == IntPtr.Zero ? error : null
                });

                if (handle != IntPtr.Zero)
                {
                    record["status"] = "loaded";
                    record["library"] = candidate;
                    record["attempts"] = attempts;
                    return record;
                }
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object?>
                {
                    ["library"] = candidate,
                    ["loaded"] = false,
                    ["error_type"] = ex.GetType().FullName,
                    ["error"] = ex.Message
                });
            }
        }

        record["status"] = "failed";
        record["attempts"] = attempts;
        return record;
    }

    private static void LogHarmonyNativeDependencyStatus(IReadOnlyDictionary<string, object?> status)
    {
        string preloadStatus = status.TryGetValue("status", out object? value)
            ? value?.ToString() ?? "unknown"
            : "unknown";
        string library = status.TryGetValue("library", out object? libraryValue)
            ? libraryValue?.ToString() ?? ""
            : "";

        if (preloadStatus == "loaded")
            GD.Print($"[STS2 Telemetry] Harmony native dependency preload: {preloadStatus} {library}");
        else if (preloadStatus == "failed")
            GD.PrintErr("[STS2 Telemetry] Harmony native dependency preload failed; patches may degrade");
        else
            GD.Print($"[STS2 Telemetry] Harmony native dependency preload: {preloadStatus}");
    }

    private static bool ShouldSuppressRelicSignal(string source, object? relic, object? targets)
    {
        if (relic == null)
            return false;

        int key = RuntimeHelpers.GetHashCode(relic);
        long now = System.Environment.TickCount64;
        lock (RecentRelicFlashSignals)
        {
            if (!RecentRelicFlashSignals.TryGetValue(key, out RecentRelicSignal? seen)
                || now - seen.SeenAtMilliseconds is < 0 or > 100)
            {
                return false;
            }

            if (string.Equals(source, "runtime.relic_model.flash_no_args", StringComparison.Ordinal))
                return true;

            bool sourceIsPrimary = IsPrimaryRelicSignalSurface(source);
            bool seenIsPrimary = IsPrimaryRelicSignalSurface(seen.Source);
            return sourceIsPrimary && seenIsPrimary;
        }
    }

    private static void RememberRelicSignal(string source, object? relic, object? targets)
    {
        if (relic == null)
            return;

        int key = RuntimeHelpers.GetHashCode(relic);
        long now = System.Environment.TickCount64;
        lock (RecentRelicFlashSignals)
        {
            RecentRelicFlashSignals[key] = new RecentRelicSignal(
                source,
                targets != null,
                now);
            foreach (int staleKey in RecentRelicFlashSignals
                         .Where(pair => now - pair.Value.SeenAtMilliseconds > 1_000)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                RecentRelicFlashSignals.Remove(staleKey);
            }
        }
    }

    private static bool IsPrimaryRelicSignalSurface(string source)
        => string.Equals(source, "runtime.relic_model.flash", StringComparison.Ordinal)
            || string.Equals(source, "runtime.relic_model.flashed_event", StringComparison.Ordinal);

    private sealed record RecentRelicSignal(
        string Source,
        bool HasTargets,
        long SeenAtMilliseconds);

    private static bool IsHarmonyDisabledForDiagnostics()
    {
        try
        {
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory;
            string markerPath = Path.Combine(assemblyDirectory, "telemetry", "disable_harmony");
            return File.Exists(markerPath)
                || string.Equals(
                    System.Environment.GetEnvironmentVariable("STS2_TELEMETRY_DISABLE_HARMONY"),
                    "1",
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTraceEnabledForDiagnostics()
    {
        long now = System.Environment.TickCount64;
        long lastChecked = Volatile.Read(ref _traceSwitchCheckedAtMilliseconds);
        if (lastChecked != 0 && now - lastChecked < 1000)
            return Volatile.Read(ref _traceSwitchCachedValue);

        bool enabled = ComputeTraceEnabledForDiagnostics();
        Volatile.Write(ref _traceSwitchCachedValue, enabled);
        Volatile.Write(ref _traceSwitchCheckedAtMilliseconds, now);
        return enabled;
    }

    private static bool ComputeTraceEnabledForDiagnostics()
    {
        try
        {
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory;
            string markerPath = Path.Combine(assemblyDirectory, "telemetry", "enable_trace");
            return File.Exists(markerPath)
                || string.Equals(
                    System.Environment.GetEnvironmentVariable("STS2_TELEMETRY_TRACE"),
                    "1",
                    StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static object? SafeCurrentRunState()
    {
        try
        {
            return RunManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr Dlopen(string fileName, int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr Dlerror();

    private static string? ConsumeDlError()
    {
        IntPtr error = Dlerror();
        return error == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(error);
    }
}
