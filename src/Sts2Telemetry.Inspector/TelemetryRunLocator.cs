namespace Sts2Telemetry.Inspector;

public static class TelemetryRunLocator
{
    public const string TelemetryFileName = "telemetry.jsonl";
    public const string LogicalRunDirectoryPrefix = "logical-run-";
    public const string SegmentsDirectoryName = "segments";

    public static TelemetryRunSource Resolve(string sourceArgument, string? runsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(sourceArgument) || string.Equals(sourceArgument, "latest", StringComparison.OrdinalIgnoreCase))
            return ResolveLatest(runsDirectory);

        string expanded = ExpandHome(sourceArgument);
        if (File.Exists(expanded))
            return FromFile(sourceArgument, Path.GetFullPath(expanded), "file");

        if (Directory.Exists(expanded))
        {
            if (TryResolveDirectory(sourceArgument, Path.GetFullPath(expanded), "run_directory", out TelemetryRunSource? source))
                return source!;

            TelemetryRunSource? latestUnderDirectory = FindLatestRunSource(expanded, "latest_under_directory");
            if (latestUnderDirectory != null)
                return latestUnderDirectory with { SourceArgument = sourceArgument, Resolution = "latest_under_directory" };
        }

        throw new FileNotFoundException($"Could not resolve telemetry source '{sourceArgument}'. Use 'latest', a telemetry.jsonl file, a legacy run directory, or a logical run directory.");
    }

    public static string DefaultRunsDirectory()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? "";

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrWhiteSpace(programFiles))
                programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programFiles, "Steam", "steamapps", "common", "Slay the Spire 2", "mods", "telemetry", "runs");
        }

        return Path.Combine(
            home,
            ".var",
            "app",
            "com.valvesoftware.Steam",
            ".local",
            "share",
            "Steam",
            "steamapps",
            "common",
            "Slay the Spire 2",
            "mods",
            "telemetry",
            "runs");
    }

    public static string DefaultOperationalDirectory()
    {
        string runsDirectory = DefaultRunsDirectory();
        string telemetryDirectory = Path.GetDirectoryName(runsDirectory) ?? runsDirectory;
        return Path.Combine(telemetryDirectory, "operational");
    }

    public static string ResolveRunsDirectory(string? runsDirectory = null)
    {
        string directory = ExpandHome(runsDirectory ?? Environment.GetEnvironmentVariable("STS2_TELEMETRY_RUNS_DIR") ?? DefaultRunsDirectory());
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Telemetry runs directory does not exist: {directory}");

        return Path.GetFullPath(directory);
    }

    public static IReadOnlyList<TelemetryRunSource> ListRecent(string? runsDirectory = null)
    {
        string directory = ResolveRunsDirectory(runsDirectory);
        return EnumerateRunSources(directory, "recent_under_directory")
            .ToArray();
    }

    public static string ResolveOperationalDirectory(string telemetryPath, TelemetryInspectorOptions options)
    {
        string? configured = options.OperationalDirectory
            ?? Environment.GetEnvironmentVariable("STS2_TELEMETRY_OPERATIONAL_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            return ExpandHome(configured);

        if (!string.IsNullOrWhiteSpace(options.RunsDirectory))
        {
            string runsDirectory = ExpandHome(options.RunsDirectory);
            string telemetryDirectory = Path.GetDirectoryName(runsDirectory) ?? runsDirectory;
            return Path.Combine(telemetryDirectory, "operational");
        }

        string fullPath = Path.GetFullPath(telemetryPath);
        string? directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (string.Equals(Path.GetFileName(directory), "runs", StringComparison.OrdinalIgnoreCase))
            {
                string telemetryDirectory = Path.GetDirectoryName(directory) ?? directory;
                return Path.Combine(telemetryDirectory, "operational");
            }

            directory = Path.GetDirectoryName(directory);
        }

        return DefaultOperationalDirectory();
    }

    private static TelemetryRunSource ResolveLatest(string? runsDirectory)
    {
        string directory = ResolveRunsDirectory(runsDirectory);

        TelemetryRunSource? latest = FindLatestRunSource(directory, "latest");
        if (latest == null)
            throw new FileNotFoundException($"No telemetry run files found under {directory}");

        return latest with { SourceArgument = "latest", Resolution = "latest" };
    }

    private static TelemetryRunSource? FindLatestRunSource(string directory, string resolution)
        => EnumerateRunSources(directory, resolution).FirstOrDefault();

    private static IEnumerable<TelemetryRunSource> EnumerateRunSources(string directory, string resolution)
    {
        var sources = new List<TelemetryRunSource>();

        if (TryResolveDirectory(directory, Path.GetFullPath(directory), resolution, out TelemetryRunSource? rootSource))
            sources.Add(rootSource!);

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (TryResolveDirectory(child, Path.GetFullPath(child), resolution, out TelemetryRunSource? source))
                sources.Add(source!);
        }

        return sources
            .GroupBy(source => source.RunDirectory ?? source.TelemetryPath, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(LastWriteTimeUtc)
            .ThenByDescending(source => source.TelemetryPath, StringComparer.Ordinal);
    }

    private static TelemetryRunSource FromFile(string sourceArgument, string path, string resolution)
    {
        string fullPath = Path.GetFullPath(path);
        return new TelemetryRunSource(
            sourceArgument,
            fullPath,
            RunDirectoryForFile(fullPath),
            resolution);
    }

    private static bool TryResolveDirectory(
        string sourceArgument,
        string directory,
        string resolution,
        out TelemetryRunSource? source)
    {
        string fullDirectory = Path.GetFullPath(directory);

        string direct = Path.Combine(fullDirectory, TelemetryFileName);
        if (File.Exists(direct))
        {
            source = FromFile(sourceArgument, direct, resolution);
            return true;
        }

        if (TryGetSegmentPaths(fullDirectory, out IReadOnlyList<string> segmentPaths))
        {
            source = FromLogicalDirectory(sourceArgument, fullDirectory, segmentPaths, resolution);
            return true;
        }

        if (string.Equals(Path.GetFileName(fullDirectory), SegmentsDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            string? runDirectory = Path.GetDirectoryName(fullDirectory);
            if (!string.IsNullOrWhiteSpace(runDirectory) && TryGetSegmentPaths(runDirectory, out segmentPaths))
            {
                source = FromLogicalDirectory(sourceArgument, runDirectory, segmentPaths, resolution);
                return true;
            }
        }

        source = null;
        return false;
    }

    private static TelemetryRunSource FromLogicalDirectory(
        string sourceArgument,
        string runDirectory,
        IReadOnlyList<string> segmentPaths,
        string resolution)
    {
        string fullRunDirectory = Path.GetFullPath(runDirectory);
        return new TelemetryRunSource(
            sourceArgument,
            fullRunDirectory,
            fullRunDirectory,
            resolution)
        {
            TelemetryPaths = segmentPaths
        };
    }

    private static bool TryGetSegmentPaths(string runDirectory, out IReadOnlyList<string> segmentPaths)
    {
        string segmentsDirectory = Path.Combine(runDirectory, SegmentsDirectoryName);
        if (!Directory.Exists(segmentsDirectory))
        {
            segmentPaths = Array.Empty<string>();
            return false;
        }

        segmentPaths = Directory
            .EnumerateFiles(segmentsDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return segmentPaths.Count > 0;
    }

    private static string? RunDirectoryForFile(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory == null)
            return null;

        if (string.Equals(Path.GetFileName(directory), SegmentsDirectoryName, StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(directory);

        return directory;
    }

    private static DateTime LastWriteTimeUtc(TelemetryRunSource source)
        => source.TelemetryPaths
            .Select(path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default)
            .DefaultIfEmpty(default)
            .Max();

    private static string ExpandHome(string path)
    {
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

        return path;
    }
}
