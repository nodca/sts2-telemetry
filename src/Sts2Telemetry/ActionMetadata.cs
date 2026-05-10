using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Telemetry;

internal static class ActionMetadata
{
    private static readonly string[] RuntimeActionMembers =
    {
        "HookId",
        "ChoiceId",
        "ChoiceContext",
        "MapCoord",
        "Coord",
        "Card",
        "Potion",
        "Target",
        "Player",
        "Reward",
        "Choice",
        "Option"
    };

    public static IReadOnlyDictionary<string, object?> FromGameAction(GameAction action, StateSnapshot? preState = null)
        => FromRuntimeAction(action, "action_executor", preState);

    internal static IReadOnlyDictionary<string, object?> BuildNormalizedTypedActionKey(
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        string actionType = NormalizedTypedActionType(selectedAction);
        var key = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_type"] = actionType
        };

        switch (actionType)
        {
            case "play_card":
                AddPlayCardActionKey(key, selectedAction);
                break;
            case "use_potion":
                AddUsePotionActionKey(key, selectedAction);
                break;
            case "discard_potion":
                AddDiscardPotionActionKey(key, selectedAction);
                break;
            case "choose_treasure_relic":
            case "skip_treasure_relic":
                AddTreasureRelicActionKey(key, selectedAction, actionType);
                break;
            case "choose_relic_select":
            case "skip_relic_select":
                AddRelicSelectActionKey(key, selectedAction, actionType);
                break;
            case "choose_card_bundle":
                AddBundleSelectActionKey(key, selectedAction);
                break;
            case "choose_event_option":
            case "proceed_event":
                AddEventOptionActionKey(key, selectedAction);
                break;
            case "choose_rest_option":
                AddRestOptionActionKey(key, selectedAction);
                break;
            case "choose_map_node":
            case "cancel_map_vote":
                AddMapActionKey(key, selectedAction);
                break;
            case "buy_shop_card":
            case "buy_shop_relic":
            case "buy_shop_potion":
            case "remove_card_at_shop":
            case "shop_purchase":
                AddShopActionKey(key, selectedAction);
                break;
            case "claim_reward":
            case "skip_reward":
            case "choose_reward_card":
            case "skip_card_reward":
            case "reroll_card_reward":
            case "sacrifice_reward_card":
                AddRewardActionKey(key, selectedAction, actionType);
                break;
        }

        return key;
    }

    public static IReadOnlyDictionary<string, object?> FromActionExecutorSignal(object action, string phase)
    {
        Type actionType = action.GetType();
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = "action_executor",
            ["phase"] = phase,
            ["runtime_type"] = actionType.FullName,
            ["runtime_type_name"] = actionType.Name,
            ["action_type"] = InferRuntimeActionTypeFromTypeName(actionType.Name),
            ["projection_policy"] = "type_only_action_executor_signal",
            ["net_action_projection"] = "skipped_signal_only_runtime_safety"
        };

        AddMapActionSignalMetadata(metadata, action);
        return metadata;
    }

    internal static IReadOnlyDictionary<string, object?> FromRuntimeAction(
        object action,
        string source,
        StateSnapshot? preState = null)
    {
        Type actionType = action.GetType();
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["runtime_type"] = actionType.FullName,
            ["runtime_type_name"] = actionType.Name,
            ["action_type"] = GetRuntimeActionType(action),
            ["projection_policy"] = "shallow_runtime_action",
            ["net_action_projection"] = IsGenericHookGameAction(actionType)
                ? "skipped_generic_hook_runtime_safety"
                : "skipped_runtime_safety"
        };

        if (IsGenericHookGameAction(actionType))
            metadata["action_family"] = "generic_hook";

        foreach (string memberName in RuntimeActionMembers)
        {
            object? value = ReflectionUtil.GetMemberValue(action, memberName);
            if (value == null)
                continue;

            metadata[ToSnakeCase(memberName)] = ProjectRuntimeActionMember(value);
        }

        AddCombatActionMetadata(metadata, action, preState);
        return metadata;
    }

    public static IReadOnlyDictionary<string, object?> FromPatchedMethod(string source, object? instance, object?[] args)
    {
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["action_type"] = InferActionType(source),
            ["runtime_type"] = instance?.GetType().FullName,
            ["argument_count"] = args.Length
        };

        if (TryProjectStableScalar(instance, out object? display))
            metadata["display"] = display;

        var projectedArgs = new List<object?>();
        for (int i = 0; i < args.Length; i++)
            projectedArgs.Add(ProjectPatchedArgument(args[i], i));

        metadata["arguments"] = projectedArgs;
        AddShopPatchedSelectionMetadata(metadata, source, instance, args);
        AddCardRewardPatchedSelectionMetadata(metadata, source, instance, args);
        AddTypedPatchedSelectionMetadata(metadata, source, args);
        AddRewardPatchedSelectionMetadata(metadata, source, instance);
        AddPlayerChoiceMetadata(metadata, source, args);
        return metadata;
    }

    private static object? ProjectPatchedArgument(object? arg, int index)
    {
        if (arg == null)
            return null;

        var projected = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["type"] = arg.GetType().FullName
        };

        if (IsIdentityLikeObject(arg))
        {
            projected["identity"] = "redacted";
            return projected;
        }

        if (TryProjectStableScalar(arg, out object? value))
        {
            projected["value"] = value;
            return projected;
        }

        projected["projection_policy"] = "type_only";
        projected["reason"] = "patched UI callback arguments may be live Godot/runtime objects during transitions";
        return projected;
    }

    private static string InferActionType(string source)
    {
        if (source.Contains("map", StringComparison.OrdinalIgnoreCase))
            return "choose_map_node";
        if (source.Contains("rest", StringComparison.OrdinalIgnoreCase))
            return "choose_rest_option";
        if (source.Contains("merchant", StringComparison.OrdinalIgnoreCase)
            || source.Contains("shop", StringComparison.OrdinalIgnoreCase))
            return "shop_purchase";
        if (source.Contains("card", StringComparison.OrdinalIgnoreCase))
            return source.Contains("skip", StringComparison.OrdinalIgnoreCase)
                ? "skip_card_selection"
                : "select_card";
        if (source.Contains("reward", StringComparison.OrdinalIgnoreCase))
            return "claim_reward";
        if (source.Contains("event", StringComparison.OrdinalIgnoreCase))
            return "choose_event_option";
        return "ui_action";
    }

    private static string InferRuntimeActionTypeFromTypeName(string runtimeTypeName)
    {
        string actionName = runtimeTypeName;
        if (actionName.EndsWith("GameAction", StringComparison.Ordinal))
            actionName = actionName[..^"GameAction".Length];
        else if (actionName.EndsWith("Action", StringComparison.Ordinal))
            actionName = actionName[..^"Action".Length];

        if (string.IsNullOrWhiteSpace(actionName))
            actionName = runtimeTypeName;

        return ToSnakeCase(actionName);
    }

    private static bool IsIdentityLikeObject(object? value)
    {
        string? typeName = value?.GetType().FullName;
        return typeName?.Contains(".Player", StringComparison.Ordinal) == true
            || typeName?.EndsWith("Player", StringComparison.Ordinal) == true
            || typeName?.Contains("Profile", StringComparison.OrdinalIgnoreCase) == true
            || typeName?.Contains("Account", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetRuntimeActionType(object action)
    {
        object? value = ReflectionUtil.GetMemberValue(action, "ActionType");
        if (TryProjectStableScalar(value, out object? scalar))
            return scalar?.ToString() ?? action.GetType().Name;

        return action.GetType().Name;
    }

    private static object? ProjectRuntimeActionMember(object? value)
    {
        if (value == null)
            return null;

        if (TryProjectStableScalar(value, out object? scalar))
            return scalar;

        var projection = new Dictionary<string, object?>
        {
            ["type"] = value.GetType().FullName,
            ["projection_policy"] = "type_only",
            ["reason"] = "runtime actions can hold live STS2/Godot objects during transitions"
        };

        if (IsIdentityLikeObject(value))
        {
            projection["identity"] = "redacted";
            return projection;
        }

        if (TryGetStableScalarMember(value, out object? id, "Id", "ID", "Key"))
            projection["id"] = id;

        return projection;
    }

    private static void AddCombatActionMetadata(
        IDictionary<string, object?> metadata,
        object action,
        StateSnapshot? preState)
    {
        string runtimeTypeName = action.GetType().Name;
        if (runtimeTypeName.EndsWith("PlayCardAction", StringComparison.Ordinal))
        {
            AddPlayCardActionMetadata(metadata, action, preState);
            return;
        }

        if (runtimeTypeName.EndsWith("UsePotionAction", StringComparison.Ordinal))
        {
            AddUsePotionActionMetadata(metadata, action, preState);
            return;
        }

        if (runtimeTypeName.EndsWith("DiscardPotionGameAction", StringComparison.Ordinal))
        {
            AddDiscardPotionActionMetadata(metadata, action, preState);
            return;
        }

        if (runtimeTypeName.EndsWith("PickRelicAction", StringComparison.Ordinal))
        {
            AddPickRelicActionMetadata(metadata, action);
            return;
        }

        if (runtimeTypeName.EndsWith("EndPlayerTurnAction", StringComparison.Ordinal))
            metadata["combat_action_extraction"] = "end_turn_no_additional_metadata";
    }

    private static void AddPlayCardActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        string? cardId = StringValue(selectedAction, "card_model_id")
            ?? StringValue(selectedAction, "card_id");
        MaybeSet(key, "card_id", cardId);

        int? handIndex = ToInt(FirstPresent(selectedAction, "card_index", "hand_index"));
        if (handIndex != null && PreStateCardMatchWasUnique(selectedAction))
            key["hand_index"] = handIndex.Value;

        string? cardTargetType = StringValue(selectedAction, "card_target_type");
        bool runtimeTargetObserved = ToInt(FirstPresent(selectedAction, "target_id")) != null;
        if (CombatProjection.RequiresTarget(cardTargetType)
            || (runtimeTargetObserved && string.IsNullOrWhiteSpace(cardTargetType)))
            MaybeAddMatchedTargetKey(key, selectedAction);
    }

    private static void AddUsePotionActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        MaybeSet(key, "potion_id", StringValue(selectedAction, "potion_id"));

        int? slot = ToInt(FirstPresent(selectedAction, "slot", "potion_index"));
        if (slot != null)
            key["slot"] = slot.Value;

        string? potionTargetType = StringValue(selectedAction, "potion_target_type");
        if (CombatProjection.RequiresTarget(potionTargetType))
            MaybeAddMatchedTargetKey(key, selectedAction);
    }

    private static void AddDiscardPotionActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        int? slot = ToInt(FirstPresent(selectedAction, "slot", "potion_index"));
        if (slot != null)
            key["slot"] = slot.Value;

        MaybeSet(key, "potion_id", StringValue(selectedAction, "potion_id"));
    }

    private static void AddTreasureRelicActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction,
        string actionType)
    {
        int? relicIndex = ToInt(FirstPresent(selectedAction, "relic_index"));
        if (relicIndex != null)
            key["relic_index"] = relicIndex.Value;
        else if (actionType == "skip_treasure_relic" && selectedAction.ContainsKey("relic_index"))
            key["relic_index"] = null;

        MaybeSet(key, "relic_id", StringValue(selectedAction, "relic_id"));
    }

    private static void AddRelicSelectActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction,
        string actionType)
    {
        int? relicIndex = ToInt(FirstPresent(selectedAction, "relic_index", "selected_index", "option_index"));
        if (relicIndex != null && relicIndex.Value >= 0)
            key["relic_index"] = relicIndex.Value;
        else if (actionType == "skip_relic_select" && selectedAction.ContainsKey("relic_index"))
            key["relic_index"] = null;

        MaybeSet(key, "relic_id", StringValue(selectedAction, "relic_id"));
    }

    private static void AddBundleSelectActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        int? bundleIndex = ToInt(FirstPresent(selectedAction, "bundle_index", "selected_index", "option_index"));
        if (bundleIndex != null && bundleIndex.Value >= 0)
            key["bundle_index"] = bundleIndex.Value;
    }

    private static void AddEventOptionActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        MaybeSet(key, "event_id", StringValue(selectedAction, "event_id"));
        int? optionIndex = ToInt(FirstPresent(selectedAction, "option_index", "selected_option_index"));
        if (optionIndex != null)
            key["option_index"] = optionIndex.Value;
        MaybeSet(key, "option_text_key", StringValue(selectedAction, "option_text_key"));
    }

    private static void AddRestOptionActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        int? optionIndex = ToInt(FirstPresent(selectedAction, "option_index", "selected_option_index"));
        if (optionIndex != null)
            key["option_index"] = optionIndex.Value;
        MaybeSet(key, "option_id", StringValue(selectedAction, "option_id"));
    }

    private static void AddMapActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        if (FirstPresent(selectedAction, "destination_coord", "coord") is IReadOnlyDictionary<string, object?> coord)
            key["coord"] = coord;
        MaybeSet(key, "map_generation_count", ToInt(FirstPresent(selectedAction, "map_generation_count")));
    }

    private static void AddShopActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        MaybeSet(key, "category", StringValue(selectedAction, "category"));
        MaybeSet(key, "id", StringValue(selectedAction, "id"));
        MaybeSet(key, "card_id", StringValue(selectedAction, "card_id"));
        MaybeSet(key, "relic_id", StringValue(selectedAction, "relic_id"));
        MaybeSet(key, "potion_id", StringValue(selectedAction, "potion_id"));
        MaybeSet(key, "removal_id", StringValue(selectedAction, "removal_id"));

        int? index = ToInt(FirstPresent(selectedAction, "index"));
        if (index != null)
            key["index"] = index.Value;
    }

    private static void AddRewardActionKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction,
        string actionType)
    {
        if (selectedAction.ContainsKey("reward_index"))
        {
            object? rewardIndex = FirstPresent(selectedAction, "reward_index");
            key["reward_index"] = rewardIndex == null ? null : ToInt(rewardIndex);
        }

        MaybeSet(key, "reward_type", StringValue(selectedAction, "reward_type"));
        MaybeSet(key, "reward_id", StringValue(selectedAction, "reward_id"));
        MaybeSet(key, "card_id", StringValue(selectedAction, "card_id"));
        MaybeSet(key, "relic_id", StringValue(selectedAction, "relic_id"));
        MaybeSet(key, "potion_id", StringValue(selectedAction, "potion_id"));
        MaybeSet(key, "removal_id", StringValue(selectedAction, "removal_id"));

        int? goldAmount = ToInt(FirstPresent(selectedAction, "gold_amount"));
        if (goldAmount != null)
            key["gold_amount"] = goldAmount.Value;

        if (actionType == "choose_reward_card")
        {
            int? cardIndex = ToInt(FirstPresent(selectedAction, "card_index", "option_index", "index"));
            if (cardIndex != null)
                key["card_index"] = cardIndex.Value;
        }

        if (actionType is "skip_card_reward" or "reroll_card_reward")
        {
            MaybeSet(key, "alternative_id",
                StringValue(selectedAction, "alternative_id") ?? StringValue(selectedAction, "option_id"));
        }

        if (actionType == "sacrifice_reward_card")
        {
            int? cardIndex = ToInt(FirstPresent(selectedAction, "card_index", "option_index", "index"));
            if (cardIndex != null)
                key["card_index"] = cardIndex.Value;

            MaybeSet(key, "relic_id", StringValue(selectedAction, "relic_id"));
            MaybeSet(key, "special_option_kind", StringValue(selectedAction, "special_option_kind"));
        }
    }

    private static void MaybeAddMatchedTargetKey(
        IDictionary<string, object?> key,
        IReadOnlyDictionary<string, object?> selectedAction)
    {
        bool hasMatchedTarget = FirstPresent(
            selectedAction,
            "target_index",
            "target_index_space",
            "target_entity_id") != null;
        if (!hasMatchedTarget)
            return;

        var target = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        MaybeSet(target, "target_id", ToInt(FirstPresent(selectedAction, "target_id")));
        MaybeSet(target, "target_index_space", StringValue(selectedAction, "target_index_space"));
        MaybeSet(target, "target_index", ToInt(FirstPresent(selectedAction, "target_index")));
        MaybeSet(target, "target_entity_id", StringValue(selectedAction, "target_entity_id"));

        if (target.Count > 0)
            key["target"] = target;
    }

    private static string NormalizedTypedActionType(IReadOnlyDictionary<string, object?> selectedAction)
    {
        string? runtimeTypeName = StringValue(selectedAction, "runtime_type_name");
        if (!string.IsNullOrWhiteSpace(runtimeTypeName))
        {
            if (runtimeTypeName.EndsWith("PlayCardAction", StringComparison.Ordinal))
                return "play_card";
            if (runtimeTypeName.EndsWith("UsePotionAction", StringComparison.Ordinal))
                return "use_potion";
            if (runtimeTypeName.EndsWith("EndPlayerTurnAction", StringComparison.Ordinal))
                return "end_turn";
            if (runtimeTypeName.EndsWith("DiscardPotionGameAction", StringComparison.Ordinal))
                return "discard_potion";
            if (runtimeTypeName.EndsWith("PickRelicAction", StringComparison.Ordinal))
            {
                string? selectionKind = StringValue(selectedAction, "selection_kind");
                if (selectionKind is "choose_treasure_relic" or "skip_treasure_relic")
                    return selectionKind;
                if (selectedAction.ContainsKey("relic_index"))
                    return FirstPresent(selectedAction, "relic_index") == null
                        ? "skip_treasure_relic"
                        : "choose_treasure_relic";
                return "pick_relic";
            }

            if (runtimeTypeName.EndsWith("VoteForMapCoordAction", StringComparison.Ordinal))
            {
                string? mapActionType = StringValue(selectedAction, "action_type");
                if (mapActionType is "choose_map_node" or "cancel_map_vote")
                    return mapActionType;
            }

            return InferRuntimeActionTypeFromTypeName(runtimeTypeName);
        }

        string? actionType = StringValue(selectedAction, "action_type");
        return string.IsNullOrWhiteSpace(actionType) ? "unknown" : ToSnakeCase(actionType);
    }

    private static bool PreStateCardMatchWasUnique(IReadOnlyDictionary<string, object?> selectedAction)
        => ExtractionStatusEquals(
                selectedAction,
                "pre_state_card",
                "matched_pre_state_hand_unique_card_id")
            || ExtractionStatusEquals(
                selectedAction,
                "card",
                "matched_pre_state_hand_unique_card_id");

    private static bool ExtractionStatusEquals(
        IReadOnlyDictionary<string, object?> selectedAction,
        string key,
        string expected)
    {
        if (!selectedAction.TryGetValue("extraction_status", out object? status)
            || status is not IReadOnlyDictionary<string, object?> statusDictionary
            || !statusDictionary.TryGetValue(key, out object? value)
            || value is not string text)
        {
            return false;
        }

        return string.Equals(text, expected, StringComparison.Ordinal);
    }

    private static void AddPlayCardActionMetadata(
        IDictionary<string, object?> metadata,
        object action,
        StateSnapshot? preState)
    {
        var status = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        object? netCombatCard = ReflectionUtil.GetMemberValue(action, "NetCombatCard");
        string? cardModelId = CombatProjection.GetModelEntry(ReflectionUtil.GetMemberValue(action, "CardModelId"));
        int? combatCardIndex = ToInt(
            ReflectionUtil.GetMemberValue(netCombatCard, "CombatCardIndex"));
        int? targetId = ReflectionUtil.GetInt(action, "TargetId");

        MaybeSet(metadata, "card_model_id", cardModelId);
        MaybeSet(metadata, "net_combat_card_index", combatCardIndex);
        MaybeSet(metadata, "target_id", targetId);

        status["card_model_id"] = cardModelId == null ? "unavailable" : "extracted";
        status["net_combat_card_index"] = combatCardIndex == null ? "unavailable" : "extracted";
        status["target_id"] = targetId == null ? "no_target" : "extracted";

        status["card_index_match"] = combatCardIndex == null
            ? "net_combat_card_index_unavailable"
            : "skipped_net_combat_card_index_runtime_identity";

        bool handAvailable = TrySnapshotList(preState, out var handCards, "local_player", "hand");
        var handMatches = handAvailable
            ? FindPreStateCards(handCards, cardModelId).ToArray()
            : Array.Empty<IReadOnlyDictionary<string, object?>>();

        if (!handAvailable)
        {
            status["card"] = "pre_state_hand_unavailable";
            status["pre_state_card"] = "pre_state_hand_unavailable";
        }
        else if (handMatches.Length == 1)
        {
            CopyCardMetadata(metadata, handMatches[0]);
            status["card"] = "matched_pre_state_hand_unique_card_id";
            status["pre_state_card"] = "matched_pre_state_hand_unique_card_id";
        }
        else if (handMatches.Length > 1)
        {
            status["card_match_count"] = handMatches.Length;
            status["card"] = "ambiguous_pre_state_hand_card_id_match";
            status["pre_state_card"] = "ambiguous_pre_state_hand_card_id_match";
        }
        else
        {
            status["card"] = cardModelId == null
                ? "card_model_id_unavailable"
                : "pre_state_hand_card_id_match_not_found";
            status["pre_state_card"] = cardModelId == null
                ? "card_model_id_unavailable"
                : "pre_state_hand_card_id_match_not_found";
        }

        string? cardTargetType = SelectedCardTargetType(metadata);
        if (targetId == null)
        {
            status["target"] = "no_target";
        }
        else if (string.IsNullOrWhiteSpace(cardTargetType))
        {
            if (FindPreStateTarget(preState, targetId.Value) is { } target)
            {
                CopyTargetMetadata(metadata, target);
                status["target"] = "matched_pre_state_target_candidates_without_card_target_type";
                status["target_card_target_type"] = "unavailable";
            }
            else
            {
                status["target"] = "pre_state_target_match_not_found_without_card_target_type";
                status["target_card_target_type"] = "unavailable";
            }
        }
        else if (!CombatProjection.RequiresTarget(cardTargetType))
        {
            status["target"] = string.IsNullOrWhiteSpace(cardTargetType)
                ? "suppressed_selected_card_target_type_unavailable"
                : "suppressed_selected_card_target_type";
            status["target_card_target_type"] = cardTargetType ?? "unavailable";
        }
        else if (FindPreStateTarget(preState, targetId.Value) is { } target)
        {
            CopyTargetMetadata(metadata, target);
            status["target"] = "matched_pre_state_target_candidates";
        }
        else
        {
            status["target"] = "pre_state_target_match_not_found";
        }

        metadata["extraction_status"] = status;
    }

    private static void AddUsePotionActionMetadata(
        IDictionary<string, object?> metadata,
        object action,
        StateSnapshot? preState)
    {
        var status = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        int? potionIndex = ReflectionUtil.GetInt(action, "PotionIndex");
        int? targetId = ReflectionUtil.GetInt(action, "TargetId");

        MaybeSet(metadata, "slot", potionIndex);
        MaybeSet(metadata, "potion_index", potionIndex);
        MaybeSet(metadata, "target_id", targetId);
        MaybeSet(metadata, "was_enqueued_in_combat", ReflectionUtil.GetBool(action, "WasEnqueuedInCombat"));
        status["potion_index"] = potionIndex == null ? "unavailable" : "extracted";
        status["target_id"] = targetId == null ? "no_target" : "extracted";

        if (potionIndex == null)
        {
            status["potion"] = "potion_index_unavailable";
        }
        else if (!TrySnapshotList(preState, out var potions, "local_player", "potions"))
        {
            status["potion"] = "pre_state_potions_unavailable";
        }
        else if (FindPreStatePotionBySlot(potions, potionIndex.Value) is { } potion)
        {
            CopyPotionMetadata(metadata, potion);
            metadata["potion"] = potion;
            status["potion"] = "matched_pre_state_potions_slot";
        }
        else
        {
            status["potion"] = "pre_state_potion_slot_match_not_found";
        }

        if (targetId == null)
        {
            status["target"] = "no_target";
        }
        else if (FindPreStateTarget(preState, targetId.Value) is { } target)
        {
            CopyTargetMetadata(metadata, target);
            status["target"] = "matched_pre_state_target_candidates";
        }
        else
        {
            status["target"] = "pre_state_target_match_not_found";
        }

        metadata["extraction_status"] = status;
    }

    private static void AddDiscardPotionActionMetadata(
        IDictionary<string, object?> metadata,
        object action,
        StateSnapshot? preState)
    {
        var status = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        bool hasSlot = ReflectionUtil.TryReadMemberValue(action, out object? slotValue, "_potionSlotIndex", "PotionSlotIndex", "PotionIndex");
        int? slot = ToInt(slotValue);

        MaybeSet(metadata, "slot", slot);
        MaybeSet(metadata, "potion_index", slot);
        MaybeSet(metadata, "was_enqueued_in_combat", ReflectionUtil.GetBool(action, "WasEnqueuedInCombat"));
        status["potion_slot_index"] = !hasSlot
            ? "unavailable"
            : slot == null
                ? "unavailable"
                : "extracted";

        if (slot == null)
        {
            status["potion"] = "potion_slot_index_unavailable";
        }
        else if (!TrySnapshotList(preState, out var potions, "local_player", "potions"))
        {
            status["potion"] = "pre_state_potions_unavailable";
        }
        else if (FindPreStatePotionBySlot(potions, slot.Value) is { } potion)
        {
            CopyPotionMetadata(metadata, potion);
            metadata["potion"] = potion;
            status["potion"] = "matched_pre_state_potions_slot";
        }
        else
        {
            status["potion"] = "pre_state_potion_slot_match_not_found";
        }

        metadata["extraction_status"] = status;
    }

    private static void AddPickRelicActionMetadata(
        IDictionary<string, object?> metadata,
        object action)
    {
        var status = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        bool hasRelicIndex = ReflectionUtil.TryReadMemberValue(action, out object? relicIndexValue, "_relicIndex", "RelicIndex");
        int? relicIndex = ToInt(relicIndexValue);

        if (hasRelicIndex)
        {
            metadata["relic_index"] = relicIndex;
            metadata["selection_kind"] = relicIndex == null ? "skip_treasure_relic" : "choose_treasure_relic";
        }

        status["relic_index"] = !hasRelicIndex
            ? "unavailable"
            : relicIndex == null
                ? "extracted_null_skip"
                : "extracted";

        if (!hasRelicIndex)
        {
            status["relic"] = "relic_index_unavailable";
        }
        else if (relicIndex == null)
        {
            status["relic"] = "skip_no_relic";
        }
        else if (FindCurrentTreasureRelic(action, relicIndex.Value) is { } relic)
        {
            CopyRelicMetadata(metadata, relic);
            status["relic"] = "matched_current_treasure_relics_index";
        }
        else
        {
            status["relic"] = "current_treasure_relic_index_match_not_found";
        }

        metadata["extraction_status"] = status;
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> FindPreStateCards(
        IEnumerable<IReadOnlyDictionary<string, object?>> handCards,
        string? cardId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
            yield break;

        foreach (IReadOnlyDictionary<string, object?> card in handCards)
        {
            object? projectedId = FirstPresent(card, "card_id", "id", "card_model_id");
            if (string.Equals(projectedId as string, cardId, StringComparison.Ordinal))
                yield return card;
        }
    }

    private static IReadOnlyDictionary<string, object?>? FindPreStatePotionBySlot(
        IEnumerable<IReadOnlyDictionary<string, object?>> potions,
        int slot)
    {
        foreach (IReadOnlyDictionary<string, object?> potion in potions)
        {
            object? projectedSlot = FirstPresent(potion, "slot", "potion_index", "index");
            if (ToInt(projectedSlot) == slot)
                return potion;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?>? FindPreStateTarget(StateSnapshot? preState, int targetId)
    {
        foreach (IReadOnlyDictionary<string, object?> target in SnapshotList(preState, "combat", "target_candidates"))
        {
            if (target.TryGetValue("combat_id", out object? combatId)
                && ToInt(combatId) == targetId)
                return target;
        }

        if (FindPreStateEntityTarget(preState, targetId, "enemies", "enemy", "enemies") is { } enemy)
            return enemy;
        if (FindPreStateEntityTarget(preState, targetId, "players", "player", "players") is { } player)
            return player;

        return null;
    }

    private static IReadOnlyDictionary<string, object?>? FindPreStateEntityTarget(
        StateSnapshot? preState,
        int targetId,
        string snapshotPath,
        string targetType,
        string targetIndexSpace)
    {
        int index = 0;
        foreach (IReadOnlyDictionary<string, object?> entity in SnapshotList(preState, "combat", snapshotPath))
        {
            if (ToInt(FirstPresent(entity, "combat_id", "target_id")) == targetId)
            {
                var target = new Dictionary<string, object?>(entity, StringComparer.Ordinal)
                {
                    ["target_index_space"] = targetIndexSpace,
                    ["target_index"] = ToInt(FirstPresent(entity, "target_index", "index")) ?? index,
                    ["target_type"] = StringValue(entity, "target_type") ?? targetType,
                    ["entity_id"] = StringValue(entity, "entity_id")
                        ?? StringValue(entity, "enemy_id")
                        ?? StringValue(entity, "id")
                        ?? $"{targetType}_{index}",
                    ["combat_id"] = targetId
                };
                return target;
            }

            index++;
        }

        return null;
    }

    private static int? ToInt(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                int intValue => intValue,
                long longValue => checked((int)longValue),
                short shortValue => shortValue,
                byte byteValue => byteValue,
                uint uintValue => checked((int)uintValue),
                ulong ulongValue => checked((int)ulongValue),
                float floatValue => Convert.ToInt32(floatValue),
                double doubleValue => Convert.ToInt32(doubleValue),
                decimal decimalValue => decimal.ToInt32(decimalValue),
                string text when int.TryParse(text, out int parsed) => parsed,
                _ => Convert.ToInt32(value)
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> SnapshotList(
        StateSnapshot? preState,
        params string[] path)
    {
        if (!TrySnapshotList(preState, out var items, path))
            yield break;

        foreach (IReadOnlyDictionary<string, object?> item in items)
            yield return item;
    }

    private static bool TrySnapshotList(
        StateSnapshot? preState,
        out IReadOnlyList<IReadOnlyDictionary<string, object?>> items,
        params string[] path)
    {
        items = Array.Empty<IReadOnlyDictionary<string, object?>>();
        object? current = preState?.RawSnapshot;
        if (current == null)
            return false;

        foreach (string segment in path)
        {
            if (current is IReadOnlyDictionary<string, object?> readOnly
                && readOnly.TryGetValue(segment, out object? next))
            {
                current = next;
                continue;
            }

            if (current is IDictionary<string, object?> dictionary
                && dictionary.TryGetValue(segment, out next))
            {
                current = next;
                continue;
            }

            return false;
        }

        if (current is System.Collections.IEnumerable enumerable && current is not string)
        {
            var result = new List<IReadOnlyDictionary<string, object?>>();
            foreach (object? item in enumerable)
            {
                if (item is IReadOnlyDictionary<string, object?> readOnlyItem)
                    result.Add(readOnlyItem);
                else if (item is IDictionary<string, object?> dictionaryItem)
                    result.Add(new Dictionary<string, object?>(dictionaryItem));
            }

            items = result;
            return true;
        }

        return false;
    }

    private static object? FirstPresent(IReadOnlyDictionary<string, object?> source, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (source.TryGetValue(key, out object? value) && value != null)
                return value;
        }

        return null;
    }

    private static string? StringValue(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out object? value) || value is not string text)
            return null;

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void CopyIfPresent(
        IDictionary<string, object?> target,
        string targetKey,
        IReadOnlyDictionary<string, object?> source,
        params string[] sourceKeys)
    {
        object? value = FirstPresent(source, sourceKeys);
        if (value != null)
            target[targetKey] = value;
    }

    private static void CopyCardMetadata(
        IDictionary<string, object?> metadata,
        IReadOnlyDictionary<string, object?> card)
    {
        CopyIfPresent(metadata, "card_index", card, "card_index", "index");
        CopyIfPresent(metadata, "card_id", card, "card_id", "id");
        CopyIfPresent(metadata, "card_name", card, "card_name", "name");
        CopyIfPresent(metadata, "card_type", card, "card_type", "type");
        CopyIfPresent(metadata, "card_target_type", card, "target_type");
        metadata["card"] = card;
    }

    private static string? SelectedCardTargetType(IDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue("card_target_type", out object? value) && value is string cardTargetType)
            return cardTargetType;
        if (metadata.TryGetValue("target_type", out value) && value is string targetType)
            return targetType;
        return null;
    }

    private static void CopyTargetMetadata(
        IDictionary<string, object?> metadata,
        IReadOnlyDictionary<string, object?> target)
    {
        metadata["target"] = target;
        CopyIfPresent(metadata, "target_index", target, "target_index");
        CopyIfPresent(metadata, "target_index_space", target, "target_index_space");
        CopyIfPresent(metadata, "target_entity_id", target, "entity_id");
        CopyIfPresent(metadata, "target_type", target, "target_type");
        CopyIfPresent(metadata, "target_name", target, "name");
    }

    private static void CopyPotionMetadata(
        IDictionary<string, object?> metadata,
        IReadOnlyDictionary<string, object?> potion)
    {
        CopyIfPresent(metadata, "slot", potion, "slot", "potion_index", "index");
        CopyIfPresent(metadata, "potion_index", potion, "potion_index", "slot", "index");
        CopyIfPresent(metadata, "potion_id", potion, "potion_id", "id");
        CopyIfPresent(metadata, "potion_name", potion, "potion_name", "name");
        CopyIfPresent(metadata, "target_type", potion, "target_type");
        CopyIfPresent(metadata, "potion_target_type", potion, "target_type");
        CopyIfPresent(metadata, "usage", potion, "usage");
    }

    private static void CopyRelicMetadata(IDictionary<string, object?> metadata, object relic)
    {
        MaybeSet(metadata, "relic_id", CombatProjection.GetModelEntry(ReflectionUtil.GetMemberValue(relic, "Id", "ID", "Key")));
        MaybeSet(metadata, "relic_rarity", StableScalarText(ReflectionUtil.GetMemberValue(relic, "Rarity")));
        metadata["relic_runtime_type"] = relic.GetType().FullName;
    }

    private static object? FindCurrentTreasureRelic(object action, int relicIndex)
    {
        object? synchronizer = ReflectionUtil.GetMemberValue(action, "TestSynchronizer")
            ?? ResolveRunManagerMember("TreasureRoomRelicSynchronizer");
        object? relics = ReflectionUtil.GetMemberValue(synchronizer, "CurrentRelics");
        int index = 0;
        foreach (object? relic in ReflectionUtil.Enumerate(relics))
        {
            if (index == relicIndex)
                return relic;
            index++;
        }

        return null;
    }

    private static void AddTypedPatchedSelectionMetadata(
        IDictionary<string, object?> metadata,
        string source,
        object?[] args)
    {
        int? optionIndex = args.Length > 0 ? ToInt(args[0]) : null;
        if (optionIndex == null)
            return;

        if (IsEventChooseLocalOptionSource(source))
        {
            metadata["selected_option_index"] = optionIndex.Value;
            if (TryProjectCurrentEventOption(optionIndex.Value, out var eventOption))
            {
                foreach (var pair in eventOption)
                    metadata[pair.Key] = pair.Value;
                metadata["extraction_status"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["event_option"] = "matched_current_event_option"
                };
            }
            else
            {
                metadata["extraction_status"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["event_option"] = "current_event_option_unavailable"
                };
            }

            metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
                new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
            return;
        }

        if (IsRestChooseLocalOptionSource(source))
        {
            metadata["selected_option_index"] = optionIndex.Value;
            if (TryProjectCurrentRestOption(optionIndex.Value, out var restOption))
            {
                foreach (var pair in restOption)
                    metadata[pair.Key] = pair.Value;
                metadata["extraction_status"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["rest_option"] = "matched_current_rest_option"
                };
            }
            else
            {
                metadata["extraction_status"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["rest_option"] = "current_rest_option_unavailable"
                };
            }

            metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
                new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
        }
    }

    private static void AddShopPatchedSelectionMetadata(
        IDictionary<string, object?> metadata,
        string source,
        object? instance,
        object?[] args)
    {
        if (!IsShopSignalSource(source))
            return;

        bool completed = IsShopPurchaseCompletedSource(source);
        object? entry = completed ? ResolveCompletedShopEntry(args, instance) : instance;
        string purchaseStatus = completed ? ResolvePurchaseStatus(args) ?? "completed" : "attempted";
        ShopProjection.AddSignalMetadata(metadata, entry, source, purchaseStatus);
        metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
    }

    private static void AddCardRewardPatchedSelectionMetadata(
        IDictionary<string, object?> metadata,
        string source,
        object? instance,
        object?[] args)
    {
        if (!IsCardRewardSignalSource(source))
            return;

        if (!RewardChoiceCache.Shared.TryProjectCardRewardSelection(source, instance, args, out var selection))
            return;

        foreach (var pair in selection)
            metadata[pair.Key] = pair.Value;
    }

    private static void AddRewardPatchedSelectionMetadata(
        IDictionary<string, object?> metadata,
        string source,
        object? instance)
    {
        if (!IsRewardSelectionSource(source))
            return;

        if (!RewardChoiceCache.Shared.TryProjectRewardSelection(instance, out var rewardSelection))
        {
            metadata["reward_selection_projection"] = "typed_reward_runtime_cache_unavailable";
            metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
                new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
            return;
        }

        foreach (var pair in rewardSelection)
            metadata[pair.Key] = pair.Value;
    }

    private static object? ResolveCompletedShopEntry(object?[] args, object? instance)
    {
        foreach (object? arg in args)
        {
            if (arg == null)
                continue;

            if (IsMerchantEntryRuntimeObject(arg))
                return arg;
        }

        return IsMerchantEntryRuntimeObject(instance) ? instance : null;
    }

    private static bool IsMerchantEntryRuntimeObject(object? value)
    {
        if (value == null)
            return false;

        Type type = value.GetType();
        if (type.IsEnum)
            return false;

        string typeName = type.Name;
        return typeName.Contains("Merchant", StringComparison.Ordinal)
            && typeName.EndsWith("Entry", StringComparison.Ordinal)
            && !typeName.Contains("Inventory", StringComparison.Ordinal);
    }

    private static string? ResolvePurchaseStatus(object?[] args)
    {
        foreach (object? arg in args)
        {
            string? status = StableScalarText(arg);
            if (status != null && status.Contains("Success", StringComparison.OrdinalIgnoreCase))
                return status;
        }

        return null;
    }

    private static void AddPlayerChoiceMetadata(
        IDictionary<string, object?> metadata,
        string source,
        object?[] args)
    {
        if (!IsPlayerChoiceSource(source))
            return;

        metadata["projection_policy"] = "typed_player_choice_signal";
        metadata["action_type"] = "player_choice";
        object? player = args.Length >= 3 ? args[0] : null;
        object? choiceId = args.Length >= 3 ? args[1] : args.Length > 0 ? args[0] : null;
        MaybeSet(metadata, "choice_id", ToInt(choiceId) ?? choiceId);

        object? result = args.Length >= 3 ? args[2] : args.Length > 1 ? args[1] : null;
        var resultMetadata = ProjectPlayerChoiceResult(result);
        metadata["player_choice_result"] = resultMetadata;
        if (resultMetadata.TryGetValue("choice_type", out object? choiceType))
            metadata["choice_result_type"] = choiceType;

        if (TryProjectShopRemovalFromPlayerChoice(player, resultMetadata, out var shopRemoval))
        {
            foreach (var pair in shopRemoval)
                metadata[pair.Key] = pair.Value;
        }
        else if (SelectionChoiceCache.Shared.TryProjectPlayerChoiceSelection(player, resultMetadata, out var selectionChoice))
        {
            foreach (var pair in selectionChoice)
                metadata[pair.Key] = pair.Value;
        }

        metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
    }

    private static SortedDictionary<string, object?> ProjectPlayerChoiceResult(object? result)
    {
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["extraction_status"] = result == null ? "result_unavailable" : "extracted"
        };

        if (result == null)
            return metadata;

        metadata["result_runtime_type"] = result.GetType().FullName;
        metadata["result_runtime_type_name"] = result.GetType().Name;

        string? choiceType = StableScalarText(ReflectionUtil.GetMemberValue(result, "type", "ChoiceType"));
        MaybeSet(metadata, "choice_type", choiceType);

        IReadOnlyList<int> indexes = ProjectChoiceIndexes(result);
        if (indexes.Count > 0)
        {
            metadata["indexes"] = indexes.ToArray();
            if (indexes.Count == 1)
                metadata["selected_index"] = indexes[0];
        }

        ProjectChoiceCards(metadata, result, "canonicalCards", "canonical_card_ids");
        ProjectChoiceCards(metadata, result, "combatCards", "combat_card_ids");
        ProjectChoiceCards(metadata, result, "deckCards", "deck_card_ids");
        ProjectChoiceCards(metadata, result, "mutableCards", "mutable_card_ids");
        MaybeSet(metadata, "mutable_card_owner_present", ReflectionUtil.GetMemberValue(result, "mutableCardOwner") != null);
        MaybeSet(metadata, "player_id_present", ReflectionUtil.GetMemberValue(result, "playerId") != null);
        metadata["text_status"] = "text_suppressed_player_choice_runtime_safety";
        return metadata;
    }

    private static IReadOnlyList<int> ProjectChoiceIndexes(object result)
    {
        object? indexes = ReflectionUtil.GetMemberValue(result, "indexes", "Indexes");
        var projected = new List<int>();
        foreach (object? index in ReflectionUtil.Enumerate(indexes))
        {
            int? value = ToInt(index);
            if (value != null)
                projected.Add(value.Value);
        }

        return projected;
    }

    private static void ProjectChoiceCards(
        IDictionary<string, object?> metadata,
        object result,
        string memberName,
        string outputKey)
    {
        object? cards = ReflectionUtil.GetMemberValue(result, memberName);
        var ids = new List<string>();
        foreach (object? card in ReflectionUtil.Enumerate(cards, maxItems: 20))
        {
            string? id = EntityId(card);
            if (id != null)
                ids.Add(id);
        }

        if (ids.Count > 0)
            metadata[outputKey] = ids.ToArray();
    }

    private static bool TryProjectShopRemovalFromPlayerChoice(
        object? player,
        IReadOnlyDictionary<string, object?> resultMetadata,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        int? selectedIndex = ToInt(FirstPresent(resultMetadata, "selected_index"));
        bool hasDeckLikeSelection = selectedIndex != null
            || resultMetadata.ContainsKey("deck_card_ids")
            || resultMetadata.ContainsKey("mutable_card_ids");
        if (!hasDeckLikeSelection)
            return false;

        if (!TryResolveCurrentShopCardRemovalEntry(player, out object? removalEntry))
            return false;

        ShopProjection.AddSignalMetadata(
            projection,
            removalEntry,
            "runtime.shop.player_choice_card_removal",
            "selected_card");
        projection["action_type"] = "remove_card_at_shop";
        projection["category"] = "card_removal";
        projection["selection_source"] = "player_choice_synchronizer";
        projection["shop_player_choice_projection"] = "current_typed_shop_card_removal";
        if (selectedIndex != null)
        {
            projection["selected_card_index"] = selectedIndex.Value;
            object? selectedCard = ResolveDeckCardAtIndex(player, selectedIndex.Value);
            MaybeSet(projection, "removed_card_id", EntityId(selectedCard));
            MaybeSet(projection, "removed_card_runtime_type", selectedCard?.GetType().FullName);
        }

        return true;
    }

    private static bool TryResolveCurrentShopCardRemovalEntry(object? player, out object? removalEntry)
    {
        removalEntry = null;
        object? runState = ReflectionUtil.GetMemberValue(player, "RunState") ?? ResolveCurrentRunState();
        object? currentRoom = ReflectionUtil.GetMemberValue(runState, "CurrentRoom");
        object? inventory = ReflectionUtil.GetMemberValue(currentRoom, "Inventory");
        removalEntry = ReflectionUtil.GetMemberValue(inventory, "CardRemovalEntry");
        return removalEntry != null;
    }

    private static object? ResolveDeckCardAtIndex(object? player, int index)
    {
        object? deck = ReflectionUtil.GetMemberValue(player, "Deck");
        object? cards = ReflectionUtil.GetMemberValue(deck, "Cards") ?? deck;
        return ElementAtOrNull(cards, index);
    }

    private static object? ResolveCurrentRunState()
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

    private static bool IsEventChooseLocalOptionSource(string source)
        => string.Equals(source, "runtime.event.choose_local_option", StringComparison.Ordinal)
            || string.Equals(source, "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer.ChooseLocalOption", StringComparison.Ordinal)
            || source.EndsWith(".EventSynchronizer.ChooseLocalOption", StringComparison.Ordinal);

    private static bool IsRestChooseLocalOptionSource(string source)
        => string.Equals(source, "runtime.rest.choose_local_option", StringComparison.Ordinal)
            || string.Equals(source, "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer.ChooseLocalOption", StringComparison.Ordinal)
            || source.EndsWith(".RestSiteSynchronizer.ChooseLocalOption", StringComparison.Ordinal);

    private static bool IsShopSignalSource(string source)
        => string.Equals(source, "ui.shop.on_try_purchase", StringComparison.Ordinal)
            || source.StartsWith("ui.shop.", StringComparison.Ordinal)
            || source.StartsWith("runtime.shop.", StringComparison.Ordinal)
            || source.Contains("MerchantEntry.OnTryPurchaseWrapper", StringComparison.Ordinal)
            || source.Contains("MerchantCardRemovalEntry.OnTryPurchaseWrapper", StringComparison.Ordinal)
            || source.Contains("MerchantEntry.InvokePurchaseCompleted", StringComparison.Ordinal);

    private static bool IsShopPurchaseCompletedSource(string source)
        => string.Equals(source, "runtime.shop.purchase_completed", StringComparison.Ordinal)
            || string.Equals(source, "runtime.shop.inventory_purchase_completed", StringComparison.Ordinal)
            || source.Contains("MerchantEntry.InvokePurchaseCompleted", StringComparison.Ordinal);

    private static bool IsRewardSelectionSource(string source)
        => string.Equals(source, "ui.reward.on_select_wrapper", StringComparison.Ordinal)
            || source.Contains("Reward.OnSelectWrapper", StringComparison.Ordinal);

    private static bool IsCardRewardSignalSource(string source)
        => source.Contains("card_reward", StringComparison.OrdinalIgnoreCase)
            || source.Contains("card_selection.skip", StringComparison.OrdinalIgnoreCase)
            || source.Contains("CardRewardSelection", StringComparison.Ordinal);

    private static bool IsPlayerChoiceSource(string source)
        => string.Equals(source, "player_choice_synchronizer.player_choice_received", StringComparison.Ordinal);

    private static bool TryProjectCurrentEventOption(
        int optionIndex,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        object? synchronizer = ResolveRunManagerMember("EventSynchronizer");
        object? eventModel = ReflectionUtil.Call(synchronizer, "GetLocalEvent");
        object? option = ElementAtOrNull(ReflectionUtil.GetMemberValue(eventModel, "CurrentOptions"), optionIndex);
        if (eventModel == null || option == null)
            return false;

        bool? isLocked = ReflectionUtil.GetBool(option, "IsLocked");
        projection["event_id"] = EntityId(eventModel);
        projection["event_runtime_type"] = eventModel.GetType().FullName;
        projection["option_index"] = optionIndex;
        projection["option_text_key"] = StableScalarText(ReflectionUtil.GetMemberValue(option, "TextKey"));
        projection["is_locked"] = isLocked;
        projection["is_proceed"] = ReflectionUtil.GetBool(option, "IsProceed");
        projection["was_chosen"] = ReflectionUtil.GetBool(option, "WasChosen");
        projection["relic_id"] = EntityId(ReflectionUtil.GetMemberValue(option, "Relic"));
        projection["availability"] = isLocked == true ? "locked" : "available";
        projection["text_status"] = "text_suppressed_locstring_runtime_safety";
        projection["effect_summary_status"] = "effect_summary_unavailable";
        return true;
    }

    private static bool TryProjectCurrentRestOption(
        int optionIndex,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        object? synchronizer = ResolveRunManagerMember("RestSiteSynchronizer");
        object? option = ElementAtOrNull(ReflectionUtil.Call(synchronizer, "GetLocalOptions"), optionIndex);
        if (option == null)
            return false;

        bool? isEnabled = ReflectionUtil.GetBool(option, "IsEnabled");
        projection["option_index"] = optionIndex;
        projection["option_id"] = StableScalarText(ReflectionUtil.GetMemberValue(option, "OptionId"));
        projection["option_runtime_type"] = option.GetType().FullName;
        projection["is_enabled"] = isEnabled;
        projection["smith_count"] = ReflectionUtil.GetInt(option, "SmithCount");
        projection["availability"] = isEnabled == false ? "disabled" : "available";
        projection["text_status"] = "text_suppressed_locstring_runtime_safety";
        projection["effect_summary_status"] = "effect_summary_unavailable";
        return true;
    }

    private static object? ElementAtOrNull(object? enumerable, int index)
    {
        if (index < 0)
            return null;

        int current = 0;
        foreach (object? item in ReflectionUtil.Enumerate(enumerable))
        {
            if (current == index)
                return item;
            current++;
        }

        return null;
    }

    private static void AddMapActionSignalMetadata(IDictionary<string, object?> metadata, object action)
    {
        string runtimeTypeName = action.GetType().Name;
        if (runtimeTypeName.EndsWith("MoveToMapCoordAction", StringComparison.Ordinal))
        {
            if (ReflectionUtil.TryReadMemberValue(action, out object? moveDestination, "_destination", "Destination"))
                metadata["destination_coord"] = ProjectMapCoord(moveDestination);
            metadata["map_signal_projection"] = "shallow_scalar_coords";
            return;
        }

        if (!runtimeTypeName.EndsWith("VoteForMapCoordAction", StringComparison.Ordinal))
            return;

        bool hasDestination = ReflectionUtil.TryReadMemberValue(action, out object? destination, "_destination", "Destination");
        if (ReflectionUtil.TryReadMemberValue(action, out object? source, "_source", "Source"))
        {
            metadata["source_coord"] = ProjectMapCoord(ReflectionUtil.GetMemberValue(source, "coord", "Coord"));
            MaybeSet(metadata, "source_act_index", ToInt(ReflectionUtil.GetMemberValue(source, "actIndex", "ActIndex")));
        }

        if (hasDestination && destination == null)
        {
            metadata["map_selection_kind"] = "cancel_map_vote";
            metadata["action_type"] = "cancel_map_vote";
        }
        else if (destination != null)
        {
            metadata["destination_coord"] = ProjectMapCoord(ReflectionUtil.GetMemberValue(destination, "coord", "Coord"));
            MaybeSet(metadata, "map_generation_count", ToInt(ReflectionUtil.GetMemberValue(destination, "mapGenerationCount", "MapGenerationCount")));
            metadata["map_selection_kind"] = "choose_map_node";
            metadata["action_type"] = "choose_map_node";
        }

        metadata["map_signal_projection"] = "shallow_scalar_coords";
        metadata["normalized_typed_action_key"] = BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(metadata, StringComparer.Ordinal));
    }

    private static Dictionary<string, object?>? ProjectMapCoord(object? coord)
    {
        int? col = ToInt(ReflectionUtil.GetMemberValue(coord, "col", "Col", "Column", "X"));
        int? row = ToInt(ReflectionUtil.GetMemberValue(coord, "row", "Row", "Y"));
        if (col == null && row == null)
            return null;

        return new Dictionary<string, object?>
        {
            ["col"] = col,
            ["row"] = row
        };
    }

    private static object? ResolveRunManagerMember(params string[] memberNames)
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

    private static string? EntityId(object? value)
    {
        object? id = ReflectionUtil.GetMemberValue(value, "Id", "ID", "Key");
        return CombatProjection.GetModelEntry(id) ?? StableScalarText(id);
    }

    private static string? StableScalarText(object? value)
    {
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

    private static void MaybeSet(IDictionary<string, object?> target, string key, object? value)
    {
        if (value != null)
            target[key] = value;
    }

    private static bool TryGetStableScalarMember(object value, out object? scalar, params string[] memberNames)
    {
        scalar = null;
        foreach (string memberName in memberNames)
        {
            object? member = ReflectionUtil.GetMemberValue(value, memberName);
            if (TryProjectStableScalar(member, out scalar))
                return true;

            if (member == null)
                continue;

            object? nested = ReflectionUtil.GetMemberValue(member, "Entry", "Value", "Id", "Key");
            if (TryProjectStableScalar(nested, out scalar))
                return true;
        }

        return false;
    }

    private static bool IsGenericHookGameAction(Type type)
        => string.Equals(type.Name, "GenericHookGameAction", StringComparison.Ordinal)
            || string.Equals(type.FullName, "MegaCrit.Sts2.Core.GameActions.GenericHookGameAction", StringComparison.Ordinal);

    private static bool TryProjectStableScalar(object? value, out object? scalar)
    {
        scalar = null;
        if (value == null)
            return false;

        Type type = value.GetType();
        if (type.IsEnum)
        {
            scalar = value.ToString();
            return true;
        }

        switch (value)
        {
            case string
                or bool
                or char
                or byte
                or sbyte
                or short
                or ushort
                or int
                or uint
                or long
                or ulong
                or float
                or double
                or decimal
                or DateTime
                or DateTimeOffset
                or Guid:
                scalar = value;
                return true;
            default:
                return false;
        }
    }

    private static string ToSnakeCase(string value)
    {
        var chars = new List<char>(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    chars.Add('_');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(char.ToLowerInvariant(c));
            }
        }
        return new string(chars.ToArray());
    }
}
