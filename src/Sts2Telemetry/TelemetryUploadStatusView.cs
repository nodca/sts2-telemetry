using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal sealed record TelemetryUploadStatusView
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.local_upload_status_view.v1";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string TelemetryBaseDirectory { get; init; } = "";
    public string UploadStatusPath { get; init; } = "";
    public TelemetryUpdateStatus? Update { get; init; }
    public TelemetryUploadRunStatus[] Runs { get; init; } = Array.Empty<TelemetryUploadRunStatus>();
}

internal sealed record TelemetryUploadRunStatus
{
    public string GroupKey { get; init; } = "";
    public string Source { get; init; } = "";
    public string RunId { get; init; } = "";
    public string LogicalRunId { get; init; } = "";
    public string Character { get; init; } = "";
    public string RunState { get; init; } = "in_progress";
    public string RunQuality { get; init; } = TelemetryRunQuality.Partial;
    public string UploadState { get; init; } = "not_queued";
    public string RewardState { get; init; } = "not_applicable";
    public string? RewardAmount { get; init; }
    public string? RedeemCode { get; init; }
    public int? FloorReached { get; init; }
    public int? Ascension { get; init; }
    public DateTimeOffset? LatestRecordedAtUtc { get; init; }
    public long? LastLocalSequence { get; init; }
    public int SourceCount { get; init; }
    public int BundleCount { get; init; }
    public int UploadedBundles { get; init; }
    public int UploadedSourceCount { get; init; }
    public int DuplicateUploadedSourceCount { get; init; }
    public int QueuedBundles { get; init; }
    public int FailedBundles { get; init; }
    public int DroppedBundles { get; init; }
    public TelemetryNonCombatMatchQuality[] NonCombatMatchQuality { get; init; } =
        Array.Empty<TelemetryNonCombatMatchQuality>();
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
}

internal sealed record TelemetryNonCombatMatchQuality
{
    public string Surface { get; init; } = "";
    public int ContextRecords { get; init; }
    public int SignalRecords { get; init; }
    public int MatchedSignals { get; init; }
    public int UnmatchedSignals { get; init; }
    public int TrainableClosedChoices { get; init; }
}

internal static class TelemetryUploadStatusReader
{
    public static TelemetryUploadStatusView Build(string telemetryBaseDirectory, int maxRuns = 12, Func<DateTimeOffset>? now = null)
    {
        var groups = new Dictionary<string, RunStatusBuilder>(StringComparer.Ordinal);
        foreach (RunFileSummary source in ReadRunFiles(telemetryBaseDirectory))
        {
            RunStatusBuilder builder = GetOrAdd(
                groups,
                source.LogicalRunId,
                source.RunId,
                source.RelativeSourcePath);
            builder.AddSource(source);
        }

        foreach (TelemetryUploadQueueItemStatus status in ReadQueueStatuses(telemetryBaseDirectory))
        {
            RunStatusBuilder builder = GetOrAdd(
                groups,
                status.LogicalRunId,
                status.RunId,
                status.SourcePath);
            builder.AddQueueStatus(status);
        }

        TelemetryUploadSummary? summary = ReadUploadSummary(telemetryBaseDirectory);
        if (summary != null)
        {
            foreach (TelemetryUploadRewardStatus reward in summary.Rewards)
            {
                RunStatusBuilder builder = GetOrAdd(groups, null, reward.RunId, null);
                builder.AddReward(reward);
            }
        }

        bool rewardsDisabled = summary?.Enabled == false || summary?.Policy.UploadDisabled == true;
        if (rewardsDisabled)
        {
            foreach (RunStatusBuilder builder in groups.Values)
                builder.MarkRewardsDisabled();
        }

        TelemetryUploadRunStatus[] runs = groups.Values
            .Select(builder => builder.Build())
            .OrderByDescending(run => run.LatestRecordedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(run => run.GroupKey, StringComparer.Ordinal)
            .Take(Math.Max(1, maxRuns))
            .ToArray();

        return new TelemetryUploadStatusView
        {
            UpdatedAtUtc = (now ?? (() => DateTimeOffset.UtcNow))(),
            TelemetryBaseDirectory = telemetryBaseDirectory,
            UploadStatusPath = Path.Combine(
                TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory),
                TelemetryUploadSettings.StatusFileName),
            Update = ReadUpdateStatus(telemetryBaseDirectory),
            Runs = runs
        };
    }

    private static RunStatusBuilder GetOrAdd(
        Dictionary<string, RunStatusBuilder> groups,
        string? logicalRunId,
        string? runId,
        string? sourcePath)
    {
        string key = BuildGroupKey(logicalRunId, runId, sourcePath);
        if (!groups.TryGetValue(key, out RunStatusBuilder? builder))
        {
            if (string.IsNullOrWhiteSpace(logicalRunId))
            {
                builder = groups.Values.FirstOrDefault(candidate => candidate.Matches(runId, sourcePath));
                if (builder != null)
                    return builder;
            }

            builder = new RunStatusBuilder(key);
            groups[key] = builder;
        }

        return builder;
    }

    private static IEnumerable<RunFileSummary> ReadRunFiles(string telemetryBaseDirectory)
    {
        string runsDirectory = Path.Combine(telemetryBaseDirectory, JsonlTelemetryWriter.RunsDirectoryName);
        if (!Directory.Exists(runsDirectory))
            yield break;

        foreach (string path in Directory.EnumerateFiles(runsDirectory, "*.jsonl", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            RunFileSummary? summary = ReadRunFile(telemetryBaseDirectory, path);
            if (summary != null)
                yield return summary;
        }
    }

    private static RunFileSummary? ReadRunFile(string telemetryBaseDirectory, string path)
    {
        var builder = new RunFileSummaryBuilder(RelativeTelemetryPath(telemetryBaseDirectory, path));
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
                builder.Add(document.RootElement);
            }
        }
        catch
        {
            builder.MarkReadError();
        }

        return builder.HasRecords
            ? builder.Build()
            : null;
    }

    private static IEnumerable<TelemetryUploadQueueItemStatus> ReadQueueStatuses(string telemetryBaseDirectory)
    {
        string queueDirectory = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory),
            "queue");
        if (!Directory.Exists(queueDirectory))
            yield break;

        foreach (string directory in Directory.EnumerateDirectories(queueDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            TelemetryUploadQueueItem? item = TelemetryUploadQueueItem.TryLoad(directory);
            if (item != null)
                yield return item.Status;
        }
    }

    private static TelemetryUploadSummary? ReadUploadSummary(string telemetryBaseDirectory)
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory),
            TelemetryUploadSettings.StatusFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TelemetryUploadSummary>(json, TelemetryJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private static TelemetryUpdateStatus? ReadUpdateStatus(string telemetryBaseDirectory)
    {
        string path = Path.Combine(
            TelemetryUpdateSettings.UpdateDirectory(telemetryBaseDirectory),
            TelemetryUpdateSettings.StatusFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TelemetryUpdateStatus>(json, TelemetryJson.Options);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildGroupKey(string? logicalRunId, string? runId, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(logicalRunId))
            return "logical:" + logicalRunId.Trim();
        if (!string.IsNullOrWhiteSpace(runId))
            return "run:" + runId.Trim();
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return "source:" + sourcePath.Trim();
        return "unknown";
    }

    private static string RelativeTelemetryPath(string telemetryBaseDirectory, string path)
        => Path.GetRelativePath(telemetryBaseDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private sealed class RunFileSummaryBuilder
    {
        private readonly string _relativeSourcePath;
        private readonly TelemetryRunQualityAccumulator _runQuality = new();
        private readonly Dictionary<string, NonCombatMatchQualityBuilder> _nonCombatMatchQuality = new(StringComparer.Ordinal);
        private bool _readError;
        private string _runState = "in_progress";

        public RunFileSummaryBuilder(string relativeSourcePath)
        {
            _relativeSourcePath = relativeSourcePath;
        }

        public bool HasRecords { get; private set; }
        public string RunId { get; private set; } = "";
        public string LogicalRunId { get; private set; } = "";
        public DateTimeOffset? LatestRecordedAtUtc { get; private set; }
        public long? LastLocalSequence { get; private set; }
        public int? FloorReached { get; private set; }
        public int? Ascension { get; private set; }
        public string Character { get; private set; } = "";

        public void Add(JsonElement root)
        {
            HasRecords = true;
            RunId = FirstNonBlank(RunId, JsonElementReader.GetString(root, "run_id"));
            LogicalRunId = FirstNonBlank(
                LogicalRunId,
                JsonElementReader.GetString(root, "logical_run_id")
                    ?? JsonElementReader.GetString(root, "logical_run_identity", "logical_run_id"));
            _runQuality.AddRecordType(JsonElementReader.GetString(root, "record_type"));
            UpdateRunState(root);
            UpdateRunFacts(root);
            UpdateNonCombatMatchQuality(root);

            long? sequence = JsonElementReader.GetInt64(root, "local_sequence");
            if (sequence != null)
                LastLocalSequence = Math.Max(LastLocalSequence ?? sequence.Value, sequence.Value);

            DateTimeOffset? recordedAt = JsonElementReader.GetDateTimeOffset(root, "recorded_at_utc");
            if (recordedAt != null)
                LatestRecordedAtUtc = Max(LatestRecordedAtUtc, recordedAt.Value);
        }

        public void MarkReadError()
        {
            _readError = true;
            _runQuality.MarkReadError();
        }

        public RunFileSummary Build()
            => new(
                _relativeSourcePath,
                RunId,
                LogicalRunId,
                _readError ? "partial" : _runState,
                _runQuality.Build(),
                LatestRecordedAtUtc,
                LastLocalSequence,
                FloorReached,
                Ascension,
                Character,
                _nonCombatMatchQuality.Values.Select(builder => builder.Build()).ToArray());

        private void UpdateRunState(JsonElement root)
        {
            string recordType = JsonElementReader.GetString(root, "record_type") ?? "";
            if (recordType == "lifecycle/unsupported_run")
            {
                _runState = "unsupported";
                return;
            }

            if (_runState == "unsupported")
                return;

            if (recordType == "lifecycle/run_ended")
            {
                _runState = "completed";
                return;
            }

            if (_runState == "completed")
                return;

            if (recordType == "lifecycle/run_suspended")
                _runState = IsAbandoned(root) ? "abandoned" : "suspended";
        }

        private void UpdateRunFacts(JsonElement root)
        {
            FloorReached = Max(FloorReached, MaxRunIntFact(root, "floor"));
            Ascension ??= FirstRunIntFact(root, "ascension");
            Character = FirstNonBlank(Character, FirstStringFact(root, "character"));
        }

        private void UpdateNonCombatMatchQuality(JsonElement root)
        {
            if (!root.TryGetProperty("non_combat_closure", out JsonElement closure)
                || closure.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string surface = JsonElementReader.GetString(closure, "surface")
                ?? JsonElementReader.GetString(closure, "state_type")
                ?? "";
            if (string.IsNullOrWhiteSpace(surface))
                return;

            if (!_nonCombatMatchQuality.TryGetValue(surface, out NonCombatMatchQualityBuilder? builder))
            {
                builder = new NonCombatMatchQualityBuilder(surface);
                _nonCombatMatchQuality[surface] = builder;
            }

            builder.Add(
                JsonElementReader.GetString(root, "record_type"),
                JsonElementReader.GetString(closure, "selected_action_match_status"),
                JsonElementReader.GetBoolean(closure, "trainable_closed_non_combat_choice"),
                !IsDiagnosticNoIdentityCardRewardCompletionSignal(root, closure, surface));
        }

        private static bool IsDiagnosticNoIdentityCardRewardCompletionSignal(
            JsonElement root,
            JsonElement closure,
            string surface)
        {
            if (!string.Equals(surface, "card_reward", StringComparison.Ordinal))
                return false;

            string? recordType = JsonElementReader.GetString(root, "record_type");
            if (recordType is not ("decision/ui_signal" or "decision/action_signal"))
                return false;

            if (!string.Equals(
                    JsonElementReader.GetString(closure, "selected_action_match_status"),
                    "unmatched",
                    StringComparison.Ordinal))
            {
                return false;
            }

            string? actionType =
                JsonElementReader.GetString(root, "ui_signal", "metadata", "action_type")
                ?? JsonElementReader.GetString(root, "action_signal", "metadata", "action_type");
            if (!string.Equals(actionType, "card_reward_selection_unavailable", StringComparison.Ordinal))
                return false;

            string? identityStatus =
                JsonElementReader.GetString(root, "ui_signal", "metadata", "selection_identity_status")
                ?? JsonElementReader.GetString(root, "action_signal", "metadata", "selection_identity_status");
            if (!string.Equals(identityStatus, "selected_card_identity_missing_from_signal", StringComparison.Ordinal))
                return false;

            string source = JsonElementReader.GetString(root, "source")
                ?? JsonElementReader.GetString(root, "ui_signal", "metadata", "source")
                ?? JsonElementReader.GetString(root, "action_signal", "metadata", "source")
                ?? "";
            return string.Equals(source, "ui.card_reward.cards_selected", StringComparison.Ordinal)
                || source.EndsWith(".CardsSelected", StringComparison.Ordinal)
                || source.Contains(".CardsSelected(", StringComparison.Ordinal);
        }

        private static bool IsAbandoned(JsonElement root)
        {
            string source = JsonElementReader.GetString(root, "source") ?? "";
            string reason = JsonElementReader.GetString(root, "details", "reason") ?? "";
            return source.Contains("abandon", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("abandon", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class RunStatusBuilder
    {
        private readonly string _groupKey;
        private readonly List<string> _sources = new();
        private readonly HashSet<string> _sourceSet = new(StringComparer.Ordinal);
        private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
        private readonly List<TelemetryUploadQueueItemStatus> _queueStatuses = new();
        private readonly Dictionary<string, NonCombatMatchQualityBuilder> _nonCombatMatchQuality = new(StringComparer.Ordinal);
        private TelemetryUploadRewardStatus? _reward;
        private bool _rewardsDisabled;

        public RunStatusBuilder(string groupKey)
        {
            _groupKey = groupKey;
        }

        public string RunId { get; private set; } = "";
        public string LogicalRunId { get; private set; } = "";
        public string RunState { get; private set; } = "in_progress";
        public string RunQuality { get; private set; } = TelemetryRunQuality.Diagnostic;
        public DateTimeOffset? LatestRecordedAtUtc { get; private set; }
        public long? LastLocalSequence { get; private set; }
        public int? FloorReached { get; private set; }
        public int? Ascension { get; private set; }
        public string Character { get; private set; } = "";
        public string? LastErrorCode { get; private set; }
        public string? LastErrorMessage { get; private set; }

        public bool Matches(string? runId, string? sourcePath)
            => (!string.IsNullOrWhiteSpace(runId) && _runIds.Contains(runId.Trim()))
                || (!string.IsNullOrWhiteSpace(sourcePath) && _sourceSet.Contains(sourcePath.Trim()));

        public void AddSource(RunFileSummary source)
        {
            _sources.Add(source.RelativeSourcePath);
            _sourceSet.Add(source.RelativeSourcePath);
            if (!string.IsNullOrWhiteSpace(source.RunId))
                _runIds.Add(source.RunId);
            RunId = FirstNonBlank(RunId, source.RunId);
            LogicalRunId = FirstNonBlank(LogicalRunId, source.LogicalRunId);
            RunState = MergeRunState(RunState, source.RunState);
            RunQuality = TelemetryRunQuality.Merge(RunQuality, source.RunQuality);
            LatestRecordedAtUtc = Max(LatestRecordedAtUtc, source.LatestRecordedAtUtc);
            LastLocalSequence = Max(LastLocalSequence, source.LastLocalSequence);
            FloorReached = Max(FloorReached, source.FloorReached);
            Ascension ??= source.Ascension;
            Character = FirstNonBlank(Character, source.Character);
            foreach (TelemetryNonCombatMatchQuality quality in source.NonCombatMatchQuality)
                AddNonCombatMatchQuality(quality);
        }

        public void AddQueueStatus(TelemetryUploadQueueItemStatus status)
        {
            _queueStatuses.Add(status);
            if (!string.IsNullOrWhiteSpace(status.SourcePath))
            {
                _sources.Add(status.SourcePath.Trim());
                _sourceSet.Add(status.SourcePath.Trim());
            }
            if (!string.IsNullOrWhiteSpace(status.RunId))
                _runIds.Add(status.RunId.Trim());
            RunId = FirstNonBlank(RunId, status.RunId);
            LogicalRunId = FirstNonBlank(LogicalRunId, status.LogicalRunId);
            RunQuality = TelemetryRunQuality.Merge(RunQuality, status.RunQuality);
            LatestRecordedAtUtc = Max(LatestRecordedAtUtc, status.UploadedAtUtc ?? status.UpdatedAtUtc);
            LastLocalSequence = Max(LastLocalSequence, status.LastLocalSequence);
            if (!string.IsNullOrWhiteSpace(status.LastErrorCode))
                LastErrorCode = status.LastErrorCode;
            if (!string.IsNullOrWhiteSpace(status.LastErrorMessage))
                LastErrorMessage = status.LastErrorMessage;
            if (status.Reward != null)
                AddReward(status.Reward);
        }

        public void AddReward(TelemetryUploadRewardStatus reward)
        {
            if (!string.IsNullOrWhiteSpace(reward.RunId))
                _runIds.Add(reward.RunId.Trim());
            if (_reward == null
                || (reward.UpdatedAtUtc ?? DateTimeOffset.MinValue) >= (_reward.UpdatedAtUtc ?? DateTimeOffset.MinValue))
            {
                _reward = reward;
            }

            RunId = FirstNonBlank(RunId, reward.RunId);
            FloorReached = Max(FloorReached, reward.FloorReached);
            Ascension ??= reward.Ascension;
            if (!string.IsNullOrWhiteSpace(reward.LastErrorCode))
                LastErrorCode = reward.LastErrorCode;
            if (!string.IsNullOrWhiteSpace(reward.LastErrorDetail))
                LastErrorMessage = reward.LastErrorDetail;
        }

        public void MarkRewardsDisabled()
            => _rewardsDisabled = true;

        public TelemetryUploadRunStatus Build()
        {
            string uploadState = BuildUploadState(_queueStatuses);
            string rewardState = BuildRewardState(uploadState, _reward, _rewardsDisabled);
            int sourceCount = _sourceSet.Count;
            int uploadedBundles = _queueStatuses.Count(status => status.State == "uploaded");
            int uploadedSourceCount = _queueStatuses
                .Where(status => status.State == "uploaded" && !string.IsNullOrWhiteSpace(status.SourcePath))
                .Select(status => status.SourcePath.Trim())
                .Distinct(StringComparer.Ordinal)
                .Count();
            return new TelemetryUploadRunStatus
            {
                GroupKey = _groupKey,
                Source = FormatSource(_sources),
                RunId = RunId,
                LogicalRunId = LogicalRunId,
                Character = Character,
                RunState = RunState,
                RunQuality = RunQuality,
                UploadState = uploadState,
                RewardState = rewardState,
                RewardAmount = _reward?.Amount,
                RedeemCode = _reward?.RedeemCode,
                FloorReached = FloorReached,
                Ascension = Ascension,
                LatestRecordedAtUtc = LatestRecordedAtUtc,
                LastLocalSequence = LastLocalSequence,
                SourceCount = sourceCount,
                BundleCount = _queueStatuses.Count,
                UploadedBundles = uploadedBundles,
                UploadedSourceCount = uploadedSourceCount,
                DuplicateUploadedSourceCount = Math.Max(0, uploadedBundles - uploadedSourceCount),
                QueuedBundles = _queueStatuses.Count(status => status.State == "pending"),
                FailedBundles = _queueStatuses.Count(status => status.State == "failed"),
                DroppedBundles = _queueStatuses.Count(status => status.State == "dropped"),
                NonCombatMatchQuality = _nonCombatMatchQuality.Values
                    .Select(builder => builder.Build())
                    .OrderBy(quality => quality.Surface, StringComparer.Ordinal)
                    .ToArray(),
                LastErrorCode = LastErrorCode,
                LastErrorMessage = LastErrorMessage
            };
        }

        private void AddNonCombatMatchQuality(TelemetryNonCombatMatchQuality quality)
        {
            if (string.IsNullOrWhiteSpace(quality.Surface))
                return;

            if (!_nonCombatMatchQuality.TryGetValue(quality.Surface, out NonCombatMatchQualityBuilder? builder))
            {
                builder = new NonCombatMatchQualityBuilder(quality.Surface);
                _nonCombatMatchQuality[quality.Surface] = builder;
            }

            builder.Add(quality);
        }

        private static string BuildUploadState(IReadOnlyList<TelemetryUploadQueueItemStatus> statuses)
        {
            if (statuses.Count == 0)
                return "not_queued";

            int uploaded = statuses.Count(status => status.State == "uploaded");
            int pending = statuses.Count(status => status.State == "pending");
            int failed = statuses.Count(status => status.State == "failed" || status.State == "dropped");
            if (uploaded > 0 && uploaded < statuses.Count)
                return "partial";
            if (uploaded == statuses.Count)
                return "uploaded";
            if (failed > 0)
                return "failed";
            if (pending > 0)
                return "queued";
            return "queued";
        }

        private static string BuildRewardState(string uploadState, TelemetryUploadRewardStatus? reward, bool rewardsDisabled)
        {
            if (reward != null && !string.IsNullOrWhiteSpace(reward.Status))
                return reward.Status;
            if (rewardsDisabled)
                return "disabled";
            return uploadState is "uploaded" or "partial"
                ? "processing"
                : "not_applicable";
        }

        private static string FormatSource(IReadOnlyList<string> sources)
        {
            if (sources.Count == 0)
                return "";

            string first = sources.OrderBy(source => source, StringComparer.Ordinal).First();
            int extra = sources.Distinct(StringComparer.Ordinal).Count() - 1;
            return extra > 0 ? $"{first} (+{extra} segments)" : first;
        }
    }

    private sealed record RunFileSummary(
        string RelativeSourcePath,
        string RunId,
        string LogicalRunId,
        string RunState,
        string RunQuality,
        DateTimeOffset? LatestRecordedAtUtc,
        long? LastLocalSequence,
        int? FloorReached,
        int? Ascension,
        string Character,
        TelemetryNonCombatMatchQuality[] NonCombatMatchQuality);

    private sealed class NonCombatMatchQualityBuilder
    {
        private readonly string _surface;

        public NonCombatMatchQualityBuilder(string surface)
        {
            _surface = surface;
        }

        public int ContextRecords { get; private set; }
        public int SignalRecords { get; private set; }
        public int MatchedSignals { get; private set; }
        public int UnmatchedSignals { get; private set; }
        public int TrainableClosedChoices { get; private set; }

        public void Add(string? recordType, string? matchStatus, bool? trainable, bool countSignalQuality = true)
        {
            if (recordType == "decision/context")
                ContextRecords++;
            else if (countSignalQuality && recordType is ("decision/ui_signal" or "decision/action_signal"))
                SignalRecords++;

            if (!countSignalQuality)
                return;

            if (string.Equals(matchStatus, "matched", StringComparison.Ordinal))
                MatchedSignals++;
            else if (string.Equals(matchStatus, "unmatched", StringComparison.Ordinal))
                UnmatchedSignals++;

            if (trainable == true)
                TrainableClosedChoices++;
        }

        public void Add(TelemetryNonCombatMatchQuality quality)
        {
            ContextRecords += quality.ContextRecords;
            SignalRecords += quality.SignalRecords;
            MatchedSignals += quality.MatchedSignals;
            UnmatchedSignals += quality.UnmatchedSignals;
            TrainableClosedChoices += quality.TrainableClosedChoices;
        }

        public TelemetryNonCombatMatchQuality Build()
            => new()
            {
                Surface = _surface,
                ContextRecords = ContextRecords,
                SignalRecords = SignalRecords,
                MatchedSignals = MatchedSignals,
                UnmatchedSignals = UnmatchedSignals,
                TrainableClosedChoices = TrainableClosedChoices
            };
    }

    private static string FirstNonBlank(string current, string? candidate)
        => !string.IsNullOrWhiteSpace(current)
            ? current
            : !string.IsNullOrWhiteSpace(candidate)
                ? candidate.Trim()
                : current;

    private static string MergeRunState(string current, string next)
    {
        int Rank(string state)
            => state switch
            {
                "completed" => 5,
                "abandoned" => 4,
                "suspended" => 3,
                "unsupported" => 2,
                "partial" => 1,
                _ => 0
            };
        return Rank(next) > Rank(current) ? next : current;
    }

    private static DateTimeOffset? Max(DateTimeOffset? current, DateTimeOffset? candidate)
        => candidate == null
            ? current
            : current == null || candidate.Value > current.Value
                ? candidate.Value
                : current;

    private static long? Max(long? current, long? candidate)
        => candidate == null
            ? current
            : current == null || candidate.Value > current.Value
                ? candidate.Value
                : current;

    private static int? Max(int? current, int? candidate)
        => candidate == null
            ? current
            : current == null || candidate.Value > current.Value
                ? candidate.Value
                : current;

    private static int? FirstRunIntFact(JsonElement root, string field)
    {
        foreach (string[] path in RunFactPaths(field))
        {
            int? value = JsonElementReader.GetInt32(root, path);
            if (value != null)
                return value;
        }

        return null;
    }

    private static int? MaxRunIntFact(JsonElement root, string field)
    {
        int? result = null;
        foreach (string[] path in RunFactPaths(field))
            result = Max(result, JsonElementReader.GetInt32(root, path));
        return result;
    }

    private static string? FirstStringFact(JsonElement root, string field)
    {
        foreach (string[] path in RunFactPaths(field).Concat(LocalPlayerFactPaths(field)))
        {
            string? value = JsonElementReader.GetString(root, path);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static IEnumerable<string[]> RunFactPaths(string field)
    {
        yield return new[] { "run", field };
        foreach (string[] root in SnapshotRoots())
            yield return root.Concat(new[] { "run", field }).ToArray();
    }

    private static IEnumerable<string[]> LocalPlayerFactPaths(string field)
    {
        yield return new[] { "local_player", field };
        foreach (string[] root in SnapshotRoots())
            yield return root.Concat(new[] { "local_player", field }).ToArray();
    }

    private static IEnumerable<string[]> SnapshotRoots()
    {
        yield return new[] { "state", "raw_snapshot" };
        yield return new[] { "state", "canonical_snapshot" };
        yield return new[] { "pre_state", "raw_snapshot" };
        yield return new[] { "pre_state", "canonical_snapshot" };
        yield return new[] { "post_state", "raw_snapshot" };
        yield return new[] { "post_state", "canonical_snapshot" };
        yield return new[] { "state", "snapshot", "raw_snapshot" };
        yield return new[] { "state", "snapshot", "canonical_snapshot" };
        yield return new[] { "pre_state", "snapshot", "raw_snapshot" };
        yield return new[] { "pre_state", "snapshot", "canonical_snapshot" };
        yield return new[] { "post_state", "snapshot", "raw_snapshot" };
        yield return new[] { "post_state", "snapshot", "canonical_snapshot" };
    }
}

internal static class TelemetryUploadStatusRenderer
{
    public static string RenderPlainText(TelemetryUploadStatusView view, int maxRuns = 8)
    {
        var builder = new StringBuilder();
        builder.AppendLine("遥测上传 / 奖励");
        builder.AppendLine($"更新：{FormatDate(view.UpdatedAtUtc)}");
        string updateLine = FormatUpdateStatus(view.Update);
        if (!string.IsNullOrWhiteSpace(updateLine))
            builder.AppendLine(updateLine);

        if (view.Runs.Length == 0)
        {
            builder.AppendLine();
            builder.AppendLine("还没有找到本地遥测运行。");
            return builder.ToString().TrimEnd();
        }

        foreach (TelemetryUploadRunStatus run in view.Runs.Take(Math.Max(1, maxRuns)))
        {
            builder.AppendLine();
            builder.AppendLine(FormatTitle(run));
            builder.AppendLine($"  记录：{Blank(run.Source)}");
            string sourceSummary = FormatSourceSummary(run);
            if (!string.IsNullOrWhiteSpace(sourceSummary))
                builder.AppendLine($"  分段：{sourceSummary}");
            string matchSummary = FormatNonCombatMatchSummary(run);
            if (!string.IsNullOrWhiteSpace(matchSummary))
                builder.AppendLine($"  非战斗匹配：{matchSummary}");
            builder.AppendLine($"  进度：第{FormatNumber(run.FloorReached)}层  进阶{FormatNumber(run.Ascension)}");
            builder.AppendLine($"  兑换码：{Blank(run.RedeemCode)}");
            builder.AppendLine($"  状态：{DisplayCurrentStatus(run)}");
            if (!string.IsNullOrWhiteSpace(run.LastErrorCode) || !string.IsNullOrWhiteSpace(run.LastErrorMessage))
                builder.AppendLine($"  最后错误：{Blank(run.LastErrorCode)} {Blank(run.LastErrorMessage)}".TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    public static string LatestGeneratedRedeemCode(TelemetryUploadStatusView view)
        => view.Runs
               .Where(run => run.RewardState == "generated" && !string.IsNullOrWhiteSpace(run.RedeemCode))
               .OrderByDescending(run => run.LatestRecordedAtUtc ?? DateTimeOffset.MinValue)
               .Select(run => run.RedeemCode!)
               .FirstOrDefault() ?? "";

    private static string FormatTitle(TelemetryUploadRunStatus run)
    {
        string character = DisplayCharacter(run.Character);
        string floor = FormatNumber(run.FloorReached);
        string ascension = FormatNumber(run.Ascension);
        string suffix = ShortRunSuffix(run);
        return string.IsNullOrWhiteSpace(suffix)
            ? $"{character} 进阶{ascension} 第{floor}层"
            : $"{character} 进阶{ascension} 第{floor}层 {suffix}";
    }

    private static string RelativeUploadStatusPath(TelemetryUploadStatusView view)
    {
        if (string.IsNullOrWhiteSpace(view.TelemetryBaseDirectory) || string.IsNullOrWhiteSpace(view.UploadStatusPath))
            return "upload/status.json";

        try
        {
            return Path.GetRelativePath(view.TelemetryBaseDirectory, view.UploadStatusPath)
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return "upload/status.json";
        }
    }

    private static string Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string FormatSourceSummary(TelemetryUploadRunStatus run)
    {
        var parts = new List<string>();
        if (run.SourceCount > 0)
            parts.Add($"源{FormatNumber(run.SourceCount)}");
        if (run.UploadedSourceCount > 0)
            parts.Add($"已上传源{FormatNumber(run.UploadedSourceCount)}");
        if (run.DuplicateUploadedSourceCount > 0)
            parts.Add($"重复源{FormatNumber(run.DuplicateUploadedSourceCount)}");
        return string.Join("  ", parts);
    }

    private static string FormatNonCombatMatchSummary(TelemetryUploadRunStatus run)
    {
        if (run.NonCombatMatchQuality.Length == 0)
            return "";

        return string.Join("  ", run.NonCombatMatchQuality
            .Where(quality => quality.SignalRecords > 0 || quality.ContextRecords > 0)
            .OrderBy(quality => quality.Surface, StringComparer.Ordinal)
            .Take(3)
            .Select(quality =>
            {
                string surface = string.IsNullOrWhiteSpace(quality.Surface) ? "unknown" : quality.Surface;
                return $"{surface} {FormatNumber(quality.MatchedSignals)}/{FormatNumber(quality.SignalRecords)}";
            }));
    }

    private static string DisplayCurrentStatus(TelemetryUploadRunStatus run)
    {
        string runState = NormalizeState(run.RunState);
        string runQuality = NormalizeState(run.RunQuality);
        string uploadState = NormalizeState(run.UploadState);
        string rewardState = NormalizeState(run.RewardState);

        if (!string.IsNullOrWhiteSpace(run.LastErrorCode)
            || !string.IsNullOrWhiteSpace(run.LastErrorMessage)
            || uploadState == "failed")
            return "上传失败";
        if (rewardState == "generated" && !string.IsNullOrWhiteSpace(run.RedeemCode))
            return "已获得兑换码";
        if (rewardState == "generated")
            return "兑换码已生成";
        if (rewardState == "processing" && uploadState is "uploaded" or "partial")
            return "等待兑换码";
        if (rewardState == "ineligible")
            return "无奖励";
        if (rewardState == "disabled")
            return "奖励已禁用";
        if (runQuality == TelemetryRunQuality.LoadOnly)
            return "仅加载片段";
        if (runQuality == TelemetryRunQuality.Diagnostic)
            return "诊断记录";
        if (uploadState == "partial")
            return "部分上传";
        if (uploadState == "uploaded")
            return "已上传";
        if (runState == "in_progress" && uploadState is "queued" or "pending" or "not_queued")
            return "进行中";
        if (runState == "completed")
            return "已完成";
        if (runState == "suspended")
            return "已暂停";
        if (runState == "abandoned")
            return "已放弃";
        return DisplayState(runState);
    }

    private static string FormatUpdateStatus(TelemetryUpdateStatus? update)
    {
        if (update == null)
            return "";

        string state = NormalizeState(update.State);
        string target = string.IsNullOrWhiteSpace(update.TargetVersion) ? "" : update.TargetVersion.Trim();
        return state switch
        {
            TelemetryUpdateStates.UpdateAvailable when update.Authorization == TelemetryUpdateAuthorization.RequiresUserConfirmation
                => string.IsNullOrWhiteSpace(target) ? "Mod 更新：有新版本，需要确认" : $"Mod 更新：{target} 可用，需要确认",
            TelemetryUpdateStates.Downloading
                => string.IsNullOrWhiteSpace(target) ? "Mod 更新：正在下载" : $"Mod 更新：正在下载 {target}",
            TelemetryUpdateStates.Staged
                => string.IsNullOrWhiteSpace(target) ? "Mod 更新：已下载，等待安装" : $"Mod 更新：{target} 已下载，等待安装",
            TelemetryUpdateStates.InstallRequested
                => string.IsNullOrWhiteSpace(target) ? "Mod 更新：退出游戏后安装" : $"Mod 更新：{target} 将在退出游戏后安装",
            TelemetryUpdateStates.HelperMissing
                => "Mod 更新：缺少更新助手，已暂存安装包",
            TelemetryUpdateStates.Failed
                => $"Mod 更新：失败 {Blank(update.LastErrorCode)}",
            TelemetryUpdateStates.Disabled
                => "Mod 更新：已禁用",
            _ => ""
        };
    }

    private static string NormalizeState(string? value)
        => value?.Trim() ?? "";

    private static string DisplayCharacter(string? character)
    {
        string value = character?.Trim() ?? "";
        string normalized = value;
        if (normalized.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["CHARACTER.".Length..];
        normalized = normalized.ToUpperInvariant();
        return normalized switch
        {
            "IRONCLAD" => "铁甲战士",
            "NECROBINDER" => "亡灵契约师",
            "SILENT" => "寂静猎手",
            "DEFECT" => "故障机器人",
            "WATCHER" => "观者",
            "" => "未知角色",
            _ => value
        };
    }

    private static string ShortRunSuffix(TelemetryUploadRunStatus run)
    {
        string value = !string.IsNullOrWhiteSpace(run.LogicalRunId)
            ? run.LogicalRunId.Trim()
            : run.RunId.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "";

        int length = Math.Min(6, value.Length);
        return $"#{value[^length..]}";
    }

    private static string DisplayState(string? value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized switch
        {
            "in_progress" => "进行中",
            "completed" => "已完成",
            "suspended" => "已暂停",
            "abandoned" => "已放弃",
            "unsupported" => "不支持",
            "partial" => "部分上传",
            "uploaded" => "已上传",
            "queued" or "pending" => "排队中",
            "failed" => "上传失败",
            "dropped" => "已丢弃",
            "processing" => "处理中",
            "generated" => "已生成",
            "ineligible" => "不符合条件",
            "disabled" => "已禁用",
            "load_only" => "仅加载片段",
            "diagnostic" => "诊断记录",
            "not_applicable" => "不适用",
            "not_queued" => "未排队",
            "" => "-",
            _ => normalized
        };
    }

    private static string FormatNumber(int? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string FormatNumber(long? value)
        => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
}
