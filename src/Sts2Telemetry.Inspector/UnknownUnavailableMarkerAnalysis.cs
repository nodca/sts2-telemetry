using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sts2Telemetry.Inspector;

internal sealed record UnknownUnavailableMarkerAnalysis(
    int RawCount,
    int UniqueNormalizedCount,
    int ExpectedGapRawCount,
    int ExpectedGapUniqueCount,
    int ContractRiskRawCount,
    int ContractRiskUniqueCount,
    IReadOnlyList<UnknownUnavailableMarkerCategory> TopExpectedGapCategories,
    IReadOnlyList<UnknownUnavailableMarkerCategory> TopContractRiskCategories)
{
    public static UnknownUnavailableMarkerAnalysis Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<UnknownUnavailableMarkerCategory>(),
        Array.Empty<UnknownUnavailableMarkerCategory>());

    public UnknownUnavailableSummary ToSummary()
        => new()
        {
            RawCount = RawCount,
            UniqueNormalizedCount = UniqueNormalizedCount,
            ExpectedGapRawCount = ExpectedGapRawCount,
            ExpectedGapUniqueCount = ExpectedGapUniqueCount,
            ContractRiskRawCount = ContractRiskRawCount,
            ContractRiskUniqueCount = ContractRiskUniqueCount,
            TopExpectedGapCategories = TopExpectedGapCategories,
            TopContractRiskCategories = TopContractRiskCategories
        };
}

public sealed record UnknownUnavailableSummary
{
    public int RawCount { get; init; }
    public int UniqueNormalizedCount { get; init; }
    public int ExpectedGapRawCount { get; init; }
    public int ExpectedGapUniqueCount { get; init; }
    public int ContractRiskRawCount { get; init; }
    public int ContractRiskUniqueCount { get; init; }
    public IReadOnlyList<UnknownUnavailableMarkerCategory> TopExpectedGapCategories { get; init; }
        = Array.Empty<UnknownUnavailableMarkerCategory>();
    public IReadOnlyList<UnknownUnavailableMarkerCategory> TopContractRiskCategories { get; init; }
        = Array.Empty<UnknownUnavailableMarkerCategory>();
}

public sealed record UnknownUnavailableMarkerCategory
{
    public string Code { get; init; } = "";
    public string Classification { get; init; } = "";
    public string NormalizedPath { get; init; } = "";
    public string Value { get; init; } = "";
    public string Summary { get; init; } = "";
    public int Count { get; init; }
}

internal static class UnknownUnavailableMarkerAnalyzer
{
    private static readonly Regex ArrayIndexPattern = new(@"\[\d+\]", RegexOptions.Compiled);

    public static UnknownUnavailableMarkerAnalysis Analyze(IEnumerable<TelemetryRecord> records)
    {
        var expected = new Dictionary<string, MarkerCount>(StringComparer.Ordinal);
        var contractRisk = new Dictionary<string, MarkerCount>(StringComparer.Ordinal);
        var uniqueNormalized = new HashSet<string>(StringComparer.Ordinal);
        var uniqueExpected = new HashSet<string>(StringComparer.Ordinal);
        var uniqueContractRisk = new HashSet<string>(StringComparer.Ordinal);

        int rawCount = 0;
        int expectedRawCount = 0;
        int contractRiskRawCount = 0;

        foreach (TelemetryRecord record in records)
        {
            if (record.IsMalformed || record.Root is not JsonElement root)
                continue;

            foreach ((string path, string value) in JsonElementAccess.EnumerateStrings(root))
            {
                if (!IsUnknownUnavailableMarker(value))
                    continue;

                rawCount++;
                string normalizedPath = NormalizePath(path);
                string uniqueKey = $"{normalizedPath}={value}";
                uniqueNormalized.Add(uniqueKey);

                MarkerClassification classification = Classify(normalizedPath, value);
                string categoryKey = $"{classification.Code}|{normalizedPath}|{value}";
                if (classification.Classification == "expected_gap")
                {
                    expectedRawCount++;
                    uniqueExpected.Add(uniqueKey);
                    MarkerCount existing = expected.TryGetValue(categoryKey, out MarkerCount? found)
                        ? found
                        : MarkerCount.Create(classification, normalizedPath, value);
                    expected[categoryKey] = MarkerCount.Increment(existing);
                }
                else
                {
                    contractRiskRawCount++;
                    uniqueContractRisk.Add(uniqueKey);
                    MarkerCount existing = contractRisk.TryGetValue(categoryKey, out MarkerCount? found)
                        ? found
                        : MarkerCount.Create(classification, normalizedPath, value);
                    contractRisk[categoryKey] = MarkerCount.Increment(existing);
                }
            }
        }

        return new UnknownUnavailableMarkerAnalysis(
            rawCount,
            uniqueNormalized.Count,
            expectedRawCount,
            uniqueExpected.Count,
            contractRiskRawCount,
            uniqueContractRisk.Count,
            BuildTopCategories(expected),
            BuildTopCategories(contractRisk));
    }

    private static IReadOnlyList<UnknownUnavailableMarkerCategory> BuildTopCategories(
        IReadOnlyDictionary<string, MarkerCount> counts)
        => counts.Values
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Code, StringComparer.Ordinal)
            .ThenBy(count => count.NormalizedPath, StringComparer.Ordinal)
            .Take(8)
            .Select(count => new UnknownUnavailableMarkerCategory
            {
                Code = count.Code,
                Classification = count.Classification,
                NormalizedPath = count.NormalizedPath,
                Value = count.Value,
                Summary = count.Summary,
                Count = count.Count
            })
            .ToArray();

    private static MarkerClassification Classify(string normalizedPath, string value)
    {
        if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".game.game_version", StringComparison.Ordinal))
        {
            return new MarkerClassification(
                "game_version_unknown",
                "expected_gap",
                "Game version metadata is currently unavailable in safe runtime capture.");
        }

        if (string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase)
            && (normalizedPath.Contains(".combat.process.marker_status.phase", StringComparison.Ordinal)
                || normalizedPath.StartsWith("combat.process.marker_status.phase", StringComparison.Ordinal)
                || normalizedPath.StartsWith("combat_process.marker_status.phase", StringComparison.Ordinal)))
        {
            return new MarkerClassification(
                "combat_phase_unavailable",
                "expected_gap",
                "Safe combat snapshots may omit phase markers; keep this visible as readiness noise, not contract failure.");
        }

        if (string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase)
            && (normalizedPath.Contains(".combat.process.marker_status.action_step", StringComparison.Ordinal)
                || normalizedPath.StartsWith("combat.process.marker_status.action_step", StringComparison.Ordinal)
                || normalizedPath.StartsWith("combat_process.marker_status.action_step", StringComparison.Ordinal)
                || normalizedPath.Contains(".combat_process.marker_status.action_step", StringComparison.Ordinal)))
        {
            return new MarkerClassification(
                "combat_action_step_unavailable",
                "expected_gap",
                "Safe combat snapshots may omit action-step markers; keep this visible as readiness noise, not contract failure.");
        }

        if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedPath, "branch.branch_status", StringComparison.Ordinal))
        {
            return new MarkerClassification(
                "branch_status_unknown",
                "expected_gap",
                "Signal-only suspend/load-preview lifecycle paths can leave branch status unresolved without indicating a broken telemetry contract.");
        }

        if (string.Equals(value, "room/unknown", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(normalizedPath, "state.state_type", StringComparison.Ordinal)
                || string.Equals(normalizedPath, "state.snapshot.state_type", StringComparison.Ordinal)))
        {
            return new MarkerClassification(
                "room_state_unknown",
                "expected_gap",
                "Early run-start and resumed-state snapshots can legitimately report room/unknown before a stable room surface settles.");
        }

        if (value.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedPath, "selected_action.raw.extraction_status.target", StringComparison.Ordinal))
        {
            return new MarkerClassification(
                "selected_action_target_unavailable",
                "expected_gap",
                "Selected-action target extraction can be intentionally suppressed when the typed card target surface is unavailable; keep it visible as readiness noise.");
        }

        if (value.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedPath, "selected_action.raw.extraction_status.target_card_target_type", StringComparison.Ordinal))
        {
            return new MarkerClassification(
                "selected_action_target_type_unavailable",
                "expected_gap",
                "Selected-action target-card target typing can be intentionally unavailable on bounded typed surfaces; keep it visible as readiness noise.");
        }

        if (string.Equals(value, "effect_summary_unavailable", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".effect_summary_status", StringComparison.Ordinal))
        {
            return new MarkerClassification(
                "effect_summary_unavailable",
                "expected_gap",
                "Signal-only effect attribution intentionally does not compute delta summaries.");
        }

        return new MarkerClassification(
            "unknown_unavailable_contract_risk",
            "contract_risk",
            "Unexpected unknown/unavailable marker outside approved readiness-gap categories.");
    }

    private static string NormalizePath(string path)
    {
        string normalized = path
            .Replace(".raw_snapshot", ".snapshot", StringComparison.Ordinal)
            .Replace(".canonical_snapshot", ".snapshot", StringComparison.Ordinal);
        return ArrayIndexPattern.Replace(normalized, "[]");
    }

    private static bool IsUnknownUnavailableMarker(string value)
        => value.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || value.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

    private sealed record MarkerClassification(string Code, string Classification, string Summary);

    private sealed record MarkerCount(
        string Code,
        string Classification,
        string NormalizedPath,
        string Value,
        string Summary,
        int Count)
    {
        public static MarkerCount Create(MarkerClassification classification, string normalizedPath, string value)
            => new(
                classification.Code,
                classification.Classification,
                normalizedPath,
                value,
                classification.Summary,
                0);

        public static MarkerCount Increment(MarkerCount count)
            => count with { Count = count.Count + 1 };
    }
}
