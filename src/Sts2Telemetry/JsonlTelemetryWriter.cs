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
    private static readonly object DiagnosticsGate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

    public bool Drain(TimeSpan timeout)
    {
        if (_disposed)
            return false;

        using var completion = new ManualResetEventSlim(false);
        try
        {
            _queue.Add(WriterItem.Drain(completion));
        }
        catch
        {
            return false;
        }

        bool completed = completion.Wait(timeout);
        if (!completed)
            WriteDrainFailureDiagnostic(timeout);
        return completed;
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
        string? currentPath = null;
        FileStream? currentStream = null;
        try
        {
            foreach (WriterItem item in _queue.GetConsumingEnumerable())
            {
                if (item.DrainCompletion != null)
                {
                    try
                    {
                        currentStream?.Flush(flushToDisk: true);
                    }
                    catch (Exception ex)
                    {
                        WriteDrainFailureDiagnostic(TimeSpan.Zero, ex);
                    }
                    finally
                    {
                        item.DrainCompletion.Set();
                    }

                    continue;
                }

                try
                {
                    string path = Path.Combine(_baseDirectory, item.RelativePath);
                    string? directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory) && !_createdDirectories.Contains(directory))
                    {
                        Directory.CreateDirectory(directory);
                        _createdDirectories.Add(directory);
                    }

                    if (currentStream == null || !string.Equals(currentPath, path, StringComparison.Ordinal))
                    {
                        currentStream?.Dispose();
                        currentStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        currentPath = path;
                    }

                    string json = JsonSerializer.Serialize(item.Record, TelemetryJson.Options);
                    byte[] line = Encoding.UTF8.GetBytes(json + "\n");
                    currentStream.Write(line, 0, line.Length);
                }
                catch (Exception ex)
                {
                    currentStream?.Dispose();
                    currentStream = null;
                    currentPath = null;
                    WriteWriterFailureDiagnostic(item, ex);
                }
            }
        }
        finally
        {
            currentStream?.Dispose();
        }
    }

    private void WriteDrainFailureDiagnostic(TimeSpan timeout, Exception? exception = null)
    {
        try
        {
            string diagnosticsDirectory = Path.Combine(_baseDirectory, "diagnostics");
            string path = Path.Combine(diagnosticsDirectory, "jsonl_writer_failures.jsonl");
            string line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["record_type"] = "diagnostic/jsonl_writer_drain_failure",
                ["recorded_at_utc"] = DateTimeOffset.UtcNow,
                ["mod_id"] = Sts2TelemetryMod.ModId,
                ["mod_version"] = Sts2TelemetryMod.Version,
                ["timeout_ms"] = (int)timeout.TotalMilliseconds,
                ["exception_type"] = exception?.GetType().FullName,
                ["exception_message"] = exception == null ? null : SanitizeDiagnosticMessage(exception.Message)
            }, TelemetryJson.Options) + "\n";

            lock (DiagnosticsGate)
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch
        {
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

    private void WriteWriterFailureDiagnostic(WriterItem item, Exception exception)
    {
        try
        {
            string diagnosticsDirectory = Path.Combine(_baseDirectory, "diagnostics");
            string path = Path.Combine(diagnosticsDirectory, "jsonl_writer_failures.jsonl");
            var record = new Dictionary<string, object?>
            {
                ["record_type"] = "diagnostic/jsonl_writer_failure",
                ["recorded_at_utc"] = DateTimeOffset.UtcNow,
                ["mod_id"] = Sts2TelemetryMod.ModId,
                ["mod_version"] = Sts2TelemetryMod.Version,
                ["relative_path"] = item.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["exception_message"] = SanitizeDiagnosticMessage(exception.Message),
                ["hresult"] = exception.HResult
            };

            if (TryGetNonBlankString(item.Record, "record_type", out string telemetryRecordType))
                record["telemetry_record_type"] = telemetryRecordType;
            if (item.Record.TryGetValue("local_sequence", out object? sequence))
                record["local_sequence"] = sequence;

            string line = JsonSerializer.Serialize(record, TelemetryJson.Options) + "\n";
            lock (DiagnosticsGate)
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch
        {
        }
    }

    private string SanitizeDiagnosticMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        string sanitized = message.Replace(_baseDirectory, "$TELEMETRY_BASE", StringComparison.Ordinal);
        return sanitized.Length > 500 ? sanitized[..500] : sanitized;
    }

    private readonly record struct WriterItem(
        string RelativePath,
        IReadOnlyDictionary<string, object?> Record,
        ManualResetEventSlim? DrainCompletion = null)
    {
        public static WriterItem Drain(ManualResetEventSlim completion)
            => new("", new Dictionary<string, object?>(), completion);
    }
}
