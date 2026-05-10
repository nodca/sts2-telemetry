namespace Sts2Telemetry;

internal static class RelicTriggerMetadata
{
    private const int MaxTargets = 40;

    public static IReadOnlyDictionary<string, object?> FromRelicFlash(
        string source,
        object? relic,
        object? targets)
    {
        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source"] = source,
            ["trigger_attribution"] = "visual_flash_observed",
            ["effect_attribution"] = "effect_delta_not_computed",
            ["effect_summary_status"] = "effect_summary_unavailable",
            ["projection_policy"] = "typed_relic_flash_signal",
            ["target_projection_policy"] = "bounded_runtime_creature_projection"
        };

        var relicMetadata = BuildRelicMetadata(relic);
        metadata["relic"] = relicMetadata;
        CopyIfPresent(metadata, relicMetadata, "relic_id");
        CopyIfPresent(metadata, relicMetadata, "relic_runtime_type");
        CopyIfPresent(metadata, relicMetadata, "relic_runtime_type_name");
        CopyIfPresent(metadata, relicMetadata, "relic_rarity");

        var targetMetadata = BuildTargetMetadata(targets);
        metadata["targets"] = targetMetadata.Targets;
        metadata["target_count"] = targetMetadata.Targets.Count;
        metadata["target_extraction"] = targetMetadata.ExtractionStatus;
        return metadata;
    }

    private static Dictionary<string, object?> BuildRelicMetadata(object? relic)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["extraction_status"] = relic == null ? "relic_unavailable" : "extracted"
        };

        if (relic == null)
            return metadata;

        Type type = relic.GetType();
        metadata["relic_runtime_type"] = type.FullName;
        metadata["relic_runtime_type_name"] = type.Name;

        MaybeSet(metadata, "relic_id", EntityId(relic));
        MaybeSet(metadata, "relic_rarity", StableScalarText(ReflectionUtil.GetMemberValue(relic, "Rarity")));
        MaybeSet(metadata, "status", StableScalarText(ReflectionUtil.GetMemberValue(relic, "Status")));
        MaybeSet(metadata, "display_amount", ReflectionUtil.GetInt(relic, "DisplayAmount"));
        MaybeSet(metadata, "stack_count", ReflectionUtil.GetInt(relic, "StackCount"));
        MaybeSet(metadata, "floor_added_to_deck", ReflectionUtil.GetInt(relic, "FloorAddedToDeck"));
        MaybeSet(metadata, "show_counter", ReflectionUtil.GetBool(relic, "ShowCounter"));
        MaybeSet(metadata, "is_used_up", ReflectionUtil.GetBool(relic, "IsUsedUp"));
        MaybeSet(metadata, "is_wax", ReflectionUtil.GetBool(relic, "IsWax"));
        MaybeSet(metadata, "is_melted", ReflectionUtil.GetBool(relic, "IsMelted"));
        MaybeSet(metadata, "should_flash_on_player", ReflectionUtil.GetBool(relic, "ShouldFlashOnPlayer"));
        MaybeSet(metadata, "has_upon_pickup_effect", ReflectionUtil.GetBool(relic, "HasUponPickupEffect"));
        MaybeSet(metadata, "is_stackable", ReflectionUtil.GetBool(relic, "IsStackable"));

        object? owner = ReflectionUtil.GetMemberValue(relic, "Owner");
        if (owner != null)
        {
            metadata["owner"] = new Dictionary<string, object?>
            {
                ["character"] = ReflectionUtil.GetText(ReflectionUtil.GetMemberValue(owner, "Character"), "Title", "Id"),
                ["is_present"] = true
            };
        }

        return metadata;
    }

    private static TargetMetadata BuildTargetMetadata(object? targets)
    {
        if (targets == null)
            return new TargetMetadata(Array.Empty<Dictionary<string, object?>>(), "target_collection_unavailable");

        var projected = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (object? target in ReflectionUtil.Enumerate(targets, MaxTargets))
        {
            if (target != null)
                projected.Add(CombatProjection.BuildCreatureSignalTarget(target, index, entityCounts, includeStatus: true));
            index++;
        }

        return new TargetMetadata(
            projected,
            projected.Count == 0 ? "target_collection_empty" : "extracted");
    }

    private static string? EntityId(object? value)
    {
        object? id = ReflectionUtil.GetMemberValue(value, "Id", "ID", "Key");
        return CombatProjection.GetModelEntry(id) ?? StableScalarText(id);
    }

    private static string? StableScalarText(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                string text => string.IsNullOrWhiteSpace(text) ? null : text,
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ when value.GetType().IsEnum => value.ToString(),
                _ => CombatProjection.GetModelEntry(value)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void CopyIfPresent(
        IDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (source.TryGetValue(key, out object? value) && value != null)
            target[key] = value;
    }

    private static void MaybeSet(IDictionary<string, object?> target, string key, object? value)
    {
        if (value != null)
            target[key] = value;
    }

    private sealed record TargetMetadata(
        IReadOnlyList<Dictionary<string, object?>> Targets,
        string ExtractionStatus);
}
