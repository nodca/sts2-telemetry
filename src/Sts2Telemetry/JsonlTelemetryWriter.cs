using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

public sealed class JsonlTelemetryWriter : IDisposable
{
    public const string RunsDirectoryName = "runs";
    public const string OperationalDirectoryName = "operational";
    public const string LogicalRunDirectoryPrefix = "logical-run-";
    public const string SegmentsDirectoryName = "segments";
    public const string LegacyTelemetryFileName = "telemetry.jsonl";

    private readonly BlockingCollection<WriterItem> _queue = new();
    private readonly Thread _thread;
    private readonly string _baseDirectory;
    private readonly string _installationId;
    private readonly HashSet<string> _createdDirectories = new(StringComparer.Ordinal);
    private bool _disposed;

    public JsonlTelemetryWriter(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(_baseDirectory);
        _createdDirectories.Add(_baseDirectory);
        _installationId = InstallationIdentity.LoadOrCreate(_baseDirectory);

        _thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "STS2_Telemetry_JSONL_Writer"
        };
        _thread.Start();
    }

    public static JsonlTelemetryWriter CreateForMod()
        => new(TelemetryDirectoryResolver.ResolveForMod());

    public string InstallationId => _installationId;
    public string BaseDirectory => _baseDirectory;

    public void Enqueue(string? runId, IReadOnlyDictionary<string, object?> record)
    {
        if (_disposed)
            return;

        string relativePath = ResolveRelativePath(runId, record);

        _queue.Add(new WriterItem(relativePath, record));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
            _thread.Join(TimeSpan.FromSeconds(1));
        _queue.Dispose();
    }

    private void WriterLoop()
    {
        foreach (WriterItem item in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(_baseDirectory, item.RelativePath);
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && !_createdDirectories.Contains(directory))
                {
                    Directory.CreateDirectory(directory);
                    _createdDirectories.Add(directory);
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(stream, item.Record, TelemetryJson.Options);
                stream.WriteByte((byte)'\n');
            }
            catch
            {
                // Local prototype: never let disk telemetry failures crash the game.
            }
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return builder.ToString();
    }

    private static string ResolveRelativePath(string? runId, IReadOnlyDictionary<string, object?> record)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return Path.Combine(OperationalDirectoryName, $"{DateTime.UtcNow:yyyyMMdd}.jsonl");

        if (TryGetNonBlankString(record, "logical_run_id", out string? logicalRunId))
        {
            string segmentId = TryGetNonBlankString(record, "segment_id", out string? explicitSegmentId)
                ? explicitSegmentId
                : runId;
            return Path.Combine(
                RunsDirectoryName,
                LogicalRunDirectoryName(logicalRunId),
                SegmentsDirectoryName,
                $"{SanitizePathSegment(segmentId)}.jsonl");
        }

        return Path.Combine(RunsDirectoryName, SanitizePathSegment(runId), LegacyTelemetryFileName);
    }

    private static string LogicalRunDirectoryName(string logicalRunId)
    {
        string sanitized = SanitizePathSegment(logicalRunId);
        return sanitized.StartsWith(LogicalRunDirectoryPrefix, StringComparison.Ordinal)
            ? sanitized
            : $"{LogicalRunDirectoryPrefix}{sanitized}";
    }

    private static bool TryGetNonBlankString(
        IReadOnlyDictionary<string, object?> record,
        string key,
        out string value)
    {
        if (record.TryGetValue(key, out object? raw) && raw is string text && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        value = "";
        return false;
    }

    private readonly record struct WriterItem(string RelativePath, IReadOnlyDictionary<string, object?> Record);
}
