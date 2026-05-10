using System.Text.Json;

namespace Sts2Telemetry;

internal sealed record TelemetryUploadSettings
{
    public const string UploadDirectoryName = "upload";
    public const string SettingsFileName = "settings.json";
    public const string NoticeFileName = "notice.json";
    public const string StatusFileName = "status.json";
    public const string TokenFileName = "token.json";
    public const string SourceStateFileName = "source_state.json";
    public const string ManualSyncRequestFileName = "manual_sync.request";
    public const string DisableUploadRequestFileName = "disable_upload.request";
    public const string NoticeVersionValue = "upload-notice.v1";
    public const string DefaultEndpointUrl = "https://sts2.cyb1.org:8443";
    public const long DefaultMaxQueueBytes = 1024L * 1024L * 1024L;
    public const int DefaultMaxQueueAgeDays = 7;
    public const int DefaultScanIntervalSeconds = 30;
    public const int DefaultStableSourceSeconds = 5;
    public const int DefaultMaxRecordsPerBundle = 50_000;
    public const int DefaultMaxRunHistoryDays = 7;

    public string SchemaVersion { get; init; } = "sts2.telemetry.upload_settings.v1";
    public bool Enabled { get; init; } = true;
    public string ActiveEndpoint { get; init; } = "staging";
    public string EndpointUrl { get; init; } = DefaultEndpointUrl;
    public string StagingEndpointUrl { get; init; } = DefaultEndpointUrl;
    public string NoticeVersion { get; init; } = NoticeVersionValue;
    public bool NoticeAcknowledged { get; init; }
    public long MaxQueueBytes { get; init; } = DefaultMaxQueueBytes;
    public int MaxQueueAgeDays { get; init; } = DefaultMaxQueueAgeDays;
    public int ScanIntervalSeconds { get; init; } = DefaultScanIntervalSeconds;
    public int StableSourceSeconds { get; init; } = DefaultStableSourceSeconds;
    public int MaxRecordsPerBundle { get; init; } = DefaultMaxRecordsPerBundle;
    public int MaxRunHistoryDays { get; init; } = DefaultMaxRunHistoryDays;

    public string EffectiveEndpointUrl
    {
        get
        {
            string envEndpoint = Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPLOAD_ENDPOINT")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(envEndpoint))
                return envEndpoint;
            return ActiveEndpoint.Equals("staging", StringComparison.OrdinalIgnoreCase)
                ? StagingEndpointUrl
                : EndpointUrl;
        }
    }

    public static TelemetryUploadSettings LoadOrCreate(string telemetryBaseDirectory)
    {
        string uploadDirectory = UploadDirectory(telemetryBaseDirectory);
        Directory.CreateDirectory(uploadDirectory);
        string path = Path.Combine(uploadDirectory, SettingsFileName);

        TelemetryUploadSettings settings = new();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<TelemetryUploadSettings>(json, TelemetryJson.Options) ?? settings;
            }
            catch
            {
                settings = new TelemetryUploadSettings();
            }
        }

        settings = settings.Normalized();
        Save(path, settings);
        return settings;
    }

    public TelemetryUploadSettings ApplyDisableRequest(string telemetryBaseDirectory)
    {
        string disablePath = Path.Combine(UploadDirectory(telemetryBaseDirectory), DisableUploadRequestFileName);
        if (!File.Exists(disablePath))
            return this;

        try
        {
            File.Delete(disablePath);
        }
        catch
        {
        }

        var disabled = this with { Enabled = false, NoticeAcknowledged = true };
        Save(Path.Combine(UploadDirectory(telemetryBaseDirectory), SettingsFileName), disabled);
        return disabled;
    }

    public TelemetryUploadSettings MarkNoticeAcknowledged(string telemetryBaseDirectory)
    {
        if (NoticeAcknowledged)
            return this;

        var acknowledged = this with { NoticeAcknowledged = true };
        Save(Path.Combine(UploadDirectory(telemetryBaseDirectory), SettingsFileName), acknowledged);
        return acknowledged;
    }

    public static string UploadDirectory(string telemetryBaseDirectory)
        => Path.Combine(telemetryBaseDirectory, UploadDirectoryName);

    private TelemetryUploadSettings Normalized()
    {
        bool envDisabled = string.Equals(
            Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPLOAD_ENABLED")?.Trim(),
            "0",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("STS2_TELEMETRY_UPLOAD_ENABLED")?.Trim(),
                "false",
                StringComparison.OrdinalIgnoreCase);

        return this with
        {
            SchemaVersion = string.IsNullOrWhiteSpace(SchemaVersion) ? "sts2.telemetry.upload_settings.v1" : SchemaVersion,
            Enabled = Enabled && !envDisabled,
            ActiveEndpoint = string.IsNullOrWhiteSpace(ActiveEndpoint) ? "staging" : ActiveEndpoint.Trim(),
            EndpointUrl = NormalizeEndpoint(EndpointUrl),
            StagingEndpointUrl = NormalizeEndpoint(StagingEndpointUrl),
            NoticeVersion = string.IsNullOrWhiteSpace(NoticeVersion) ? NoticeVersionValue : NoticeVersion.Trim(),
            MaxQueueBytes = MaxQueueBytes > 0 ? MaxQueueBytes : DefaultMaxQueueBytes,
            MaxQueueAgeDays = MaxQueueAgeDays > 0 ? MaxQueueAgeDays : DefaultMaxQueueAgeDays,
            ScanIntervalSeconds = ScanIntervalSeconds > 0 ? ScanIntervalSeconds : DefaultScanIntervalSeconds,
            StableSourceSeconds = StableSourceSeconds >= 0 ? StableSourceSeconds : DefaultStableSourceSeconds,
            MaxRecordsPerBundle = MaxRecordsPerBundle > 0 ? MaxRecordsPerBundle : DefaultMaxRecordsPerBundle,
            MaxRunHistoryDays = MaxRunHistoryDays > 0 ? MaxRunHistoryDays : DefaultMaxRunHistoryDays
        };
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return DefaultEndpointUrl;
        return endpoint.TrimEnd('/');
    }

    private static void Save(string path, TelemetryUploadSettings settings)
    {
        try
        {
            AtomicJsonFile.Write(path, settings);
        }
        catch
        {
        }
    }
}

internal static class TelemetryUploadNotice
{
    public static bool Ensure(string telemetryBaseDirectory, TelemetryUploadSettings settings)
    {
        string uploadDirectory = TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory);
        Directory.CreateDirectory(uploadDirectory);
        string path = Path.Combine(uploadDirectory, TelemetryUploadSettings.NoticeFileName);
        if (settings.NoticeAcknowledged && File.Exists(path))
            return false;

        var notice = new
        {
            schema_version = "sts2.telemetry.upload_notice.v1",
            notice_version = TelemetryUploadSettings.NoticeVersionValue,
            upload_default = "enabled",
            current_upload_enabled = settings.Enabled,
            collected_data_categories = new[]
            {
                "local gameplay telemetry JSONL",
                "scrubbed native save payloads from current run and history files",
                "run and segment metadata",
                "game version, mod version, and telemetry schema version",
                "UTC record timestamps and local sequence numbers"
            },
            excluded_data_categories = new[]
            {
                "Steam ID",
                "OS username",
                "local filesystem paths",
                "raw native save local paths and local identity fields",
                "hardware fingerprint",
                "IP-derived location"
            },
            disable_path = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.DisableUploadRequestFileName}",
            settings_path = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.SettingsFileName}",
            manual_sync_path = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.ManualSyncRequestFileName}",
            status_path = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.StatusFileName}"
        };

        try
        {
            AtomicJsonFile.Write(path, notice);
        }
        catch
        {
        }

        return !settings.NoticeAcknowledged;
    }
}

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(value, TelemetryJson.Options);
        File.WriteAllText(tempPath, json);
        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
    }
}
