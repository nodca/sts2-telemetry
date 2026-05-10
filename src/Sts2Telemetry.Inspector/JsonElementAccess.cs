using System.Globalization;
using System.Text.Json;

namespace Sts2Telemetry.Inspector;

internal static class JsonElementAccess
{
    public static bool TryGetPath(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    public static bool HasPath(JsonElement element, string path)
        => TryGetPath(element, path, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            && value.ValueKind != JsonValueKind.Undefined;

    public static string? GetString(JsonElement element, string path)
    {
        if (!TryGetPath(element, path, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long? GetInt64(JsonElement element, string path)
    {
        if (!TryGetPath(element, path, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;

        return null;
    }

    public static bool? GetBoolean(JsonElement element, string path)
    {
        if (!TryGetPath(element, path, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null
        };
    }

    public static DateTimeOffset? GetDateTimeOffset(JsonElement element, string path)
    {
        string? value = GetString(element, path);
        if (value == null)
            return null;

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    public static bool ContainsString(JsonElement element, Func<string, bool> predicate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string? value = element.GetString();
                return value != null && predicate(value);
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (predicate(property.Name) || ContainsString(property.Value, predicate))
                        return true;
                }

                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsString(item, predicate))
                        return true;
                }

                return false;
            default:
                return false;
        }
    }

    public static int CountStrings(JsonElement element, Func<string, bool> predicate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string? value = element.GetString();
                return value != null && predicate(value) ? 1 : 0;
            case JsonValueKind.Object:
                int objectCount = 0;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (predicate(property.Name))
                        objectCount++;
                    objectCount += CountStrings(property.Value, predicate);
                }

                return objectCount;
            case JsonValueKind.Array:
                int arrayCount = 0;
                foreach (JsonElement item in element.EnumerateArray())
                    arrayCount += CountStrings(item, predicate);
                return arrayCount;
            default:
                return 0;
        }
    }

    public static IEnumerable<(string Path, string Value)> EnumerateStrings(JsonElement element, string path = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string? value = element.GetString();
                if (value != null)
                    yield return (path, value);
                yield break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string nextPath = string.IsNullOrEmpty(path)
                        ? property.Name
                        : $"{path}.{property.Name}";
                    foreach ((string childPath, string childValue) in EnumerateStrings(property.Value, nextPath))
                        yield return (childPath, childValue);
                }

                yield break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string nextPath = $"{path}[{index}]";
                    foreach ((string childPath, string childValue) in EnumerateStrings(item, nextPath))
                        yield return (childPath, childValue);
                    index++;
                }

                yield break;
            default:
                yield break;
        }
    }

    public static bool HasPropertyName(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    || HasPropertyName(property.Value, propertyName))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (HasPropertyName(item, propertyName))
                    return true;
            }
        }

        return false;
    }

    public static bool TryFindPositiveNumberByPropertyName(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out long number)
                    && number > 0)
                {
                    return true;
                }

                if (TryFindPositiveNumberByPropertyName(property.Value, propertyName))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindPositiveNumberByPropertyName(item, propertyName))
                    return true;
            }
        }

        return false;
    }
}
