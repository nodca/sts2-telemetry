using System.Runtime.InteropServices;

namespace Sts2Telemetry;

internal sealed record TelemetryModReleaseManifest
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.mod_release.v1";
    public string LatestVersion { get; init; } = "";
    public string? MinSupportedVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public bool RequiresConfirmation { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public TelemetryModReleaseArtifact[] Artifacts { get; init; } = Array.Empty<TelemetryModReleaseArtifact>();
}

internal sealed record TelemetryModReleaseArtifact
{
    public string Platform { get; init; } = "";
    public string Kind { get; init; } = "mod_package";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? FileName { get; init; }
}

internal sealed record TelemetryUpdatePlan
{
    public string State { get; init; } = TelemetryUpdateStates.Current;
    public string Reason { get; init; } = "current";
    public string CurrentVersion { get; init; } = "";
    public string TargetVersion { get; init; } = "";
    public string Platform { get; init; } = "";
    public string UpdateKind { get; init; } = TelemetryUpdateKinds.None;
    public string Authorization { get; init; } = TelemetryUpdateAuthorization.None;
    public TelemetryModReleaseArtifact? Artifact { get; init; }
    public bool ShouldAutoDownloadAndInstall
        => State == TelemetryUpdateStates.AutoInstallReady && Artifact != null;
}

internal static class TelemetryUpdateStates
{
    public const string Current = "current";
    public const string Disabled = "disabled";
    public const string Unavailable = "unavailable";
    public const string UpdateAvailable = "update_available";
    public const string AutoInstallReady = "auto_install_ready";
    public const string Downloading = "downloading";
    public const string Staged = "staged";
    public const string InstallRequested = "install_requested";
    public const string HelperMissing = "helper_missing";
    public const string Failed = "failed";
}

internal static class TelemetryUpdateKinds
{
    public const string None = "none";
    public const string Patch = "patch";
    public const string Minor = "minor";
    public const string Major = "major";
    public const string Prerelease = "prerelease";
}

internal static class TelemetryUpdateAuthorization
{
    public const string None = "none";
    public const string AutomaticPatch = "automatic_patch";
    public const string RequiresUserConfirmation = "requires_user_confirmation";
}

internal sealed record TelemetryUpdateStatus
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.update_status.v1";
    public bool Enabled { get; init; } = true;
    public string State { get; init; } = TelemetryUpdateStates.Current;
    public string Reason { get; init; } = "current";
    public string CurrentVersion { get; init; } = "";
    public string? TargetVersion { get; init; }
    public string? UpdateKind { get; init; }
    public string? Authorization { get; init; }
    public string? Platform { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CheckedAtUtc { get; init; }
    public DateTimeOffset? DownloadedAtUtc { get; init; }
    public DateTimeOffset? InstallRequestedAtUtc { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastErrorMessage { get; init; }
}

internal sealed record TelemetryUpdateInstallRequest
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.update_install_request.v1";
    public string RequestId { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string TargetVersion { get; init; } = "";
    public string PackagePath { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public string TargetModDirectory { get; init; } = "";
    public string TelemetryBaseDirectory { get; init; } = "";
    public int? GameProcessId { get; init; }
    public int WaitForProcessExitTimeoutSeconds { get; init; } = TelemetryUpdateSettings.DefaultProcessExitTimeoutSeconds;
    public string ResultPath { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record TelemetryUpdateInstallResult
{
    public string SchemaVersion { get; init; } = "sts2.telemetry.update_install_result.v1";
    public string RequestId { get; init; } = "";
    public string State { get; init; } = "failed";
    public string CurrentVersion { get; init; } = "";
    public string TargetVersion { get; init; } = "";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

internal static class TelemetryUpdatePlanner
{
    public static TelemetryUpdatePlan Plan(
        string currentVersion,
        TelemetryModReleaseManifest manifest,
        string platform)
    {
        currentVersion = currentVersion.Trim();
        platform = platform.Trim();
        string targetVersion = manifest.LatestVersion.Trim();
        if (!TelemetryModVersion.TryParse(currentVersion, out TelemetryModVersion current))
        {
            return Unavailable(currentVersion, targetVersion, platform, "invalid_current_version");
        }

        if (!TelemetryModVersion.TryParse(targetVersion, out TelemetryModVersion target))
        {
            return Unavailable(currentVersion, targetVersion, platform, "invalid_release_version");
        }

        int comparison = target.CompareTo(current);
        if (comparison <= 0)
        {
            return new TelemetryUpdatePlan
            {
                State = TelemetryUpdateStates.Current,
                Reason = "current",
                CurrentVersion = currentVersion,
                TargetVersion = targetVersion,
                Platform = platform,
                UpdateKind = TelemetryUpdateKinds.None,
                Authorization = TelemetryUpdateAuthorization.None
            };
        }

        TelemetryModReleaseArtifact? artifact = manifest.Artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Kind?.Trim(), "mod_package", StringComparison.OrdinalIgnoreCase));
        string updateKind = Kind(current, target);
        string authorization = updateKind == TelemetryUpdateKinds.Patch && !manifest.RequiresConfirmation
            ? TelemetryUpdateAuthorization.AutomaticPatch
            : TelemetryUpdateAuthorization.RequiresUserConfirmation;
        if (artifact == null)
        {
            return new TelemetryUpdatePlan
            {
                State = TelemetryUpdateStates.UpdateAvailable,
                Reason = "no_platform_artifact",
                CurrentVersion = currentVersion,
                TargetVersion = targetVersion,
                Platform = platform,
                UpdateKind = updateKind,
                Authorization = authorization
            };
        }

        return new TelemetryUpdatePlan
        {
            State = authorization == TelemetryUpdateAuthorization.AutomaticPatch
                ? TelemetryUpdateStates.AutoInstallReady
                : TelemetryUpdateStates.UpdateAvailable,
            Reason = authorization == TelemetryUpdateAuthorization.AutomaticPatch
                ? "patch_update_auto_authorized"
                : "user_confirmation_required",
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            Platform = platform,
            UpdateKind = updateKind,
            Authorization = authorization,
            Artifact = artifact
        };
    }

    public static string CurrentPlatform()
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "osx"
                    : "unknown";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }

    private static TelemetryUpdatePlan Unavailable(
        string currentVersion,
        string targetVersion,
        string platform,
        string reason)
        => new()
        {
            State = TelemetryUpdateStates.Unavailable,
            Reason = reason,
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            Platform = platform,
            Authorization = TelemetryUpdateAuthorization.None
        };

    private static string Kind(TelemetryModVersion current, TelemetryModVersion target)
    {
        if (!string.Equals(current.Prerelease, target.Prerelease, StringComparison.Ordinal)
            && current.Major == target.Major
            && current.Minor == target.Minor
            && current.Patch == target.Patch)
        {
            return TelemetryUpdateKinds.Prerelease;
        }

        if (target.Major != current.Major)
            return TelemetryUpdateKinds.Major;
        if (target.Minor != current.Minor)
            return TelemetryUpdateKinds.Minor;
        if (target.Patch != current.Patch)
            return TelemetryUpdateKinds.Patch;
        return TelemetryUpdateKinds.None;
    }
}

internal readonly record struct TelemetryModVersion(
    int Major,
    int Minor,
    int Patch,
    string Prerelease) : IComparable<TelemetryModVersion>
{
    public static bool TryParse(string value, out TelemetryModVersion version)
    {
        version = default;
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        int buildIndex = value.IndexOf('+', StringComparison.Ordinal);
        if (buildIndex >= 0)
            value = value[..buildIndex];

        string prerelease = "";
        int prereleaseIndex = value.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseIndex >= 0)
        {
            prerelease = value[(prereleaseIndex + 1)..];
            value = value[..prereleaseIndex];
        }

        string[] parts = value.Split('.');
        if (parts.Length is < 1 or > 3)
            return false;

        if (!TryParsePart(parts[0], out int major))
            return false;
        int minor = 0;
        int patch = 0;
        if (parts.Length >= 2 && !TryParsePart(parts[1], out minor))
            return false;
        if (parts.Length >= 3 && !TryParsePart(parts[2], out patch))
            return false;

        version = new TelemetryModVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(TelemetryModVersion other)
    {
        int core = Major.CompareTo(other.Major);
        if (core != 0)
            return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0)
            return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0)
            return core;
        if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease))
            return 1;
        if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease))
            return -1;
        return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
    }

    private static bool TryParsePart(string value, out int part)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out part)
            && part >= 0;
}
