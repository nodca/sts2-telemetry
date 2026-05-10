using System.Runtime.CompilerServices;

namespace Sts2Telemetry;

internal sealed class RewardChoiceCache
{
    private const int MaxRewards = 20;
    private const int MaxCards = 20;

    private readonly object _gate = new();
    private RewardSurface? _activeRewards;
    private RewardSurface? _activeCardReward;
    private bool _preserveRewardsAcrossNextRoomEntered;

    public static RewardChoiceCache Shared { get; } = new();

    public void Clear()
    {
        lock (_gate)
        {
            _activeRewards = null;
            _activeCardReward = null;
            _preserveRewardsAcrossNextRoomEntered = false;
        }
    }

    public void PreserveRewardsAcrossNextRoomEntered()
    {
        lock (_gate)
        {
            _preserveRewardsAcrossNextRoomEntered = _activeRewards != null;
        }
    }

    public void ClearForRoomEntered()
    {
        lock (_gate)
        {
            if (_preserveRewardsAcrossNextRoomEntered && _activeRewards != null)
            {
                _preserveRewardsAcrossNextRoomEntered = false;
                _activeCardReward = null;
                return;
            }

            _activeRewards = null;
            _activeCardReward = null;
            _preserveRewardsAcrossNextRoomEntered = false;
        }
    }

    public void CompleteScheduledRewardsContext(bool recorded)
    {
        lock (_gate)
        {
            _preserveRewardsAcrossNextRoomEntered = false;
            if (!recorded)
                _activeRewards = null;
        }
    }

    public void CaptureRewardsSet(object? rewardsSet, string source)
    {
        if (rewardsSet == null)
            return;

        object? rewards = ReflectionUtil.GetMemberValue(rewardsSet, "Rewards");
        CaptureRewards(rewardsSet, rewards, source);
    }

    public void CaptureRewards(object? rewardsSet, object? rewards, string source)
    {
        var rewardEntries = new List<RewardEntry>();
        int index = 0;
        foreach (object? reward in ReflectionUtil.Enumerate(rewards, MaxRewards))
        {
            if (reward != null)
                rewardEntries.Add(ProjectRewardEntry(reward, index, source));
            index++;
        }

        var actions = rewardEntries
            .Select(entry => new Dictionary<string, object?>(entry.RewardAction, StringComparer.Ordinal))
            .ToList();

        bool? disallowSkipping = ReflectionUtil.GetBool(rewardsSet, "DisallowSkipping");
        if (disallowSkipping == false)
            actions.Add(BuildSkipRewardAction(source, rewardEntries.Count));

        string? roomType = StableTypeName(ReflectionUtil.GetMemberValue(rewardsSet, "Room"));
        if (roomType != null)
        {
            foreach (Dictionary<string, object?> action in actions)
                action["reward_room_runtime_type"] = roomType;
        }

        var surface = new RewardSurface(
            Source: source,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Entries: rewardEntries,
            Actions: actions);

        lock (_gate)
            _activeRewards = surface;
    }

    public void CaptureCardReward(object? reward, string source)
    {
        if (reward == null || !IsCardReward(reward))
            return;

        RewardEntry parentEntry = FindActiveRewardEntry(reward) ?? ProjectRewardEntry(reward, null, source);
        IReadOnlyList<Dictionary<string, object?>> actions = ProjectCardRewardActions(reward, parentEntry, source);
        var surface = new RewardSurface(
            Source: source,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Entries: new[] { parentEntry },
            Actions: actions);

        lock (_gate)
            _activeCardReward = surface;
    }

    public IReadOnlyList<Dictionary<string, object?>>? BuildLegalActions(string stateType)
    {
        RewardSurface? surface;
        lock (_gate)
        {
            surface = stateType switch
            {
                "rewards" => _activeRewards,
                "card_reward" => _activeCardReward,
                _ => null
            };
        }

        return surface?.Actions
            .Select(action => new Dictionary<string, object?>(action, StringComparer.Ordinal))
            .ToArray();
    }

    public bool TryProjectRewardSelection(
        object? reward,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (reward == null)
            return false;

        RewardEntry entry = FindActiveRewardEntry(reward) ?? ProjectRewardEntry(reward, null, "runtime.reward.on_select_wrapper");
        foreach (var (key, value) in entry.RewardAction)
        {
            if (key is "source" or "state_type" or "message")
                continue;
            projection[key] = value;
        }

        projection["source"] = "runtime.reward.on_select_wrapper";
        projection["selection_source"] = "reward_runtime_cache";
        projection["projection_policy"] = "typed_reward_runtime_cache";
        projection["selected_reward_runtime_hash"] = RuntimeHash(reward);
        projection["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(projection, StringComparer.Ordinal));
        return true;
    }

    private RewardEntry? FindActiveRewardEntry(object reward)
    {
        lock (_gate)
        {
            return _activeRewards?.Entries.FirstOrDefault(entry =>
                entry.TryGetReward(out object? candidate) && ReferenceEquals(candidate, reward));
        }
    }

    private static RewardEntry ProjectRewardEntry(object reward, int? rewardIndex, string source)
    {
        string rewardRuntimeType = reward.GetType().FullName ?? reward.GetType().Name;
        string rewardRuntimeTypeName = reward.GetType().Name;
        string rewardType = NormalizeRewardType(
            StableScalarText(ReflectionUtil.GetMemberValue(reward, "RewardType"))
            ?? RewardTypeFromRuntimeName(rewardRuntimeTypeName));

        var action = new Dictionary<string, object?>
        {
            ["action_type"] = "claim_reward",
            ["source"] = "reward_runtime_cache",
            ["state_type"] = "rewards",
            ["reward_index"] = rewardIndex,
            ["reward_type"] = rewardType,
            ["reward_runtime_type"] = rewardRuntimeType,
            ["reward_runtime_type_name"] = rewardRuntimeTypeName,
            ["reward_runtime_hash"] = RuntimeHash(reward),
            ["rewards_set_index"] = ReflectionUtil.GetInt(reward, "RewardsSetIndex"),
            ["is_populated"] = ReflectionUtil.GetBool(reward, "IsPopulated"),
            ["can_select"] = ReflectionUtil.GetBool(reward, "IsPopulated") != false,
            ["availability"] = ReflectionUtil.GetBool(reward, "IsPopulated") == false
                ? "reward_not_populated"
                : "available",
            ["projection_policy"] = "typed_reward_runtime_cache",
            ["text_status"] = "text_suppressed_locstring_runtime_safety",
            ["effect_summary_status"] = "effect_summary_unavailable"
        };

        AddRewardTypeMetadata(action, reward, rewardType);
        action["reward_id"] = RewardId(action, rewardType);
        action["match_key"] = RewardActionMatchKey(action);
        return new RewardEntry(new WeakReference<object>(reward), action);
    }

    private static void AddRewardTypeMetadata(IDictionary<string, object?> action, object reward, string rewardType)
    {
        switch (rewardType)
        {
            case "gold":
                action["gold_amount"] = ReflectionUtil.GetInt(reward, "Amount");
                action["reward_id"] = "gold";
                break;
            case "potion":
                object? potion = ReflectionUtil.GetMemberValue(reward, "Potion", "ClaimedPotion");
                action["potion_id"] = EntityId(potion);
                action["reward_id"] = EntityId(potion) ?? "potion";
                action["potion_runtime_type"] = potion?.GetType().FullName;
                action["potion_rarity"] = StableScalarText(ReflectionUtil.GetMemberValue(potion, "Rarity"));
                action["potion_usage"] = StableScalarText(ReflectionUtil.GetMemberValue(potion, "Usage"));
                action["potion_target_type"] = StableScalarText(ReflectionUtil.GetMemberValue(potion, "TargetType"));
                break;
            case "relic":
                object? relic = ReflectionUtil.GetMemberValue(reward, "_relic", "Relic", "ClaimedRelic");
                action["relic_id"] = EntityId(relic);
                action["reward_id"] = EntityId(relic) ?? "relic";
                action["relic_runtime_type"] = relic?.GetType().FullName;
                action["relic_rarity"] = StableScalarText(ReflectionUtil.GetMemberValue(reward, "Rarity"))
                    ?? StableScalarText(ReflectionUtil.GetMemberValue(relic, "Rarity"));
                break;
            case "card":
            case "special_card":
                action["reward_id"] = "card_reward";
                action["child_decision_surface"] = "card_reward";
                action["child_decision_link_role"] = "opens_child_decision_context";
                action["child_decision_link_status"] = "pending_card_reward_context";
                action["card_count"] = ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(reward, "Cards"), MaxCards).Count(card => card != null);
                action["can_skip"] = ReflectionUtil.GetBool(reward, "CanSkip");
                action["can_reroll"] = ReflectionUtil.GetBool(reward, "CanReroll");
                action["card_ids"] = ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(reward, "Cards"), MaxCards)
                    .Select(EntityId)
                    .Where(id => id != null)
                    .ToArray();
                break;
            case "remove_card":
                action["reward_id"] = "card_removal";
                action["removal_id"] = "card_removal";
                break;
        }
    }

    private static IReadOnlyList<Dictionary<string, object?>> ProjectCardRewardActions(
        object reward,
        RewardEntry parentEntry,
        string source)
    {
        var actions = new List<Dictionary<string, object?>>();
        int? rewardIndex = ToInt(parentEntry.RewardAction.GetValueOrDefault("reward_index"));
        int cardIndex = 0;
        foreach (object? card in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(reward, "Cards"), MaxCards))
        {
            if (card == null)
            {
                cardIndex++;
                continue;
            }

            var action = new Dictionary<string, object?>
            {
                ["action_type"] = "choose_reward_card",
                ["source"] = "card_reward_runtime_cache",
                ["state_type"] = "card_reward",
                ["reward_index"] = rewardIndex,
                ["card_index"] = cardIndex,
                ["option_index"] = cardIndex,
                ["card_id"] = EntityId(card),
                ["card_type"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "Type")),
                ["rarity"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "Rarity")),
                ["is_upgraded"] = ReflectionUtil.GetBool(card, "IsUpgraded", "Upgraded"),
                ["target_type"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "TargetType")),
                ["energy_cost"] = ResolveEnergyCost(card),
                ["can_select"] = true,
                ["availability"] = "available",
                ["projection_policy"] = "typed_card_reward_runtime_cache",
                ["text_status"] = "text_suppressed_locstring_runtime_safety"
            };
            action["match_key"] = RewardActionMatchKey(action);
            actions.Add(action);
            cardIndex++;
        }

        if (ReflectionUtil.GetBool(reward, "CanSkip") == true)
            actions.Add(BuildCardRewardAlternativeAction("skip_card_reward", "Skip", rewardIndex, source));

        if (ReflectionUtil.GetBool(reward, "CanReroll") == true)
            actions.Add(BuildCardRewardAlternativeAction("reroll_card_reward", "REROLL", rewardIndex, source));

        AddCardRewardSacrificeActions(actions, reward, rewardIndex, source);

        if (actions.Count == 0)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_type"] = "card_reward_typed_builder_unavailable",
                ["source"] = "card_reward_runtime_cache",
                ["state_type"] = "card_reward",
                ["availability"] = "active_card_reward_options_not_found",
                ["message"] = "card reward legal actions require cached CardReward.Cards or typed skip/reroll flags; broad card reward UI traversal is disabled"
            });
        }

        return actions;
    }

    public bool TryProjectCardRewardSelection(
        string source,
        object? instance,
        object?[] args,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["state_type"] = "card_reward",
            ["selection_source"] = "card_reward_ui_signal",
            ["projection_policy"] = "typed_card_reward_runtime_cache_signal"
        };

        if (source.Contains("skip", StringComparison.OrdinalIgnoreCase))
        {
            AddCardRewardAlternativeSelection(projection, "skip_card_reward", "Skip");
            return true;
        }

        if (source.Contains("reroll", StringComparison.OrdinalIgnoreCase))
        {
            AddCardRewardAlternativeSelection(projection, "reroll_card_reward", "REROLL");
            return true;
        }

        object? selectedIdentity = UnwrapCardRewardSelectedIdentity(FirstNonNull(args) ?? SafeSelectedCardFromScreen(instance));
        int? selectedIndex = ToInt(selectedIdentity);
        string? selectedCardId = selectedIndex == null ? EntityId(selectedIdentity) : null;
        IReadOnlyList<Dictionary<string, object?>>? legalActions = BuildLegalActions("card_reward");
        string desiredActionType = source.Contains("sacrifice", StringComparison.OrdinalIgnoreCase)
            ? "sacrifice_reward_card"
            : "choose_reward_card";
        Dictionary<string, object?>? matchedAction = legalActions?
            .Where(action => StringValue(action, "action_type") == desiredActionType)
            .FirstOrDefault(action =>
                (selectedIndex != null && ToInt(action.GetValueOrDefault("card_index")) == selectedIndex)
                || (!string.IsNullOrWhiteSpace(selectedCardId)
                    && string.Equals(StringValue(action, "card_id"), selectedCardId, StringComparison.Ordinal)));

        if (matchedAction != null)
        {
            CopySelectionFields(projection, matchedAction);
            projection["selection_identity_status"] = selectedIndex != null
                ? "matched_card_reward_option_index"
                : "matched_card_reward_card_id";
            projection["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(
                new Dictionary<string, object?>(projection, StringComparer.Ordinal));
            return true;
        }

        projection["action_type"] = "card_reward_selection_unavailable";
        projection["availability"] = "selected_card_identity_unavailable";
        projection["selection_identity_status"] = selectedIdentity == null
            ? "selected_card_identity_missing_from_signal"
            : "selected_card_identity_not_matched_to_cached_card_reward";
        projection["selection_identity_reason"] =
            "card reward UI signal did not expose a stable selected card id/index that matched the cached CardReward options";
        if (selectedIndex != null)
            projection["selected_card_index"] = selectedIndex.Value;
        if (!string.IsNullOrWhiteSpace(selectedCardId))
            projection["selected_card_id"] = selectedCardId;
        projection["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(projection, StringComparer.Ordinal));
        return true;
    }

    private static void AddCardRewardSacrificeActions(
        ICollection<Dictionary<string, object?>> actions,
        object reward,
        int? rewardIndex,
        string source)
    {
        object? sacrificeOptions = ReflectionUtil.GetMemberValue(
            reward,
            "SacrificeOptions",
            "CardsToSacrifice",
            "SacrificeCards",
            "RewardSacrificeOptions");
        int index = 0;
        foreach (object? option in ReflectionUtil.Enumerate(sacrificeOptions, MaxCards))
        {
            if (option == null)
            {
                index++;
                continue;
            }

            var action = new Dictionary<string, object?>
            {
                ["action_type"] = "sacrifice_reward_card",
                ["source"] = "card_reward_runtime_cache",
                ["state_type"] = "card_reward",
                ["reward_index"] = rewardIndex,
                ["option_index"] = index,
                ["card_index"] = index,
                ["card_id"] = EntityId(option),
                ["card_type"] = StableScalarText(ReflectionUtil.GetMemberValue(option, "Type")),
                ["relic_id"] = "RELIC.PAELS_WING",
                ["special_option_kind"] = "sacrifice_reward_card",
                ["can_select"] = true,
                ["availability"] = "available",
                ["projection_policy"] = "typed_card_reward_special_option_runtime_cache",
                ["captured_from"] = source,
                ["text_status"] = "text_suppressed_locstring_runtime_safety"
            };
            action["match_key"] = RewardActionMatchKey(action);
            actions.Add(action);
            index++;
        }

        bool hasPaelsWing =
            ReflectionUtil.GetBool(reward, "HasPaelsWingSacrifice", "CanSacrificeReward", "CanSacrifice") == true
            || ContainsPaelsWingRelicReference(reward);
        if (!hasPaelsWing || actions.Any(action => Equals(action.GetValueOrDefault("action_type"), "sacrifice_reward_card")))
            return;

        var unavailable = new Dictionary<string, object?>
        {
            ["action_type"] = "sacrifice_reward_card_unavailable",
            ["source"] = "card_reward_runtime_cache",
            ["state_type"] = "card_reward",
            ["reward_index"] = rewardIndex,
            ["relic_id"] = "RELIC.PAELS_WING",
            ["special_option_kind"] = "sacrifice_reward_card",
            ["availability"] = "selected_sacrifice_identity_unavailable",
            ["selection_identity_status"] = "sacrifice_option_identity_unavailable",
            ["selection_identity_reason"] = "PAELS_WING sacrifice was indicated by typed reward data, but no stable sacrifice option identity was exposed",
            ["projection_policy"] = "typed_card_reward_special_option_identity_unavailable",
            ["captured_from"] = source
        };
        unavailable["match_key"] = RewardActionMatchKey(unavailable);
        actions.Add(unavailable);
    }

    private static Dictionary<string, object?> BuildCardRewardAlternativeAction(
        string actionType,
        string optionId,
        int? rewardIndex,
        string source)
    {
        var action = new Dictionary<string, object?>
        {
            ["action_type"] = actionType,
            ["source"] = "card_reward_runtime_cache",
            ["state_type"] = "card_reward",
            ["reward_index"] = rewardIndex,
            ["alternative_id"] = optionId,
            ["option_id"] = optionId,
            ["availability"] = "available",
            ["can_select"] = true,
            ["projection_policy"] = "typed_card_reward_flags_no_hook_generation",
            ["text_status"] = "text_suppressed_locstring_runtime_safety",
            ["captured_from"] = source
        };
        action["match_key"] = RewardActionMatchKey(action);
        return action;
    }

    private static Dictionary<string, object?> BuildSkipRewardAction(string source, int rewardCount)
    {
        var action = new Dictionary<string, object?>
        {
            ["action_type"] = "skip_reward",
            ["source"] = "reward_runtime_cache",
            ["state_type"] = "rewards",
            ["reward_index"] = null,
            ["reward_count"] = rewardCount,
            ["availability"] = "available",
            ["can_select"] = true,
            ["projection_policy"] = "typed_rewards_set_disallow_skipping_flag",
            ["captured_from"] = source
        };
        action["match_key"] = RewardActionMatchKey(action);
        return action;
    }

    private static IReadOnlyDictionary<string, object?> RewardActionMatchKey(IReadOnlyDictionary<string, object?> action)
        => ActionMetadata.BuildNormalizedTypedActionKey(action);

    private static void AddCardRewardAlternativeSelection(
        IDictionary<string, object?> projection,
        string actionType,
        string alternativeId)
    {
        projection["action_type"] = actionType;
        projection["alternative_id"] = alternativeId;
        projection["option_id"] = alternativeId;
        projection["availability"] = "available";
        projection["selection_identity_status"] = "typed_alternative_source";
        projection["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(projection, StringComparer.Ordinal));
    }

    private static void CopySelectionFields(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> source)
    {
        foreach (string key in new[]
                 {
                     "action_type",
                     "reward_index",
                     "card_index",
                     "option_index",
                     "card_id",
                     "card_type",
                     "rarity",
                     "is_upgraded",
                     "target_type",
                     "energy_cost",
                     "alternative_id",
                     "option_id",
                     "relic_id",
                     "special_option_kind",
                     "availability"
                 })
        {
            if (source.TryGetValue(key, out object? value))
                target[key] = value;
        }
    }

    private static object? FirstNonNull(IEnumerable<object?> values)
    {
        foreach (object? value in values)
        {
            if (value == null)
                continue;

            int? scalarIndex = ToInt(value);
            if (scalarIndex != null)
                return scalarIndex.Value;

            foreach (object? item in ReflectionUtil.Enumerate(value, MaxCards))
            {
                if (item != null)
                    return item;
            }

            return value;
        }

        return null;
    }

    private static object? SafeSelectedCardFromScreen(object? instance)
    {
        if (instance == null)
            return null;

        foreach (string member in new[]
                 {
                     "SelectedCard",
                     "SelectedCards",
                     "ChosenCard",
                     "ChosenCards",
                     "GetSelectedCards",
                     "_selectedCards",
                     "selectedCards",
                     "_localSelectedCard",
                     "localSelectedCard",
                     "MockSelectedCard",
                     "_mockSelectedCard"
                 })
        {
            object? value = ReflectionUtil.GetMemberValue(instance, member);
            if (value == null)
                continue;

            foreach (object? item in ReflectionUtil.Enumerate(value, MaxCards))
            {
                if (item != null)
                    return item;
            }

            return value;
        }

        return null;
    }

    private static object? UnwrapCardRewardSelectedIdentity(object? selectedIdentity)
    {
        if (selectedIdentity == null)
            return null;

        int? selectedIndex = ToInt(selectedIdentity);
        if (selectedIndex != null)
            return selectedIndex.Value;

        object? cardModel = ReflectionUtil.GetMemberValue(selectedIdentity, "CardModel");
        if (cardModel != null)
            return cardModel;

        object? cardNode = ReflectionUtil.GetMemberValue(selectedIdentity, "CardNode");
        object? nodeModel = ReflectionUtil.GetMemberValue(cardNode, "Model", "CardModel");
        if (nodeModel != null)
            return nodeModel;

        object? creationResult = ReflectionUtil.GetMemberValue(selectedIdentity, "CreationResult");
        object? createdCard = ReflectionUtil.GetMemberValue(creationResult, "Card");
        return createdCard ?? selectedIdentity;
    }

    private static bool ContainsPaelsWingRelicReference(object reward)
    {
        foreach (string member in new[] { "RelicId", "SourceRelicId", "SpecialRelicId", "Relic" })
        {
            string? value = StableScalarText(ReflectionUtil.GetMemberValue(reward, member));
            if (value?.Contains("PAELS_WING", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }

        return false;
    }

    private static string? RewardId(IReadOnlyDictionary<string, object?> action, string rewardType)
        => StringValue(action, "reward_id")
            ?? StringValue(action, "card_id")
            ?? StringValue(action, "relic_id")
            ?? StringValue(action, "potion_id")
            ?? StringValue(action, "removal_id")
            ?? (rewardType == "gold" ? "gold" : null);

    private static object? ResolveEnergyCost(object card)
    {
        object? energyCost = ReflectionUtil.GetMemberValue(card, "EnergyCost");
        return ReflectionUtil.GetInt(energyCost, "Amount")
            ?? ReflectionUtil.GetInt(card, "CurrentStarCost", "Cost");
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

    private static string NormalizeRewardType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown_reward";

        string normalized = ToSnakeCase(value);
        return normalized switch
        {
            "remove_card_reward" => "remove_card",
            "card_reward" => "card",
            "gold_reward" => "gold",
            "potion_reward" => "potion",
            "relic_reward" => "relic",
            _ => normalized
        };
    }

    private static string RewardTypeFromRuntimeName(string typeName)
    {
        if (typeName.Contains("GoldReward", StringComparison.Ordinal))
            return "gold";
        if (typeName.Contains("PotionReward", StringComparison.Ordinal))
            return "potion";
        if (typeName.Contains("RelicReward", StringComparison.Ordinal))
            return "relic";
        if (typeName.Contains("CardRemovalReward", StringComparison.Ordinal))
            return "remove_card";
        if (typeName.Contains("CardReward", StringComparison.Ordinal))
            return "card";
        if (typeName.Contains("SpecialCardReward", StringComparison.Ordinal))
            return "special_card";
        return "unknown_reward";
    }

    private static bool IsCardReward(object reward)
    {
        string typeName = reward.GetType().Name;
        string? rewardType = NormalizeRewardType(
            StableScalarText(ReflectionUtil.GetMemberValue(reward, "RewardType"))
            ?? RewardTypeFromRuntimeName(typeName));
        return rewardType is "card" or "special_card"
            || typeName.Contains("CardReward", StringComparison.Ordinal);
    }

    private static string? StableTypeName(object? value)
        => value?.GetType().FullName;

    private static string RuntimeHash(object value)
        => RuntimeHelpers.GetHashCode(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
                string text when int.TryParse(text, out int parsed) => parsed,
                _ => Convert.ToInt32(value)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? StringValue(IReadOnlyDictionary<string, object?> source, string key)
        => source.TryGetValue(key, out object? value) ? value as string : null;

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

    private sealed record RewardSurface(
        string Source,
        DateTimeOffset CapturedAtUtc,
        IReadOnlyList<RewardEntry> Entries,
        IReadOnlyList<Dictionary<string, object?>> Actions);

    private sealed record RewardEntry(
        WeakReference<object> Reward,
        IReadOnlyDictionary<string, object?> RewardAction)
    {
        public bool TryGetReward(out object? reward)
        {
            if (Reward.TryGetTarget(out object? target))
            {
                reward = target;
                return true;
            }

            reward = null;
            return false;
        }
    }
}
