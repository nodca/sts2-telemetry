using System.Text.Json;

namespace Sts2Telemetry;

internal sealed class TelemetryUpdateClient
{
    private readonly HttpClient _httpClient;

    public TelemetryUpdateClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<TelemetryModReleaseManifest> GetManifestAsync(Uri manifestUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(manifestUri, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);

        TelemetryModReleaseManifest manifest = JsonSerializer.Deserialize<TelemetryModReleaseManifest>(
                body,
                TelemetryJson.Options)
            ?? throw new TelemetryUploadHttpException("invalid_update_manifest", "update manifest was empty", null);
        if (!string.Equals(manifest.SchemaVersion, "sts2.telemetry.mod_release.v1", StringComparison.Ordinal))
            throw new TelemetryUploadHttpException(
                "unsupported_update_manifest_schema",
                "update manifest schema is not supported",
                null);
        return manifest;
    }

    public async Task DownloadArtifactAsync(
        TelemetryModReleaseArtifact artifact,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new TelemetryUploadHttpException("invalid_update_artifact_url", "update artifact URL is invalid", null);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            string body = "";
            if (!response.IsSuccessStatusCode)
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw TelemetryUploadHttpException.FromResponse(response.StatusCode, body);
            }

            await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await remote.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            string actualSha = TelemetryUpdateHash.Sha256HexFile(tempPath);
            string expectedSha = artifact.Sha256.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(expectedSha) || !string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
                throw new TelemetryUploadHttpException(
                    "update_artifact_sha256_mismatch",
                    "downloaded update artifact did not match the release manifest hash",
                    null);

            if (File.Exists(destinationPath))
                File.Replace(tempPath, destinationPath, null);
            else
                File.Move(tempPath, destinationPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }
}
