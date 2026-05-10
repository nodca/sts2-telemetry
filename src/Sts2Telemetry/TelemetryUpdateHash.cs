using System.Security.Cryptography;

namespace Sts2Telemetry;

internal static class TelemetryUpdateHash
{
    public static string Sha256HexFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
