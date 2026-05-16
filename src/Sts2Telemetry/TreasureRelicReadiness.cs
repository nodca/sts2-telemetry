namespace Sts2Telemetry;

internal readonly record struct TreasureRelicReadiness(
    bool CanReadRelics,
    string Availability,
    string Message,
    string? CurrentScreenRuntimeType = null,
    bool? RelicCollectionVisible = null)
{
    public static TreasureRelicReadiness Ready(string? currentScreenRuntimeType, bool? relicCollectionVisible)
        => new(
            CanReadRelics: true,
            Availability: "available",
            Message: "treasure relic collection is visible and stable",
            CurrentScreenRuntimeType: currentScreenRuntimeType,
            RelicCollectionVisible: relicCollectionVisible);

    public static TreasureRelicReadiness Unavailable(
        string availability,
        string message,
        string? currentScreenRuntimeType = null,
        bool? relicCollectionVisible = null)
        => new(
            CanReadRelics: false,
            Availability: availability,
            Message: message,
            CurrentScreenRuntimeType: currentScreenRuntimeType,
            RelicCollectionVisible: relicCollectionVisible);
}
