using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal sealed record TelemetryUploadPolicy
{
    public long MaxBundleBytes { get; init; } = 52_428_800;
    public string[] AcceptedSchemaVersions { get; init; } = { TelemetryRecorder.SchemaVersion };
    public string[] AcceptedCompression { get; init; } = { "gzip" };
    public int? RetryAfterSeconds { get; init; }
    public bool UploadDisabled { get; init; }

    public static TelemetryUploadPolicy LocalDefault => new();

    public bool AcceptsSchema(string schemaVersion)
        => AcceptedSchemaVersions.Any(value => string.Equals(value, schemaVersion, StringComparison.Ordinal));

    public bool AcceptsCompression(string compression)
        => AcceptedCompression.Any(value => string.Equals(value, compression, StringComparison.Ordinal));
}

internal sealed record TelemetryUploadToken
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.upload_token.v1";
    public string InstallationId { get; init; } = "";
    public string UploadTokenId { get; init; } = "";
    public string UploadSecret { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsUsableFor(string installationId)
        => InstallationId == installationId
            && !string.IsNullOrWhiteSpace(UploadTokenId)
            && !string.IsNullOrWhiteSpace(UploadSecret);
}

internal static class TelemetryUploadTokenStore
{
    public static TelemetryUploadToken? Load(string telemetryBaseDirectory, string installationId)
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory),
            TelemetryUploadSettings.TokenFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            TelemetryUploadToken? token = JsonSerializer.Deserialize<TelemetryUploadToken>(json, TelemetryJson.Options);
            return token != null && token.IsUsableFor(installationId) ? token : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string telemetryBaseDirectory, TelemetryUploadToken token)
    {
        string path = Path.Combine(
            TelemetryUploadSettings.UploadDirectory(telemetryBaseDirectory),
            TelemetryUploadSettings.TokenFileName);
        AtomicJsonFile.Write(path, token);
    }
}

internal sealed record TelemetryUploadBundleManifest
{
    public string BundleId { get; init; } = "";
    public string InstallationId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string? LogicalRunId { get; init; }
    public string? SegmentId { get; init; }
    public string? BranchId { get; init; }
    public int? Floor { get; init; }
    public string SchemaVersion { get; init; } = TelemetryRecorder.SchemaVersion;
    public string Compression { get; init; } = "gzip";
    public string Sha256 { get; init; } = "";
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }
    public int RecordCount { get; init; }
    public long? FirstLocalSequence { get; init; }
    public long? LastLocalSequence { get; init; }
    public DateTimeOffset? FirstRecordedAtUtc { get; init; }
    public DateTimeOffset? LastRecordedAtUtc { get; init; }
    public string? GameVersion { get; init; }
    public string? ModVersion { get; init; }
}

internal sealed record TelemetryUploadQueueItemStatus
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.upload_queue_item.v1";
    public string BundleId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string State { get; init; } = "pending";
    public string Compression { get; init; } = "gzip";
    public int AttemptCount { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextAttemptAtUtc { get; init; }
    public DateTimeOffset? UploadedAtUtc { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    public string? DropReason { get; init; }
    public long CompressedSize { get; init; }
    public long UncompressedSize { get; init; }
    public int RecordCount { get; init; }
    public long? FirstLocalSequence { get; init; }
    public long? LastLocalSequence { get; init; }
    public string? RunId { get; init; }
    public string? LogicalRunId { get; init; }
    public string? SourceSha256 { get; init; }
    public string RunQuality { get; init; } = TelemetryRunQuality.Partial;
    public TelemetryUploadRewardStatus? Reward { get; init; }

    public bool IsUploadable(DateTimeOffset now)
        => State is "pending" or "failed"
            && (NextAttemptAtUtc == null || NextAttemptAtUtc <= now);
}

internal sealed record TelemetryUploadSourceState
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.upload_source_state.v1";
    public Dictionary<string, long> LastQueuedLocalSequenceBySource { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LastQueuedSourceSha256BySource { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record TelemetryUploadSummary
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.upload_status.v1";
    public bool Enabled { get; init; }
    public string EndpointMode { get; init; } = "staging";
    public string EndpointUrl { get; init; } = "";
    public string NoticeVersion { get; init; } = TelemetryUploadSettings.NoticeVersionValue;
    public string DisablePath { get; init; } = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.DisableUploadRequestFileName}";
    public string ManualSyncPath { get; init; } = $"{TelemetryUploadSettings.UploadDirectoryName}/{TelemetryUploadSettings.ManualSyncRequestFileName}";
    public bool HasUploadToken { get; init; }
    public string CompressionMode { get; init; } = "gzip_fallback";
    public string ZstdStatus { get; init; } = "not_available_no_dependency";
    public long QueuedBytes { get; init; }
    public int PendingBundles { get; init; }
    public int FailedBundles { get; init; }
    public int UploadedBundles { get; init; }
    public int UploadedSourceCount { get; init; }
    public int DuplicateUploadedSourceCount { get; init; }
    public int DroppedBundles { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? LastSyncState { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
    public TelemetryUploadPolicy Policy { get; init; } = TelemetryUploadPolicy.LocalDefault;
    public TelemetryUploadRewardStatus[] Rewards { get; init; } = Array.Empty<TelemetryUploadRewardStatus>();
}

internal sealed record TelemetryUploadRewardStatus
{
    public string InstallationId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string FormulaVersion { get; init; } = "";
    public string Status { get; init; } = "processing";
    public int? AmountCents { get; init; }
    public string? Amount { get; init; }
    public int? FloorReached { get; init; }
    public int? Ascension { get; init; }
    public string? RedeemCode { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public DateTimeOffset? GeneratedAtUtc { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorDetail { get; init; }

    public bool IsTerminal
        => Status is "generated" or "ineligible" or "disabled";
}

internal static class TelemetryUploadCrypto
{
    public const string BundleUploadPath = "/v1/bundles";
    public const string RunRewardPathPrefix = "/v1/rewards/runs/";

    public static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return "nonce-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string FormatTimestamp(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");

    public static string SigningMaterial(
        string installationId,
        string uploadTokenId,
        string timestamp,
        string nonce,
        string method,
        string path,
        string manifestSha256,
        string bundleSha256)
        => string.Join("\n", new[]
        {
            installationId,
            uploadTokenId,
            timestamp,
            nonce,
            method,
            path,
            manifestSha256,
            bundleSha256
        });

    public static string RewardStatusSigningMaterial(
        string installationId,
        string uploadTokenId,
        string timestamp,
        string nonce,
        string method,
        string path)
        => string.Join("\n", new[]
        {
            installationId,
            uploadTokenId,
            timestamp,
            nonce,
            method,
            path
        });

    public static string SignatureHex(string uploadSecret, string signingMaterial)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(uploadSecret));
        byte[] signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingMaterial));
        return Convert.ToHexString(signature).ToLowerInvariant();
    }
}
