namespace Sts2Telemetry.Inspector;

public static class TelemetryRunCatalog
{
    public static readonly IReadOnlyList<string> KnownIndicators = new[]
    {
        "combat",
        "map",
        "shop",
        "event",
        "reward",
        "rest",
        "treasure",
        "card_reward",
        "relic_select",
        "bundle_select",
        "relic_trigger",
        "branch_matched"
    };

    public static RecentRunsReport BuildRecent(
        string? runsDirectory,
        TelemetryInspectorOptions? options,
        int limit,
        IReadOnlyList<string> surfaceFilters)
    {
        TelemetryInspectorOptions effectiveOptions = options ?? new TelemetryInspectorOptions();
        string effectiveRunsDirectory = TelemetryRunLocator.ResolveRunsDirectory(runsDirectory ?? effectiveOptions.RunsDirectory);
        TelemetryInspectorOptions inspectionOptions = effectiveOptions with { RunsDirectory = effectiveRunsDirectory };
        IReadOnlyList<string> normalizedFilters = NormalizeSurfaceFilters(surfaceFilters);
        int effectiveLimit = Math.Max(1, limit);

        var runs = new List<RecentRunSummary>();
        int scanned = 0;
        foreach (TelemetryRunSource source in TelemetryRunLocator.ListRecent(effectiveRunsDirectory))
        {
            scanned++;
            TelemetryInspectionReport report = TelemetryRunInspector.Inspect(source, inspectionOptions);
            RecentRunSummary summary = BuildSummary(source, report);
            if (ContainsAll(summary, normalizedFilters))
                runs.Add(summary);

            if (runs.Count >= effectiveLimit)
                break;
        }

        return new RecentRunsReport
        {
            RunsDirectory = effectiveRunsDirectory,
            SurfaceFilters = normalizedFilters,
            ScannedRunCount = scanned,
            Runs = runs
        };
    }

    private static RecentRunSummary BuildSummary(TelemetryRunSource source, TelemetryInspectionReport report)
    {
        string runName = !string.IsNullOrWhiteSpace(source.RunDirectory)
            ? Path.GetFileName(source.RunDirectory)
            : Path.GetFileName(source.TelemetryPath);

        return new RecentRunSummary
        {
            RunName = string.IsNullOrWhiteSpace(runName) ? source.TelemetryPath : runName,
            TelemetryPath = source.TelemetryPath,
            TelemetryPaths = source.TelemetryPaths,
            RunDirectory = source.RunDirectory,
            SegmentCount = source.SegmentCount,
            LastWriteTimeUtc = LastWriteTimeUtc(source),
            RunId = report.RunSummary.RunId,
            RecordCount = report.RunSummary.RecordCount,
            FirstRecordedAtUtc = report.RunSummary.FirstRecordedAtUtc,
            LastRecordedAtUtc = report.RunSummary.LastRecordedAtUtc,
            NormalEnded = report.RunSummary.NormalEnded,
            IsValid = report.Validation.IsValid,
            HardFailureCount = report.Validation.Errors.Count,
            Indicators = BuildIndicators(report)
        };
    }

    private static DateTime LastWriteTimeUtc(TelemetryRunSource source)
        => source.TelemetryPaths
            .Select(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default)
            .DefaultIfEmpty(default)
            .Max();

    private static IReadOnlyList<SurfaceCoverage> BuildIndicators(TelemetryInspectionReport report)
    {
        var indicators = new List<SurfaceCoverage>(report.Coverage.Surfaces);
        indicators.Add(new SurfaceCoverage("branch_matched")
        {
            Appeared = report.Branch.BranchMatchedCount > 0,
            RecordCount = report.Branch.BranchMatchedCount
        });

        return indicators;
    }

    private static bool ContainsAll(RecentRunSummary summary, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
            return true;

        HashSet<string> appeared = summary.Indicators
            .Where(indicator => indicator.Appeared)
            .Select(indicator => indicator.Surface)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return filters.All(appeared.Contains);
    }

    private static IReadOnlyList<string> NormalizeSurfaceFilters(IReadOnlyList<string> surfaceFilters)
    {
        if (surfaceFilters.Count == 0)
            return Array.Empty<string>();

        var normalized = new List<string>();
        foreach (string filter in surfaceFilters)
        {
            string value = NormalizeSurfaceName(filter);
            if (!KnownIndicators.Contains(value, StringComparer.Ordinal))
            {
                string known = string.Join(", ", KnownIndicators);
                throw new ArgumentException($"Unknown surface '{filter}'. Known surfaces: {known}.");
            }

            if (!normalized.Contains(value, StringComparer.Ordinal))
                normalized.Add(value);
        }

        return normalized;
    }

    private static string NormalizeSurfaceName(string surface)
    {
        string value = surface.Trim().ToLowerInvariant().Replace('-', '_');
        return value switch
        {
            "rewards" => "reward",
            "rest_site" => "rest",
            "relictrigger" => "relic_trigger",
            "branchmatch" => "branch_matched",
            _ => value
        };
    }
}
