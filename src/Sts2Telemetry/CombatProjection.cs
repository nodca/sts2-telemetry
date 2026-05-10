namespace Sts2Telemetry;

internal static class CombatProjection
{
    private const int MaxCardsPerPile = 120;
    private const int MaxTargets = 40;
    private const int MaxPowers = 80;

    public static Dictionary<string, object?> BuildCardProjection(
        object card,
        int? index,
        string pile,
        bool includePlayability,
        object? combatState,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates = null,
        bool includeTargetCandidateDetails = true)
    {
        var projection = new Dictionary<string, object?>
        {
            ["pile"] = pile
        };

        if (index != null)
        {
            projection["index"] = index.Value;
            if (pile == "hand")
                projection["card_index"] = index.Value;
        }

        MaybeSet(projection, "card_id", GetModelEntry(ReflectionUtil.GetMemberValue(card, "Id")));
        MaybeSet(projection, "card_name", ReflectionUtil.GetText(card, "Title", "Name"));
        MaybeSet(projection, "card_type", ReflectionUtil.GetText(card, "Type"));
        MaybeSet(projection, "rarity", ReflectionUtil.GetText(card, "Rarity"));
        MaybeSet(projection, "is_upgraded", ReflectionUtil.GetBool(card, "IsUpgraded", "Upgraded"));
        MaybeSet(projection, "costs_x", ReflectionUtil.GetBool(ReflectionUtil.GetMemberValue(card, "EnergyCost"), "CostsX"));
        MaybeSet(projection, "energy_cost", ResolveEnergyCost(card));
        MaybeSet(projection, "star_costs_x", ReflectionUtil.GetBool(card, "HasStarCostX"));
        MaybeSet(projection, "star_cost", ResolveStarCost(card));

        string? targetType = ReflectionUtil.GetText(card, "TargetType");
        MaybeSet(projection, "target_type", targetType);
        bool requiresTarget = RequiresTarget(targetType);
        projection["requires_target"] = requiresTarget;
        string? targetIndexSpace = TargetIndexSpace(targetType);
        MaybeSet(projection, "target_index_space", targetIndexSpace);

        if (requiresTarget)
        {
            object? owner = ReflectionUtil.GetMemberValue(card, "Owner");
            var validTargets = BuildValidTargets(targetCandidates, targetIndexSpace, targetType, owner);
            projection["valid_target_indices"] = validTargets
                .Select(target => target.GetValueOrDefault("target_index"))
                .Where(value => value != null)
                .ToArray();
            if (includeTargetCandidateDetails)
                projection["target_candidates"] = validTargets;
        }

        if (includePlayability)
        {
            var playability = BuildPlayability(card);
            projection["can_play"] = playability.CanPlay;
            projection["playable"] = playability.CanPlay;
            projection["unplayable_reason"] = playability.UnplayableReason;
            projection["playability_extraction"] = playability.ExtractionStatus;
        }

        return projection;
    }

    public static IReadOnlyList<Dictionary<string, object?>> BuildPileCards(
        object? pile,
        string pileName,
        bool includePlayability,
        object? combatState,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates = null)
    {
        object? cards = ReflectionUtil.GetMemberValue(pile, "Cards");
        var result = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (object? card in ReflectionUtil.Enumerate(cards, MaxCardsPerPile))
        {
            if (card != null)
                result.Add(BuildCardProjection(card, index, pileName, includePlayability, combatState, targetCandidates));
            index++;
        }

        return result;
    }

    public static IReadOnlyList<Dictionary<string, object?>> BuildTargetCandidates(object? combatState, object? localPlayer)
    {
        var targets = new List<Dictionary<string, object?>>();
        AddCreatureTargets(targets, ReflectionUtil.GetMemberValue(combatState, "Enemies"), "enemies", "enemy");

        object? players = ReflectionUtil.GetMemberValue(combatState, "Players");
        if (players == null && localPlayer != null)
            players = new[] { localPlayer };

        AddPlayerTargets(targets, players);
        return targets;
    }

    public static IReadOnlyList<Dictionary<string, object?>> BuildEnemySnapshots(object? combatState)
    {
        var enemies = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (object? enemy in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(combatState, "Enemies"), MaxTargets))
        {
            if (enemy != null)
                enemies.Add(BuildCreatureTarget(enemy, index, "enemies", "enemy", entityCounts, includeStatus: true));
            index++;
        }

        return enemies;
    }

    public static Dictionary<string, object?> BuildCreatureSignalTarget(
        object creature,
        int index,
        IDictionary<string, int> entityCounts,
        bool includeStatus)
    {
        object? monster = ReflectionUtil.GetMemberValue(creature, "Monster");
        object? player = ReflectionUtil.GetMemberValue(creature, "Player");
        string indexSpace = monster != null
            ? "enemies"
            : player != null
                ? "players"
                : "creatures";
        string targetType = monster != null
            ? "enemy"
            : player != null
                ? "player"
                : "creature";

        var target = BuildCreatureTarget(creature, index, indexSpace, targetType, entityCounts, includeStatus);
        if (player != null)
        {
            MaybeSet(target, "player_net_id", ReflectionUtil.GetMemberValue(player, "NetId"));
            MaybeSet(target, "name", ReflectionUtil.GetText(ReflectionUtil.GetMemberValue(player, "Character"), "Title", "Id"));
        }

        MaybeSet(target, "is_pet", ReflectionUtil.GetBool(creature, "IsPet"));
        MaybeSet(target, "slot_name", StableScalarText(ReflectionUtil.GetMemberValue(creature, "SlotName")));
        return target;
    }

    public static IReadOnlyList<Dictionary<string, object?>> BuildPowers(object? creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (object? power in ReflectionUtil.Enumerate(ReflectionUtil.GetMemberValue(creature, "Powers"), MaxPowers))
        {
            if (power == null)
            {
                index++;
                continue;
            }

            var projection = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["power_id"] = GetModelEntry(ReflectionUtil.GetMemberValue(power, "Id")) ?? "unknown_power",
                ["name"] = ReflectionUtil.GetText(power, "Title", "Name"),
                ["amount"] = ReflectionUtil.GetInt(power, "Amount"),
                ["type"] = ReflectionUtil.GetText(power, "TypeForCurrentAmount", "Type")
            };

            string? powerType = projection["type"] as string;
            if (!string.IsNullOrWhiteSpace(powerType))
                projection["is_debuff"] = powerType.Contains("Debuff", StringComparison.OrdinalIgnoreCase);

            powers.Add(projection);
            index++;
        }

        return powers;
    }

    public static Dictionary<string, object?> BuildPotionProjection(
        object potion,
        int slot,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates,
        bool includeTargetCandidateDetails = true)
    {
        string? targetType = ReflectionUtil.GetText(potion, "TargetType");
        object? owner = ReflectionUtil.GetMemberValue(potion, "Owner");
        bool requiresTarget = PotionRequiresTarget(potion, targetType, targetCandidates);
        string? targetIndexSpace = PotionTargetIndexSpace(targetType, requiresTarget);
        var validTargets = requiresTarget
            ? BuildValidTargets(targetCandidates, targetIndexSpace, targetType, owner)
            : Array.Empty<Dictionary<string, object?>>();
        bool targetAvailable = !requiresTarget || validTargets.Length > 0;
        bool? isQueued = ReflectionUtil.GetBool(potion, "IsQueued");
        bool? passesUsabilityCheck = ReflectionUtil.GetBool(potion, "PassesCustomUsabilityCheck");
        bool? canUse = BuildPotionCanUse(passesUsabilityCheck, isQueued, targetAvailable);

        var projection = new Dictionary<string, object?>
        {
            ["action_type"] = "use_potion",
            ["source"] = "potion_slots",
            ["slot"] = slot,
            ["potion_id"] = GetModelEntry(ReflectionUtil.GetMemberValue(potion, "Id")),
            ["potion_name"] = ReflectionUtil.GetText(potion, "Title", "Name"),
            ["target_type"] = targetType,
            ["usage"] = ReflectionUtil.GetText(potion, "Usage"),
            ["requires_target"] = requiresTarget,
            ["target_index_space"] = targetIndexSpace,
            ["is_queued"] = isQueued,
            ["passes_usability_check"] = passesUsabilityCheck,
            ["can_use"] = canUse,
            ["availability"] = PotionAvailability(requiresTarget, targetAvailable, passesUsabilityCheck, isQueued, canUse)
        };

        if (requiresTarget)
        {
            projection["valid_target_indices"] = validTargets
                .Select(target => target.GetValueOrDefault("target_index"))
                .Where(value => value != null)
                .ToArray();
            if (includeTargetCandidateDetails)
                projection["target_candidates"] = validTargets;
        }

        return projection;
    }

    public static string? GetModelEntry(object? value)
        => ReflectionUtil.GetText(value, "Entry", "Value", "Id", "Key");

    public static bool RequiresTarget(string? targetType)
        => string.Equals(targetType, "AnyEnemy", StringComparison.Ordinal)
            || string.Equals(targetType, "AnyAlly", StringComparison.Ordinal)
            || string.Equals(targetType, "AnyPlayer", StringComparison.Ordinal);

    public static string? TargetIndexSpace(string? targetType)
        => targetType switch
        {
            "AnyEnemy" => "enemies",
            "AnyAlly" or "AnyPlayer" or "Self" => "players",
            _ => null
        };

    public static object? ResolveCombatState(object? player)
    {
        object? creature = ReflectionUtil.GetMemberValue(player, "Creature");
        return ReflectionUtil.GetMemberValue(creature, "CombatState")
            ?? ReflectionUtil.GetMemberValue(ReflectionUtil.GetMemberValue(player, "PlayerCombatState"), "CombatState");
    }

    private static void AddCreatureTargets(
        ICollection<Dictionary<string, object?>> targets,
        object? creatures,
        string indexSpace,
        string targetType)
    {
        var entityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (object? creature in ReflectionUtil.Enumerate(creatures, MaxTargets))
        {
            if (creature != null)
                targets.Add(BuildCreatureTarget(creature, index, indexSpace, targetType, entityCounts, includeStatus: true));
            index++;
        }
    }

    private static void AddPlayerTargets(ICollection<Dictionary<string, object?>> targets, object? players)
    {
        int index = 0;
        foreach (object? player in ReflectionUtil.Enumerate(players, MaxTargets))
        {
            object? creature = ReflectionUtil.GetMemberValue(player, "Creature") ?? player;
            if (creature != null)
            {
                var target = BuildCreatureTarget(
                    creature,
                    index,
                    "players",
                    "player",
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    includeStatus: true);
                MaybeSet(target, "player_net_id", ReflectionUtil.GetMemberValue(player, "NetId"));
                MaybeSet(target, "name", ReflectionUtil.GetText(ReflectionUtil.GetMemberValue(player, "Character"), "Title", "Id"));
                targets.Add(target);
            }
            index++;
        }
    }

    private static Dictionary<string, object?> BuildCreatureTarget(
        object creature,
        int index,
        string indexSpace,
        string targetType,
        IDictionary<string, int> entityCounts,
        bool includeStatus)
    {
        object? monster = ReflectionUtil.GetMemberValue(creature, "Monster");
        string baseId = GetModelEntry(ReflectionUtil.GetMemberValue(monster, "Id"))
            ?? GetModelEntry(ReflectionUtil.GetMemberValue(creature, "ModelId"))
            ?? targetType;
        if (!entityCounts.TryGetValue(baseId, out int ordinal))
            ordinal = 0;
        entityCounts[baseId] = ordinal + 1;

        var target = new Dictionary<string, object?>
        {
            ["target_index_space"] = indexSpace,
            ["target_index"] = index,
            ["target_type"] = targetType,
            ["entity_id"] = $"{baseId}_{ordinal}",
            ["combat_id"] = ReflectionUtil.GetInt(creature, "CombatId"),
            ["enemy_id"] = targetType == "enemy" ? baseId : null,
            ["id"] = baseId,
            ["name"] = targetType == "enemy"
                ? ReflectionUtil.GetText(monster, "Title", "Name") ?? ReflectionUtil.GetText(creature, "Name")
                : null,
            ["hp"] = ReflectionUtil.GetInt(creature, "CurrentHp"),
            ["max_hp"] = ReflectionUtil.GetInt(creature, "MaxHp"),
            ["block"] = ReflectionUtil.GetInt(creature, "Block"),
            ["is_alive"] = ReflectionUtil.GetBool(creature, "IsAlive"),
            ["is_hittable"] = ReflectionUtil.GetBool(creature, "IsHittable")
        };

        object? nextMove = ReflectionUtil.GetMemberValue(monster, "NextMove");
        MaybeSet(target, "intent", GetModelEntry(ReflectionUtil.GetMemberValue(nextMove, "Id"))
            ?? ReflectionUtil.GetText(nextMove, "Id", "Name"));
        MaybeSet(target, "move_id", target.GetValueOrDefault("intent"));

        if (includeStatus)
        {
            var powers = BuildPowers(creature);
            target["powers"] = powers;
            target["status"] = powers;
        }

        return target;
    }

    private static CombatPlayability BuildPlayability(object card)
    {
        object?[] args = { null, null };
        object? canPlayResult = ReflectionUtil.Call(card, "CanPlay", args);
        bool? canPlay = ReflectionUtil.ToBool(canPlayResult);
        string? reason = args[0] == null ? null : ReflectionUtil.SafeText(args[0]);
        if (string.Equals(reason, "None", StringComparison.Ordinal))
            reason = null;

        return new CombatPlayability(
            canPlay,
            reason,
            canPlay == null ? "can_play_unavailable" : "can_play_extracted");
    }

    private static Dictionary<string, object?>[] BuildValidTargets(
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates,
        string? indexSpace,
        string? targetType,
        object? owner)
    {
        if (targetCandidates == null || string.IsNullOrWhiteSpace(indexSpace))
            return Array.Empty<Dictionary<string, object?>>();

        return targetCandidates
            .Where(target => Equals(target.GetValueOrDefault("target_index_space"), indexSpace)
                && IsSelectableTarget(target, indexSpace)
                && !IsDisallowedSelfTarget(target, targetType, owner))
            .ToArray();
    }

    private static bool IsSelectableTarget(IReadOnlyDictionary<string, object?> target, string indexSpace)
    {
        if (Equals(target.GetValueOrDefault("is_alive"), false))
            return false;
        if (indexSpace == "enemies" && Equals(target.GetValueOrDefault("is_hittable"), false))
            return false;
        return true;
    }

    private static bool IsDisallowedSelfTarget(
        IReadOnlyDictionary<string, object?> target,
        string? targetType,
        object? owner)
    {
        if (!string.Equals(targetType, "AnyAlly", StringComparison.Ordinal))
            return false;

        string? ownerNetId = StableScalarText(ReflectionUtil.GetMemberValue(owner, "NetId"));
        string? targetNetId = target.TryGetValue("player_net_id", out object? value)
            ? StableScalarText(value)
            : null;
        return ownerNetId != null && string.Equals(ownerNetId, targetNetId, StringComparison.Ordinal);
    }

    private static bool PotionRequiresTarget(
        object potion,
        string? targetType,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates)
    {
        if (string.Equals(targetType, "AnyPlayer", StringComparison.Ordinal))
            return PotionRequiresExplicitPlayerSelection(potion, targetCandidates);

        return string.Equals(targetType, "AnyEnemy", StringComparison.Ordinal)
            || string.Equals(targetType, "AnyAlly", StringComparison.Ordinal);
    }

    private static bool PotionRequiresExplicitPlayerSelection(
        object potion,
        IReadOnlyList<Dictionary<string, object?>>? targetCandidates)
    {
        object? owner = ReflectionUtil.GetMemberValue(potion, "Owner");
        return BuildValidTargets(targetCandidates, "players", "AnyPlayer", owner).Length > 1;
    }

    private static string? PotionTargetIndexSpace(string? targetType, bool requiresTarget)
        => targetType switch
        {
            "AnyEnemy" => "enemies",
            "AnyAlly" => "players",
            "AnyPlayer" when requiresTarget => "players",
            _ => null
        };

    private static bool? BuildPotionCanUse(
        bool? passesUsabilityCheck,
        bool? isQueued,
        bool targetAvailable)
    {
        if (isQueued == true || passesUsabilityCheck == false || !targetAvailable)
            return false;
        if (passesUsabilityCheck == true)
            return true;
        return null;
    }

    private static string PotionAvailability(
        bool requiresTarget,
        bool targetAvailable,
        bool? passesUsabilityCheck,
        bool? isQueued,
        bool? canUse)
    {
        if (isQueued == true)
            return "potion_already_queued";
        if (passesUsabilityCheck == false)
            return "potion_unusable";
        if (requiresTarget && !targetAvailable)
            return "no_valid_targets";
        if (canUse == true)
            return "available";
        return "availability_unknown";
    }

    private static int? ResolveEnergyCost(object card)
    {
        object? energyCost = ReflectionUtil.GetMemberValue(card, "EnergyCost");
        return ToInt(ReflectionUtil.Call(energyCost, "GetAmountToSpend"))
            ?? ToInt(ReflectionUtil.Call(energyCost, "GetWithModifiers", null!))
            ?? ReflectionUtil.GetInt(energyCost, "Amount", "Cost", "BaseCost");
    }

    private static int? ResolveStarCost(object card)
        => ToInt(ReflectionUtil.Call(card, "GetStarCostWithModifiers"))
            ?? ReflectionUtil.GetInt(card, "CurrentStarCost", "StarCost");

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

    private static string? StableScalarText(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                string text => text,
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ when value.GetType().IsEnum => value.ToString(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void MaybeSet(IDictionary<string, object?> target, string key, object? value)
    {
        if (value != null)
            target[key] = value;
    }

    private readonly record struct CombatPlayability(bool? CanPlay, string? UnplayableReason, string ExtractionStatus);
}
