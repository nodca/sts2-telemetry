using System.Collections;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Telemetry;

public sealed class LegalActionBuilder
{
    private readonly Func<CombatAvailability> _combatAvailabilityFactory;
    private readonly Func<string[], object?> _runManagerMemberFactory;
    private readonly Func<TreasureRelicReadiness> _treasureRelicReadinessFactory;
    private readonly RewardChoiceCache _rewardChoiceCache;
    private readonly SelectionChoiceCache _selectionChoiceCache;

    private static readonly HashSet<string> SurfaceSpecificUnavailableScreens = new(StringComparer.Ordinal)
    {
        "rewards",
        "card_reward",
        "card_select",
        "bundle_select",
        "relic_select",
        "crystal_sphere",
        "pack_select",
        "special_select"
    };

    public LegalActionBuilder()
        : this(GetRuntimeCombatAvailability, ResolveRuntimeRunManagerMember, TreasureRelicReadinessProbe.GetRuntimeReadiness)
    {
    }

    internal LegalActionBuilder(Func<CombatAvailability> combatAvailabilityFactory)
        : this(combatAvailabilityFactory, ResolveRuntimeRunManagerMember, TreasureRelicReadinessProbe.GetRuntimeReadiness)
    {
    }

    internal LegalActionBuilder(
        Func<CombatAvailability> combatAvailabilityFactory,
        Func<string[], object?> runManagerMemberFactory)
        : this(combatAvailabilityFactory, runManagerMemberFactory, TreasureRelicReadinessProbe.GetRuntimeReadiness)
    {
    }

    internal LegalActionBuilder(
        Func<CombatAvailability> combatAvailabilityFactory,
        Func<string[], object?> runManagerMemberFactory,
        Func<TreasureRelicReadiness> treasureRelicReadinessFactory)
        : this(
            combatAvailabilityFactory,
            runManagerMemberFactory,
            treasureRelicReadinessFactory,
            RewardChoiceCache.Shared,
            SelectionChoiceCache.Shared)
    {
    }

    internal LegalActionBuilder(
        Func<CombatAvailability> combatAvailabilityFactory,
        Func<string[], object?> runManagerMemberFactory,
        RewardChoiceCache rewardChoiceCache)
        : this(
            combatAvailabilityFactory,
            runManagerMemberFactory,
            TreasureRelicReadinessProbe.GetRuntimeReadiness,
            rewardChoiceCache,
            SelectionChoiceCache.Shared)
    {
    }

    internal LegalActionBuilder(
        Func<CombatAvailability> combatAvailabilityFactory,
        Func<string[], object?> runManagerMemberFactory,
        Func<TreasureRelicReadiness> treasureRelicReadinessFactory,
        RewardChoiceCache rewardChoiceCache,
        SelectionChoiceCache selectionChoiceCache)
    {
        _combatAvailabilityFactory = combatAvailabilityFactory;
        _runManagerMemberFactory = runManagerMemberFactory;
        _treasureRelicReadinessFactory = treasureRelicReadinessFactory;
        _rewardChoiceCache = rewardChoiceCache;
        _selectionChoiceCache = selectionChoiceCache;
    }

    public IReadOnlyList<Dictionary<string, object?>> Build(StateSnapshot snapshot, object? runState, object? localPlayer)
    {
        var actions = new List<Dictionary<string, object?>>();

        if (snapshot.StateType == "combat")
        {
            AddCombatActions(actions, snapshot, localPlayer);
        }
        else if (snapshot.StateType == "shop")
        {
            AddShopActions(actions, runState, localPlayer);
        }
        else if (snapshot.StateType == "map")
        {
            AddMapActions(actions, runState, localPlayer);
        }
        else if (snapshot.StateType == "event")
        {
            AddEventActions(actions);
        }
        else if (snapshot.StateType == "rest_site")
        {
            AddRestSiteActions(actions);
        }
        else if (snapshot.StateType == "treasure")
        {
            AddTreasureRelicActions(actions);
        }
        else if (snapshot.StateType is "rewards" or "card_reward")
        {
            AddRewardActions(actions, snapshot.StateType);
        }
        else if (snapshot.StateType is "relic_select" or "bundle_select")
        {
            AddSelectionActions(actions, snapshot.StateType);
        }
        else if (SurfaceSpecificUnavailableScreens.Contains(snapshot.StateType))
        {
            AddSurfaceSpecificUnavailableActions(actions, snapshot.StateType);
        }
        else if (snapshot.StateType.StartsWith("overlay/", StringComparison.Ordinal)
            || snapshot.StateType.StartsWith("room/", StringComparison.Ordinal))
        {
            AddPendingTypedBuilderAction(actions, snapshot.StateType);
        }

        AddNonCombatDiscardPotionActions(actions, snapshot.StateType, localPlayer);

        if (actions.Count == 0)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_type"] = "unknown",
                ["source"] = "prototype_reflection",
                ["availability"] = "no_legal_action_builder_match",
                ["state_type"] = snapshot.StateType
            });
        }

        return actions;
    }

    internal IReadOnlyList<Dictionary<string, object?>> BuildShopOffers(
        StateSnapshot snapshot,
        object? runState,
        object? localPlayer)
    {
        var offers = new List<Dictionary<string, object?>>();
        if (snapshot.StateType != "shop")
            return offers;

        var shop = ResolveShopContext(runState, localPlayer);
        if (shop.Inventory == null)
        {
            AddShopOffersExtractionGap(offers, "merchant_inventory_not_found");
            return offers;
        }

        int initialCount = offers.Count;
        var seenEntries = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AddShopEntryGroup(offers, seenEntries, shop, "character_card", "buy_shop_card", "CharacterCardEntries", ShopProjection.BuildOffer);
        AddShopEntryGroup(offers, seenEntries, shop, "colorless_card", "buy_shop_card", "ColorlessCardEntries", ShopProjection.BuildOffer);
        AddShopEntryGroup(offers, seenEntries, shop, "card", "buy_shop_card", "CardEntries", ShopProjection.BuildOffer);
        AddShopEntryGroup(offers, seenEntries, shop, "relic", "buy_shop_relic", "RelicEntries", ShopProjection.BuildOffer);
        AddShopEntryGroup(offers, seenEntries, shop, "potion", "buy_shop_potion", "PotionEntries", ShopProjection.BuildOffer);

        object? removalEntry = ReflectionUtil.GetMemberValue(shop.Inventory, "CardRemovalEntry");
        if (removalEntry != null)
            AddShopEntry(offers, "card_removal", "remove_card_at_shop", removalEntry, index: 0, shop.Player, ShopProjection.BuildOffer);

        if (offers.Count == initialCount)
            AddShopOffersExtractionGap(offers, "no_visible_shop_entries_found");

        return offers;
    }

    private void AddMapActions(ICollection<Dictionary<string, object?>> actions, object? runState, object? localPlayer)
    {
        object? map = ReflectionUtil.GetMemberValue(runState, "Map");
        if (map == null)
        {
            AddUnavailableAction(actions, "map_typed_builder_unavailable", "run_state_map", "map", "run_state_map_not_found",
                "map legal actions require RunState.Map and typed MapPoint children");
            return;
        }

        object? currentPoint = ReflectionUtil.GetMemberValue(runState, "CurrentMapPoint");
        object? currentCoord = ReflectionUtil.GetMemberValue(runState, "CurrentMapCoord");
        object? sourceCoord = ReflectionUtil.GetMemberValue(currentPoint, "coord", "Coord") ?? currentCoord;
        int? currentActIndex = ReflectionUtil.GetInt(runState, "CurrentActIndex", "ActIndex");
        int? mapGenerationCount = ResolveMapGenerationCount();
        object? startMapPoints = ReflectionUtil.GetMemberValue(map, "startMapPoints", "StartMapPoints");
        object? candidatesSource = currentPoint != null && sourceCoord != null
            ? ReflectionUtil.GetMemberValue(currentPoint, "Children")
            : startMapPoints;

        var candidates = ReflectionUtil.Enumerate(candidatesSource)
            .Where(candidate => candidate != null)
            .ToList();
        if (candidates.Count == 0 && currentPoint != null)
        {
            candidates.AddRange(ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(currentPoint, "Children"))
                .Where(candidate => candidate != null));
        }
        if (candidates.Count == 0)
        {
            object? startingPoint = ReflectionUtil.GetMemberValue(map, "StartingMapPoint");
            candidates.AddRange(ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(startingPoint, "Children"))
                .Where(candidate => candidate != null));
        }

        if (candidates.Count == 0)
        {
            AddUnavailableAction(actions, "map_typed_builder_unavailable", "run_state_map", "map", "candidate_map_points_not_found",
                "map legal actions require typed current point children or start map points");
            return;
        }

        bool hasWingedBoots = HasWingedBoots(localPlayer);
        if (hasWingedBoots)
        {
            int? sourceRow = MapPointCoordValue(currentPoint, "row") ?? MapCoordValue(sourceCoord, "row");
            foreach (object? mapPoint in EnumerateAllMapPoints(map))
            {
                if (mapPoint == null
                    || candidates.Any(candidate => ReferenceEquals(candidate, mapPoint))
                    || ReferenceEquals(mapPoint, currentPoint))
                {
                    continue;
                }

                int? row = MapPointCoordValue(mapPoint, "row");
                if (sourceRow == null || row == null || row > sourceRow)
                    candidates.Add(mapPoint);
            }
        }

        var candidateSummaries = candidates
            .Select(ProjectMapPoint)
            .Where(candidate => candidate != null)
            .Cast<Dictionary<string, object?>>()
            .ToArray();

        for (int i = 0; i < candidates.Count; i++)
        {
            object? candidate = candidates[i];
            var action = new Dictionary<string, object?>
            {
                ["action_type"] = "choose_map_node",
                ["source"] = "run_state_map",
                ["index"] = i,
                ["coord"] = ProjectMapCoord(ReflectionUtil.GetMemberValue(candidate, "coord", "Coord")),
                ["point_type"] = StableMemberText(candidate, "PointType", "RoomType", "Type"),
                ["source_coord"] = ProjectMapCoord(sourceCoord),
                ["current_act_index"] = currentActIndex,
                ["map_generation_count"] = mapGenerationCount,
                ["candidate_count"] = candidates.Count,
                ["candidate_paths"] = candidateSummaries,
                ["can_select"] = true,
                ["availability"] = "available"
            };

            if (hasWingedBoots
                && currentPoint != null
                && !ReferenceEquals(candidate, currentPoint)
                && !ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(currentPoint, "Children"))
                    .Any(child => ReferenceEquals(child, candidate)))
            {
                action["route_override_reason"] = "winged_boots";
                action["requires_relic"] = "WINGED_BOOTS";
                action["source"] = "run_state_map_winged_boots";
            }

            if (action["coord"] is IReadOnlyDictionary<string, object?> coord)
            {
                action["coord_col"] = coord.GetValueOrDefault("col");
                action["coord_row"] = coord.GetValueOrDefault("row");
            }

            actions.Add(action);
        }
    }

    private void AddEventActions(ICollection<Dictionary<string, object?>> actions)
    {
        object? synchronizer = ResolveRunManagerMember("EventSynchronizer");
        object? eventModel = ReflectionUtil.Call(synchronizer, "GetLocalEvent");
        if (eventModel == null)
        {
            AddUnavailableAction(actions, "event_typed_builder_unavailable", "event_synchronizer", "event", "local_event_not_found",
                "event legal actions require EventSynchronizer.GetLocalEvent().CurrentOptions");
            return;
        }

        object? options = ReflectionUtil.GetMemberValue(eventModel, "CurrentOptions");
        int initialCount = actions.Count;
        int index = 0;
        foreach (object? option in ReflectionUtil.Enumerate(options))
        {
            if (option == null)
            {
                index++;
                continue;
            }

            bool? isLocked = ReflectionUtil.GetBool(option, "IsLocked");
            bool canSelect = isLocked != true;
            actions.Add(new Dictionary<string, object?>
            {
                ["action_type"] = "choose_event_option",
                ["source"] = "event_synchronizer",
                ["event_source"] = IsRunStartEvent(eventModel) ? "run_start" : "event",
                ["event_id"] = GetEntityId(eventModel),
                ["event_runtime_type"] = eventModel.GetType().FullName,
                ["is_shared"] = ReflectionUtil.GetBool(synchronizer, "IsShared"),
                ["is_finished"] = ReflectionUtil.GetBool(eventModel, "IsFinished"),
                ["option_index"] = index,
                ["option_text_key"] = StableMemberText(option, "TextKey"),
                ["is_locked"] = isLocked,
                ["is_proceed"] = ReflectionUtil.GetBool(option, "IsProceed"),
                ["was_chosen"] = ReflectionUtil.GetBool(option, "WasChosen"),
                ["relic_id"] = GetEntityId(ReflectionUtil.GetMemberValue(option, "Relic")),
                ["can_select"] = canSelect,
                ["availability"] = canSelect ? "available" : "locked",
                ["text_status"] = "text_suppressed_locstring_runtime_safety",
                ["effect_summary_status"] = "effect_summary_unavailable"
            });
            index++;
        }

        if (actions.Count == initialCount)
        {
            AddUnavailableAction(actions, "event_typed_builder_unavailable", "event_synchronizer", "event", "current_options_not_found",
                "event legal actions require typed current event options");
        }
    }

    private void AddRestSiteActions(ICollection<Dictionary<string, object?>> actions)
    {
        object? synchronizer = ResolveRunManagerMember("RestSiteSynchronizer");
        object? options = ReflectionUtil.Call(synchronizer, "GetLocalOptions");
        int initialCount = actions.Count;
        int index = 0;
        foreach (object? option in ReflectionUtil.Enumerate(options))
        {
            if (option == null)
            {
                index++;
                continue;
            }

            bool? isEnabled = ReflectionUtil.GetBool(option, "IsEnabled");
            actions.Add(new Dictionary<string, object?>
            {
                ["action_type"] = "choose_rest_option",
                ["source"] = "rest_site_synchronizer",
                ["option_index"] = index,
                ["option_id"] = StableMemberText(option, "OptionId"),
                ["option_runtime_type"] = option.GetType().FullName,
                ["is_enabled"] = isEnabled,
                ["smith_count"] = ReflectionUtil.GetInt(option, "SmithCount"),
                ["can_select"] = isEnabled != false,
                ["availability"] = isEnabled == false ? "disabled" : "available",
                ["text_status"] = "text_suppressed_locstring_runtime_safety",
                ["effect_summary_status"] = "effect_summary_unavailable"
            });
            index++;
        }

        if (actions.Count == initialCount)
        {
            AddUnavailableAction(actions, "rest_site_typed_builder_unavailable", "rest_site_synchronizer", "rest_site", "local_options_not_found",
                "rest-site legal actions require RestSiteSynchronizer.GetLocalOptions()");
        }
    }

    private void AddTreasureRelicActions(ICollection<Dictionary<string, object?>> actions)
    {
        TreasureRelicReadiness readiness = _treasureRelicReadinessFactory();
        if (!readiness.CanReadRelics)
        {
            AddTreasureRelicUnavailableAction(actions, readiness);
            return;
        }

        object? synchronizer = ResolveRunManagerMember("TreasureRoomRelicSynchronizer");
        object? relics = ReflectionUtil.GetMemberValue(synchronizer, "CurrentRelics");
        int initialCount = actions.Count;
        int index = 0;
        foreach (object? relic in ReflectionUtil.Enumerate(relics))
        {
            if (relic == null)
            {
                index++;
                continue;
            }

            actions.Add(new Dictionary<string, object?>
            {
                ["action_type"] = "choose_treasure_relic",
                ["source"] = "treasure_room_relic_synchronizer",
                ["reward_source"] = "treasure",
                ["relic_index"] = index,
                ["relic_id"] = GetEntityId(relic),
                ["relic_rarity"] = StableMemberText(relic, "Rarity"),
                ["relic_runtime_type"] = relic.GetType().FullName,
                ["can_select"] = true,
                ["availability"] = "available"
            });
            index++;
        }

        if (actions.Count == initialCount)
        {
            AddUnavailableAction(actions, "treasure_relic_typed_builder_unavailable", "treasure_room_relic_synchronizer", "treasure",
                "current_relics_not_found", "treasure relic legal actions require a visible relic collection and TreasureRoomRelicSynchronizer.CurrentRelics");
            return;
        }

        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "skip_treasure_relic",
            ["source"] = "treasure_room_relic_synchronizer",
            ["reward_source"] = "treasure",
            ["relic_index"] = null,
            ["can_select"] = true,
            ["availability"] = "available"
        });
    }

    private static void AddTreasureRelicUnavailableAction(
        ICollection<Dictionary<string, object?>> actions,
        TreasureRelicReadiness readiness)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "treasure_relic_typed_builder_unavailable",
            ["source"] = "treasure_relic_readiness_probe",
            ["availability"] = readiness.Availability,
            ["state_type"] = "treasure",
            ["message"] = readiness.Message,
            ["current_screen_runtime_type"] = readiness.CurrentScreenRuntimeType,
            ["relic_collection_visible"] = readiness.RelicCollectionVisible
        });
    }

    private static void AddNonCombatDiscardPotionActions(
        ICollection<Dictionary<string, object?>> actions,
        string stateType,
        object? player)
    {
        if (!AllowsNonCombatPotionDiscard(stateType) || player == null)
            return;

        object? potionSlots = ReflectionUtil.GetMemberValue(player, "PotionSlots");
        bool? canRemovePotions = ReflectionUtil.GetBool(player, "CanRemovePotions");
        int slot = 0;
        foreach (object? potion in ReflectionUtil.Enumerate(potionSlots))
        {
            if (potion != null)
            {
                bool? isQueued = ReflectionUtil.GetBool(potion, "IsQueued");
                bool canDiscard = canRemovePotions != false && isQueued != true;
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_type"] = "discard_potion",
                    ["source"] = "local_player_potion_slots",
                    ["state_type"] = stateType,
                    ["slot"] = slot,
                    ["potion_id"] = CombatProjection.GetModelEntry(ReflectionUtil.GetMemberValue(potion, "Id")),
                    ["potion_name_status"] = "name_suppressed_locstring_runtime_safety",
                    ["target_type"] = StableMemberText(potion, "TargetType"),
                    ["usage"] = StableMemberText(potion, "Usage"),
                    ["is_queued"] = isQueued,
                    ["can_discard"] = canDiscard,
                    ["availability"] = canRemovePotions == false
                        ? "potion_discard_disabled"
                        : isQueued == true
                            ? "potion_queued"
                            : "available"
                });
            }

            slot++;
        }
    }

    private void AddRewardActions(ICollection<Dictionary<string, object?>> actions, string stateType)
    {
        IReadOnlyList<Dictionary<string, object?>>? cachedActions = _rewardChoiceCache.BuildLegalActions(stateType);
        if (cachedActions == null || cachedActions.Count == 0)
        {
            AddRewardCacheUnavailableAction(actions, stateType);
            return;
        }

        foreach (Dictionary<string, object?> action in cachedActions)
            actions.Add(action);
    }

    private void AddSelectionActions(ICollection<Dictionary<string, object?>> actions, string stateType)
    {
        IReadOnlyList<Dictionary<string, object?>>? cachedActions = _selectionChoiceCache.BuildLegalActions(stateType);
        if (cachedActions == null || cachedActions.Count == 0)
        {
            AddSurfaceSpecificUnavailableActions(actions, stateType);
            return;
        }

        foreach (Dictionary<string, object?> action in cachedActions)
            actions.Add(action);
    }

    private void AddCombatActions(ICollection<Dictionary<string, object?>> actions, StateSnapshot snapshot, object? player)
    {
        CombatAvailability availability = _combatAvailabilityFactory();
        if (!availability.CanBuild)
        {
            AddCombatAvailabilityMarker(actions, availability);
            return;
        }

        if (player == null)
        {
            AddCombatRuntimeUnavailableMarker(actions, "local_player_unavailable", availability);
            return;
        }

        object? combatState = ReflectionUtil.GetMemberValue(player, "PlayerCombatState");
        if (combatState == null)
        {
            AddCombatRuntimeUnavailableMarker(actions, "player_combat_state_unavailable", availability);
            return;
        }

        object? hand = ReflectionUtil.GetMemberValue(combatState, "Hand");
        object? cards = ReflectionUtil.GetMemberValue(hand, "Cards");
        object? runtimeCombatState = CombatProjection.ResolveCombatState(player);
        if (runtimeCombatState == null)
        {
            AddCombatRuntimeUnavailableMarker(actions, "combat_state_unavailable", availability);
            return;
        }

        if (cards == null)
        {
            AddCombatRuntimeUnavailableMarker(actions, "combat_hand_unavailable", availability);
            return;
        }

        IReadOnlyList<Dictionary<string, object?>> targetCandidates =
            SnapshotCombatTargetCandidates(snapshot) ?? CombatProjection.BuildTargetCandidates(runtimeCombatState, player);

        int index = 0;
        foreach (object? card in ReflectionUtil.Enumerate(cards))
        {
            if (card == null)
            {
                index++;
                continue;
            }

            var action = CombatProjection.BuildCardProjection(
                card,
                index,
                "hand",
                includePlayability: true,
                runtimeCombatState,
                targetCandidates,
                includeTargetCandidateDetails: false);
            action["action_type"] = "play_card";
            action["source"] = "combat_hand";
            action["availability"] = BuildCardAvailability(action);
            actions.Add(action);
            index++;
        }

        object? potionSlots = ReflectionUtil.GetMemberValue(player, "PotionSlots");
        int slot = 0;
        foreach (object? potion in ReflectionUtil.Enumerate(potionSlots))
        {
            if (potion != null)
            {
                var action = CombatProjection.BuildPotionProjection(
                    potion,
                    slot,
                    targetCandidates,
                    includeTargetCandidateDetails: false);
                actions.Add(action);
            }
            slot++;
        }

        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "end_turn",
            ["source"] = "combat_manager",
            ["can_select"] = true,
            ["availability"] = "available"
        });
    }

    private static CombatAvailability GetRuntimeCombatAvailability()
    {
        try
        {
            bool isInProgress = CombatManager.Instance.IsInProgress;
            bool? isPlayPhase = ReflectionUtil.GetBool(
                CombatManager.Instance,
                "IsPlayPhase",
                "InPlayPhase",
                "CanPlayCards");
            bool playerActionsDisabled = CombatManager.Instance.PlayerActionsDisabled;

            if (!isInProgress)
                return new CombatAvailability(false, "not_in_combat", isInProgress, isPlayPhase, playerActionsDisabled);
            if (isPlayPhase == false)
                return new CombatAvailability(false, "combat_not_in_play_phase", isInProgress, isPlayPhase, playerActionsDisabled);
            if (isPlayPhase == null)
                return new CombatAvailability(false, "combat_play_phase_unavailable", isInProgress, isPlayPhase, playerActionsDisabled);
            if (playerActionsDisabled)
                return new CombatAvailability(false, "player_actions_disabled", isInProgress, isPlayPhase, playerActionsDisabled);

            return new CombatAvailability(true, "available", isInProgress, isPlayPhase, playerActionsDisabled);
        }
        catch
        {
            return new CombatAvailability(false, "combat_manager_unavailable", null, null, null);
        }
    }

    private static void AddCombatAvailabilityMarker(
        ICollection<Dictionary<string, object?>> actions,
        CombatAvailability availability)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "combat_typed_builder_unavailable",
            ["source"] = "combat_manager",
            ["availability"] = availability.Availability,
            ["state_type"] = "combat",
            ["is_in_progress"] = availability.IsInProgress,
            ["is_play_phase"] = availability.IsPlayPhase,
            ["player_actions_disabled"] = availability.PlayerActionsDisabled,
            ["message"] = "combat legal actions require an active play phase with player actions enabled"
        });
    }

    private static void AddCombatRuntimeUnavailableMarker(
        ICollection<Dictionary<string, object?>> actions,
        string reason,
        CombatAvailability availability)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "combat_typed_builder_unavailable",
            ["source"] = "combat_runtime",
            ["availability"] = reason,
            ["state_type"] = "combat",
            ["is_in_progress"] = availability.IsInProgress,
            ["is_play_phase"] = availability.IsPlayPhase,
            ["player_actions_disabled"] = availability.PlayerActionsDisabled,
            ["message"] = "combat legal actions require typed local player, combat state, and hand members"
        });
    }

    private static string BuildCardAvailability(IReadOnlyDictionary<string, object?> action)
    {
        if (Equals(action.GetValueOrDefault("requires_target"), true)
            && !HasAny(action.GetValueOrDefault("valid_target_indices")))
        {
            return "no_valid_targets";
        }

        if (Equals(action.GetValueOrDefault("can_play"), true))
            return "available";
        if (Equals(action.GetValueOrDefault("can_play"), false))
            return "card_unplayable";
        return "playability_unknown";
    }

    private static bool HasAny(object? value)
        => ReflectionUtil.Enumerate(value, maxItems: 1).Any();

    private static IReadOnlyList<Dictionary<string, object?>>? SnapshotCombatTargetCandidates(StateSnapshot snapshot)
    {
        if (!TryGetDictionary(snapshot.RawSnapshot, "combat", out IReadOnlyDictionary<string, object?> combat)
            || !combat.TryGetValue("target_candidates", out object? rawTargets)
            || rawTargets == null)
        {
            return null;
        }

        return NormalizeTargetCandidateList(rawTargets);
    }

    private static IReadOnlyList<Dictionary<string, object?>>? NormalizeTargetCandidateList(object rawTargets)
    {
        if (rawTargets is IReadOnlyList<Dictionary<string, object?>> typedTargets)
            return typedTargets;

        if (rawTargets is not IEnumerable enumerable || rawTargets is string)
            return null;

        var targets = new List<Dictionary<string, object?>>();
        foreach (object? item in enumerable)
        {
            if (item is Dictionary<string, object?> dictionary)
            {
                targets.Add(dictionary);
            }
            else if (item is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                targets.Add(new Dictionary<string, object?>(readOnlyDictionary, StringComparer.Ordinal));
            }
        }

        return targets.Count == 0 ? null : targets;
    }

    private static bool TryGetDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key,
        out IReadOnlyDictionary<string, object?> value)
    {
        value = new Dictionary<string, object?>();
        if (!source.TryGetValue(key, out object? raw) || raw is not IReadOnlyDictionary<string, object?> dictionary)
            return false;

        value = dictionary;
        return true;
    }

    private static void AddShopActions(ICollection<Dictionary<string, object?>> actions, object? runState, object? localPlayer)
    {
        var shop = ResolveShopContext(runState, localPlayer);
        if (shop.Inventory == null)
        {
            AddShopExtractionGap(actions, "merchant_inventory_not_found");
            return;
        }

        int initialCount = actions.Count;
        var seenEntries = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AddShopEntryGroup(actions, seenEntries, shop, "character_card", "buy_shop_card", "CharacterCardEntries", ShopProjection.BuildLegalAction);
        AddShopEntryGroup(actions, seenEntries, shop, "colorless_card", "buy_shop_card", "ColorlessCardEntries", ShopProjection.BuildLegalAction);
        AddShopEntryGroup(actions, seenEntries, shop, "card", "buy_shop_card", "CardEntries", ShopProjection.BuildLegalAction);
        AddShopEntryGroup(actions, seenEntries, shop, "relic", "buy_shop_relic", "RelicEntries", ShopProjection.BuildLegalAction);
        AddShopEntryGroup(actions, seenEntries, shop, "potion", "buy_shop_potion", "PotionEntries", ShopProjection.BuildLegalAction);

        object? removalEntry = ReflectionUtil.GetMemberValue(shop.Inventory, "CardRemovalEntry");
        if (removalEntry != null)
            AddShopEntry(actions, "card_removal", "remove_card_at_shop", removalEntry, index: 0, shop.Player, ShopProjection.BuildLegalAction);

        if (actions.Count == initialCount)
            AddShopExtractionGap(actions, "no_purchasable_shop_entries_found");
    }

    private static void AddShopEntryGroup(
        ICollection<Dictionary<string, object?>> actions,
        ISet<object> seenEntries,
        ShopContext shop,
        string category,
        string actionType,
        string memberName,
        Func<object, string, string, int, object?, Dictionary<string, object?>?> projectEntry)
    {
        int index = 0;
        foreach (object? entry in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(shop.Inventory, memberName)))
        {
            if (entry == null)
            {
                index++;
                continue;
            }

            if (seenEntries.Add(entry))
                AddShopEntry(actions, category, actionType, entry, index, shop.Player, projectEntry);
            index++;
        }
    }

    private static void AddShopEntry(
        ICollection<Dictionary<string, object?>> actions,
        string category,
        string actionType,
        object entry,
        int index,
        object? player,
        Func<object, string, string, int, object?, Dictionary<string, object?>?> projectEntry)
    {
        Dictionary<string, object?>? action = projectEntry(entry, category, actionType, index, player);
        if (action != null)
            actions.Add(action);
    }

    private static ShopContext ResolveShopContext(object? runState, object? localPlayer)
    {
        object? currentRoom = ReflectionUtil.GetMemberValue(runState, "CurrentRoom");
        object? merchantRoom = IsTypeNamed(currentRoom, "MerchantRoom") ? currentRoom : null;
        object? player = localPlayer
            ?? ReflectionUtil.GetMemberValue(merchantRoom, "Player")
            ?? ReflectionUtil.GetMemberValue(runState, "Player");

        object? inventory = ResolveInventoryFromRoom(merchantRoom);
        if (inventory != null)
            return new ShopContext(inventory, player);

        object? merchantNode = ReflectionUtil.GetSingletonInstance(
            "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantRoom",
            "MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom");
        object? nodeRoom = ReflectionUtil.GetMemberValue(merchantNode, "MerchantRoom", "Room");
        inventory = ResolveInventoryFromRoom(IsTypeNamed(nodeRoom, "MerchantRoom") ? nodeRoom : null)
            ?? ResolveInventoryWrapper(merchantNode);
        player ??= ReflectionUtil.GetMemberValue(nodeRoom, "Player");
        if (inventory != null)
            return new ShopContext(inventory, player);

        object? inventoryNode = ReflectionUtil.GetSingletonInstance(
            "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory",
            "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NFakeMerchantInventory");
        inventory = ResolveInventoryWrapper(inventoryNode);
        player ??= ReflectionUtil.GetMemberValue(inventory, "Player");
        return new ShopContext(inventory, player);
    }

    private static object? ResolveInventoryFromRoom(object? merchantRoom)
        => ResolveInventoryWrapper(ReflectionUtil.GetMemberValue(merchantRoom, "Inventory", "MerchantInventory"));

    private static object? ResolveInventoryWrapper(object? value)
    {
        if (value == null)
            return null;

        object? nestedInventory = ReflectionUtil.GetMemberValue(value, "Inventory");
        return nestedInventory ?? value;
    }

    private static string? GetEntityId(object? value)
    {
        object? id = ReflectionUtil.GetMemberValue(value, "Id", "ID", "Key");
        return ReflectionUtil.GetText(id, "Entry", "Id", "Value", "Name")
            ?? ReflectionUtil.SafeText(id);
    }

    private object? ResolveRunManagerMember(params string[] memberNames)
        => _runManagerMemberFactory(memberNames);

    private static object? ResolveRuntimeRunManagerMember(string[] memberNames)
    {
        try
        {
            return ReflectionUtil.GetMemberValue(RunManager.Instance, memberNames);
        }
        catch
        {
            return null;
        }
    }

    private int? ResolveMapGenerationCount()
    {
        object? synchronizer = ResolveRunManagerMember("MapSelectionSynchronizer");
        return ReflectionUtil.GetInt(synchronizer, "MapGenerationCount");
    }

    private static Dictionary<string, object?>? ProjectMapPoint(object? point)
    {
        if (point == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["coord"] = ProjectMapCoord(ReflectionUtil.GetMemberValue(point, "coord", "Coord")),
            ["point_type"] = StableMemberText(point, "PointType", "RoomType", "Type")
        };
    }

    private static Dictionary<string, object?>? ProjectMapCoord(object? coord)
    {
        int? col = ReflectionUtil.GetInt(coord, "col", "Col", "Column", "X");
        int? row = ReflectionUtil.GetInt(coord, "row", "Row", "Y");
        if (col == null && row == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["col"] = col,
            ["row"] = row
        };
    }

    private static string? StableMemberText(object? target, params string[] memberNames)
    {
        object? value = memberNames.Length == 0 ? target : ReflectionUtil.GetMemberValue(target, memberNames);
        if (value == null)
            return null;

        Type type = value.GetType();
        if (type.IsEnum)
            return value.ToString();

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            bool boolValue => boolValue.ToString(),
            int intValue => intValue.ToString(),
            long longValue => longValue.ToString(),
            uint uintValue => uintValue.ToString(),
            ulong ulongValue => ulongValue.ToString(),
            _ => CombatProjection.GetModelEntry(value)
        };
    }

    private static bool IsRunStartEvent(object eventModel)
    {
        string typeName = eventModel.GetType().Name;
        if (typeName.Contains("Neow", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Ancient", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? eventId = GetEntityId(eventModel);
        return eventId?.Contains("NEOW", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool AllowsNonCombatPotionDiscard(string stateType)
        => stateType != "combat"
            && stateType != "menu"
            && stateType != "unknown"
            && stateType != "rewards"
            && stateType != "card_reward"
            && stateType != "card_select"
            && stateType != "bundle_select"
            && stateType != "relic_select"
            && stateType != "treasure"
            && stateType != "crystal_sphere"
            && stateType != "pack_select"
            && stateType != "special_select";

    private static bool HasWingedBoots(object? localPlayer)
    {
        foreach (object? relic in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(localPlayer, "Relics")))
        {
            string? relicId = GetEntityId(relic);
            if (string.Equals(relicId, "WINGED_BOOTS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relicId, "RELIC.WINGED_BOOTS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relicId, "WingedBoots", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<object?> EnumerateAllMapPoints(object? map)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object?>();

        foreach (object? root in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(map, "startMapPoints", "StartMapPoints")))
            queue.Enqueue(root);

        object? startingPoint = ReflectionUtil.GetMemberValue(map, "StartingMapPoint");
        if (startingPoint != null)
            queue.Enqueue(startingPoint);

        while (queue.Count > 0)
        {
            object? point = queue.Dequeue();
            if (point == null || !seen.Add(point))
                continue;

            yield return point;

            foreach (object? child in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(point, "Children")))
                queue.Enqueue(child);
        }
    }

    private static int? MapPointCoordValue(object? mapPoint, string key)
        => MapCoordValue(ReflectionUtil.GetMemberValue(mapPoint, "coord", "Coord"), key);

    private static int? MapCoordValue(object? coord, string key)
    {
        object? value = ReflectionUtil.GetMemberValue(coord, key, key.ToUpperInvariant());
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            _ => int.TryParse(value?.ToString(), out int parsed) ? parsed : null
        };
    }

    private static void AddSurfaceSpecificUnavailableActions(ICollection<Dictionary<string, object?>> actions, string stateType)
    {
        string actionType = stateType switch
        {
            "rewards" => "rewards_typed_builder_unavailable",
            "card_reward" => "card_reward_typed_builder_unavailable",
            "card_select" => "card_select_typed_builder_unavailable",
            "bundle_select" => "bundle_select_typed_builder_unavailable",
            "relic_select" => "relic_select_typed_builder_unavailable",
            "crystal_sphere" => "crystal_sphere_typed_builder_unavailable",
            "pack_select" => "pack_select_typed_builder_unavailable",
            "special_select" => "special_select_typed_builder_unavailable",
            _ => "typed_builder_unavailable"
        };

        AddUnavailableAction(actions, actionType, "legal_action_builder", stateType, "typed_runtime_surface_not_tracked",
            "legal action extraction for this decision surface requires a typed active runtime tracker; broad UI traversal is disabled");

        if (stateType is "rewards" or "card_reward")
        {
            AddUnavailableAction(actions, "potion_replace_typed_builder_unavailable", "legal_action_builder", stateType,
                "typed_potion_replacement_surface_not_found",
                "potion replacement requires a typed runtime decision surface; broad potion UI traversal is disabled");
            AddUnavailableAction(actions, "potion_full_slot_skip_unavailable", "legal_action_builder", stateType,
                "typed_potion_full_slot_skip_surface_not_found",
                "full-slot potion skip requires a typed runtime decision surface; broad potion UI traversal is disabled");
        }
    }

    private static void AddPendingTypedBuilderAction(ICollection<Dictionary<string, object?>> actions, string stateType)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "typed_builder_pending",
            ["source"] = "legal_action_builder",
            ["availability"] = "typed_builder_pending",
            ["state_type"] = stateType,
            ["message"] = "legal action extraction for this decision surface requires a typed runtime builder"
        });
    }

    private static void AddUnavailableAction(
        ICollection<Dictionary<string, object?>> actions,
        string actionType,
        string source,
        string stateType,
        string availability,
        string message)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = actionType,
            ["source"] = source,
            ["availability"] = availability,
            ["state_type"] = stateType,
            ["message"] = message
        });
    }

    private static void AddShopExtractionGap(ICollection<Dictionary<string, object?>> actions, string reason)
    {
        actions.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "shop_typed_builder_unavailable",
            ["source"] = "merchant_inventory",
            ["availability"] = reason,
            ["state_type"] = "shop",
            ["message"] = "shop legal actions require typed merchant inventory members; broad Godot UI traversal is disabled"
        });
    }

    private static void AddShopOffersExtractionGap(ICollection<Dictionary<string, object?>> offers, string reason)
    {
        offers.Add(new Dictionary<string, object?>
        {
            ["action_type"] = "shop_offers_typed_builder_unavailable",
            ["source"] = "merchant_inventory",
            ["availability"] = reason,
            ["state_type"] = "shop",
            ["message"] = "shop offers require typed merchant inventory members; broad Godot UI traversal is disabled"
        });
    }

    private static void AddRewardCacheUnavailableAction(ICollection<Dictionary<string, object?>> actions, string stateType)
    {
        string actionType = stateType switch
        {
            "rewards" => "rewards_typed_builder_unavailable",
            "card_reward" => "card_reward_typed_builder_unavailable",
            _ => "typed_builder_unavailable"
        };

        AddUnavailableAction(actions, actionType, "reward_runtime_cache", stateType, "typed_reward_runtime_cache_not_populated",
            "reward legal actions require a telemetry-owned typed RewardsSet/CardReward cache; broad reward UI traversal is disabled");

        if (stateType is "rewards" or "card_reward")
        {
            AddUnavailableAction(actions, "potion_replace_typed_builder_unavailable", "legal_action_builder", stateType,
                "typed_potion_replacement_surface_not_found",
                "potion replacement requires a typed runtime decision surface; broad potion UI traversal is disabled");
            AddUnavailableAction(actions, "potion_full_slot_skip_unavailable", "legal_action_builder", stateType,
                "typed_potion_full_slot_skip_surface_not_found",
                "full-slot potion skip requires a typed runtime decision surface; broad potion UI traversal is disabled");
        }
    }

    private static bool IsTypeNamed(object? value, string typeName)
        => string.Equals(value?.GetType().Name, typeName, StringComparison.Ordinal);

    internal readonly record struct CombatAvailability(
        bool CanBuild,
        string Availability,
        bool? IsInProgress,
        bool? IsPlayPhase,
        bool? PlayerActionsDisabled);

    private readonly record struct ShopContext(object? Inventory, object? Player);
}
