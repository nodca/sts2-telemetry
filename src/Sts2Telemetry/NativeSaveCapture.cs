using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal interface INativeSaveCapture
{
    NativeSaveCaptureResult CaptureRecent(string telemetryBaseDirectory);
}

internal sealed record NativeSaveCaptureResult(
    IReadOnlyList<NativeSaveCaptureRef> Refs,
    IReadOnlyList<NativeSaveCapturedPayload> NewCaptures)
{
    public static NativeSaveCaptureResult Empty { get; } = new(
        Array.Empty<NativeSaveCaptureRef>(),
        Array.Empty<NativeSaveCapturedPayload>());
}

internal sealed record NativeSaveCaptureRef(
    string Sha256,
    long Bytes,
    string FileKind,
    string OriginalCategory,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, object?> Metadata)
{
    public Dictionary<string, object?> ToRecord()
        => new()
        {
            ["sha256"] = Sha256,
            ["bytes"] = Bytes,
            ["file_kind"] = FileKind,
            ["original_category"] = OriginalCategory,
            ["captured_at"] = CapturedAtUtc,
            ["metadata"] = Metadata
        };
}

internal sealed record NativeSaveCapturedPayload(NativeSaveCaptureRef Ref, object? Payload);

internal sealed class NativeSaveCapture : INativeSaveCapture
{
    private const string DirectoryName = "native_saves";
    private const string ObjectsDirectoryName = "objects";
    private const string ManifestFileName = "manifest.json";
    private const long MaxCandidateBytes = 2 * 1024 * 1024;
    private const int MaxFilesPerCapture = 12;
    private const int MaxSaveRootDiscoveryDepth = 6;
    private const int MaxSaveRootDiscoveryDirectories = 256;
    private const int MaxManifestEntries = 24;
    private static readonly TimeSpan ObjectRetention = TimeSpan.FromDays(1);

    private readonly Func<string, IReadOnlyList<string>> _rootResolver;
    private readonly Func<DateTimeOffset> _now;

    public NativeSaveCapture(
        Func<string, IReadOnlyList<string>> rootResolver,
        Func<DateTimeOffset>? now = null)
    {
        _rootResolver = rootResolver;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static NativeSaveCapture CreateDefault()
        => new(NativeSaveRootResolver.Resolve);

    public NativeSaveCaptureResult CaptureRecent(string telemetryBaseDirectory)
    {
        DateTimeOffset capturedAt = _now();
        string nativeSaveDirectory = Path.Combine(telemetryBaseDirectory, DirectoryName);
        string objectsDirectory = Path.Combine(nativeSaveDirectory, ObjectsDirectoryName);
        Directory.CreateDirectory(objectsDirectory);

        NativeSaveManifest manifest = LoadManifest(nativeSaveDirectory);
        bool pruned = PruneLocalCache(nativeSaveDirectory, objectsDirectory, manifest, capturedAt);
        var knownHashes = new HashSet<string>(
            manifest.Captures.Select(capture => capture.Sha256),
            StringComparer.Ordinal);
        var refs = new List<NativeSaveCaptureRef>();
        var newCaptures = new List<NativeSaveCapturedPayload>();

        foreach (NativeSaveCandidate candidate in DiscoverCandidates(telemetryBaseDirectory).Take(MaxFilesPerCapture))
        {
            NativeSaveCapturedPayload? capture = TryCaptureCandidate(candidate, objectsDirectory, capturedAt);
            if (capture == null)
                continue;

            NativeSaveCaptureRef captureRef = capture.Ref;
            refs.Add(captureRef);
            if (knownHashes.Add(captureRef.Sha256))
            {
                manifest.Captures.Add(NativeSaveManifestEntry.FromRef(captureRef, ObjectRelativePath(captureRef.Sha256)));
                newCaptures.Add(capture);
            }
        }

        manifest.Captures = manifest.Captures
            .OrderByDescending(capture => capture.CapturedAt)
            .ThenBy(capture => capture.Sha256, StringComparer.Ordinal)
            .Take(MaxManifestEntries)
            .ToList();
        bool prunedAfterCapture = PruneLocalObjects(objectsDirectory, manifest);
        if (refs.Count > 0 || pruned || prunedAfterCapture)
            AtomicJsonFile.Write(Path.Combine(nativeSaveDirectory, ManifestFileName), manifest);

        NativeSaveCaptureRef[] dedupedRefs = refs
            .GroupBy(save => save.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new NativeSaveCaptureResult(dedupedRefs, newCaptures);
    }

    private IEnumerable<NativeSaveCandidate> DiscoverCandidates(string telemetryBaseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string telemetryRoot = SafeFullPath(telemetryBaseDirectory);
        foreach (string root in _rootResolver(telemetryBaseDirectory))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (string saveRoot in CandidateSaveRoots(root, telemetryRoot))
            {
                foreach (NativeSaveCandidate candidate in CandidateFiles(saveRoot))
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(candidate.FullPath);
                    }
                    catch
                    {
                        continue;
                    }

                    if (seen.Add(fullPath))
                        yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> CandidateSaveRoots(string root, string telemetryRoot)
    {
        string rootFullPath = SafeFullPath(root);
        if (string.IsNullOrWhiteSpace(rootFullPath) || !Directory.Exists(rootFullPath))
            yield break;

        var queue = new Queue<(string Directory, int Depth)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((rootFullPath, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < MaxSaveRootDiscoveryDirectories)
        {
            (string directory, int depth) = queue.Dequeue();
            string fullPath = SafeFullPath(directory);
            if (string.IsNullOrWhiteSpace(fullPath)
                || !seen.Add(fullPath)
                || ShouldSkipDiscoveryDirectory(fullPath, telemetryRoot))
            {
                continue;
            }

            visited++;
            string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (depth == 0
                || string.Equals(name, "saves", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "save", StringComparison.OrdinalIgnoreCase))
            {
                yield return fullPath;
            }

            if (depth >= MaxSaveRootDiscoveryDepth)
                continue;

            foreach (string child in SafeEnumerateDirectories(fullPath))
                queue.Enqueue((child, depth + 1));
        }
    }

    private static bool ShouldSkipDiscoveryDirectory(string directory, string telemetryRoot)
    {
        if (!string.IsNullOrWhiteSpace(telemetryRoot)
            && (string.Equals(directory, telemetryRoot, StringComparison.Ordinal)
                || directory.StartsWith(telemetryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || directory.StartsWith(telemetryRoot + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            return true;
        }

        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name is "native_saves"
            or "objects"
            or "upload"
            or "queue"
            or "runs"
            or "operational"
            or "mods"
            or "mod"
            or "logs";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(64)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<NativeSaveCandidate> CandidateFiles(string saveRoot)
    {
        if (!Directory.Exists(saveRoot))
            yield break;

        foreach (string file in SafeEnumerateFiles(saveRoot, "current_run*.save*"))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
            {
                yield return new NativeSaveCandidate(
                    file,
                    "current_run_save",
                    $"current_run/{name}");
            }
            else if (name.EndsWith(".save.backup", StringComparison.OrdinalIgnoreCase))
            {
                yield return new NativeSaveCandidate(
                    file,
                    "current_run_save_backup",
                    $"current_run_backup/{name}");
            }
        }

        string historyDirectory = Path.Combine(saveRoot, "history");
        foreach (string file in SafeEnumerateFiles(historyDirectory, "*.run"))
        {
            yield return new NativeSaveCandidate(
                file,
                "history_run",
                $"history/{Path.GetFileName(file)}");
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static NativeSaveCapturedPayload? TryCaptureCandidate(
        NativeSaveCandidate candidate,
        string objectsDirectory,
        DateTimeOffset capturedAt)
    {
        byte[] sourceBytes;
        try
        {
            var info = new FileInfo(candidate.FullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxCandidateBytes)
                return null;

            sourceBytes = File.ReadAllBytes(candidate.FullPath);
        }
        catch
        {
            return null;
        }

        object? scrubbed;
        try
        {
            using JsonDocument document = JsonDocument.Parse(sourceBytes);
            scrubbed = NativeSavePrivacyScrubber.Scrub(document.RootElement);
        }
        catch
        {
            return null;
        }

        byte[] scrubbedBytes = JsonSerializer.SerializeToUtf8Bytes(scrubbed, TelemetryJson.Options);
        string sha256 = TelemetryUploadCrypto.Sha256Hex(scrubbedBytes);
        string objectPath = Path.Combine(objectsDirectory, $"{sha256}.json");
        if (!File.Exists(objectPath))
            File.WriteAllBytes(objectPath, scrubbedBytes);

        IReadOnlyDictionary<string, object?> metadata = NativeSaveMetadata.Extract(scrubbed);
        var captureRef = new NativeSaveCaptureRef(
            sha256,
            scrubbedBytes.LongLength,
            candidate.FileKind,
            candidate.OriginalCategory,
            capturedAt,
            metadata);
        return new NativeSaveCapturedPayload(captureRef, scrubbed);
    }

    private static NativeSaveManifest LoadManifest(string nativeSaveDirectory)
    {
        string path = Path.Combine(nativeSaveDirectory, ManifestFileName);
        if (!File.Exists(path))
            return new NativeSaveManifest();

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NativeSaveManifest>(json, TelemetryJson.Options)
                ?? new NativeSaveManifest();
        }
        catch
        {
            return new NativeSaveManifest();
        }
    }

    private static string ObjectRelativePath(string sha256)
        => $"{ObjectsDirectoryName}/{sha256}.json";

    private static bool PruneLocalCache(
        string nativeSaveDirectory,
        string objectsDirectory,
        NativeSaveManifest manifest,
        DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.Subtract(ObjectRetention);
        int beforeCount = manifest.Captures.Count;
        manifest.Captures = manifest.Captures
            .Where(capture => capture.CapturedAt >= cutoff)
            .OrderByDescending(capture => capture.CapturedAt)
            .ThenBy(capture => capture.Sha256, StringComparer.Ordinal)
            .Take(MaxManifestEntries)
            .ToList();

        bool prunedObjects = PruneLocalObjects(objectsDirectory, manifest);
        if (manifest.Captures.Count == 0)
        {
            string manifestPath = Path.Combine(nativeSaveDirectory, ManifestFileName);
            TryDelete(manifestPath);
        }

        return beforeCount != manifest.Captures.Count || prunedObjects;
    }

    private static bool PruneLocalObjects(string objectsDirectory, NativeSaveManifest manifest)
    {
        if (!Directory.Exists(objectsDirectory))
            return false;

        var retainedRelativePaths = new HashSet<string>(
            manifest.Captures
                .Select(capture => capture.ObjectPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        bool changed = false;
        foreach (string file in SafeEnumerateFiles(objectsDirectory, "*.json"))
        {
            string relative = $"{ObjectsDirectoryName}/{Path.GetFileName(file)}";
            if (!retainedRelativePaths.Contains(relative) && TryDelete(file))
                changed = true;
        }

        return changed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return "";
        }
    }

    private sealed record NativeSaveCandidate(
        string FullPath,
        string FileKind,
        string OriginalCategory);

    private sealed record NativeSaveManifest
    {
        public string SchemaVersion { get; init; } = "sts2.telemetry.native_save_manifest.v1";
        public List<NativeSaveManifestEntry> Captures { get; set; } = new();
    }

    private sealed record NativeSaveManifestEntry
    {
        public string Sha256 { get; init; } = "";
        public long Bytes { get; init; }
        public string FileKind { get; init; } = "";
        public string OriginalCategory { get; init; } = "";
        public DateTimeOffset CapturedAt { get; init; }
        public string ObjectPath { get; init; } = "";
        public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);

        public static NativeSaveManifestEntry FromRef(NativeSaveCaptureRef captureRef, string objectPath)
            => new()
            {
                Sha256 = captureRef.Sha256,
                Bytes = captureRef.Bytes,
                FileKind = captureRef.FileKind,
                OriginalCategory = captureRef.OriginalCategory,
                CapturedAt = captureRef.CapturedAtUtc,
                ObjectPath = objectPath,
                Metadata = captureRef.Metadata
            };
    }
}

internal static class NativeSaveRootResolver
{
    public static IReadOnlyList<string> Resolve(string telemetryBaseDirectory)
        => Resolve(
            telemetryBaseDirectory,
            FirstNonBlank(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("HOME"),
                Environment.GetEnvironmentVariable("USERPROFILE")),
            FirstNonBlank(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("LOCALAPPDATA")),
            FirstNonBlank(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("APPDATA")),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

    internal static IReadOnlyList<string> Resolve(
        string telemetryBaseDirectory,
        string? home,
        string? localAppData,
        string? appData,
        string? programFilesX86)
    {
        var roots = new List<string>();
        AddParentOfTelemetryDirectory(roots, telemetryBaseDirectory);

        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "SlayTheSpire2"));
            roots.Add(Path.Combine(home, ".local", "share", "SlayTheSpire2"));
            roots.Add(Path.Combine(home, ".steam", "steam", "userdata"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam", "userdata"));
            roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "userdata"));
            AddProtonRoots(roots, Path.Combine(home, ".steam", "steam", "steamapps", "compatdata"));
            AddProtonRoots(roots, Path.Combine(home, ".local", "share", "Steam", "steamapps", "compatdata"));
            AddProtonRoots(roots, Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "compatdata"));
            roots.Add(Path.Combine(home, "Library", "Application Support", "SlayTheSpire2"));
            roots.Add(Path.Combine(home, "Library", "Application Support", "Mega Crit", "SlayTheSpire2"));
            roots.Add(Path.Combine(home, "AppData", "LocalLow", "Mega Crit", "SlayTheSpire2"));
            roots.Add(Path.Combine(home, "AppData", "LocalLow", "SlayTheSpire2"));
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            roots.Add(Path.Combine(localAppData, "SlayTheSpire2"));
            roots.Add(Path.Combine(localAppData, "Mega Crit", "SlayTheSpire2"));
        }

        if (!string.IsNullOrWhiteSpace(appData))
        {
            roots.Add(Path.Combine(appData, "SlayTheSpire2"));
            roots.Add(Path.Combine(appData, "Mega Crit", "SlayTheSpire2"));
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots.Add(Path.Combine(programFilesX86, "Steam", "userdata"));

        return roots
            .Select(SafeFullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();
    }

    private static void AddProtonRoots(ICollection<string> roots, string compatDataDirectory)
    {
        foreach (string appDirectory in SafeEnumerateDirectories(compatDataDirectory).Take(64))
        {
            string usersDirectory = Path.Combine(appDirectory, "pfx", "drive_c", "users");
            foreach (string userDirectory in SafeEnumerateDirectories(usersDirectory).Take(8))
            {
                roots.Add(Path.Combine(userDirectory, "AppData", "LocalLow", "Mega Crit", "SlayTheSpire2"));
                roots.Add(Path.Combine(userDirectory, "AppData", "LocalLow", "SlayTheSpire2"));
                roots.Add(Path.Combine(userDirectory, "AppData", "Local", "SlayTheSpire2"));
                roots.Add(Path.Combine(userDirectory, "AppData", "Roaming", "SlayTheSpire2"));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateDirectories(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static void AddParentOfTelemetryDirectory(ICollection<string> roots, string telemetryBaseDirectory)
    {
        try
        {
            string full = Path.GetFullPath(telemetryBaseDirectory);
            DirectoryInfo? directory = new(full);
            if (string.Equals(directory.Name, TelemetryDirectoryResolver.TelemetryDirectoryName, StringComparison.Ordinal)
                && directory.Parent != null)
            {
                roots.Add(directory.Parent.FullName);
            }
        }
        catch
        {
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return "";
        }
    }
}

internal static class NativeSavePrivacyScrubber
{
    private const string ScrubbedValue = "[scrubbed]";
    private const string ScrubbedPathValue = "[scrubbed_local_path]";

    public static object? Scrub(JsonElement element)
        => Scrub(element, key: null);

    private static object? Scrub(JsonElement element, string? key)
    {
        if (IsSensitiveKey(key))
            return ScrubbedValue;

        return element.ValueKind switch
        {
            JsonValueKind.Object => ScrubObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(item => Scrub(item, key: null)).ToArray(),
            JsonValueKind.String => ScrubString(element.GetString()),
            JsonValueKind.Number => NumberValue(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static Dictionary<string, object?> ScrubObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            result[property.Name] = Scrub(property.Value, property.Name);
        return result;
    }

    private static object? ScrubString(string? value)
        => LooksLikeLocalPath(value) ? ScrubbedPathValue : value;

    private static object NumberValue(JsonElement element)
    {
        if (element.TryGetInt64(out long integer))
            return integer;
        if (element.TryGetDouble(out double number))
            return number;
        return element.GetRawText();
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string normalized = NormalizeKey(key);
        if (GameplayIdKeys.Contains(normalized))
            return false;

        return SensitiveExactKeys.Contains(normalized)
            || normalized.Contains("steam", StringComparison.Ordinal)
            || normalized.Contains("account", StringComparison.Ordinal)
            || normalized.Contains("profile", StringComparison.Ordinal)
            || normalized.Contains("uniqueid", StringComparison.Ordinal)
            || normalized.Contains("userid", StringComparison.Ordinal)
            || normalized.Contains("useruuid", StringComparison.Ordinal)
            || normalized.Contains("localpath", StringComparison.Ordinal)
            || normalized.EndsWith("filepath", StringComparison.Ordinal)
            || normalized.EndsWith("directory", StringComparison.Ordinal);
    }

    private static bool LooksLikeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();
        return text.StartsWith("/", StringComparison.Ordinal)
            || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || (text.Length >= 3
                && char.IsLetter(text[0])
                && text[1] == ':'
                && (text[2] == '\\' || text[2] == '/'));
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> SensitiveExactKeys = new(StringComparer.Ordinal)
    {
        "netid",
        "playerid",
        "uniqueid",
        "steamid",
        "steamuserid",
        "userid",
        "user",
        "username",
        "accountid",
        "profileid",
        "installationid",
        "machineid",
        "deviceid"
    };

    private static readonly HashSet<string> GameplayIdKeys = new(StringComparer.Ordinal)
    {
        "cardid",
        "relicid",
        "potionid",
        "enemyid",
        "eventid",
        "encounterid",
        "roomid",
        "actid",
        "seed"
    };
}

internal static class NativeSaveMetadata
{
    public static IReadOnlyDictionary<string, object?> Extract(object? scrubbed)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyFirstString(metadata, scrubbed, "schema_version", "schema_version", "schemaversion");
        CopyFirstString(metadata, scrubbed, "build_id", "build_id", "buildid");
        CopyFirstString(metadata, scrubbed, "seed", "seed");
        CopyFirstString(metadata, scrubbed, "start_time", "start_time", "starttime");
        CopyFirstInt(metadata, scrubbed, "current_act_index", "current_act_index", "currentactindex");
        CopyFirstInt(metadata, scrubbed, "floor", "floor");
        CopyFirst(metadata, scrubbed, "visited_coords", IsVisitedCoordsKey);
        return metadata;
    }

    private static void CopyFirstString(
        IDictionary<string, object?> metadata,
        object? value,
        string outputKey,
        params string[] names)
    {
        object? found = FindFirst(value, key => names.Contains(NormalizeKey(key), StringComparer.Ordinal));
        if (found is string text && !string.IsNullOrWhiteSpace(text))
            metadata[outputKey] = text;
        else if (found is long or int)
            metadata[outputKey] = Convert.ToString(found, CultureInfo.InvariantCulture);
    }

    private static void CopyFirstInt(
        IDictionary<string, object?> metadata,
        object? value,
        string outputKey,
        params string[] names)
    {
        object? found = FindFirst(value, key => names.Contains(NormalizeKey(key), StringComparer.Ordinal));
        if (found is int intValue)
            metadata[outputKey] = intValue;
        else if (found is long longValue && longValue is >= int.MinValue and <= int.MaxValue)
            metadata[outputKey] = (int)longValue;
        else if (found is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            metadata[outputKey] = parsed;
    }

    private static void CopyFirst(
        IDictionary<string, object?> metadata,
        object? value,
        string outputKey,
        Func<string, bool> predicate)
    {
        object? found = FindFirst(value, predicate);
        if (found != null)
            metadata[outputKey] = found;
    }

    private static object? FindFirst(object? value, Func<string, bool> keyPredicate)
    {
        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            foreach ((string key, object? child) in dictionary)
            {
                if (keyPredicate(key))
                    return child;
                object? nested = FindFirst(child, keyPredicate);
                if (nested != null)
                    return nested;
            }
        }
        else if (value is IEnumerable<object?> array)
        {
            foreach (object? child in array)
            {
                object? nested = FindFirst(child, keyPredicate);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }

    private static bool IsVisitedCoordsKey(string key)
    {
        string normalized = NormalizeKey(key);
        return normalized.Contains("visited", StringComparison.Ordinal)
            && (normalized.Contains("coord", StringComparison.Ordinal)
                || normalized.Contains("room", StringComparison.Ordinal));
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
