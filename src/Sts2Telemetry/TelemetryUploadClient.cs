using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

internal sealed class TelemetryUploadClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _now;

    public TelemetryUploadClient(HttpClient httpClient, Func<DateTimeOffset>? now = null)
    {
        _httpClient = httpClient;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<TelemetryUploadPolicy> GetPolicyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(new Uri(endpoint, "/v1/policy"), cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);
        return JsonSerializer.Deserialize<TelemetryUploadPolicy>(body, TelemetryJson.Options)
            ?? TelemetryUploadPolicy.LocalDefault;
    }

    public async Task<(TelemetryUploadToken Token, TelemetryUploadPolicy Policy)> RegisterAsync(
        Uri endpoint,
        string installationId,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            installation_id = installationId,
            mod_id = Sts2TelemetryMod.ModId,
            mod_version = Sts2TelemetryMod.Version,
            telemetry_schema_version = TelemetryRecorder.SchemaVersion,
            client_capabilities = new
            {
                compression = new[] { "gzip" },
                bundle_upload = "multipart.v1"
            },
            consent = new
            {
                upload_default = "enabled",
                notice_version = TelemetryUploadSettings.NoticeVersionValue
            }
        };
        string json = JsonSerializer.Serialize(request, TelemetryJson.Options);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(
                new Uri(endpoint, "/v1/installations/register"),
                content,
                cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);

        RegistrationResponse registration = JsonSerializer.Deserialize<RegistrationResponse>(body, TelemetryJson.Options)
            ?? throw new TelemetryUploadHttpException("invalid_registration_response", "registration response was empty", null);
        var token = new TelemetryUploadToken
        {
            InstallationId = registration.InstallationId,
            UploadTokenId = registration.UploadTokenId,
            UploadSecret = registration.UploadSecret,
            CreatedAtUtc = _now()
        };
        return (token, registration.Policy ?? TelemetryUploadPolicy.LocalDefault);
    }

    public async Task<TelemetryUploadPolicy> UploadAsync(
        Uri endpoint,
        TelemetryUploadToken token,
        TelemetryUploadQueueItem item,
        CancellationToken cancellationToken)
    {
        byte[] manifestBytes = item.ManifestBytes();
        string manifestSha = TelemetryUploadCrypto.Sha256Hex(manifestBytes);
        using FileStream bundle = item.OpenBundle();
        string bundleSha = TelemetryUploadCrypto.Sha256Hex(bundle);
        bundle.Position = 0;

        string timestamp = TelemetryUploadCrypto.FormatTimestamp(_now());
        string nonce = TelemetryUploadCrypto.NewNonce();
        string signingMaterial = TelemetryUploadCrypto.SigningMaterial(
            token.InstallationId,
            token.UploadTokenId,
            timestamp,
            nonce,
            "POST",
            TelemetryUploadCrypto.BundleUploadPath,
            manifestSha,
            bundleSha);
        string signature = TelemetryUploadCrypto.SignatureHex(token.UploadSecret, signingMaterial);

        using var content = new MultipartFormDataContent();
        var manifestContent = new ByteArrayContent(manifestBytes);
        manifestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(manifestContent, "manifest", "manifest.json");

        var bundleContent = new StreamContent(bundle);
        bundleContent.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(bundleContent, "bundle", $"{item.Status.BundleId}.jsonl.gz");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, TelemetryUploadCrypto.BundleUploadPath))
        {
            Content = content
        };
        request.Headers.Add("X-STS2-Installation-ID", token.InstallationId);
        request.Headers.Add("X-STS2-Upload-Token-ID", token.UploadTokenId);
        request.Headers.Add("X-STS2-Timestamp", timestamp);
        request.Headers.Add("X-STS2-Nonce", nonce);
        request.Headers.Add("X-STS2-Manifest-SHA256", manifestSha);
        request.Headers.Add("X-STS2-Bundle-SHA256", bundleSha);
        request.Headers.Add("X-STS2-Signature", signature);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);

        UploadResponse upload = JsonSerializer.Deserialize<UploadResponse>(body, TelemetryJson.Options)
            ?? throw new TelemetryUploadHttpException("invalid_upload_response", "upload response was empty", null);
        return upload.Policy ?? TelemetryUploadPolicy.LocalDefault;
    }

    public async Task<TelemetryUploadRewardStatus> GetRunRewardAsync(
        Uri endpoint,
        TelemetryUploadToken token,
        string runId,
        CancellationToken cancellationToken)
    {
        runId = runId.Trim();
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("run id is required", nameof(runId));

        string path = TelemetryUploadCrypto.RunRewardPathPrefix + Uri.EscapeDataString(runId);
        string timestamp = TelemetryUploadCrypto.FormatTimestamp(_now());
        string nonce = TelemetryUploadCrypto.NewNonce();
        string signingMaterial = TelemetryUploadCrypto.RewardStatusSigningMaterial(
            token.InstallationId,
            token.UploadTokenId,
            timestamp,
            nonce,
            "GET",
            path);
        string signature = TelemetryUploadCrypto.SignatureHex(token.UploadSecret, signingMaterial);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Add("X-STS2-Installation-ID", token.InstallationId);
        request.Headers.Add("X-STS2-Upload-Token-ID", token.UploadTokenId);
        request.Headers.Add("X-STS2-Timestamp", timestamp);
        request.Headers.Add("X-STS2-Nonce", nonce);
        request.Headers.Add("X-STS2-Signature", signature);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);

        return JsonSerializer.Deserialize<TelemetryUploadRewardStatus>(body, TelemetryJson.Options)
            ?? throw new TelemetryUploadHttpException("invalid_reward_response", "reward response was empty", null);
    }

    private sealed record RegistrationResponse
    {
        public string InstallationId { get; init; } = "";
        public string UploadTokenId { get; init; } = "";
        public string UploadSecret { get; init; } = "";
        public TelemetryUploadPolicy? Policy { get; init; }
    }

    private sealed record UploadResponse
    {
        public string Status { get; init; } = "";
        public string ValidationStatus { get; init; } = "";
        public bool Idempotent { get; init; }
        public TelemetryUploadPolicy? Policy { get; init; }
    }
}

internal sealed class TelemetryUploadHttpException : Exception
{
    public TelemetryUploadHttpException(string code, string message, HttpStatusCode? statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }

    public static TelemetryUploadHttpException FromResponse(HttpStatusCode statusCode, string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement error = document.RootElement.GetProperty("error");
            string code = JsonElementReader.GetString(error, "code") ?? $"http_{(int)statusCode}";
            string message = JsonElementReader.GetString(error, "message") ?? "upload request failed";
            return new TelemetryUploadHttpException(code, message, statusCode);
        }
        catch
        {
            return new TelemetryUploadHttpException($"http_{(int)statusCode}", "upload request failed", statusCode);
        }
    }
}
