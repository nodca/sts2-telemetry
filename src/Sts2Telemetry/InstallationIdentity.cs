using System.Security.Cryptography;

namespace Sts2Telemetry;

internal static class InstallationIdentity
{
    private const string FileName = "installation_id";

    public static string LoadOrCreate(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        string path = Path.Combine(baseDirectory, FileName);

        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16)
                    return existing;
            }
        }
        catch
        {
        }

        string created = CreateOpaqueId();
        try
        {
            File.WriteAllText(path, created);
        }
        catch
        {
        }

        return created;
    }

    private static string CreateOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return "local_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
