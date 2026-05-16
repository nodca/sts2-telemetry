using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace Sts2Telemetry;

internal static class TreasureRelicReadinessProbe
{
    public static TreasureRelicReadiness ReadyForTests
        => TreasureRelicReadiness.Ready("test_treasure_relic_collection", true);

    public static TreasureRelicReadiness GetRuntimeReadiness()
    {
        try
        {
            return FromCurrentScreen(ActiveScreenContext.Instance.GetCurrentScreen());
        }
        catch
        {
            return TreasureRelicReadiness.Unavailable(
                "active_screen_context_unavailable",
                "treasure relic legal actions require ActiveScreenContext.GetCurrentScreen() and a visible NTreasureRoomRelicCollection");
        }
    }

    private static TreasureRelicReadiness FromCurrentScreen(IScreenContext? currentScreen)
    {
        string? currentScreenRuntimeType = currentScreen?.GetType().FullName;
        if (currentScreen == null)
        {
            return TreasureRelicReadiness.Unavailable(
                "active_screen_unavailable",
                "treasure relic legal actions require an active treasure screen",
                currentScreenRuntimeType);
        }

        try
        {
            if (currentScreen is NTreasureRoomRelicCollection relicCollection)
                return ValidateRelicCollection(relicCollection, currentScreenRuntimeType);

            if (currentScreen is NTreasureRoom treasureRoom)
            {
                if (!GodotObject.IsInstanceValid(treasureRoom))
                {
                    return TreasureRelicReadiness.Unavailable(
                        "treasure_room_screen_invalid",
                        "treasure room screen is no longer a valid Godot instance",
                        currentScreenRuntimeType);
                }

                return ValidateRelicCollection(
                    treasureRoom.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection"),
                    currentScreenRuntimeType);
            }

            return TreasureRelicReadiness.Unavailable(
                "active_screen_not_treasure_room",
                "treasure relic legal actions require NTreasureRoom or NTreasureRoomRelicCollection as the active screen",
                currentScreenRuntimeType);
        }
        catch
        {
            return TreasureRelicReadiness.Unavailable(
                "treasure_relic_collection_probe_failed",
                "treasure relic collection readiness probe failed",
                currentScreenRuntimeType);
        }
    }

    private static TreasureRelicReadiness ValidateRelicCollection(
        NTreasureRoomRelicCollection? relicCollection,
        string? currentScreenRuntimeType)
    {
        if (relicCollection == null)
        {
            return TreasureRelicReadiness.Unavailable(
                "treasure_relic_collection_not_found",
                "treasure relic collection is not attached yet",
                currentScreenRuntimeType);
        }

        if (!GodotObject.IsInstanceValid(relicCollection))
        {
            return TreasureRelicReadiness.Unavailable(
                "treasure_relic_collection_invalid",
                "treasure relic collection is no longer a valid Godot instance",
                currentScreenRuntimeType);
        }

        bool isVisible = relicCollection.Visible;
        if (!isVisible)
        {
            return TreasureRelicReadiness.Unavailable(
                "empty_or_no_visible_treasure_relic_choices",
                "hidden treasure relic collection can mean an empty treasure room or no selectable relic choices",
                currentScreenRuntimeType,
                isVisible);
        }

        return TreasureRelicReadiness.Ready(currentScreenRuntimeType, isVisible);
    }
}
