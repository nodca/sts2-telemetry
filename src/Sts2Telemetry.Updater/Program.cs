using System.Text.Json;
using Sts2Telemetry;

string? requestPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--request", StringComparison.Ordinal) && i + 1 < args.Length)
    {
        requestPath = args[i + 1];
        i++;
    }
}

if (string.IsNullOrWhiteSpace(requestPath))
{
    Console.Error.WriteLine("usage: Sts2Telemetry.Updater --request <install.request.json>");
    return 2;
}

TelemetryUpdateInstallRequest request;
try
{
    string json = File.ReadAllText(requestPath);
    request = JsonSerializer.Deserialize<TelemetryUpdateInstallRequest>(json, TelemetryJson.Options)
        ?? throw new InvalidOperationException("install request was empty");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"failed to read update request: {ex.Message}");
    return 2;
}

TelemetryUpdateInstallResult result = TelemetryUpdateInstaller.Apply(request);
if (result.State == "installed")
{
    Console.WriteLine($"installed STS2 Telemetry {result.TargetVersion}");
    return 0;
}

Console.Error.WriteLine($"update install failed ({result.ErrorCode}): {result.ErrorMessage}");
return 1;
