using System.Text.Json;
using Sts2Telemetry.Inspector;

namespace Sts2Telemetry.Cli;

public static class TelemetryCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp(output);
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        if (command is not ("inspect" or "frames" or "branch" or "coverage" or "perf" or "show" or "validate" or "runs"))
        {
            error.WriteLine($"Unknown command '{args[0]}'.");
            WriteHelp(error);
            return 2;
        }

        if (!TryParseOptions(args.Skip(1).ToArray(), error, out ParsedOptions parsed))
            return 2;

        try
        {
            if (command == "runs")
                return RunRuns(parsed, output);

            TelemetryRunSource source = TelemetryRunLocator.Resolve(parsed.Source ?? "latest", parsed.InspectorOptions.RunsDirectory);

            if (command == "show")
                return RunShow(source, parsed, output, error);

            TelemetryInspectionReport report = TelemetryRunInspector.Inspect(source, parsed.InspectorOptions);
            return command switch
            {
                "inspect" => RunInspect(report, parsed, output),
                "frames" => RunFrames(report, parsed.TopSize ?? 10, output),
                "branch" => RunBranch(report, output),
                "coverage" => RunCoverage(report, output),
                "perf" => RunPerf(report, output),
                "validate" => RunValidate(report, output, error),
                _ => 2
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int RunInspect(TelemetryInspectionReport report, ParsedOptions parsed, TextWriter output)
    {
        if (parsed.Json)
            output.WriteLine(JsonSerializer.Serialize(report, TelemetryInspectorJson.IndentedOptions));
        else
            ReportRenderer.WriteInspect(report, output);

        return 0;
    }

    private static int RunFrames(TelemetryInspectionReport report, int topSize, TextWriter output)
    {
        ReportRenderer.WriteFrames(report, Math.Max(1, topSize), output);
        return 0;
    }

    private static int RunBranch(TelemetryInspectionReport report, TextWriter output)
    {
        ReportRenderer.WriteBranch(report, output);
        return 0;
    }

    private static int RunCoverage(TelemetryInspectionReport report, TextWriter output)
    {
        ReportRenderer.WriteCoverage(report, output);
        return 0;
    }

    private static int RunPerf(TelemetryInspectionReport report, TextWriter output)
    {
        ReportRenderer.WritePerf(report, output);
        return 0;
    }

    private static int RunRuns(ParsedOptions parsed, TextWriter output)
    {
        RecentRunsReport report = TelemetryRunCatalog.BuildRecent(
            parsed.Source,
            parsed.InspectorOptions,
            parsed.TopSize ?? 10,
            parsed.SurfaceFilters);
        ReportRenderer.WriteRuns(report, output);
        return 0;
    }

    private static int RunShow(TelemetryRunSource source, ParsedOptions parsed, TextWriter output, TextWriter error)
    {
        if (parsed.Sequence == null)
        {
            error.WriteLine("show requires --seq <local_sequence>.");
            return 2;
        }

        TelemetryRecord? record = TelemetryRunInspector.FindBySequence(source, parsed.Sequence.Value);
        if (record == null)
        {
            error.WriteLine($"No record found with local_sequence {parsed.Sequence.Value} in {source.TelemetryPath}.");
            return 1;
        }

        if (record.IsMalformed)
        {
            error.WriteLine($"Record at local_sequence {parsed.Sequence.Value} is malformed JSON: {record.ParseError}");
            return 1;
        }

        output.WriteLine(record.RawJson);
        return 0;
    }

    private static int RunValidate(TelemetryInspectionReport report, TextWriter output, TextWriter error)
    {
        if (report.Validation.IsValid)
        {
            output.WriteLine($"OK: {report.RunSummary.RecordCount} record(s) inspected; no hard validation failures.");
            return 0;
        }

        error.WriteLine($"Validation failed: {report.Validation.Errors.Count} hard failure(s).");
        foreach (TelemetryFinding finding in report.Validation.Errors)
        {
            string location = finding.Sequence.HasValue
                ? $"seq {finding.Sequence.Value}"
                : finding.LineNumber.HasValue ? $"line {finding.LineNumber.Value}" : "run";
            string source = string.IsNullOrWhiteSpace(finding.SourcePath) ? "" : $" in {finding.SourcePath}";
            error.WriteLine($"- {finding.Code} at {location}{source}: {finding.Message}");
        }

        return 1;
    }

    private static bool TryParseOptions(string[] args, TextWriter error, out ParsedOptions parsed)
    {
        parsed = new ParsedOptions();
        var options = new TelemetryInspectorOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--json":
                    parsed = parsed with { Json = true };
                    break;
                case "--runs-dir":
                    if (!TryReadValue(args, ref i, arg, error, out string? runsDirectory))
                        return false;
                    options = options with { RunsDirectory = runsDirectory };
                    break;
                case "--operational-dir":
                    if (!TryReadValue(args, ref i, arg, error, out string? operationalDirectory))
                        return false;
                    options = options with { OperationalDirectory = operationalDirectory };
                    break;
                case "--max-frame-bytes":
                    if (!TryReadLong(args, ref i, arg, error, out long maxFrameBytes))
                        return false;
                    options = options with { MaxFrameBytes = maxFrameBytes };
                    break;
                case "--unknown-threshold":
                    if (!TryReadInt(args, ref i, arg, error, out int unknownThreshold))
                        return false;
                    options = options with { UnknownMarkerThreshold = unknownThreshold };
                    break;
                case "--top-size":
                case "--limit":
                    if (!TryReadInt(args, ref i, arg, error, out int topSize))
                        return false;
                    parsed = parsed with { TopSize = topSize };
                    break;
                case "--surface":
                    if (!TryReadValue(args, ref i, arg, error, out string? surfaceFilter))
                        return false;
                    IReadOnlyList<string> surfaceFilters = SplitSurfaceFilters(surfaceFilter);
                    if (surfaceFilters.Count == 0)
                    {
                        error.WriteLine("--surface requires at least one surface name.");
                        return false;
                    }

                    parsed = parsed with
                    {
                        SurfaceFilters = parsed.SurfaceFilters.Concat(surfaceFilters).ToArray()
                    };
                    break;
                case "--top-examples":
                    if (!TryReadInt(args, ref i, arg, error, out int topExamples))
                        return false;
                    options = options with { TopExamples = topExamples };
                    break;
                case "--seq":
                    if (!TryReadLong(args, ref i, arg, error, out long sequence))
                        return false;
                    parsed = parsed with { Sequence = sequence };
                    break;
                case "--help":
                case "-h":
                    parsed = parsed with { HelpRequested = true };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine($"Unknown option '{arg}'.");
                        return false;
                    }

                    if (parsed.Source != null)
                    {
                        error.WriteLine($"Unexpected positional argument '{arg}'.");
                        return false;
                    }

                    parsed = parsed with { Source = arg };
                    break;
            }
        }

        if (parsed.HelpRequested)
        {
            WriteHelp(error);
            return false;
        }

        parsed = parsed with { InspectorOptions = options };
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, string option, TextWriter error, out string value)
    {
        value = "";
        if (index + 1 >= args.Length)
        {
            error.WriteLine($"{option} requires a value.");
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TryReadInt(string[] args, ref int index, string option, TextWriter error, out int value)
    {
        value = 0;
        if (!TryReadValue(args, ref index, option, error, out string raw))
            return false;

        if (!int.TryParse(raw, out value) || value < 0)
        {
            error.WriteLine($"{option} requires a non-negative integer.");
            return false;
        }

        return true;
    }

    private static bool TryReadLong(string[] args, ref int index, string option, TextWriter error, out long value)
    {
        value = 0;
        if (!TryReadValue(args, ref index, option, error, out string raw))
            return false;

        if (!long.TryParse(raw, out value) || value < 0)
        {
            error.WriteLine($"{option} requires a non-negative integer.");
            return false;
        }

        return true;
    }

    private static bool IsHelp(string arg)
        => arg is "--help" or "-h" or "help";

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage: sts2-telemetry <command> [latest|telemetry.jsonl|run-dir] [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  inspect    Print run summary, health, performance, branch, coverage, suspicious, and examples.");
        writer.WriteLine("  frames     List largest records by byte size.");
        writer.WriteLine("  branch     Summarize branch and attempt timeline evidence.");
        writer.WriteLine("  coverage   Summarize combat/map/shop/event/reward/rest/treasure/relic-trigger coverage.");
        writer.WriteLine("  perf       Summarize frame size, write rate, and decision timing phases.");
        writer.WriteLine("  show       Print the exact JSON record for --seq <local_sequence>.");
        writer.WriteLine("  validate   Exit non-zero when hard telemetry validation failures exist.");
        writer.WriteLine("  runs       List recent telemetry runs and compact surface/action indicators.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --json                         Structured JSON output for inspect.");
        writer.WriteLine("  --runs-dir <path>              Override the default latest-runs directory.");
        writer.WriteLine("  --operational-dir <path>       Override operational JSONL directory for callback failures.");
        writer.WriteLine($"  --max-frame-bytes <n>          Default {TelemetryInspectorOptions.DefaultMaxFrameBytes}.");
        writer.WriteLine($"  --unknown-threshold <n>        Default {TelemetryInspectorOptions.DefaultUnknownMarkerThreshold}.");
        writer.WriteLine("  --top-size <n>                 Number of records for frames output or runs output.");
        writer.WriteLine("  --limit <n>                    Alias for --top-size on runs.");
        writer.WriteLine("  --surface <name[,name]>        For runs, return runs containing all listed indicators.");
        writer.WriteLine("  --top-examples <n>             Number of examples for inspect output.");
        writer.WriteLine("  --seq <n>                      local_sequence for show.");
        writer.WriteLine();
        writer.WriteLine("Default latest directory can also be overridden with STS2_TELEMETRY_RUNS_DIR.");
        writer.WriteLine("Default operational directory can also be overridden with STS2_TELEMETRY_OPERATIONAL_DIR.");
        writer.WriteLine("This tool is local-only and read-only; it never uploads or mutates telemetry.");
    }

    private sealed record ParsedOptions
    {
        public string? Source { get; init; }
        public bool Json { get; init; }
        public bool HelpRequested { get; init; }
        public int? TopSize { get; init; }
        public long? Sequence { get; init; }
        public IReadOnlyList<string> SurfaceFilters { get; init; } = Array.Empty<string>();
        public TelemetryInspectorOptions InspectorOptions { get; init; } = new();
    }

    private static IReadOnlyList<string> SplitSurfaceFilters(string value)
        => value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(surface => !string.IsNullOrWhiteSpace(surface))
            .ToArray();
}
