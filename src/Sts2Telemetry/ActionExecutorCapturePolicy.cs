namespace Sts2Telemetry;

internal static class ActionExecutorCapturePolicy
{
    public const string SignalOnlyNoStateSnapshotNoLegalActions =
        "signal_only_no_state_snapshot_no_legal_actions";
    public const string TreasureRelicPickTransitionSafety =
        "signal_only_treasure_relic_pick_transition_safety_no_state_snapshot_no_legal_actions";

    private static readonly HashSet<string> SignalOnlyActionTypes = new(StringComparer.Ordinal)
    {
        "MegaCrit.Sts2.Core.GameActions.VoteForMapCoordAction",
        "MegaCrit.Sts2.Core.GameActions.MoveToMapCoordAction",
        "MegaCrit.Sts2.Core.GameActions.GenericHookGameAction",
        "MegaCrit.Sts2.Core.GameActions.VoteToMoveToNextActAction",
        "MegaCrit.Sts2.Core.GameActions.ReadyToBeginEnemyTurnAction",
        "MegaCrit.Sts2.Core.GameActions.UndoEndPlayerTurnAction",
        "VoteForMapCoordAction",
        "MoveToMapCoordAction",
        "GenericHookGameAction",
        "VoteToMoveToNextActAction",
        "ReadyToBeginEnemyTurnAction",
        "UndoEndPlayerTurnAction",
        "MegaCrit.Sts2.Core.GameActions.PickRelicAction",
        "PickRelicAction"
    };

    public static bool IsTreasureRelicPickTransition(object? action)
        => action != null && IsTreasureRelicPickTransitionType(action.GetType());

    public static bool IsTreasureRelicPickTransitionType(Type? actionType)
        => actionType != null
            && (IsTreasureRelicPickTransitionTypeName(actionType.FullName)
                || IsTreasureRelicPickTransitionTypeName(actionType.Name));

    public static bool IsTreasureRelicPickTransitionTypeName(string? actionTypeName)
        => string.Equals(SimpleTypeName(actionTypeName ?? ""), "PickRelicAction", StringComparison.Ordinal);

    public static bool IsSignalOnly(object? action)
        => action != null && IsSignalOnlyType(action.GetType());

    public static bool IsSignalOnlyType(Type? actionType)
        => actionType != null
            && (IsSignalOnlyTypeName(actionType.FullName)
                || IsSignalOnlyTypeName(actionType.Name));

    public static bool IsSignalOnlyTypeName(string? actionTypeName)
    {
        if (string.IsNullOrWhiteSpace(actionTypeName))
            return false;

        if (SignalOnlyActionTypes.Contains(actionTypeName))
            return true;

        string simpleName = SimpleTypeName(actionTypeName);
        return SignalOnlyActionTypes.Contains(simpleName);
    }

    private static string SimpleTypeName(string actionTypeName)
    {
        int dot = actionTypeName.LastIndexOf('.');
        int nested = actionTypeName.LastIndexOf('+');
        int index = Math.Max(dot, nested);
        return index < 0 ? actionTypeName : actionTypeName[(index + 1)..];
    }
}
