using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal sealed class TelemetryUploadQueue
{
    private const string QueueDirectoryName = "queue";
    private const string BundleFileName = "bundle.jsonl.gz";
    private const string ManifestFileName = "manifest.json";
    private const string StatusFileName = "status.json";
    private readonly string _telemetryBaseDirectory;
    private readonly string _installationId;
    private readonly Func<DateTimeOffset> _now;

    public TelemetryUploadQueue(string telemetryBaseDirectory, string installationId, Func<DateTimeOffset>? now = null)
    {
        _telemetryBaseDirectory = telemetryBaseDirectory;
        _installationId = installationId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(QueueDirectory);
    }

    public string QueueDirectory
        => Path.Combine(TelemetryUploadSettings.UploadDirectory(_telemetryBaseDirectory), QueueDirectoryName);

    public IReadOnlyList<TelemetryUploadQueueItem> Items()
        => Directory.Exists(QueueDirectory)
            ? Directory.EnumerateDirectories(QueueDirectory)
                .Select(TelemetryUploadQueueItem.TryLoad)
                .Where(item => item != null)
                .Select(item => item!)
                .OrderBy(item => item.Status.CreatedAtUtc)
                .ToArray()
            : Array.Empty<TelemetryUploadQueueItem>();

    public TelemetryUploadSummary BuildSummary(
        TelemetryUploadSettings settings,
        TelemetryUploadPolicy policy,
        bool hasToken,
        string? lastSyncState = null,
        string? lastErrorCode = null,
        string? lastErrorMessage = null)
    {
        IReadOnlyList<TelemetryUploadQueueItem> items = Items();
        TelemetryUploadQueueItem[] uploadedItems = items
            .Where(item => item.Status.State == "uploaded")
            .ToArray();
        int uploadedSourceCount = uploadedItems
            .Select(item => item.Status.SourcePath)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .Count();
        TelemetryUploadRewardStatus[] rewards = items
            .Select(item => item.Status.Reward)
            .Where(reward => reward != null && !string.IsNullOrWhiteSpace(reward.RunId))
            .Select(reward => reward!)
            .GroupBy(reward => reward.RunId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(reward => reward.UpdatedAtUtc ?? DateTimeOffset.MinValue)
                .First())
            .OrderBy(reward => reward.RunId, StringComparer.Ordinal)
            .ToArray();
        return new TelemetryUploadSummary
        {
            Enabled = settings.Enabled,
            EndpointMode = settings.ActiveEndpoint,
            EndpointUrl = settings.EffectiveEndpointUrl,
            NoticeVersion = TelemetryUploadSettings.NoticeVersionValue,
            HasUploadToken = hasToken,
            QueuedBytes = items
                .Where(item => item.Status.State is "pending" or "failed")
                .Sum(item => item.Status.CompressedSize),
            PendingBundles = items.Count(item => item.Status.State == "pending"),
            FailedBundles = items.Count(item => item.Status.State == "failed"),
            UploadedBundles = uploadedItems.Length,
            UploadedSourceCount = uploadedSourceCount,
            DuplicateUploadedSourceCount = Math.Max(0, uploadedItems.Length - uploadedSourceCount),
            DroppedBundles = items.Count(item => item.Status.State == "dropped"),
            UpdatedAtUtc = _now(),
            LastSyncState = lastSyncState,
            LastErrorCode = lastErrorCode,
            LastErrorMessage = lastErrorMessage,
            Policy = policy,
            Rewards = rewards
        };
    }

    public IReadOnlyList<TelemetryUploadQueueItem> PackagePendingSources(
        TelemetryUploadSettings settings,
        TelemetryUploadPolicy policy,
        bool force)
    {
        if (policy.UploadDisabled || !policy.AcceptsSchema(TelemetryRecorder.SchemaVersion))
            return Array.Empty<TelemetryUploadQueueItem>();

        string compression = ChooseCompression(policy);
        if (compression != "gzip")
            return Array.Empty<TelemetryUploadQueueItem>();

        var created = new List<TelemetryUploadQueueItem>();
        TelemetryUploadSourceState sourceState = LoadSourceState();
        IReadOnlyList<TelemetryUploadQueueItem> existingItems = Items();
        DateTimeOffset now = _now();
        foreach (string source in EnumerateRunJsonlSources())
        {
            if (!force && File.GetLastWriteTimeUtc(source) > now.UtcDateTime.AddSeconds(-settings.StableSourceSeconds))
                continue;

            string relativeSource = RelativeTelemetryPath(source);
            long sourceStateLastQueued = sourceState.LastQueuedLocalSequenceBySource.TryGetValue(relativeSource, out long value)
                ? value
                : 0;
            Dictionary<string, long> lastQueuedByDigest = LastQueuedBySourceDigest(existingItems, relativeSource);
            SourceScan scan = ScanSource(
                source,
                relativeSource,
                CandidateThresholds(sourceStateLastQueued, lastQueuedByDigest.Values),
                settings.MaxRecordsPerBundle);
            string sourceSha256 = scan.SourceSha256;
            long lastQueued = Math.Max(
                sourceStateLastQueued,
                lastQueuedByDigest.TryGetValue(sourceSha256, out long digestQueued) ? digestQueued : 0);
            if (SourceDigestAlreadyQueued(
                    sourceState,
                    existingItems,
                    relativeSource,
                    sourceSha256,
                    lastQueued,
                    scan.MaxLocalSequence))
            {
                continue;
            }

            BundleCandidate candidate = scan.CandidateFor(lastQueued);
            if (candidate.Records.Count == 0)
                continue;
            if (string.IsNullOrWhiteSpace(candidate.RunId))
                continue;

            TelemetryUploadQueueItem item = WriteQueueItem(candidate, compression, now);
            sourceState.LastQueuedLocalSequenceBySource[relativeSource] = candidate.LastLocalSequence ?? lastQueued;
            sourceState.LastQueuedSourceSha256BySource[relativeSource] = sourceSha256;
            created.Add(item);
        }

        SaveSourceState(sourceState);
        EnforceBounds(settings);
        return created;
    }

    public void MarkUploaded(TelemetryUploadQueueItem item)
    {
        item.SaveStatus(item.Status with
        {
            State = "uploaded",
            UpdatedAtUtc = _now(),
            UploadedAtUtc = _now(),
            LastErrorCode = null,
            LastErrorMessage = null,
            NextAttemptAtUtc = null
        });
        TryDelete(item.BundlePath);
        TryDelete(item.ManifestPath);
    }

    public void MarkFailed(TelemetryUploadQueueItem item, string errorCode, string errorMessage, int? retryAfterSeconds)
    {
        DateTimeOffset now = _now();
        int attempts = item.Status.AttemptCount + 1;
        int backoffSeconds = retryAfterSeconds ?? Math.Min(3600, (int)Math.Pow(2, Math.Min(attempts, 8)));
        item.SaveStatus(item.Status with
        {
            State = "failed",
            AttemptCount = attempts,
            UpdatedAtUtc = now,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage,
            NextAttemptAtUtc = now.AddSeconds(backoffSeconds)
        });
    }

    public bool ConsumeManualSyncRequest()
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(_telemetryBaseDirectory),
            TelemetryUploadSettings.ManualSyncRequestFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
        }
        catch
        {
        }

        return true;
    }

    public void WriteSummary(TelemetryUploadSummary summary)
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(_telemetryBaseDirectory),
            TelemetryUploadSettings.StatusFileName);
        AtomicJsonFile.Write(path, summary);
    }

    private TelemetryUploadQueueItem WriteQueueItem(BundleCandidate candidate, string compression, DateTimeOffset now)
    {
        byte[] uncompressed = Encoding.UTF8.GetBytes(string.Join('\n', candidate.Records) + "\n");
        byte[] compressed = Gzip(uncompressed);
        string compressedSha = TelemetryUploadCrypto.Sha256Hex(compressed);
        string bundleId = BuildBundleId(candidate, compressedSha);
        string itemDirectory = Path.Combine(QueueDirectory, bundleId);
        Directory.CreateDirectory(itemDirectory);

        var manifest = new TelemetryUploadBundleManifest
        {
            BundleId = bundleId,
            InstallationId = _installationId,
            RunId = candidate.RunId,
            LogicalRunId = candidate.LogicalRunId,
            SegmentId = candidate.SegmentId,
            BranchId = candidate.BranchId,
            Floor = candidate.Floor,
            SchemaVersion = candidate.SchemaVersion,
            Compression = compression,
            Sha256 = compressedSha,
            CompressedSize = compressed.LongLength,
            UncompressedSize = uncompressed.LongLength,
            RecordCount = candidate.Records.Count,
            FirstLocalSequence = candidate.FirstLocalSequence,
            LastLocalSequence = candidate.LastLocalSequence,
            FirstRecordedAtUtc = candidate.FirstRecordedAtUtc,
            LastRecordedAtUtc = candidate.LastRecordedAtUtc,
            GameVersion = candidate.GameVersion,
            ModVersion = candidate.ModVersion ?? Sts2TelemetryMod.Version
        };
        string manifestJson = JsonSerializer.Serialize(manifest, TelemetryJson.Options);
        File.WriteAllBytes(Path.Combine(itemDirectory, BundleFileName), compressed);
        File.WriteAllText(Path.Combine(itemDirectory, ManifestFileName), manifestJson);

        var status = new TelemetryUploadQueueItemStatus
        {
            BundleId = bundleId,
            SourcePath = candidate.RelativeSourcePath,
            State = "pending",
            Compression = compression,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompressedSize = compressed.LongLength,
            UncompressedSize = uncompressed.LongLength,
            RecordCount = candidate.Records.Count,
            FirstLocalSequence = candidate.FirstLocalSequence,
            LastLocalSequence = candidate.LastLocalSequence,
            RunId = candidate.RunId,
            LogicalRunId = candidate.LogicalRunId,
            SourceSha256 = candidate.SourceSha256,
            RunQuality = candidate.RunQuality
        };
        var item = new TelemetryUploadQueueItem(itemDirectory, status);
        item.SaveStatus(status);
        return item;
    }

    public void PruneUploadedRunSources(TelemetryUploadSettings settings)
    {
        DateTimeOffset cutoff = _now().AddDays(-settings.MaxRunHistoryDays);
        IReadOnlyList<TelemetryUploadQueueItem> items = Items();
        HashSet<string> sourcesWithUnsuccessfulEvidence = SourcesWithUnsuccessfulQueueEvidence(items);
        Dictionary<string, long> uploadedThroughBySource = UploadedThroughBySource(items);
        foreach (string source in EnumerateRunJsonlSources())
        {
            if (File.GetLastWriteTimeUtc(source) > cutoff.UtcDateTime)
                continue;

            string relativeSource = RelativeTelemetryPath(source);
            if (sourcesWithUnsuccessfulEvidence.Contains(relativeSource))
                continue;

            long? maxSequence = MaxLocalSequence(source);
            if (maxSequence == null)
                continue;

            long uploadedThrough = uploadedThroughBySource.TryGetValue(relativeSource, out long value)
                ? value
                : 0;

            if (uploadedThrough < maxSequence.Value)
                continue;

            TryDelete(source);
            PruneEmptyRunDirectories(Path.GetDirectoryName(source));
        }
    }

    private void EnforceBounds(TelemetryUploadSettings settings)
    {
        DateTimeOffset now = _now();
        IReadOnlyList<TelemetryUploadQueueItem> items = Items();
        foreach (TelemetryUploadQueueItem item in items
                     .Where(item => item.Status.State is "pending" or "failed")
                     .Where(item => now - item.Status.CreatedAtUtc > TimeSpan.FromDays(settings.MaxQueueAgeDays)))
        {
            Drop(item, "queue_max_age_exceeded");
        }

        var pending = items.Where(item => item.Status.State is "pending" or "failed");
        long total = pending.Sum(item => item.Status.CompressedSize);
        foreach (TelemetryUploadQueueItem item in pending)
        {
            if (total <= settings.MaxQueueBytes)
                break;
            total -= item.Status.CompressedSize;
            Drop(item, "queue_max_bytes_exceeded");
        }
    }

    private static HashSet<string> SourcesWithUnsuccessfulQueueEvidence(IEnumerable<TelemetryUploadQueueItem> items)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (TelemetryUploadQueueItem item in items)
        {
            if (item.Status.State is "pending" or "failed" or "dropped")
                sources.Add(item.Status.SourcePath);
        }

        return sources;
    }

    private static Dictionary<string, long> UploadedThroughBySource(IEnumerable<TelemetryUploadQueueItem> items)
    {
        var uploadedThrough = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (TelemetryUploadQueueItem item in items)
        {
            if (item.Status.State != "uploaded" || item.Status.LastLocalSequence == null)
                continue;

            string source = item.Status.SourcePath;
            long sequence = item.Status.LastLocalSequence.Value;
            if (!uploadedThrough.TryGetValue(source, out long current) || sequence > current)
                uploadedThrough[source] = sequence;
        }

        return uploadedThrough;
    }

    private static void Drop(TelemetryUploadQueueItem item, string reason)
    {
        TryDelete(item.BundlePath);
        TryDelete(item.ManifestPath);
        item.SaveStatus(item.Status with
        {
            State = "dropped",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DropReason = reason,
            LastErrorCode = reason,
            LastErrorMessage = "local upload queue limit dropped this bundle"
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private void PruneEmptyRunDirectories(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string runsDirectory = Path.Combine(_telemetryBaseDirectory, JsonlTelemetryWriter.RunsDirectoryName);
        string current = directory;
        while (current.StartsWith(runsDirectory, StringComparison.Ordinal)
               && !string.Equals(current, runsDirectory, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
                    Directory.Delete(current);
            }
            catch
            {
                return;
            }

            current = Path.GetDirectoryName(current) ?? "";
        }
    }

    private static string ChooseCompression(TelemetryUploadPolicy policy)
    {
        // zstd remains the preferred protocol compression, but this mod has no
        // approved zstd dependency yet. Advertise and use gzip until that gap is closed.
        return policy.AcceptsCompression("gzip") ? "gzip" : "";
    }

    private IEnumerable<string> EnumerateRunJsonlSources()
    {
        string runsDirectory = Path.Combine(_telemetryBaseDirectory, JsonlTelemetryWriter.RunsDirectoryName);
        if (!Directory.Exists(runsDirectory))
            yield break;

        foreach (string path in Directory.EnumerateFiles(runsDirectory, "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
            yield return path;
    }

    private string RelativeTelemetryPath(string path)
        => Path.GetRelativePath(_telemetryBaseDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private TelemetryUploadSourceState LoadSourceState()
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(_telemetryBaseDirectory),
            TelemetryUploadSettings.SourceStateFileName);
        if (!File.Exists(path))
            return new TelemetryUploadSourceState();

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TelemetryUploadSourceState>(json, TelemetryJson.Options)
                ?? new TelemetryUploadSourceState();
        }
        catch
        {
            return new TelemetryUploadSourceState();
        }
    }

    private void SaveSourceState(TelemetryUploadSourceState state)
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(_telemetryBaseDirectory),
            TelemetryUploadSettings.SourceStateFileName);
        AtomicJsonFile.Write(path, state);
    }

    private static bool SourceDigestAlreadyQueued(
        TelemetryUploadSourceState sourceState,
        IEnumerable<TelemetryUploadQueueItem> existingItems,
        string relativeSourcePath,
        string sourceSha256,
        long lastQueuedLocalSequence,
        long? sourceMaxLocalSequence)
    {
        if (string.IsNullOrWhiteSpace(sourceSha256) || sourceMaxLocalSequence == null)
            return false;

        if (sourceState.LastQueuedSourceSha256BySource.TryGetValue(relativeSourcePath, out string? knownDigest)
            && string.Equals(knownDigest, sourceSha256, StringComparison.Ordinal))
        {
            return lastQueuedLocalSequence >= sourceMaxLocalSequence.Value;
        }

        return existingItems.Any(item =>
            string.Equals(item.Status.SourcePath, relativeSourcePath, StringComparison.Ordinal)
            && string.Equals(item.Status.SourceSha256, sourceSha256, StringComparison.Ordinal)
            && item.Status.LastLocalSequence != null
            && item.Status.LastLocalSequence.Value >= sourceMaxLocalSequence.Value);
    }

    private static Dictionary<string, long> LastQueuedBySourceDigest(
        IEnumerable<TelemetryUploadQueueItem> existingItems,
        string relativeSourcePath)
    {
        var lastQueuedByDigest = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (TelemetryUploadQueueItem item in existingItems)
        {
            if (!string.Equals(item.Status.SourcePath, relativeSourcePath, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(item.Status.SourceSha256)
                || item.Status.LastLocalSequence == null)
            {
                continue;
            }

            string sourceSha256 = item.Status.SourceSha256;
            long sequence = item.Status.LastLocalSequence.Value;
            if (!lastQueuedByDigest.TryGetValue(sourceSha256, out long current) || sequence > current)
                lastQueuedByDigest[sourceSha256] = sequence;
        }

        return lastQueuedByDigest;
    }

    private static long[] CandidateThresholds(long sourceStateLastQueued, IEnumerable<long> digestQueuedSequences)
    {
        var thresholds = new HashSet<long> { sourceStateLastQueued };
        foreach (long sequence in digestQueuedSequences)
            thresholds.Add(Math.Max(sourceStateLastQueued, sequence));
        return thresholds.OrderBy(sequence => sequence).ToArray();
    }

    private static SourceScan ScanSource(
        string path,
        string relativeSourcePath,
        IReadOnlyCollection<long> candidateThresholds,
        int maxRecords)
    {
        var candidatesByThreshold = candidateThresholds
            .Distinct()
            .ToDictionary(
                threshold => threshold,
                _ => new CandidateScan(new BundleCandidate(relativeSourcePath)));
        using var sha = SHA256.Create();
        long? maxLocalSequence = null;
        bool maxLocalSequenceAvailable = true;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var hashingStream = new CryptoStream(stream, sha, CryptoStreamMode.Read))
        using (var reader = new StreamReader(hashingStream, Encoding.UTF8))
        {
            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (Exception ex)
                {
                    maxLocalSequenceAvailable = false;
                    foreach (CandidateScan scan in candidatesByThreshold.Values)
                        scan.NoteParseFailure(ex, maxRecords);
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    long? sequence = JsonElementReader.GetInt64(root, "local_sequence");
                    if (sequence != null)
                        maxLocalSequence = Math.Max(maxLocalSequence ?? sequence.Value, sequence.Value);

                    string schema = JsonElementReader.GetString(root, "schema_version") ?? "";
                    if (schema != TelemetryRecorder.SchemaVersion || sequence == null)
                        continue;

                    foreach ((long threshold, CandidateScan scan) in candidatesByThreshold)
                    {
                        if (scan.Candidate.Records.Count < maxRecords && sequence > threshold)
                            scan.Candidate.Add(line, root);
                    }
                }
            }
        }

        string sourceSha256 = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
        foreach (CandidateScan scan in candidatesByThreshold.Values)
            scan.Candidate.SetSourceSha256(sourceSha256);
        return new SourceScan(
            sourceSha256,
            maxLocalSequenceAvailable ? maxLocalSequence : null,
            candidatesByThreshold);
    }

    private static long? MaxLocalSequence(string path)
    {
        long? result = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using JsonDocument document = JsonDocument.Parse(line);
                long? sequence = JsonElementReader.GetInt64(document.RootElement, "local_sequence");
                if (sequence != null)
                    result = Math.Max(result ?? sequence.Value, sequence.Value);
            }
        }
        catch
        {
            return null;
        }

        return result;
    }

    private static byte[] Gzip(byte[] uncompressed)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(uncompressed, 0, uncompressed.Length);
        return output.ToArray();
    }

    private static string BuildBundleId(BundleCandidate candidate, string compressedSha)
    {
        string material = string.Join("|", new[]
        {
            candidate.RelativeSourcePath,
            candidate.RunId,
            candidate.SegmentId ?? "",
            candidate.FirstLocalSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            candidate.LastLocalSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            compressedSha
        });
        string hash = TelemetryUploadCrypto.Sha256Hex(Encoding.UTF8.GetBytes(material));
        return $"bundle_{hash[..32]}";
    }

    private sealed class SourceScan
    {
        private readonly IReadOnlyDictionary<long, CandidateScan> _candidatesByThreshold;

        public SourceScan(
            string sourceSha256,
            long? maxLocalSequence,
            IReadOnlyDictionary<long, CandidateScan> candidatesByThreshold)
        {
            SourceSha256 = sourceSha256;
            MaxLocalSequence = maxLocalSequence;
            _candidatesByThreshold = candidatesByThreshold;
        }

        public string SourceSha256 { get; }
        public long? MaxLocalSequence { get; }

        public BundleCandidate CandidateFor(long lastQueuedLocalSequence)
        {
            if (!_candidatesByThreshold.TryGetValue(lastQueuedLocalSequence, out CandidateScan? scan))
                return new BundleCandidate("", SourceSha256);
            if (scan.ParseFailure != null)
                throw scan.ParseFailure;
            return scan.Candidate;
        }
    }

    private sealed class CandidateScan
    {
        public CandidateScan(BundleCandidate candidate)
        {
            Candidate = candidate;
        }

        public BundleCandidate Candidate { get; }
        public Exception? ParseFailure { get; private set; }

        public void NoteParseFailure(Exception exception, int maxRecords)
        {
            if (Candidate.Records.Count < maxRecords)
                ParseFailure ??= exception;
        }
    }

    private sealed class BundleCandidate
    {
        private readonly TelemetryRunQualityAccumulator _runQuality = new();

        public BundleCandidate(string relativeSourcePath, string sourceSha256 = "")
        {
            RelativeSourcePath = relativeSourcePath;
            SourceSha256 = sourceSha256;
        }

        public string RelativeSourcePath { get; }
        public string SourceSha256 { get; private set; }
        public List<string> Records { get; } = new();
        public string SchemaVersion { get; private set; } = TelemetryRecorder.SchemaVersion;
        public string RunId { get; private set; } = "";
        public string? LogicalRunId { get; private set; }
        public string? SegmentId { get; private set; }
        public string? BranchId { get; private set; }
        public int? Floor { get; private set; }
        public long? FirstLocalSequence { get; private set; }
        public long? LastLocalSequence { get; private set; }
        public DateTimeOffset? FirstRecordedAtUtc { get; private set; }
        public DateTimeOffset? LastRecordedAtUtc { get; private set; }
        public string? GameVersion { get; private set; }
        public string? ModVersion { get; private set; }
        public string RunQuality => _runQuality.Build();

        public void SetSourceSha256(string sourceSha256)
            => SourceSha256 = sourceSha256;

        public void Add(string line, JsonElement root)
        {
            Records.Add(line);
            SchemaVersion = JsonElementReader.GetString(root, "schema_version") ?? SchemaVersion;
            _runQuality.AddRecordType(JsonElementReader.GetString(root, "record_type"));
            RunId = FirstNonBlank(RunId, JsonElementReader.GetString(root, "run_id"));
            LogicalRunId = FirstNonBlank(LogicalRunId, JsonElementReader.GetString(root, "logical_run_id"));
            SegmentId = FirstNonBlank(SegmentId, JsonElementReader.GetString(root, "segment_id"));
            BranchId = FirstNonBlank(BranchId, JsonElementReader.GetString(root, "branch", "branch_id"));
            Floor ??= JsonElementReader.GetInt32(root, "state", "raw_snapshot", "run", "floor")
                ?? JsonElementReader.GetInt32(root, "pre_state", "snapshot", "raw_snapshot", "run", "floor");
            GameVersion = FirstNonBlank(
                GameVersion,
                JsonElementReader.GetString(root, "state", "raw_snapshot", "game", "game_version")
                    ?? JsonElementReader.GetString(root, "pre_state", "snapshot", "raw_snapshot", "game", "game_version"));
            ModVersion = FirstNonBlank(
                ModVersion,
                JsonElementReader.GetString(root, "operational_metadata", "mod_version")
                    ?? JsonElementReader.GetString(root, "state", "raw_snapshot", "game", "mod_version"));

            long? sequence = JsonElementReader.GetInt64(root, "local_sequence");
            if (sequence != null)
            {
                FirstLocalSequence ??= sequence;
                LastLocalSequence = sequence;
            }

            DateTimeOffset? recordedAt = JsonElementReader.GetDateTimeOffset(root, "recorded_at_utc");
            if (recordedAt != null)
            {
                FirstRecordedAtUtc ??= recordedAt;
                LastRecordedAtUtc = recordedAt;
            }
        }

        private static string FirstNonBlank(string? current, string? candidate)
            => !string.IsNullOrWhiteSpace(current)
                ? current
                : !string.IsNullOrWhiteSpace(candidate)
                    ? candidate
                    : current ?? "";
    }
}

internal sealed class TelemetryUploadQueueItem
{
    public TelemetryUploadQueueItem(string directory, TelemetryUploadQueueItemStatus status)
    {
        Directory = directory;
        Status = status;
    }

    public string Directory { get; }
    public TelemetryUploadQueueItemStatus Status { get; private set; }
    public string BundlePath => Path.Combine(Directory, "bundle.jsonl.gz");
    public string ManifestPath => Path.Combine(Directory, "manifest.json");
    public string StatusPath => Path.Combine(Directory, "status.json");

    public byte[] ManifestBytes()
        => File.ReadAllBytes(ManifestPath);

    public FileStream OpenBundle()
        => new(BundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    public void SaveStatus(TelemetryUploadQueueItemStatus status)
    {
        Status = status;
        AtomicJsonFile.Write(StatusPath, status);
    }

    public void SaveReward(TelemetryUploadRewardStatus reward)
    {
        SaveStatus(Status with
        {
            Reward = reward
        });
    }

    public static TelemetryUploadQueueItem? TryLoad(string directory)
    {
        string statusPath = Path.Combine(directory, "status.json");
        if (!File.Exists(statusPath))
            return null;

        try
        {
            string json = File.ReadAllText(statusPath);
            TelemetryUploadQueueItemStatus? status = JsonSerializer.Deserialize<TelemetryUploadQueueItemStatus>(
                json,
                TelemetryJson.Options);
            return status == null ? null : new TelemetryUploadQueueItem(directory, status);
        }
        catch
        {
            return null;
        }
    }
}

internal static class JsonElementReader
{
    public static string? GetString(JsonElement element, params string[] path)
        => TryGet(element, path, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? GetInt64(JsonElement element, params string[] path)
    {
        if (!TryGet(element, path, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    public static int? GetInt32(JsonElement element, params string[] path)
    {
        long? value = GetInt64(element, path);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    public static bool? GetBoolean(JsonElement element, params string[] path)
    {
        if (!TryGet(element, path, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out bool parsed))
        {
            return parsed;
        }

        return null;
    }

    public static DateTimeOffset? GetDateTimeOffset(JsonElement element, params string[] path)
    {
        string? value = GetString(element, path);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static bool TryGet(JsonElement element, IReadOnlyList<string> path, out JsonElement value)
    {
        value = element;
        foreach (string segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }

        return true;
    }
}
