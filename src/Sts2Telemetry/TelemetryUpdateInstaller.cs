using System.Diagnostics;
using System.IO.Compression;

namespace Sts2Telemetry;

internal static class TelemetryUpdateInstaller
{
    private static readonly string[] RequiredPackageFiles =
    {
        "Sts2Telemetry.dll",
        "Sts2Telemetry.json"
    };

    private static readonly string[] OptionalPackageFiles =
    {
        "Sts2Telemetry.pdb"
    };

    public static TelemetryUpdateInstallResult Apply(TelemetryUpdateInstallRequest request)
    {
        try
        {
            WaitForGameExit(request);
            Install(request);
            var result = Result(request, "installed");
            TelemetryUpdateJsonFile.Write(request.ResultPath, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = Result(request, "failed", ErrorCode(ex), ex.Message);
            TryWriteResult(request.ResultPath, result);
            return result;
        }
    }

    internal static void Install(TelemetryUpdateInstallRequest request)
    {
        ValidateRequest(request);
        string packageSha = TelemetryUpdateHash.Sha256HexFile(request.PackagePath);
        if (!string.Equals(packageSha, request.PackageSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException("update package sha256 does not match request");

        string updateRoot = TelemetryUpdateSettings.UpdateDirectory(request.TelemetryBaseDirectory);
        string stagingDirectory = Path.Combine(
            updateRoot,
            TelemetryUpdateSettings.StagingDirectoryName,
            request.RequestId);
        string backupDirectory = Path.Combine(
            updateRoot,
            TelemetryUpdateSettings.BackupsDirectoryName,
            request.RequestId);

        ResetDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);
        ExtractPackage(request.PackagePath, stagingDirectory);
        ValidatePackage(stagingDirectory);

        var replaced = new List<string>();
        try
        {
            foreach (string fileName in RequiredPackageFiles.Concat(OptionalPackageFiles))
            {
                string source = Path.Combine(stagingDirectory, fileName);
                if (!File.Exists(source))
                    continue;
                string target = Path.Combine(request.TargetModDirectory, fileName);
                string backup = Path.Combine(backupDirectory, fileName);
                if (File.Exists(target))
                    File.Copy(target, backup, overwrite: true);
                ReplaceFile(source, target, request.RequestId, fileName);
                replaced.Add(fileName);
            }
        }
        catch
        {
            RestoreBackups(request.TargetModDirectory, backupDirectory, replaced);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void WaitForGameExit(TelemetryUpdateInstallRequest request)
    {
        if (request.GameProcessId is not { } processId || processId <= 0)
            return;

        try
        {
            using Process process = Process.GetProcessById(processId);
            int timeoutMilliseconds = Math.Max(1, request.WaitForProcessExitTimeoutSeconds) * 1000;
            if (!process.WaitForExit(timeoutMilliseconds))
                throw new TimeoutException("game process did not exit before update helper timeout");
        }
        catch (ArgumentException)
        {
            // The process has already exited.
        }
        catch (InvalidOperationException)
        {
            // The process has already exited or cannot be waited on.
        }
    }

    private static void ValidateRequest(TelemetryUpdateInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new InvalidOperationException("update request id is required");
        if (string.IsNullOrWhiteSpace(request.TargetVersion))
            throw new InvalidOperationException("update target version is required");
        if (string.IsNullOrWhiteSpace(request.PackagePath) || !File.Exists(request.PackagePath))
            throw new FileNotFoundException("update package is missing", request.PackagePath);
        if (string.IsNullOrWhiteSpace(request.PackageSha256))
            throw new InvalidOperationException("update package sha256 is required");
        if (string.IsNullOrWhiteSpace(request.TelemetryBaseDirectory))
            throw new InvalidOperationException("telemetry base directory is required");
        if (!IsExpectedModDirectory(request.TargetModDirectory))
            throw new InvalidOperationException("target mod directory must be a mods/telemetry directory");
        Directory.CreateDirectory(request.TargetModDirectory);
    }

    internal static bool IsExpectedModDirectory(string targetModDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetModDirectory))
            return false;

        try
        {
            var target = new DirectoryInfo(Path.GetFullPath(targetModDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)));
            return string.Equals(target.Name, "telemetry", StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Parent?.Name, "mods", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractPackage(string packagePath, string stagingDirectory)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.Contains("../", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidOperationException("update package contains unsafe entry paths");
            if (normalized.Contains('/', StringComparison.Ordinal))
                throw new InvalidOperationException("update package files must be at the package root");
            if (!RequiredPackageFiles.Concat(OptionalPackageFiles).Contains(entry.Name, StringComparer.Ordinal))
                throw new InvalidOperationException($"update package contains unexpected file {entry.Name}");

            string destination = Path.Combine(stagingDirectory, entry.Name);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void ValidatePackage(string stagingDirectory)
    {
        foreach (string fileName in RequiredPackageFiles)
        {
            string path = Path.Combine(stagingDirectory, fileName);
            if (!File.Exists(path))
                throw new InvalidOperationException($"update package is missing {fileName}");
            if (new FileInfo(path).Length <= 0)
                throw new InvalidOperationException($"update package file {fileName} is empty");
        }
    }

    private static void ReplaceFile(string source, string target, string requestId, string fileName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tempPath = Path.Combine(Path.GetDirectoryName(target)!, $".sts2-update-{requestId}-{fileName}.tmp");
        if (File.Exists(tempPath))
            File.Delete(tempPath);
        File.Copy(source, tempPath, overwrite: false);
        if (File.Exists(target))
            File.Replace(tempPath, target, null);
        else
            File.Move(tempPath, target);
    }

    private static void RestoreBackups(string targetModDirectory, string backupDirectory, IReadOnlyList<string> replaced)
    {
        foreach (string fileName in replaced)
        {
            string backup = Path.Combine(backupDirectory, fileName);
            if (!File.Exists(backup))
                continue;

            string target = Path.Combine(targetModDirectory, fileName);
            File.Copy(backup, target, overwrite: true);
        }
    }

    private static void ResetDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }

    private static TelemetryUpdateInstallResult Result(
        TelemetryUpdateInstallRequest request,
        string state,
        string? errorCode = null,
        string? errorMessage = null)
        => new()
        {
            RequestId = request.RequestId,
            State = state,
            CurrentVersion = request.CurrentVersion,
            TargetVersion = request.TargetVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };

    private static void TryWriteResult(string path, TelemetryUpdateInstallResult result)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                TelemetryUpdateJsonFile.Write(path, result);
        }
        catch
        {
        }
    }

    private static string ErrorCode(Exception ex)
        => ex switch
        {
            TimeoutException => "game_exit_timeout",
            FileNotFoundException => "update_file_missing",
            InvalidDataException => "invalid_update_package",
            _ => "update_install_failed"
        };
}
