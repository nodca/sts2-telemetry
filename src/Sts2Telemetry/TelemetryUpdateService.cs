using System.Diagnostics;
using Godot;

namespace Sts2Telemetry;

internal sealed class TelemetryUpdateService : IDisposable
{
    private readonly string _telemetryBaseDirectory;
    private readonly string _targetModDirectory;
    private readonly System.Net.Http.HttpClient _httpClient;
    private readonly TelemetryUpdateClient _client;
    private readonly TelemetryUpdateStore _store;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _syncRequested = new(false);
    private readonly Thread _thread;
    private bool _disposed;
    private bool _started;

    internal TelemetryUpdateService(
        string telemetryBaseDirectory,
        string targetModDirectory,
        System.Net.Http.HttpClient httpClient)
    {
        _telemetryBaseDirectory = telemetryBaseDirectory;
        _targetModDirectory = targetModDirectory;
        _httpClient = httpClient;
        _client = new TelemetryUpdateClient(httpClient);
        _store = new TelemetryUpdateStore(telemetryBaseDirectory);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "STS2_Telemetry_Update"
        };
    }

    public static TelemetryUpdateService Start(string telemetryBaseDirectory, string targetModDirectory)
    {
        var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TelemetryUpdateSettings.DefaultHttpTimeoutSeconds)
        };
        var service = new TelemetryUpdateService(telemetryBaseDirectory, targetModDirectory, httpClient);
        service.WriteInitialStatus();
        service._thread.Start();
        service._started = true;
        return service;
    }

    public void RequestSync()
        => _syncRequested.Set();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
        _syncRequested.Set();
        if (_started && !_thread.Join(TimeSpan.FromSeconds(3)))
            _thread.Join(TimeSpan.FromSeconds(1));
        _httpClient.Dispose();
        _stop.Dispose();
        _syncRequested.Dispose();
    }

    internal Task RunUpdateCycleForTests(TelemetryUpdateSettings settings, CancellationToken cancellationToken)
        => RunUpdateCycle(settings, cancellationToken);

    private void WriteInitialStatus()
    {
        try
        {
            TelemetryUpdateSettings settings = TelemetryUpdateSettings.LoadOrCreate(_telemetryBaseDirectory);
            TelemetryUpdateInstallResult? result = _store.ReadHelperResult();
            if (result?.State == "installed")
            {
                _store.WriteStatus(new TelemetryUpdateStatus
                {
                    Enabled = settings.Enabled,
                    State = TelemetryUpdateStates.Current,
                    Reason = string.Equals(result.TargetVersion, Sts2TelemetryMod.Version, StringComparison.Ordinal)
                        ? "installed_version_confirmed"
                        : "installed_result_loaded_but_version_differs",
                    CurrentVersion = Sts2TelemetryMod.Version,
                    TargetVersion = result.TargetVersion,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
                return;
            }

            _store.WriteStatus(new TelemetryUpdateStatus
            {
                Enabled = settings.Enabled,
                State = settings.Enabled ? TelemetryUpdateStates.Current : TelemetryUpdateStates.Disabled,
                Reason = settings.Enabled ? "initialized" : "disabled",
                CurrentVersion = Sts2TelemetryMod.Version,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] update status initialization failed: {ex.Message}");
        }
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TelemetryUpdateSettings settings = TelemetryUpdateSettings.LoadOrCreate(_telemetryBaseDirectory);
            try
            {
                if (!settings.Enabled)
                {
                    WriteStatus(settings, TelemetryUpdateStates.Disabled, "disabled");
                }
                else
                {
                    RunUpdateCycle(settings, _stop.Token).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                WriteStatus(settings, TelemetryUpdateStates.Failed, "update_check_failed", errorCode: UpdateErrorCode(ex), errorMessage: ex.Message);
            }

            int waitSeconds = Math.Max(60, settings.ScanIntervalSeconds);
            _syncRequested.WaitOne(TimeSpan.FromSeconds(waitSeconds));
        }
    }

    private async Task RunUpdateCycle(TelemetryUpdateSettings settings, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.ReleaseManifestUrl, UriKind.Absolute, out Uri? manifestUri)
            || manifestUri.Scheme is not ("http" or "https"))
        {
            WriteStatus(settings, TelemetryUpdateStates.Failed, "invalid_manifest_url", errorCode: "invalid_manifest_url");
            return;
        }

        TelemetryModReleaseManifest manifest = await _client.GetManifestAsync(manifestUri, cancellationToken)
            .ConfigureAwait(false);
        _store.WriteManifest(manifest);

        string platform = TelemetryUpdatePlanner.CurrentPlatform();
        TelemetryUpdatePlan plan = TelemetryUpdatePlanner.Plan(Sts2TelemetryMod.Version, manifest, platform);
        if (!plan.ShouldAutoDownloadAndInstall)
        {
            WriteStatus(settings, plan.State, plan.Reason, manifest, plan);
            return;
        }

        TelemetryModReleaseArtifact artifact = plan.Artifact!;
        string packagePath = _store.PackagePath(artifact, plan.TargetVersion);
        if (!File.Exists(packagePath)
            || !string.Equals(
                TelemetryUpdateHash.Sha256HexFile(packagePath),
                artifact.Sha256.Trim().ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            WriteStatus(settings, TelemetryUpdateStates.Downloading, "downloading_update_package", manifest, plan);
            await _client.DownloadArtifactAsync(artifact, packagePath, cancellationToken).ConfigureAwait(false);
        }

        TelemetryUpdateInstallRequest request = BuildInstallRequest(settings, packagePath, artifact.Sha256, plan);
        _store.WriteInstallRequest(request);
        WriteStatus(settings, TelemetryUpdateStates.Staged, "update_package_staged", manifest, plan, downloadedAtUtc: DateTimeOffset.UtcNow);

        string helperPath = HelperPath(_targetModDirectory);
        if (!File.Exists(helperPath))
        {
            WriteStatus(settings, TelemetryUpdateStates.HelperMissing, "helper_missing", manifest, plan, downloadedAtUtc: DateTimeOffset.UtcNow);
            return;
        }

        LaunchHelper(helperPath, _store.InstallRequestPath);
        WriteStatus(
            settings,
            TelemetryUpdateStates.InstallRequested,
            "helper_launched_waiting_for_game_exit",
            manifest,
            plan,
            downloadedAtUtc: DateTimeOffset.UtcNow,
            installRequestedAtUtc: DateTimeOffset.UtcNow);
    }

    private TelemetryUpdateInstallRequest BuildInstallRequest(
        TelemetryUpdateSettings settings,
        string packagePath,
        string packageSha256,
        TelemetryUpdatePlan plan)
        => new()
        {
            RequestId = $"update-{plan.TargetVersion}-{Guid.NewGuid():N}",
            CurrentVersion = plan.CurrentVersion,
            TargetVersion = plan.TargetVersion,
            PackagePath = packagePath,
            PackageSha256 = packageSha256.Trim().ToLowerInvariant(),
            TargetModDirectory = _targetModDirectory,
            TelemetryBaseDirectory = _telemetryBaseDirectory,
            GameProcessId = System.Environment.ProcessId,
            WaitForProcessExitTimeoutSeconds = settings.ProcessExitTimeoutSeconds,
            ResultPath = _store.HelperResultPath,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    private void WriteStatus(
        TelemetryUpdateSettings settings,
        string state,
        string reason,
        TelemetryModReleaseManifest? manifest = null,
        TelemetryUpdatePlan? plan = null,
        DateTimeOffset? downloadedAtUtc = null,
        DateTimeOffset? installRequestedAtUtc = null,
        string? errorCode = null,
        string? errorMessage = null)
        => _store.WriteStatus(new TelemetryUpdateStatus
        {
            Enabled = settings.Enabled,
            State = state,
            Reason = reason,
            CurrentVersion = Sts2TelemetryMod.Version,
            TargetVersion = plan?.TargetVersion,
            UpdateKind = plan?.UpdateKind,
            Authorization = plan?.Authorization,
            Platform = plan?.Platform ?? TelemetryUpdatePlanner.CurrentPlatform(),
            ReleaseNotes = manifest?.ReleaseNotes,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            DownloadedAtUtc = downloadedAtUtc,
            InstallRequestedAtUtc = installRequestedAtUtc,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage
        });

    private static string HelperPath(string targetModDirectory)
        => Path.Combine(
            targetModDirectory,
            OperatingSystem.IsWindows()
                ? TelemetryUpdateSettings.HelperExecutableBaseName + ".exe"
                : TelemetryUpdateSettings.HelperExecutableBaseName);

    private static void LaunchHelper(string helperPath, string requestPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        Process.Start(startInfo)?.Dispose();
    }

    private static string UpdateErrorCode(Exception ex)
        => ex is TelemetryUploadHttpException http ? http.Code : "update_check_failed";
}
