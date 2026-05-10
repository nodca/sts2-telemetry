using System.Runtime.CompilerServices;

namespace Sts2Telemetry;

internal sealed class SelectionChoiceCache
{
    private const int MaxOptions = 20;
    private const int MaxCardsPerBundle = 20;

    private readonly object _gate = new();
    private SelectionSurface? _activeRelicSelect;
    private SelectionSurface? _activeBundleSelect;

    public static SelectionChoiceCache Shared { get; } = new();

    public void Clear()
    {
        lock (_gate)
        {
            _activeRelicSelect = null;
            _activeBundleSelect = null;
        }
    }

    public bool CaptureRelicSelect(object? player, object? relics, string source)
    {
        var entries = new List<SelectionEntry>();
        int index = 0;
        foreach (object? relic in ReflectionUtil.Enumerate(relics, MaxOptions))
        {
            if (relic != null)
                entries.Add(new SelectionEntry(index, BuildRelicSelectAction(relic, index)));
            index++;
        }

        entries.Add(new SelectionEntry(-1, BuildRelicSelectSkipAction()));
        return CaptureSurface("relic_select", source, player, entries, surface => _activeRelicSelect = surface);
    }

    public bool CaptureBundleSelect(object? player, object? bundles, string source)
    {
        var entries = new List<SelectionEntry>();
        int index = 0;
        foreach (object? bundle in ReflectionUtil.Enumerate(bundles, MaxOptions))
        {
            var cards = ReflectionUtil.Enumerate(bundle, MaxCardsPerBundle)
                .Where(card => card != null)
                .Cast<object>()
                .ToArray();
            if (cards.Length > 0)
                entries.Add(new SelectionEntry(index, BuildBundleSelectAction(cards, index)));
            index++;
        }

        return CaptureSurface("bundle_select", source, player, entries, surface => _activeBundleSelect = surface);
    }

    public IReadOnlyList<Dictionary<string, object?>>? BuildLegalActions(string stateType)
    {
        SelectionSurface? surface;
        lock (_gate)
        {
            surface = stateType switch
            {
                "relic_select" => _activeRelicSelect,
                "bundle_select" => _activeBundleSelect,
                _ => null
            };
        }

        return surface?.Entries
            .Select(entry => new Dictionary<string, object?>(entry.Action, StringComparer.Ordinal))
            .ToArray();
    }

    public bool TryProjectPlayerChoiceSelection(
        object? player,
        IReadOnlyDictionary<string, object?> resultMetadata,
        out SortedDictionary<string, object?> projection)
    {
        projection = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        int? selectedIndex = ToInt(FirstPresent(resultMetadata, "selected_index"));
        if (selectedIndex == null)
            return false;

        SelectionSurface? relicSelect;
        SelectionSurface? bundleSelect;
        lock (_gate)
        {
            relicSelect = _activeRelicSelect;
            bundleSelect = _activeBundleSelect;
        }

        SelectionSurface? surface = new[] { relicSelect, bundleSelect }
            .Where(candidate => candidate != null && candidate.MatchesPlayer(player))
            .OrderByDescending(candidate => candidate!.CapturedAtUtc)
            .FirstOrDefault(candidate => candidate!.TryFindAction(selectedIndex.Value, out _));

        if (surface == null || !surface.TryFindAction(selectedIndex.Value, out SelectionEntry entry))
            return false;

        foreach (var (key, value) in entry.Action)
        {
            if (key is "source" or "message")
                continue;
            projection[key] = value;
        }

        projection["source"] = "player_choice_synchronizer.player_choice_received";
        projection["selection_source"] = "player_choice_synchronizer";
        projection["choice_surface"] = surface.StateType;
        projection["runtime_opening_source"] = surface.Source;
        projection["selected_index"] = selectedIndex.Value;
        projection["selected_option_index"] = selectedIndex.Value;
        projection["projection_policy"] = $"typed_{surface.StateType}_runtime_cache";
        projection["normalized_typed_action_key"] = ActionMetadata.BuildNormalizedTypedActionKey(
            new Dictionary<string, object?>(projection, StringComparer.Ordinal));
        return true;
    }

    private bool CaptureSurface(
        string stateType,
        string source,
        object? player,
        IReadOnlyList<SelectionEntry> entries,
        Action<SelectionSurface> assign)
    {
        if (entries.Count == 0)
            return false;

        var surface = new SelectionSurface(
            StateType: stateType,
            Source: source,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Player: player == null ? null : new WeakReference<object>(player),
            Entries: entries);

        lock (_gate)
            assign(surface);
        return true;
    }

    private static Dictionary<string, object?> BuildRelicSelectAction(object relic, int index)
    {
        var action = new Dictionary<string, object?>
        {
            ["action_type"] = "choose_relic_select",
            ["source"] = "relic_select_runtime_cache",
            ["state_type"] = "relic_select",
            ["relic_index"] = index,
            ["option_index"] = index,
            ["relic_id"] = EntityId(relic),
            ["relic_rarity"] = StableScalarText(ReflectionUtil.GetMemberValue(relic, "Rarity")),
            ["relic_runtime_type"] = relic.GetType().FullName,
            ["can_select"] = true,
            ["availability"] = "available",
            ["projection_policy"] = "typed_relic_select_runtime_cache",
            ["text_status"] = "text_suppressed_locstring_runtime_safety"
        };
        action["match_key"] = ActionMetadata.BuildNormalizedTypedActionKey(action);
        return action;
    }

    private static Dictionary<string, object?> BuildRelicSelectSkipAction()
    {
        var action = new Dictionary<string, object?>
        {
            ["action_type"] = "skip_relic_select",
            ["source"] = "relic_select_runtime_cache",
            ["state_type"] = "relic_select",
            ["relic_index"] = null,
            ["option_index"] = -1,
            ["alternative_id"] = "Skip",
            ["can_select"] = true,
            ["availability"] = "available",
            ["projection_policy"] = "typed_relic_select_skip_button",
            ["text_status"] = "text_suppressed_locstring_runtime_safety"
        };
        action["match_key"] = ActionMetadata.BuildNormalizedTypedActionKey(action);
        return action;
    }

    private static Dictionary<string, object?> BuildBundleSelectAction(IReadOnlyList<object> cards, int index)
    {
        var cardSummaries = cards
            .Select((card, cardIndex) => ProjectBundleCard(card, cardIndex))
            .ToArray();

        var action = new Dictionary<string, object?>
        {
            ["action_type"] = "choose_card_bundle",
            ["source"] = "bundle_select_runtime_cache",
            ["state_type"] = "bundle_select",
            ["bundle_index"] = index,
            ["option_index"] = index,
            ["card_count"] = cards.Count,
            ["card_ids"] = cardSummaries
                .Select(summary => StringValue(summary, "card_id"))
                .Where(id => id != null)
                .ToArray(),
            ["cards"] = cardSummaries,
            ["can_select"] = true,
            ["availability"] = "available",
            ["projection_policy"] = "typed_bundle_select_runtime_cache",
            ["text_status"] = "text_suppressed_locstring_runtime_safety"
        };
        action["match_key"] = ActionMetadata.BuildNormalizedTypedActionKey(action);
        return action;
    }

    private static Dictionary<string, object?> ProjectBundleCard(object card, int index)
        => new()
        {
            ["card_index"] = index,
            ["card_id"] = EntityId(card),
            ["card_type"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "Type")),
            ["rarity"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "Rarity")),
            ["is_upgraded"] = ReflectionUtil.GetBool(card, "IsUpgraded", "Upgraded"),
            ["target_type"] = StableScalarText(ReflectionUtil.GetMemberValue(card, "TargetType")),
            ["energy_cost"] = ResolveEnergyCost(card),
            ["card_runtime_type"] = card.GetType().FullName
        };

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

    private static object? FirstPresent(IReadOnlyDictionary<string, object?> source, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (source.TryGetValue(key, out object? value))
                return value;
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

    private sealed record SelectionSurface(
        string StateType,
        string Source,
        DateTimeOffset CapturedAtUtc,
        WeakReference<object>? Player,
        IReadOnlyList<SelectionEntry> Entries)
    {
        public bool MatchesPlayer(object? player)
        {
            if (Player == null)
                return true;
            return player != null && Player.TryGetTarget(out object? captured) && ReferenceEquals(captured, player);
        }

        public bool TryFindAction(int selectedIndex, out SelectionEntry entry)
        {
            foreach (SelectionEntry candidate in Entries)
            {
                if (candidate.SelectedIndex == selectedIndex)
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = default;
            return false;
        }
    }

    private readonly record struct SelectionEntry(
        int SelectedIndex,
        Dictionary<string, object?> Action);
}
