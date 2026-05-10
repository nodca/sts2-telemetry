using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal static class CrashBreadcrumbWriter
{
    public const string DiagnosticsDirectoryName = "diagnostics";
    public const string FileName = "crash_breadcrumbs.jsonl";

    private static readonly object Gate = new();
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static void Write(string telemetryBaseDirectory, string source, string stage, string? runId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(telemetryBaseDirectory))
                return;

            string diagnosticsDirectory = Path.Combine(telemetryBaseDirectory, DiagnosticsDirectoryName);
            string path = Path.Combine(diagnosticsDirectory, FileName);
            var record = new Dictionary<string, object?>
            {
                ["record_type"] = "diagnostic/crash_breadcrumb",
                ["recorded_at_utc"] = DateTimeOffset.UtcNow,
                ["mod_id"] = Sts2TelemetryMod.ModId,
                ["mod_version"] = Sts2TelemetryMod.Version,
                ["source"] = source,
                ["stage"] = stage
            };

            if (!string.IsNullOrWhiteSpace(runId))
                record["run_id"] = runId;

            string line = JsonSerializer.Serialize(record, TelemetryJson.Options) + "\n";
            lock (Gate)
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch
        {
            // Crash breadcrumbs are best-effort diagnostics and must never affect game callbacks.
        }
    }
}
