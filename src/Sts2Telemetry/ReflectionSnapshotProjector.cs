using System.Collections;
using System.Reflection;

namespace Sts2Telemetry;

internal sealed class ReflectionSnapshotProjector
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public;
    private readonly int _maxDepth;
    private readonly int _maxCollectionItems;
    private readonly int _maxMembers;

    public ReflectionSnapshotProjector(int maxDepth = 3, int maxCollectionItems = 60, int maxMembers = 80)
    {
        _maxDepth = maxDepth;
        _maxCollectionItems = maxCollectionItems;
        _maxMembers = maxMembers;
    }

    public object? Project(object? value)
        => ProjectValue(value, depth: 0, seen: new HashSet<object>(ReferenceEqualityComparer.Instance));

    private object? ProjectValue(object? value, int depth, HashSet<object> seen)
    {
        if (value == null)
            return null;

        Type type = value.GetType();
        if (IsScalar(type))
            return value is Enum ? value.ToString() : value;

        if (value is string text)
            return text.Length <= 1024 ? text : text[..1024];

        if (depth >= _maxDepth)
            return new Dictionary<string, object?>
            {
                ["type"] = type.FullName,
                ["summary"] = ShouldRedactSummary(type) ? "redacted" : ReflectionUtil.SafeText(value) ?? value.ToString()
            };

        if (!type.IsValueType && !seen.Add(value))
            return new Dictionary<string, object?> { ["type"] = type.FullName, ["cycle"] = true };

        if (value is IDictionary dictionary)
            return ProjectDictionary(dictionary, depth, seen);

        if (value is IEnumerable enumerable && value is not string)
            return ProjectEnumerable(enumerable, depth, seen);

        return ProjectObject(value, depth, seen);
    }

    private object ProjectDictionary(IDictionary dictionary, int depth, HashSet<object> seen)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        int count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (count++ >= _maxCollectionItems)
            {
                result["_truncated"] = true;
                break;
            }

            string key = entry.Key?.ToString() ?? "";
            result[key] = ProjectValue(entry.Value, depth + 1, seen);
        }
        return result;
    }

    private object ProjectEnumerable(IEnumerable enumerable, int depth, HashSet<object> seen)
    {
        var result = new List<object?>();
        int count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= _maxCollectionItems)
            {
                result.Add(new Dictionary<string, object?> { ["_truncated"] = true });
                break;
            }

            result.Add(ProjectValue(item, depth + 1, seen));
        }
        return result;
    }

    private object ProjectObject(object value, int depth, HashSet<object> seen)
    {
        Type type = value.GetType();
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_type"] = type.FullName
        };

        int memberCount = 0;
        foreach (var property in type.GetProperties(AnyInstance).OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (memberCount >= _maxMembers)
            {
                result["_member_truncated"] = true;
                break;
            }

            if (property.GetIndexParameters().Length != 0 || ShouldSkipMember(property.Name, property.PropertyType))
                continue;

            try
            {
                object? memberValue = property.GetValue(value);
                result[ToSnakeCase(property.Name)] = ProjectValue(memberValue, depth + 1, seen);
                memberCount++;
            }
            catch
            {
            }
        }

        foreach (var field in type.GetFields(AnyInstance).OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (memberCount >= _maxMembers)
            {
                result["_member_truncated"] = true;
                break;
            }

            if (ShouldSkipMember(field.Name, field.FieldType))
                continue;

            try
            {
                object? memberValue = field.GetValue(value);
                result[ToSnakeCase(field.Name)] = ProjectValue(memberValue, depth + 1, seen);
                memberCount++;
            }
            catch
            {
            }
        }

        return result;
    }

    private static bool IsScalar(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(Guid);

    private static bool ShouldSkipMember(string name, Type memberType)
    {
        if (memberType.FullName?.StartsWith("Godot.", StringComparison.Ordinal) == true
            && memberType.Name is not "Vector2" and not "Vector2I")
            return true;

        string lower = name.ToLowerInvariant();
        return lower.Contains("event", StringComparison.Ordinal)
            || lower.Contains("steam", StringComparison.Ordinal)
            || lower.Contains("account", StringComparison.Ordinal)
            || lower.Contains("email", StringComparison.Ordinal)
            || lower.Contains("contact", StringComparison.Ordinal)
            || lower.Contains("nickname", StringComparison.Ordinal)
            || lower.Contains("username", StringComparison.Ordinal)
            || lower.Contains("user_name", StringComparison.Ordinal)
            || lower.Contains("display_name", StringComparison.Ordinal)
            || lower.Contains("hardware", StringComparison.Ordinal)
            || lower.Contains("machine", StringComparison.Ordinal)
            || lower.Contains("filesystem", StringComparison.Ordinal)
            || lower.Contains("file_path", StringComparison.Ordinal)
            || lower.Contains("directory", StringComparison.Ordinal)
            || lower.Contains("ip_address", StringComparison.Ordinal)
            || IsIdentityContainerType(memberType)
            || lower.Contains("callback", StringComparison.Ordinal)
            || lower.Contains("signal", StringComparison.Ordinal)
            || lower.Contains("delegate", StringComparison.Ordinal)
            || lower.Contains("logger", StringComparison.Ordinal)
            || lower.Contains("texture", StringComparison.Ordinal)
            || lower.Contains("sprite", StringComparison.Ordinal)
            || lower.Contains("audio", StringComparison.Ordinal);
    }

    private static bool ShouldRedactSummary(Type type)
    {
        string? fullName = type.FullName;
        return fullName?.Contains(".Player", StringComparison.Ordinal) == true
            || fullName?.EndsWith("Player", StringComparison.Ordinal) == true
            || IsIdentityContainerType(type);
    }

    private static bool IsIdentityContainerType(Type type)
    {
        string? fullName = type.FullName;
        return fullName?.Contains("Profile", StringComparison.OrdinalIgnoreCase) == true
            || fullName?.Contains("Account", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
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
