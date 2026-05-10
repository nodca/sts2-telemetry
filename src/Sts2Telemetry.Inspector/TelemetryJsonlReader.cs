using System.Text;
using System.Text.Json;

namespace Sts2Telemetry.Inspector;

public static class TelemetryJsonlReader
{
    public static IReadOnlyList<TelemetryRecord> Read(string telemetryPath)
        => ReadMany(new[] { telemetryPath });

    public static IReadOnlyList<TelemetryRecord> ReadMany(IEnumerable<string> telemetryPaths)
    {
        var records = new List<TelemetryRecord>();
        foreach (string telemetryPath in telemetryPaths)
            records.AddRange(ReadSingle(telemetryPath));

        return records;
    }

    private static IReadOnlyList<TelemetryRecord> ReadSingle(string telemetryPath)
    {
        string sourcePath = Path.GetFullPath(telemetryPath);
        var records = new List<TelemetryRecord>();
        int lineNumber = 0;

        foreach (string line in File.ReadLines(telemetryPath))
        {
            lineNumber++;
            long byteSize = Encoding.UTF8.GetByteCount(line);
            if (string.IsNullOrWhiteSpace(line))
            {
                records.Add(new TelemetryRecord(sourcePath, lineNumber, byteSize, line, null, "blank JSONL line"));
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                records.Add(new TelemetryRecord(sourcePath, lineNumber, byteSize, line, document.RootElement.Clone(), null));
            }
            catch (JsonException ex)
            {
                records.Add(new TelemetryRecord(sourcePath, lineNumber, byteSize, line, null, ex.Message));
            }
        }

        return records;
    }
}
