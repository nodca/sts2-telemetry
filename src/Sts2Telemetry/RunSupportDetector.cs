using MegaCrit.Sts2.Core.Runs;

namespace Sts2Telemetry;

public static class RunSupportDetector
{
    private const string Sts2AssemblyName = "sts2";

    private static readonly string[] UnsupportedBooleanMembers =
    {
        "IsDailyRun",
        "IsDaily",
        "IsCustomRun",
        "IsCustom",
        "IsSeededRun",
        "IsSeeded",
        "IsDebugRun",
        "IsDebug",
        "IsDevRun",
        "IsDeveloperRun",
        "IsChallengeRun",
        "IsSpecialRun"
    };

    private static readonly string[] ModeMembers =
    {
        "RunType",
        "GameMode",
        "Mode",
        "RunMode",
        "QueueMode"
    };

    public static RunSupportResult Inspect(object? runState)
    {
        var detected = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        if (runState == null)
            return RunSupportResult.Unsupported("unknown_run_state", "run state was not available", detected);

        foreach (string member in UnsupportedBooleanMembers)
        {
            bool? value = ReflectionUtil.GetBool(runState, member);
            if (value == true)
            {
                detected[member] = true;
                return RunSupportResult.Unsupported(
                    member,
                    $"{member} indicates a non-normal run",
                    detected);
            }
        }

        foreach (string member in ModeMembers)
        {
            string? value = ReflectionUtil.GetText(runState, member);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            detected[member] = value;
            if (!LooksNormalMode(value))
            {
                return RunSupportResult.Unsupported(
                    member,
                    $"mode value '{value}' is not single-player normal",
                    detected);
            }
        }

        if (ShouldProbeGameSingletons(runState) && IsMultiplayerRun())
        {
            detected["net_type"] = SafeNetType();
            return RunSupportResult.Unsupported("multiplayer", "multiplayer runs are out of scope for the local prototype", detected);
        }

        if (ShouldProbeGameSingletons(runState))
            detected["net_type"] = SafeNetType();
        detected["mode_assumption"] = "single_player_normal";
        return RunSupportResult.Supported(detected);
    }

    private static bool ShouldProbeGameSingletons(object runState)
        => string.Equals(
            runState.GetType().Assembly.GetName().Name,
            Sts2AssemblyName,
            StringComparison.Ordinal);

    private static bool IsMultiplayerRun()
    {
        try
        {
            object? netType = ReflectionUtil.GetMemberValue(
                ReflectionUtil.GetMemberValue(RunManager.Instance, "NetService"),
                "Type");

            object? result = ReflectionUtil.Call(netType, "IsMultiplayer");
            return result is bool boolResult && boolResult;
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeNetType()
    {
        try
        {
            object? netType = ReflectionUtil.GetMemberValue(
                ReflectionUtil.GetMemberValue(RunManager.Instance, "NetService"),
                "Type");
            return ReflectionUtil.SafeText(netType);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksNormalMode(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized is "" or "normal" or "singleplayer" or "single_player" or "local" or "none")
            return true;

        return !normalized.Contains("daily", StringComparison.Ordinal)
            && !normalized.Contains("custom", StringComparison.Ordinal)
            && !normalized.Contains("seeded", StringComparison.Ordinal)
            && !normalized.Contains("debug", StringComparison.Ordinal)
            && !normalized.Contains("dev", StringComparison.Ordinal)
            && !normalized.Contains("challenge", StringComparison.Ordinal)
            && !normalized.Contains("multiplayer", StringComparison.Ordinal)
            && !normalized.Contains("special", StringComparison.Ordinal);
    }
}

public sealed record RunSupportResult(
    bool IsSupported,
    string Mode,
    string Reason,
    IReadOnlyDictionary<string, object?> Detected
)
{
    public static RunSupportResult Supported(IReadOnlyDictionary<string, object?> detected)
        => new(true, "single_player_normal", "supported by local prototype", detected);

    public static RunSupportResult Unsupported(string mode, string reason, IReadOnlyDictionary<string, object?> detected)
        => new(false, mode, reason, detected);

    public Dictionary<string, object?> ToRecord()
        => new()
        {
            ["is_supported"] = IsSupported,
            ["mode"] = Mode,
            ["reason"] = Reason,
            ["detected"] = Detected
        };
}
