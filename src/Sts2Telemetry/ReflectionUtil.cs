using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace Sts2Telemetry;

internal static class ReflectionUtil
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<string, CachedLookup<Type>> TypeLookupCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<MemberLookupKey, CachedLookup<PropertyInfo>> PropertyLookupCache = new();
    private static readonly ConcurrentDictionary<MemberLookupKey, CachedLookup<FieldInfo>> FieldLookupCache = new();
    private static readonly ConcurrentDictionary<MethodLookupKey, CachedLookup<MethodInfo>> ExactMethodLookupCache = new();
    private static readonly ConcurrentDictionary<MethodLookupKey, CachedLookup<MethodInfo>> MethodByArityLookupCache = new();

    public static object? GetMemberValue(object? target, params string[] memberNames)
    {
        if (target == null)
            return null;

        Type type = target.GetType();
        foreach (string memberName in memberNames)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                continue;

            try
            {
                var property = GetCachedProperty(type, memberName, isStatic: false);
                if (property != null)
                    return property.GetValue(target);
            }
            catch
            {
                // Individual game properties can be unstable during transitions.
            }

            try
            {
                var field = GetCachedField(type, memberName, isStatic: false);
                if (field != null)
                    return field.GetValue(target);
            }
            catch
            {
            }

            try
            {
                var method = GetCachedParameterlessMethod(type, memberName, isStatic: false);
                if (method != null)
                    return method.Invoke(target, null);
            }
            catch
            {
            }
        }

        return null;
    }

    public static bool TryReadMemberValue(object? target, out object? value, params string[] memberNames)
    {
        value = null;
        if (target == null)
            return false;

        Type type = target.GetType();
        foreach (string memberName in memberNames)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                continue;

            try
            {
                var property = GetCachedProperty(type, memberName, isStatic: false);
                if (property != null)
                {
                    value = property.GetValue(target);
                    return true;
                }
            }
            catch
            {
                // Individual game properties can be unstable during transitions.
            }

            try
            {
                var field = GetCachedField(type, memberName, isStatic: false);
                if (field != null)
                {
                    value = field.GetValue(target);
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    public static object? GetStaticMemberValue(string typeName, params string[] memberNames)
    {
        Type? type = TypeByName(typeName);
        if (type == null)
            return null;

        foreach (string memberName in memberNames)
        {
            try
            {
                var property = GetCachedProperty(type, memberName, isStatic: true);
                if (property != null)
                    return property.GetValue(null);
            }
            catch
            {
            }

            try
            {
                var field = GetCachedField(type, memberName, isStatic: true);
                if (field != null)
                    return field.GetValue(null);
            }
            catch
            {
            }
        }

        return null;
    }

    public static object? Call(object? target, string methodName, params object?[] args)
    {
        if (target == null)
            return null;

        try
        {
            var method = GetCachedMethodByArgumentCount(target.GetType(), methodName, isStatic: false, argumentCount: args.Length);
            return method?.Invoke(target, args);
        }
        catch
        {
            return null;
        }
    }

    public static object? CallStatic(string typeName, string methodName, params object?[] args)
    {
        Type? type = TypeByName(typeName);
        if (type == null)
            return null;

        try
        {
            var method = GetCachedMethodByArgumentCount(type, methodName, isStatic: true, argumentCount: args.Length);
            return method?.Invoke(null, args);
        }
        catch
        {
            return null;
        }
    }

    public static bool? GetBool(object? target, params string[] memberNames)
    {
        object? value = GetMemberValue(target, memberNames);
        return ToBool(value);
    }

    public static int? GetInt(object? target, params string[] memberNames)
    {
        object? value = GetMemberValue(target, memberNames);
        if (value == null)
            return null;

        try
        {
            return value switch
            {
                int intValue => intValue,
                long longValue => checked((int)longValue),
                short shortValue => shortValue,
                byte byteValue => byteValue,
                uint uintValue => checked((int)uintValue),
                ulong ulongValue => checked((int)ulongValue),
                float floatValue => Convert.ToInt32(floatValue),
                double doubleValue => Convert.ToInt32(doubleValue),
                decimal decimalValue => decimal.ToInt32(decimalValue),
                string text when int.TryParse(text, out int parsed) => parsed,
                _ => Convert.ToInt32(value)
            };
        }
        catch
        {
            return null;
        }
    }

    public static string? GetText(object? target, params string[] memberNames)
    {
        object? value = memberNames.Length == 0 ? target : GetMemberValue(target, memberNames);
        return SafeText(value);
    }

    public static string? SafeText(object? value)
    {
        if (value == null)
            return null;

        try
        {
            object? formatted = Call(value, "GetFormattedText");
            string text = formatted?.ToString() ?? value.ToString() ?? "";
            return StripRichTextTags(text).Replace("\n", " ", StringComparison.Ordinal);
        }
        catch
        {
            return null;
        }
    }

    public static bool? ToBool(object? value)
    {
        if (value == null)
            return null;

        try
        {
            return value switch
            {
                bool boolValue => boolValue,
                string text when bool.TryParse(text, out bool parsed) => parsed,
                _ => Convert.ToBoolean(value)
            };
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<object?> Enumerate(object? value, int maxItems = 80)
    {
        if (value is not IEnumerable enumerable || value is string)
            yield break;

        int count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= maxItems)
                yield break;
            yield return item;
        }
    }

    public static object? GetSingletonInstance(params string[] typeNames)
    {
        foreach (string typeName in typeNames)
        {
            object? value = GetStaticMemberValue(typeName, "Instance");
            if (value != null)
                return value;
        }

        return null;
    }

    private static Type? TypeByName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        if (TypeLookupCache.TryGetValue(typeName, out CachedLookup<Type> cached))
            return cached.Value;

        try
        {
            Type? type = AccessTools.TypeByName(typeName);
            TypeLookupCache.TryAdd(typeName, new CachedLookup<Type>(type));
            return type;
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo? GetCachedProperty(Type type, string memberName, bool isStatic)
    {
        var key = new MemberLookupKey(type, memberName, isStatic);
        if (PropertyLookupCache.TryGetValue(key, out CachedLookup<PropertyInfo> cached))
            return cached.Value;

        try
        {
            var property = type.GetProperty(memberName, isStatic ? AnyStatic : AnyInstance);
            if (property?.GetIndexParameters().Length > 0)
                property = null;
            PropertyLookupCache.TryAdd(key, new CachedLookup<PropertyInfo>(property));
            return property;
        }
        catch
        {
            return null;
        }
    }

    private static FieldInfo? GetCachedField(Type type, string memberName, bool isStatic)
    {
        var key = new MemberLookupKey(type, memberName, isStatic);
        if (FieldLookupCache.TryGetValue(key, out CachedLookup<FieldInfo> cached))
            return cached.Value;

        try
        {
            var field = type.GetField(memberName, isStatic ? AnyStatic : AnyInstance);
            FieldLookupCache.TryAdd(key, new CachedLookup<FieldInfo>(field));
            return field;
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? GetCachedParameterlessMethod(Type type, string methodName, bool isStatic)
    {
        var key = new MethodLookupKey(type, methodName, isStatic, ArgumentCount: 0);
        if (ExactMethodLookupCache.TryGetValue(key, out CachedLookup<MethodInfo> cached))
            return cached.Value;

        try
        {
            var method = type.GetMethod(methodName, isStatic ? AnyStatic : AnyInstance, null, Type.EmptyTypes, null);
            ExactMethodLookupCache.TryAdd(key, new CachedLookup<MethodInfo>(method));
            return method;
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? GetCachedMethodByArgumentCount(Type type, string methodName, bool isStatic, int argumentCount)
    {
        var key = new MethodLookupKey(type, methodName, isStatic, argumentCount);
        if (MethodByArityLookupCache.TryGetValue(key, out CachedLookup<MethodInfo> cached))
            return cached.Value;

        try
        {
            var method = type.GetMethods(isStatic ? AnyStatic : AnyInstance)
                .FirstOrDefault(candidate => candidate.Name == methodName
                    && candidate.GetParameters().Length == argumentCount);
            MethodByArityLookupCache.TryAdd(key, new CachedLookup<MethodInfo>(method));
            return method;
        }
        catch
        {
            return null;
        }
    }

    private static string StripRichTextTags(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int end = text.IndexOf(']', i);
                if (end >= 0)
                {
                    i = end + 1;
                    continue;
                }
            }

            builder.Append(text[i]);
            i++;
        }

        return builder.ToString();
    }

    private readonly record struct CachedLookup<T>(T? Value)
        where T : class;

    private readonly record struct MemberLookupKey(Type Type, string Name, bool IsStatic);

    private readonly record struct MethodLookupKey(Type Type, string Name, bool IsStatic, int ArgumentCount);
}
