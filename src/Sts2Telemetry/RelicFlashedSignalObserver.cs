using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Sts2Telemetry;

internal sealed class RelicFlashedSignalObserver
{
    private readonly Action<string, object?, object?> _onObservedSignal;
    private readonly object _gate = new();
    private readonly Dictionary<int, Subscription> _relicSubscriptions = new();
    private readonly Dictionary<int, Subscription> _playerSubscriptions = new();
    private readonly HashSet<int> _missingRelicSubscriptions = new();
    private readonly HashSet<int> _missingPlayerSubscriptions = new();
    private int _observePlayerRelicsCount;
    private int _playerUnavailableCount;
    private int _playerRelicsUnavailableCount;
    private int _playerRelicsEmptyCount;
    private int _relicNullCount;
    private int _playerRelicObtainedSubscribedCount;
    private int _playerRelicObtainedMissingCount;
    private int _playerRelicObtainedFiredCount;
    private int _relicFlashedSubscribedCount;
    private int _relicFlashedMissingCount;
    private int _relicFlashedFiredCount;

    public RelicFlashedSignalObserver(Action<string, object?, object?> onObservedSignal)
        => _onObservedSignal = onObservedSignal;

    public void ObservePlayerRelics(object? player)
    {
        Increment(ref _observePlayerRelicsCount);
        if (player == null)
        {
            Increment(ref _playerUnavailableCount);
            TraceDiagnostic("observe_player:no_current_player");
            return;
        }

        ObserveRelicObtainedSignal(player);
        object? relics = ReflectionUtil.GetMemberValue(player, "Relics");
        if (relics == null)
        {
            Increment(ref _playerRelicsUnavailableCount);
            TraceDiagnostic($"observe_player:relics_unavailable:{TypeName(player)}");
            return;
        }

        int relicSlotCount = 0;
        int observableRelicCount = 0;
        foreach (object? relic in ReflectionUtil.Enumerate(relics, maxItems: 64))
        {
            relicSlotCount++;
            if (relic == null)
            {
                Increment(ref _relicNullCount);
                continue;
            }

            observableRelicCount++;
            ObserveRelic(relic);
        }

        if (relicSlotCount == 0)
        {
            Increment(ref _playerRelicsEmptyCount);
            TraceDiagnostic($"observe_player:relics_empty:{TypeName(player)}");
        }
        else if (observableRelicCount == 0)
        {
            Increment(ref _playerRelicsUnavailableCount);
            TraceDiagnostic($"observe_player:relics_all_null:{TypeName(player)}");
        }
    }

    internal Diagnostics GetDiagnosticsSnapshot()
    {
        lock (_gate)
        {
            return new Diagnostics(
                ObservePlayerRelicsCount: _observePlayerRelicsCount,
                PlayerUnavailableCount: _playerUnavailableCount,
                PlayerRelicsUnavailableCount: _playerRelicsUnavailableCount,
                PlayerRelicsEmptyCount: _playerRelicsEmptyCount,
                RelicNullCount: _relicNullCount,
                PlayerRelicObtainedSubscribedCount: _playerRelicObtainedSubscribedCount,
                PlayerRelicObtainedMissingCount: _playerRelicObtainedMissingCount,
                PlayerRelicObtainedFiredCount: _playerRelicObtainedFiredCount,
                RelicFlashedSubscribedCount: _relicFlashedSubscribedCount,
                RelicFlashedMissingCount: _relicFlashedMissingCount,
                RelicFlashedFiredCount: _relicFlashedFiredCount,
                ActivePlayerSubscriptionCount: _playerSubscriptions.Count,
                ActiveRelicSubscriptionCount: _relicSubscriptions.Count);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            foreach (Subscription subscription in _relicSubscriptions.Values)
                RemoveSubscription(subscription);
            foreach (Subscription subscription in _playerSubscriptions.Values)
                RemoveSubscription(subscription);
            _relicSubscriptions.Clear();
            _playerSubscriptions.Clear();
            _missingRelicSubscriptions.Clear();
            _missingPlayerSubscriptions.Clear();
            _observePlayerRelicsCount = 0;
            _playerUnavailableCount = 0;
            _playerRelicsUnavailableCount = 0;
            _playerRelicsEmptyCount = 0;
            _relicNullCount = 0;
            _playerRelicObtainedSubscribedCount = 0;
            _playerRelicObtainedMissingCount = 0;
            _playerRelicObtainedFiredCount = 0;
            _relicFlashedSubscribedCount = 0;
            _relicFlashedMissingCount = 0;
            _relicFlashedFiredCount = 0;
        }
    }

    private void ObserveRelicObtainedSignal(object? player)
    {
        if (player == null)
            return;

        int key = RuntimeHelpers.GetHashCode(player);
        lock (_gate)
        {
            if (_playerSubscriptions.ContainsKey(key))
                return;

            Subscription? subscription = TrySubscribeEvent(player, "RelicObtained", BuildRelicObtainedHandler)
                ?? TrySubscribeDelegateField(player, "RelicObtained", BuildRelicObtainedHandler);
            if (subscription == null)
            {
                if (_missingPlayerSubscriptions.Add(key))
                {
                    _playerRelicObtainedMissingCount++;
                    TraceDiagnostic($"subscribe_missing:player_relic_obtained:{TypeName(player)}.RelicObtained");
                }

                return;
            }

            _playerSubscriptions[key] = subscription;
            _missingPlayerSubscriptions.Remove(key);
            _playerRelicObtainedSubscribedCount++;
            TraceDiagnostic($"subscribe_success:player_relic_obtained:{subscription.Surface}:{subscription.Description}");
        }
    }

    private void ObserveRelic(object? relic)
    {
        if (relic == null)
        {
            Increment(ref _relicNullCount);
            return;
        }

        int key = RuntimeHelpers.GetHashCode(relic);
        lock (_gate)
        {
            if (_relicSubscriptions.ContainsKey(key))
                return;

            Subscription? subscription = TrySubscribeEvent(relic, "Flashed", handlerType => BuildFlashedHandler(handlerType, relic))
                ?? TrySubscribeDelegateField(relic, "Flashed", handlerType => BuildFlashedHandler(handlerType, relic));
            if (subscription == null)
            {
                if (_missingRelicSubscriptions.Add(key))
                {
                    _relicFlashedMissingCount++;
                    TraceDiagnostic($"subscribe_missing:relic_flashed:{TypeName(relic)}.Flashed");
                }

                return;
            }

            _relicSubscriptions[key] = subscription;
            _missingRelicSubscriptions.Remove(key);
            _relicFlashedSubscribedCount++;
            TraceDiagnostic($"subscribe_success:relic_flashed:{subscription.Surface}:{subscription.Description}");
        }
    }

    private Subscription? TrySubscribeEvent(
        object target,
        string signalName,
        Func<Type, Delegate?> handlerFactory)
    {
        EventInfo? eventInfo = FindInstanceEvent(target.GetType(), signalName);
        if (eventInfo?.EventHandlerType == null)
            return null;

        try
        {
            Delegate? handler = handlerFactory(eventInfo.EventHandlerType);
            if (handler == null)
            {
                TraceDiagnostic(
                    $"subscribe_event_handler_unavailable:{target.GetType().FullName}.{signalName}");
                return null;
            }

            eventInfo.AddEventHandler(target, handler);
            return new Subscription(
                () => eventInfo.RemoveEventHandler(target, handler),
                $"{target.GetType().FullName}.{signalName}",
                "event");
        }
        catch (Exception ex)
        {
            TraceDiagnostic($"subscribe_event_failed:{target.GetType().FullName}.{signalName}:{ex.GetType().Name}");
            return null;
        }
    }

    private Subscription? TrySubscribeDelegateField(
        object target,
        string signalName,
        Func<Type, Delegate?> handlerFactory)
    {
        FieldInfo? field = FindInstanceField(target.GetType(), signalName);
        if (field == null)
            return null;

        if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
        {
            TraceDiagnostic($"subscribe_field_not_delegate:{target.GetType().FullName}.{signalName}");
            return null;
        }

        try
        {
            Delegate? handler = handlerFactory(field.FieldType);
            if (handler == null)
            {
                TraceDiagnostic(
                    $"subscribe_field_handler_unavailable:{target.GetType().FullName}.{signalName}");
                return null;
            }

            Delegate? existing = field.GetValue(target) as Delegate;
            field.SetValue(target, Delegate.Combine(existing, handler));
            return new Subscription(() =>
            {
                Delegate? current = field.GetValue(target) as Delegate;
                field.SetValue(target, Delegate.Remove(current, handler));
            }, $"{target.GetType().FullName}.{signalName}", "delegate_field");
        }
        catch (Exception ex)
        {
            TraceDiagnostic($"subscribe_field_failed:{target.GetType().FullName}.{signalName}:{ex.GetType().Name}");
            return null;
        }
    }

    private Delegate? BuildFlashedHandler(Type eventHandlerType, object observedRelic)
    {
        MethodInfo? invoke = eventHandlerType.GetMethod("Invoke");
        ParameterInfo[] parameters = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
        if (parameters.Length != 2)
            return null;

        ParameterExpression relicParameter = Expression.Parameter(parameters[0].ParameterType, "relic");
        ParameterExpression targetsParameter = Expression.Parameter(parameters[1].ParameterType, "targets");
        MethodInfo callback = typeof(RelicFlashedSignalObserver).GetMethod(
            nameof(OnRelicFlashed),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodCallExpression body = Expression.Call(
            Expression.Constant(this),
            callback,
            Expression.Convert(relicParameter, typeof(object)),
            Expression.Convert(targetsParameter, typeof(object)),
            Expression.Constant(observedRelic, typeof(object)));
        return Expression.Lambda(eventHandlerType, body, relicParameter, targetsParameter).Compile();
    }

    private Delegate? BuildRelicObtainedHandler(Type eventHandlerType)
    {
        MethodInfo? invoke = eventHandlerType.GetMethod("Invoke");
        ParameterInfo[] parameters = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
        if (parameters.Length != 1)
            return null;

        ParameterExpression relicParameter = Expression.Parameter(parameters[0].ParameterType, "relic");
        MethodInfo callback = typeof(RelicFlashedSignalObserver).GetMethod(
            nameof(OnRelicObtained),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodCallExpression body = Expression.Call(
            Expression.Constant(this),
            callback,
            Expression.Convert(relicParameter, typeof(object)));
        return Expression.Lambda(eventHandlerType, body, relicParameter).Compile();
    }

    private void OnRelicObtained(object? relic)
    {
        Increment(ref _playerRelicObtainedFiredCount);
        TraceDiagnostic($"fired:player_relic_obtained:{TypeName(relic)}");
        if (relic != null)
            ObserveRelic(relic);
    }

    private void OnRelicFlashed(object? relic, object? targets, object observedRelic)
    {
        Increment(ref _relicFlashedFiredCount);
        TraceDiagnostic($"fired:relic_flashed:{TypeName(relic ?? observedRelic)}");
        _onObservedSignal("runtime.relic_model.flashed_event", relic ?? observedRelic, targets);
    }

    private static void RemoveSubscription(Subscription subscription)
    {
        try
        {
            subscription.Remove();
        }
        catch (Exception ex)
        {
            TraceDiagnostic($"unsubscribe_failed:{subscription.Description}:{ex.GetType().Name}");
        }
    }

    private void Increment(ref int counter)
    {
        lock (_gate)
        {
            counter++;
        }
    }

    private static void TraceDiagnostic(string message)
        => Sts2TelemetryMod.TraceDiagnostic($"relic_signal_observer.{message}");

    private static string TypeName(object? value)
        => value?.GetType().FullName ?? "null";

    private static EventInfo? FindInstanceEvent(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            EventInfo? eventInfo = current.GetEvent(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (eventInfo != null)
                return eventInfo;
        }

        return null;
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? fieldInfo = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (fieldInfo != null)
                return fieldInfo;
        }

        return null;
    }

    internal sealed record Diagnostics(
        int ObservePlayerRelicsCount,
        int PlayerUnavailableCount,
        int PlayerRelicsUnavailableCount,
        int PlayerRelicsEmptyCount,
        int RelicNullCount,
        int PlayerRelicObtainedSubscribedCount,
        int PlayerRelicObtainedMissingCount,
        int PlayerRelicObtainedFiredCount,
        int RelicFlashedSubscribedCount,
        int RelicFlashedMissingCount,
        int RelicFlashedFiredCount,
        int ActivePlayerSubscriptionCount,
        int ActiveRelicSubscriptionCount);

    private sealed record Subscription(Action Remove, string Description, string Surface);
}
