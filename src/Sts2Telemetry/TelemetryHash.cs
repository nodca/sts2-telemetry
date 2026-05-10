using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sts2Telemetry;

public static class TelemetryHash
{
    public static string HashCanonical(object? payload)
        => HashJson(Canonicalize(payload));

    public static string HashCanonicalPayload(object? canonicalPayload)
        => HashJson(canonicalPayload);

    public static string HashRaw(object? payload)
        => HashJson(NormalizeForStableJson(payload));

    public static object? Canonicalize(object? payload)
        => CanonicalizeValue(NormalizeForStableJson(payload), parentKey: null);

    public static object? NormalizeForStableJson(object? payload)
    {
        if (payload == null)
            return null;

        if (payload is JsonElement element)
            return NormalizeJsonElement(element);

        if (payload is float floatValue)
            return float.IsFinite(floatValue) ? floatValue : floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (payload is double doubleValue)
            return double.IsFinite(doubleValue) ? doubleValue : doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (payload is string or bool or int or long or short or byte or uint or ulong or decimal)
            return payload;

        if (payload is IDictionary<string, object?> stringDictionary)
        {
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in stringDictionary)
                result[key] = NormalizeForStableJson(value);
            return result;
        }

        if (payload is System.Collections.IDictionary dictionary)
        {
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                if (entry.Key == null)
                    continue;
                result[entry.Key.ToString() ?? ""] = NormalizeForStableJson(entry.Value);
            }
            return result;
        }

        if (payload is System.Collections.IEnumerable enumerable && payload is not string)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
                result.Add(NormalizeForStableJson(item));
            return result;
        }

        return payload.ToString();
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(
                    property => property.Name,
                    property => NormalizeJsonElement(property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out double doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private static object? CanonicalizeValue(object? payload, string? parentKey)
    {
        if (payload == null)
            return null;

        if (payload is IDictionary<string, object?> dictionary)
        {
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in dictionary)
            {
                if (IsOperationalOnlyKey(key, parentKey))
                    continue;

                var canonicalValue = CanonicalizeValue(value, key);
                if (canonicalValue != null)
                    result[key] = canonicalValue;
            }
            return result;
        }

        if (payload is System.Collections.IDictionary nonGenericDictionary)
        {
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in nonGenericDictionary)
            {
                string key = entry.Key?.ToString() ?? "";
                if (IsOperationalOnlyKey(key, parentKey))
                    continue;

                var canonicalValue = CanonicalizeValue(entry.Value, key);
                if (canonicalValue != null)
                    result[key] = canonicalValue;
            }
            return result;
        }

        if (payload is System.Collections.IEnumerable enumerable && payload is not string)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
                result.Add(CanonicalizeValue(item, parentKey));
            return result;
        }

        return payload;
    }

    private static bool IsOperationalOnlyKey(string key, string? parentKey)
    {
        string normalized = ToSnakeCase(key);
        string parent = parentKey == null ? "" : ToSnakeCase(parentKey);

        if (parent is "operational_metadata" or "telemetry" or "writer" or "projection_notes")
            return true;

        if (normalized is "recorded_at_utc" or "captured_at_utc" or "local_sequence" or "envelope_id"
            or "record_id" or "telemetry_record_id" or "writer_retry_count" or "projection_notes"
            or "schema_version" or "mod_version")
            return true;

        return normalized.Contains("wall_clock", StringComparison.Ordinal)
            || normalized.Contains("timestamp", StringComparison.Ordinal)
            || normalized.EndsWith("_time", StringComparison.Ordinal)
            || normalized.EndsWith("_timer", StringComparison.Ordinal)
            || normalized.Contains("animation", StringComparison.Ordinal)
            || normalized.Contains("hover", StringComparison.Ordinal)
            || normalized.Contains("focus", StringComparison.Ordinal)
            || normalized.Contains("layout", StringComparison.Ordinal)
            || normalized.Contains("mouse", StringComparison.Ordinal)
            || normalized.Contains("cursor", StringComparison.Ordinal)
            || normalized.Contains("screen_position", StringComparison.Ordinal)
            || normalized.Contains("global_position", StringComparison.Ordinal);
    }

    private static string HashJson(object? payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryJson.Options);
        byte[] digest = SHA256.HashData(json);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var builder = new StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && builder[^1] != '_')
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c is '-' or ' ' or '.')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
            }
            else
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString();
    }
}
