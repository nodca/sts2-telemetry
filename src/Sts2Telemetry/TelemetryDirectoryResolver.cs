namespace Sts2Telemetry;

internal static class TelemetryDirectoryResolver
{
    public const string EnvironmentVariableName = "STS2_TELEMETRY_DIR";
    public const string TelemetryDirectoryName = "sts2-telemetry";
    private const string GameProcessName = "SlayTheSpire2";

    public static string ResolveForMod()
        => ResolveForMod(ReadGodotUserDataDirectory, ResolveFallbackUserDataRoot);

    internal static string ResolveForMod(
        Func<string?> readUserDataDirectory,
        Func<string> readFallbackUserDataRoot)
    {
        string overrideDirectory = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return NormalizeDirectory(overrideDirectory);

        string userDataDirectory = SafeReadDirectory(readUserDataDirectory);
        if (!string.IsNullOrWhiteSpace(userDataDirectory))
            return NormalizeDirectory(Path.Combine(userDataDirectory, TelemetryDirectoryName));

        return NormalizeDirectory(Path.Combine(readFallbackUserDataRoot(), "SlayTheSpire2", TelemetryDirectoryName));
    }

    private static string SafeReadDirectory(Func<string?> readDirectory)
    {
        try
        {
            return readDirectory()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string? ReadGodotUserDataDirectory()
    {
        if (!IsLikelyGodotGameProcess())
            return null;

        try
        {
            return Godot.OS.GetUserDataDir();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLikelyGodotGameProcess()
    {
        string processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        return string.Equals(processName, GameProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFallbackUserDataRoot()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return localAppData;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "share");

        return AppContext.BaseDirectory;
    }

    private static string NormalizeDirectory(string directory)
        => Path.GetFullPath(ExpandHome(directory));

    private static string ExpandHome(string directory)
    {
        if (directory == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (directory.StartsWith("~/", StringComparison.Ordinal)
            || directory.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || directory.StartsWith("~" + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, directory[2..]);
        }

        return directory;
    }
}
