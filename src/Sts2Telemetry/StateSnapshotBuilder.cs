using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Telemetry;

public interface IStateSnapshotBuilder
{
    StateSnapshot Capture(string reason, bool includePlayerMetadata = true);

    object? SafeRunState();

    object? GetLocalPlayer(object? runState);
}

public sealed class StateSnapshotBuilder : IStateSnapshotBuilder
{
    public StateSnapshot Capture(string reason, bool includePlayerMetadata = true)
    {
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:start");
        CapturePolicy policy = CapturePolicy.ForReason(reason);
        var raw = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        object? runState = SafeRunState();
        object? combatState = SafeCombatState();
        string stateType = DetermineStateType(runState, policy);
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:state_type:{stateType}");

        raw["state_type"] = stateType;
        raw["game"] = BuildGameMetadata();
        raw["run"] = BuildRunMetadata(runState, Safe(() => RunManager.Instance));
        raw["room"] = BuildRoomMetadata(runState);
        object? player = includePlayerMetadata ? GetLocalPlayer(runState) : null;
        raw["combat"] = BuildCombatMetadata(combatState, player);
        if (!policy.SkipVolatileScreenMetadata)
            raw["screen"] = BuildScreenMetadata();
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:metadata:complete");

        if (includePlayerMetadata && player != null)
            raw["local_player"] = BuildPlayerMetadata(player, includeCombatDetails: stateType == "combat");
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:player:complete");

        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:projection:start");
        raw["raw_projection"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["run_state"] = BuildSafeObjectProjection(runState),
            ["combat_state"] = BuildSafeObjectProjection(combatState)
        };
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:projection:complete");

        raw["projection_notes"] = new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["builder"] = "safe_runtime_projection",
            ["screen_metadata_policy"] = policy.SkipVolatileScreenMetadata
                ? "runtime_only_no_godot_ui_singletons"
                : "include_bounded_screen_singletons",
            ["canonicalization_policy"] = "excludes wall-clock, UI hover/focus/animation/layout, telemetry ordering, and writer metadata"
        };

        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:canonicalize:start");
        var canonical = TelemetryHash.Canonicalize(raw) as IReadOnlyDictionary<string, object?>
            ?? new SortedDictionary<string, object?>(StringComparer.Ordinal);
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:canonicalize:complete");

        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:hash:start");
        var snapshot = new StateSnapshot(
            stateType,
            raw,
            canonical,
            TelemetryHash.HashRaw(raw),
            TelemetryHash.HashCanonicalPayload(canonical));
        Sts2TelemetryMod.TraceDiagnostic($"snapshot.capture:{reason}:hash:complete");
        return snapshot;
    }

    internal static bool UsesRuntimeOnlyScreenPolicyForTests(string reason)
        => CapturePolicy.ForReason(reason).SkipVolatileScreenMetadata;

    private static Dictionary<string, object?> BuildSafeObjectProjection(object? value)
    {
        if (value == null)
            return new Dictionary<string, object?> { ["is_present"] = false };

        Type type = value.GetType();
        return new Dictionary<string, object?>
        {
            ["is_present"] = true,
            ["type"] = type.FullName,
            ["projection_policy"] = "deep_reflection_disabled",
            ["reason"] = "unbounded reflection over live STS2 runtime objects can invoke unsafe getters during game callbacks"
        };
    }

    public object? SafeRunState()
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

    public object? SafeCombatState()
    {
        try
        {
            return CombatManager.Instance.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }
    }

    public object? GetLocalPlayer(object? runState)
    {
        if (runState == null)
            return null;

        object? player = ReflectionUtil.CallStatic("MegaCrit.Sts2.Core.Context.LocalContext", "GetMe", runState);
        if (player != null)
            return player;

        object? players = ReflectionUtil.GetMemberValue(runState, "Players", "PlayerStates");
        return ReflectionUtil.Enumerate(players, maxItems: 1).FirstOrDefault();
    }

    private static string DetermineStateType(object? runState, CapturePolicy policy)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return "menu";
        }
        catch
        {
            return "unknown";
        }

        if (!policy.SkipVolatileScreenMetadata)
        {
            object? overlay = ReflectionUtil.Call(
                ReflectionUtil.GetSingletonInstance("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack"),
                "Peek");
            string? overlayType = overlay?.GetType().Name;
            if (!string.IsNullOrWhiteSpace(overlayType))
                return overlayType switch
                {
                    "NRewardsScreen" => "rewards",
                    "NCardRewardSelectionScreen" => "card_reward",
                    "NCardGridSelectionScreen" or "NChooseACardSelectionScreen" => "card_select",
                    "NChooseABundleSelectionScreen" => "bundle_select",
                    "NChooseARelicSelection" => "relic_select",
                    "NCrystalSphereScreen" => "crystal_sphere",
                    _ => $"overlay/{overlayType}"
                };

            object? mapScreen = ReflectionUtil.GetSingletonInstance("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen");
            if (ReflectionUtil.GetBool(mapScreen, "IsOpen") == true)
                return "map";
        }

        try
        {
            if (CombatManager.Instance.IsInProgress)
                return "combat";
        }
        catch
        {
        }

        object? currentRoom = ReflectionUtil.GetMemberValue(runState, "CurrentRoom");
        string roomType = currentRoom?.GetType().Name ?? "unknown";
        return roomType switch
        {
            "EventRoom" => "event",
            "MerchantRoom" => "shop",
            "RestSiteRoom" => "rest_site",
            "TreasureRoom" => "treasure",
            "MapRoom" => "map",
            _ => $"room/{roomType}"
        };
    }

    private static Dictionary<string, object?> BuildGameMetadata()
    {
        return new Dictionary<string, object?>
        {
            ["game_version"] = ReflectionUtil.SafeText(ReflectionUtil.GetStaticMemberValue("MegaCrit.Sts2.Core.BuildInfo", "Version"))
                ?? ReflectionUtil.SafeText(ReflectionUtil.GetStaticMemberValue("MegaCrit.Sts2.Core.GameVersion", "Version"))
                ?? "unknown",
            ["mod_version"] = Sts2TelemetryMod.Version,
            ["schema_version"] = TelemetryRecorder.SchemaVersion
        };
    }

    private static Dictionary<string, object?> BuildRunMetadata(object? runState, object? runManager)
    {
        var run = new Dictionary<string, object?>();
        if (runState == null)
            return run;

        MaybeSet(run, "act_index", ReflectionUtil.GetInt(runState, "CurrentActIndex"));
        MaybeSet(run, "act", AddOne(ReflectionUtil.GetInt(runState, "CurrentActIndex")));
        MaybeSet(run, "floor", ReflectionUtil.GetInt(runState, "TotalFloor", "Floor"));
        MaybeSet(run, "ascension", ReflectionUtil.GetInt(runState, "AscensionLevel", "Ascension"));
        MaybeSet(run, "seed", ResolveRunSeed(runState));
        MaybeSet(run, "run_type", ReflectionUtil.GetText(runState, "RunType", "Mode", "GameMode"));
        MaybeSet(run, "game_mode", ReflectionUtil.GetText(runState, "GameMode", "RunMode", "Mode", "RunType"));
        MaybeSet(run, "start_time", ResolveRunStartTime(runState, runManager));
        MaybeSet(run, "character", ResolveRunCharacter(runState));
        IReadOnlyList<string>? modifiers = BuildStableModifierList(runState);
        if (modifiers != null)
            run["modifiers"] = modifiers;
        run["logical_run_identity"] = BuildLogicalRunIdentity(run);
        return run;
    }

    internal static Dictionary<string, object?> BuildRunMetadataForTests(object? runState)
        => BuildRunMetadata(runState, runManager: null);

    internal static Dictionary<string, object?> BuildRunMetadataForTests(object? runState, object? runManager)
        => BuildRunMetadata(runState, runManager);

    private static Dictionary<string, object?> BuildRoomMetadata(object? runState)
    {
        object? room = ReflectionUtil.GetMemberValue(runState, "CurrentRoom");
        var metadata = new Dictionary<string, object?>();
        if (room == null)
            return metadata;

        metadata["type"] = room.GetType().FullName;
        MaybeSet(metadata, "room_type", ReflectionUtil.GetText(room, "RoomType"));
        MaybeSet(metadata, "id", ReflectionUtil.GetText(room, "Id"));
        return metadata;
    }

    private static Dictionary<string, object?> BuildCombatMetadata(object? combatState, object? localPlayer)
    {
        var metadata = new Dictionary<string, object?>();
        if (combatState == null)
            return metadata;

        MaybeSet(metadata, "round", ReflectionUtil.GetInt(combatState, "RoundNumber", "Round"));
        MaybeSet(metadata, "current_side", ReflectionUtil.GetText(combatState, "CurrentSide"));
        MaybeSet(metadata, "is_in_progress", Safe(() => CombatManager.Instance.IsInProgress));
        MaybeSet(metadata, "is_play_phase", Safe(() =>
            ReflectionUtil.GetBool(CombatManager.Instance, "IsPlayPhase", "InPlayPhase", "CanPlayCards")));
        MaybeSet(metadata, "player_actions_disabled", Safe(() => CombatManager.Instance.PlayerActionsDisabled));
        metadata["process"] = BuildCombatProcessMetadata(combatState);
        metadata["enemies"] = CombatProjection.BuildEnemySnapshots(combatState);
        metadata["target_candidates"] = CombatProjection.BuildTargetCandidates(combatState, localPlayer);
        return metadata;
    }

    private static Dictionary<string, object?> BuildCombatProcessMetadata(object combatState)
    {
        int? turnIndex = ReflectionUtil.GetInt(combatState, "RoundNumber", "Round", "TurnNumber", "Turn");
        string? turnSide = ReflectionUtil.GetText(combatState, "CurrentSide", "TurnSide", "ActiveSide");
        string? phase = ReflectionUtil.GetText(combatState, "CurrentPhase", "Phase", "TurnPhase", "CurrentTurnPhase");
        string? actionStep = ReflectionUtil.GetText(combatState, "ActionStep", "CurrentActionStep", "Step", "CurrentStep");
        int? actionIndex = ReflectionUtil.GetInt(combatState, "ActionIndex", "CurrentActionIndex", "StepIndex");

        return new Dictionary<string, object?>
        {
            ["turn_index"] = turnIndex,
            ["turn_side"] = turnSide,
            ["phase"] = phase,
            ["action_step"] = actionStep,
            ["action_index"] = actionIndex,
            ["marker_status"] = new Dictionary<string, object?>
            {
                ["turn_index"] = turnIndex != null ? "present" : "unavailable",
                ["turn_side"] = turnSide != null ? "present" : "unavailable",
                ["phase"] = phase != null ? "present" : "unavailable",
                ["action_step"] = actionStep != null ? "present" : "unavailable"
            }
        };
    }

    private static Dictionary<string, object?> BuildScreenMetadata()
    {
        var metadata = new Dictionary<string, object?>();

        object? mapScreen = ReflectionUtil.GetSingletonInstance("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen");
        if (mapScreen != null)
        {
            metadata["map_screen"] = new Dictionary<string, object?>
            {
                ["type"] = mapScreen.GetType().FullName,
                ["is_open"] = ReflectionUtil.GetBool(mapScreen, "IsOpen")
            };
        }

        object? overlay = ReflectionUtil.Call(
            ReflectionUtil.GetSingletonInstance("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack"),
            "Peek");
        if (overlay != null)
        {
            metadata["top_overlay"] = new Dictionary<string, object?>
            {
                ["type"] = overlay.GetType().FullName,
                ["name"] = overlay.GetType().Name
            };
        }

        return metadata;
    }

    internal static Dictionary<string, object?> BuildCombatMetadataForTests(object? combatState, object? localPlayer)
        => BuildCombatMetadata(combatState, localPlayer);

    private static Dictionary<string, object?> BuildPlayerMetadata(object player, bool includeCombatDetails)
    {
        object? creature = ReflectionUtil.GetMemberValue(player, "Creature");
        object? combatState = ReflectionUtil.GetMemberValue(player, "PlayerCombatState");
        var metadata = new Dictionary<string, object?>();

        MaybeSet(metadata, "character", ReflectionUtil.GetText(ReflectionUtil.GetMemberValue(player, "Character"), "Title", "Id"));
        MaybeSet(metadata, "hp", ReflectionUtil.GetInt(creature, "CurrentHp"));
        MaybeSet(metadata, "max_hp", ReflectionUtil.GetInt(creature, "MaxHp"));
        MaybeSet(metadata, "block", ReflectionUtil.GetInt(creature, "Block"));
        MaybeSet(metadata, "gold", ReflectionUtil.GetInt(player, "Gold"));
        MaybeSet(metadata, "energy", ReflectionUtil.GetInt(combatState, "Energy"));
        MaybeSet(metadata, "max_energy", ReflectionUtil.GetInt(combatState, "MaxEnergy"));

        object? hand = ReflectionUtil.GetMemberValue(combatState, "Hand");
        object? handCards = ReflectionUtil.GetMemberValue(hand, "Cards");
        metadata["hand_count"] = ReflectionUtil.Enumerate(handCards).Count();
        metadata["relic_count"] = ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(player, "Relics")).Count();
        metadata["potion_slot_count"] = ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(player, "PotionSlots")).Count();

        metadata["powers"] = CombatProjection.BuildPowers(creature);
        metadata["status"] = metadata["powers"];

        if (includeCombatDetails)
            AddCombatPileMetadata(metadata, player, combatState);

        return metadata;
    }

    internal static Dictionary<string, object?> BuildPlayerMetadataForTests(object player, bool includeCombatDetails)
        => BuildPlayerMetadata(player, includeCombatDetails);

    private static void AddCombatPileMetadata(
        IDictionary<string, object?> metadata,
        object player,
        object? combatState)
    {
        object? runtimeCombatState = CombatProjection.ResolveCombatState(player);
        var targetCandidates = CombatProjection.BuildTargetCandidates(runtimeCombatState, player);

        object? hand = ReflectionUtil.GetMemberValue(combatState, "Hand");
        object? drawPile = ReflectionUtil.GetMemberValue(combatState, "DrawPile");
        object? discardPile = ReflectionUtil.GetMemberValue(combatState, "DiscardPile");
        object? exhaustPile = ReflectionUtil.GetMemberValue(combatState, "ExhaustPile");
        object? deck = ReflectionUtil.GetMemberValue(player, "Deck");
        object? potionSlots = ReflectionUtil.GetMemberValue(player, "PotionSlots");

        var handCards = CombatProjection.BuildPileCards(hand, "hand", includePlayability: true, runtimeCombatState, targetCandidates);
        var drawCards = CombatProjection.BuildPileCards(drawPile, "draw", includePlayability: false, runtimeCombatState);
        var discardCards = CombatProjection.BuildPileCards(discardPile, "discard", includePlayability: false, runtimeCombatState);
        var exhaustCards = CombatProjection.BuildPileCards(exhaustPile, "exhaust", includePlayability: false, runtimeCombatState);
        var deckCards = CombatProjection.BuildPileCards(deck, "deck", includePlayability: false, runtimeCombatState);
        var potions = BuildPotionMetadata(potionSlots, targetCandidates);

        metadata["hand"] = handCards;
        metadata["draw_pile"] = drawCards;
        metadata["discard_pile"] = discardCards;
        metadata["exhaust_pile"] = exhaustCards;
        metadata["deck"] = deckCards;
        metadata["potions"] = potions;
        metadata["draw_pile_count"] = drawCards.Count;
        metadata["discard_pile_count"] = discardCards.Count;
        metadata["exhaust_pile_count"] = exhaustCards.Count;
        metadata["deck_count"] = deckCards.Count;
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildPotionMetadata(
        object? potionSlots,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates)
    {
        var potions = new List<Dictionary<string, object?>>();
        int slot = 0;
        foreach (object? potion in ReflectionUtil.Enumerate(potionSlots))
        {
            if (potion != null)
                potions.Add(CombatProjection.BuildPotionProjection(potion, slot, targetCandidates));
            slot++;
        }

        return potions;
    }

    private static void MaybeSet(IDictionary<string, object?> target, string key, object? value)
    {
        if (value != null)
            target[key] = value;
    }

    private static IReadOnlyList<string>? BuildStableModifierList(object runState)
    {
        object? modifiers = ReflectionUtil.GetMemberValue(
            runState,
            "Modifiers",
            "RunModifiers",
            "GameModifiers",
            "CustomModifiers");
        if (modifiers == null)
            return null;

        return ReflectionUtil.Enumerate(modifiers, maxItems: 40)
            .Select(ReflectionUtil.SafeText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .OrderBy(text => text, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
    }

    private static string? ResolveRunSeed(object runState)
    {
        string? directSeed = FirstNonBlankText(runState, "Seed", "RunSeed", "StringSeed");
        if (!string.IsNullOrWhiteSpace(directSeed))
            return directSeed;

        object? rng = ReflectionUtil.GetMemberValue(runState, "Rng", "RunRng", "RunRngSet");
        return FirstNonBlankText(rng, "StringSeed", "Seed");
    }

    private static string? ResolveRunStartTime(object runState, object? runManager)
    {
        string? directStartTime = FirstNonBlankText(
            runState,
            "StartTime",
            "RunStartTime",
            "StartedAt",
            "CreatedAt");
        if (!string.IsNullOrWhiteSpace(directStartTime))
            return directStartTime;

        return FirstNonBlankText(
            runManager,
            "_startTime",
            "StartTime",
            "RunStartTime",
            "StartedAt",
            "CreatedAt");
    }

    private static string? ResolveRunCharacter(object runState)
    {
        string? directCharacter = StableCharacterId(ReflectionUtil.GetMemberValue(runState, "Character"));
        if (!string.IsNullOrWhiteSpace(directCharacter))
            return directCharacter;

        object? players = ReflectionUtil.GetMemberValue(runState, "Players", "PlayerStates");
        object? player = ReflectionUtil.Enumerate(players, maxItems: 1).FirstOrDefault();
        return StableCharacterId(ReflectionUtil.GetMemberValue(player, "Character"));
    }

    private static string? StableCharacterId(object? character)
        => FirstNonBlankText(character, "Id", "ID", "Key", "Title", "Name");

    private static string? FirstNonBlankText(object? target, params string[] memberNames)
    {
        if (target == null)
            return null;

        foreach (string memberName in memberNames)
        {
            string? text = ReflectionUtil.GetText(target, memberName);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static Dictionary<string, object?> BuildLogicalRunIdentity(
        IReadOnlyDictionary<string, object?> run)
    {
        string[] requiredFields =
        {
            "seed",
            "character",
            "ascension",
            "game_mode",
            "start_time",
            "modifiers"
        };

        var missingFields = requiredFields
            .Where(field => !HasStableIdentityValue(run, field))
            .ToArray();
        var degradedFields = requiredFields
            .Where(field => run.TryGetValue(field, out object? value) && IsDegradedIdentityValue(field, value))
            .ToArray();

        var identity = new Dictionary<string, object?>
        {
            ["identity_policy"] = "seed_character_ascension_game_mode_start_time_modifiers",
            ["excluded_fields"] = new[] { "floor", "hp", "deck", "current_room" },
            ["identity_quality"] = degradedFields.Length > 0 ? "degraded" : "stable"
        };

        if (missingFields.Length > 0)
        {
            identity["status"] = "incomplete";
            identity["identity_quality"] = "incomplete";
            identity["missing_fields"] = missingFields;
            return identity;
        }

        var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (string field in requiredFields)
            fields[field] = run[field];

        // Identity fields are an explicit stable allow-list, so do not drop start_time like state hashes do.
        string logicalRunKey = TelemetryHash.HashRaw(fields);
        identity["fields"] = fields;
        if (degradedFields.Length > 0)
        {
            identity["status"] = "degraded";
            identity["degraded_fields"] = degradedFields;
            identity["degraded_reason"] = "start_time_zero_loaded_save_identity";
            identity["observed_logical_run_key"] = logicalRunKey;
            identity["observed_logical_run_id"] = $"logical-run-{logicalRunKey[..16]}";
            return identity;
        }

        identity["status"] = "complete";
        identity["logical_run_key"] = logicalRunKey;
        identity["logical_run_id"] = $"logical-run-{logicalRunKey[..16]}";
        return identity;
    }

    private static bool HasStableIdentityValue(IReadOnlyDictionary<string, object?> run, string field)
    {
        if (!run.TryGetValue(field, out object? value) || value == null)
            return false;

        if (value is string text)
            return !string.IsNullOrWhiteSpace(text);

        if (field == "modifiers")
            return true;

        if (value is System.Collections.IEnumerable enumerable && value is not string)
            return enumerable.Cast<object?>().Any();

        return true;
    }

    private static bool IsDegradedIdentityValue(string field, object? value)
    {
        if (!string.Equals(field, "start_time", StringComparison.Ordinal))
            return false;

        return value?.ToString()?.Trim() == "0";
    }

    private static int? AddOne(int? value)
        => value == null ? null : value + 1;

    private static T? Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private readonly record struct CapturePolicy(bool SkipVolatileScreenMetadata)
    {
        public static CapturePolicy ForReason(string reason)
            => new(IsActionExecutorReason(reason));

        private static bool IsActionExecutorReason(string reason)
            => reason.StartsWith("action_executor", StringComparison.Ordinal);
    }
}
