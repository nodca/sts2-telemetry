using System.Text.Json;

namespace Sts2Telemetry;

internal sealed class TelemetryUpdateStore
{
    private readonly string _telemetryBaseDirectory;

    public TelemetryUpdateStore(string telemetryBaseDirectory)
    {
        _telemetryBaseDirectory = telemetryBaseDirectory;
    }

    public string UpdateDirectory => TelemetryUpdateSettings.UpdateDirectory(_telemetryBaseDirectory);
    public string StatusPath => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.StatusFileName);
    public string ManifestPath => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.LatestManifestFileName);
    public string InstallRequestPath => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.InstallRequestFileName);
    public string HelperResultPath => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.HelperResultFileName);
    public string PackagesDirectory => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.PackagesDirectoryName);
    public string BackupsDirectory => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.BackupsDirectoryName);
    public string StagingDirectory => Path.Combine(UpdateDirectory, TelemetryUpdateSettings.StagingDirectoryName);

    public TelemetryUpdateStatus? ReadStatus()
        => Read<TelemetryUpdateStatus>(StatusPath);

    public void WriteStatus(TelemetryUpdateStatus status)
        => TelemetryUpdateJsonFile.Write(StatusPath, status);

    public TelemetryModReleaseManifest? ReadManifest()
        => Read<TelemetryModReleaseManifest>(ManifestPath);

    public void WriteManifest(TelemetryModReleaseManifest manifest)
        => TelemetryUpdateJsonFile.Write(ManifestPath, manifest);

    public TelemetryUpdateInstallRequest? ReadInstallRequest()
        => Read<TelemetryUpdateInstallRequest>(InstallRequestPath);

    public void WriteInstallRequest(TelemetryUpdateInstallRequest request)
        => TelemetryUpdateJsonFile.Write(InstallRequestPath, request);

    public TelemetryUpdateInstallResult? ReadHelperResult()
        => Read<TelemetryUpdateInstallResult>(HelperResultPath);

    public void WriteHelperResult(TelemetryUpdateInstallResult result)
        => TelemetryUpdateJsonFile.Write(HelperResultPath, result);

    public string PackagePath(TelemetryModReleaseArtifact artifact, string targetVersion)
    {
        string fileName = SafeFileName(artifact.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            try
            {
                fileName = SafeFileName(Path.GetFileName(new Uri(artifact.Url).LocalPath));
            }
            catch
            {
                fileName = "";
            }
        }

        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"sts2-telemetry-{targetVersion}-{artifact.Platform}.zip";
        return Path.Combine(PackagesDirectory, targetVersion, fileName);
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, TelemetryJson.Options);
        }
        catch
        {
            return default;
        }
    }

    private static string SafeFileName(string? value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "";

        char[] invalid = Path.GetInvalidFileNameChars();
        var buffer = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(buffer);
    }
}

internal static class TelemetryUpdateJsonFile
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
