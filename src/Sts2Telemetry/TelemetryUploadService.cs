using Godot;

namespace Sts2Telemetry;

internal sealed class TelemetryUploadService : IDisposable
{
    private readonly string _telemetryBaseDirectory;
    private readonly string _installationId;
    private readonly TelemetryUploadQueue _queue;
    private readonly TelemetryUploadClient _client;
    private readonly System.Net.Http.HttpClient _httpClient;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _syncRequested = new(false);
    private readonly Thread _thread;
    private TelemetryUploadPolicy _policy = TelemetryUploadPolicy.LocalDefault;
    private bool _disposed;
    private bool _started;

    internal TelemetryUploadService(string telemetryBaseDirectory, string installationId, System.Net.Http.HttpClient httpClient)
    {
        _telemetryBaseDirectory = telemetryBaseDirectory;
        _installationId = installationId;
        _queue = new TelemetryUploadQueue(telemetryBaseDirectory, installationId);
        _httpClient = httpClient;
        _client = new TelemetryUploadClient(httpClient);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "STS2_Telemetry_Upload"
        };
    }

    public static TelemetryUploadService Start(string telemetryBaseDirectory, string installationId)
    {
        var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var service = new TelemetryUploadService(telemetryBaseDirectory, installationId, httpClient);
        service.WriteInitialNoticeAndStatus();
        service._thread.Start();
        service._started = true;
        return service;
    }

    public void RequestSync()
        => _syncRequested.Set();

    internal Task RunSyncCycleForTests(TelemetryUploadSettings settings, bool forcePackaging, CancellationToken cancellationToken)
        => RunSyncCycle(settings, forcePackaging, cancellationToken);

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

    private void WriteInitialNoticeAndStatus()
    {
        try
        {
            TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(_telemetryBaseDirectory);
            bool firstNotice = TelemetryUploadNotice.Ensure(_telemetryBaseDirectory, settings);
            _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "initialized"));
            if (firstNotice)
            {
                GD.Print(
                    "[STS2 Telemetry] upload is enabled by default; create upload/disable_upload.request under the telemetry data directory or set upload/settings.json enabled=false to disable. Status: upload/status.json");
                settings.MarkNoticeAcknowledged(_telemetryBaseDirectory);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] upload notice/status initialization failed: {ex.Message}");
        }
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TelemetryUploadSettings settings = TelemetryUploadSettings.LoadOrCreate(_telemetryBaseDirectory)
                .ApplyDisableRequest(_telemetryBaseDirectory);
            bool manualSync = _queue.ConsumeManualSyncRequest();
            try
            {
                if (!settings.Enabled)
                {
                    _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "disabled"));
                }
                else
                {
                    RunSyncCycle(settings, manualSync, _stop.Token).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                string code = ex is TelemetryUploadHttpException http ? http.Code : "upload_sync_failed";
                _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "failed", code, ex.Message));
                GD.PrintErr($"[STS2 Telemetry] background upload sync failed ({code}): {ex.Message}");
            }

            int waitSeconds = Math.Max(5, settings.ScanIntervalSeconds);
            _syncRequested.WaitOne(TimeSpan.FromSeconds(waitSeconds));
        }
    }

    private async Task RunSyncCycle(TelemetryUploadSettings settings, bool forcePackaging, CancellationToken cancellationToken)
    {
        Uri endpoint = BuildEndpoint(settings);
        try
        {
            _policy = await _client.GetPolicyAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string code = ex is TelemetryUploadHttpException http ? http.Code : "policy_fetch_failed";
            _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "policy_failed", code, ex.Message));
            return;
        }

        if (_policy.UploadDisabled)
        {
            _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "server_upload_disabled"));
            return;
        }

        TelemetryUploadToken? token = TelemetryUploadTokenStore.Load(_telemetryBaseDirectory, _installationId);
        if (token == null)
        {
            try
            {
                token = await RegisterAndSaveToken(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string code = ex is TelemetryUploadHttpException http ? http.Code : "registration_failed";
                _queue.WriteSummary(_queue.BuildSummary(settings, _policy, false, "registration_failed", code, ex.Message));
                return;
            }
        }

        _queue.PackagePendingSources(settings, _policy, forcePackaging);
        foreach (TelemetryUploadQueueItem item in _queue.Items().Where(item => item.Status.IsUploadable(DateTimeOffset.UtcNow)))
        {
            if (!File.Exists(item.BundlePath) || !File.Exists(item.ManifestPath))
                continue;

            try
            {
                await UploadAndMarkItem(endpoint, token, item, cancellationToken).ConfigureAwait(false);
            }
            catch (TelemetryUploadHttpException ex) when (IsRefreshableTokenFailure(ex))
            {
                try
                {
                    token = await RegisterAndSaveToken(endpoint, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception registrationEx)
                {
                    string code = UploadErrorCode(registrationEx, "registration_failed");
                    _queue.MarkFailed(item, code, registrationEx.Message, _policy.RetryAfterSeconds);
                    continue;
                }

                try
                {
                    await UploadAndMarkItem(endpoint, token, item, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception retryEx)
                {
                    MarkUploadFailed(item, retryEx);
                }
            }
            catch (Exception ex)
            {
                MarkUploadFailed(item, ex);
            }
        }

        await RefreshRewardStatuses(endpoint, token, cancellationToken).ConfigureAwait(false);
        _queue.PruneUploadedRunSources(settings);
        _queue.WriteSummary(_queue.BuildSummary(settings, _policy, HasToken(), "synced"));
    }

    private async Task<TelemetryUploadToken> RegisterAndSaveToken(Uri endpoint, CancellationToken cancellationToken)
    {
        (TelemetryUploadToken token, TelemetryUploadPolicy policy) =
            await _client.RegisterAsync(endpoint, _installationId, cancellationToken).ConfigureAwait(false);
        _policy = policy;
        TelemetryUploadTokenStore.Save(_telemetryBaseDirectory, token);
        return token;
    }

    private async Task UploadAndMarkItem(
        Uri endpoint,
        TelemetryUploadToken token,
        TelemetryUploadQueueItem item,
        CancellationToken cancellationToken)
    {
        _policy = await _client.UploadAsync(endpoint, token, item, cancellationToken).ConfigureAwait(false);
        _queue.MarkUploaded(item);
    }

    private void MarkUploadFailed(TelemetryUploadQueueItem item, Exception ex)
    {
        string code = UploadErrorCode(ex, "bundle_upload_failed");
        _queue.MarkFailed(item, code, ex.Message, _policy.RetryAfterSeconds);
    }

    private async Task RefreshRewardStatuses(Uri endpoint, TelemetryUploadToken token, CancellationToken cancellationToken)
    {
        foreach (TelemetryUploadQueueItem item in _queue.Items().Where(ShouldRefreshRewardStatus))
        {
            string runId = RewardStatusRunId(item.Status);
            try
            {
                TelemetryUploadRewardStatus reward = await _client.GetRunRewardAsync(endpoint, token, runId, cancellationToken)
                    .ConfigureAwait(false);
                item.SaveReward(reward);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Reward retrieval is best-effort on the existing upload sync path;
                // bundle upload state remains the source of truth for retry behavior.
            }
        }
    }

    private static bool ShouldRefreshRewardStatus(TelemetryUploadQueueItem item)
        => item.Status.State == "uploaded"
            && !string.IsNullOrWhiteSpace(RewardStatusRunId(item.Status))
            && item.Status.Reward?.IsTerminal != true;

    private static string RewardStatusRunId(TelemetryUploadQueueItemStatus status)
        => !string.IsNullOrWhiteSpace(status.LogicalRunId)
            ? status.LogicalRunId.Trim()
            : (status.RunId ?? "").Trim();

    private bool HasToken()
        => TelemetryUploadTokenStore.Load(_telemetryBaseDirectory, _installationId) != null;

    private static bool IsRefreshableTokenFailure(TelemetryUploadHttpException ex)
        => ex.Code is "unknown_upload_token" or "upload_token_inactive" or "invalid_signature";

    private static string UploadErrorCode(Exception ex, string fallback)
        => ex is TelemetryUploadHttpException http ? http.Code : fallback;

    private static Uri BuildEndpoint(TelemetryUploadSettings settings)
    {
        if (Uri.TryCreate(settings.EffectiveEndpointUrl, UriKind.Absolute, out Uri? endpoint)
            && endpoint.Scheme is "http" or "https")
            return endpoint;
        throw new InvalidOperationException("upload endpoint must be an absolute http or https URL");
    }
}
