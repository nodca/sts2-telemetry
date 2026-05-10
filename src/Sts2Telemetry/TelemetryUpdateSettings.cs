using System.Text.Json;

namespace Sts2Telemetry;

internal sealed record TelemetryUpdateSettings
{
    public const string UpdateDirectoryName = "update";
    public const string SettingsFileName = "settings.json";
    public const string StatusFileName = "status.json";
    public const string LatestManifestFileName = "latest_manifest.json";
    public const string InstallRequestFileName = "install.request.json";
    public const string HelperResultFileName = "helper_result.json";
    public const string PackagesDirectoryName = "packages";
    public const string BackupsDirectoryName = "backups";
    public const string StagingDirectoryName = "staging";
    public const string HelperExecutableBaseName = "Sts2Telemetry.Updater";
    public const string DefaultReleaseManifestUrl = "https://sts2.cyb1.org/v1/mod/releases/latest";
    public const int DefaultHttpTimeoutSeconds = 3 * 60;
    public const int DefaultScanIntervalSeconds = 6 * 60 * 60;
    public const int DefaultProcessExitTimeoutSeconds = 15 * 60;

    public string SchemaVersion { get; init; } = "sts2.telemetry.update_settings.v1";
    public bool Enabled { get; init; } = true;
    public string ReleaseManifestUrl { get; init; } = DefaultReleaseManifestUrl;
    public int ScanIntervalSeconds { get; init; } = DefaultScanIntervalSeconds;
    public int ProcessExitTimeoutSeconds { get; init; } = DefaultProcessExitTimeoutSeconds;

    public static TelemetryUpdateSettings LoadOrCreate(string telemetryBaseDirectory)
    {
        string updateDirectory = UpdateDirectory(telemetryBaseDirectory);
        Directory.CreateDirectory(updateDirectory);
        string path = Path.Combine(updateDirectory, SettingsFileName);

        TelemetryUpdateSettings settings = new();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<TelemetryUpdateSettings>(json, TelemetryJson.Options) ?? settings;
            }
            catch
            {
                settings = new TelemetryUpdateSettings();
            }
        }

        settings = settings.Normalized();
        TrySave(path, settings);
        return settings;
    }

    public static string UpdateDirectory(string telemetryBaseDirectory)
        => Path.Combine(telemetryBaseDirectory, UpdateDirectoryName);

    private TelemetryUpdateSettings Normalized()
    {
        bool envDisabled = string.Equals(
            Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPDATE_ENABLED")?.Trim(),
            "0",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPDATE_ENABLED")?.Trim(),
                "false",
                StringComparison.OrdinalIgnoreCase);

        string envManifestUrl = Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPDATE_MANIFEST_URL")?.Trim() ?? "";
        return this with
        {
            SchemaVersion = string.IsNullOrWhiteSpace(SchemaVersion) ? "sts2.telemetry.update_settings.v1" : SchemaVersion,
            Enabled = Enabled && !envDisabled,
            ReleaseManifestUrl = NormalizeManifestUrl(string.IsNullOrWhiteSpace(envManifestUrl)
                ? ReleaseManifestUrl
                : envManifestUrl),
            ScanIntervalSeconds = ScanIntervalSeconds > 0 ? ScanIntervalSeconds : DefaultScanIntervalSeconds,
            ProcessExitTimeoutSeconds = ProcessExitTimeoutSeconds > 0
                ? ProcessExitTimeoutSeconds
                : DefaultProcessExitTimeoutSeconds
        };
    }

    private static string NormalizeManifestUrl(string value)
    {
        value = value.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            ? value
            : DefaultReleaseManifestUrl;
    }

    private static void TrySave(string path, TelemetryUpdateSettings settings)
    {
        try
        {
            TelemetryUpdateJsonFile.Write(path, settings);
        }
        catch
        {
        }
    }
}
