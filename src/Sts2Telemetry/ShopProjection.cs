namespace Sts2Telemetry;

internal static class ShopProjection
{
    public static Dictionary<string, object?>? BuildLegalAction(
        object entry,
        string category,
        string actionType,
        int index,
        object? player)
    {
        ShopEntryProjection projection = ProjectEntry(entry, category, actionType, player);
        if (projection.IsStocked == false || projection.Used == true || projection.EnoughGold == false)
            return null;

        var action = new Dictionary<string, object?>
        {
            ["action_type"] = projection.ActionType,
            ["source"] = "merchant_inventory",
            ["category"] = projection.Category,
            ["index"] = index,
            ["id"] = projection.Id,
            ["name"] = ReflectionUtil.GetText(projection.Item, "Title", "Name")
                ?? ReflectionUtil.GetText(entry, "Title", "Name", "Description"),
            ["price"] = projection.Price,
            ["is_stocked"] = projection.IsStocked,
            ["enough_gold"] = projection.EnoughGold,
            ["used"] = projection.Used,
            ["can_buy"] = projection.CanBuy,
            ["match_key"] = BuildMatchKey(projection.ActionType, projection.Category, index, projection.Id)
        };

        AddTypedItemId(action, projection);
        return action;
    }

    public static Dictionary<string, object?> BuildOffer(
        object entry,
        string category,
        string actionType,
        int index,
        object? player)
    {
        ShopEntryProjection projection = ProjectEntry(entry, category, actionType, player);
        var offer = new Dictionary<string, object?>
        {
            ["action_type"] = projection.ActionType,
            ["source"] = "merchant_inventory",
            ["category"] = projection.Category,
            ["index"] = index,
            ["id"] = projection.Id,
            ["price"] = projection.Price,
            ["is_stocked"] = projection.IsStocked,
            ["enough_gold"] = projection.EnoughGold,
            ["used"] = projection.Used,
            ["can_buy"] = projection.CanBuy,
            ["availability"] = BuildAvailability(projection),
            ["shop_entry_runtime_type"] = entry.GetType().FullName,
            ["shop_entry_runtime_type_name"] = entry.GetType().Name,
            ["item_runtime_type"] = projection.Item?.GetType().FullName,
            ["item_runtime_type_name"] = projection.Item?.GetType().Name,
            ["text_status"] = "text_suppressed_shop_offer_runtime_safety",
            ["match_key"] = BuildMatchKey(projection.ActionType, projection.Category, index, projection.Id)
        };

        AddTypedItemId(offer, projection);
        return offer;
    }

    public static void AddSignalMetadata(
        IDictionary<string, object?> metadata,
        object? entry,
        string source,
        string purchaseStatus)
    {
        string normalizedStatus = NormalizePurchaseStatus(purchaseStatus);
        metadata["shop_signal_status"] = normalizedStatus;
        metadata["purchase_status"] = normalizedStatus;
        if (!string.Equals(normalizedStatus, purchaseStatus, StringComparison.Ordinal))
            metadata["raw_purchase_status"] = purchaseStatus;
        metadata["projection_policy"] = "typed_merchant_entry_signal";
        metadata["text_status"] = "text_suppressed_runtime_signal_safety";

        if (entry == null)
        {
            metadata["shop_entry_extraction"] = "entry_unavailable";
            return;
        }

        string category = InferCategory(entry, source);
        string actionType = InferActionType(category);
        ShopEntryProjection projection = ProjectEntry(entry, category, actionType, player: null);

        metadata["action_type"] = projection.ActionType;
        metadata["category"] = projection.Category;
        metadata["shop_entry_runtime_type"] = entry.GetType().FullName;
        metadata["shop_entry_runtime_type_name"] = entry.GetType().Name;
        metadata["id"] = projection.Id;
        metadata["price"] = projection.Price;
        metadata["is_stocked"] = projection.IsStocked;
        metadata["enough_gold"] = projection.EnoughGold;
        metadata["used"] = projection.Used;
        metadata["can_buy"] = projection.CanBuy;
        metadata["item_runtime_type"] = projection.Item?.GetType().FullName;
        metadata["item_runtime_type_name"] = projection.Item?.GetType().Name;
        metadata["match_key"] = BuildMatchKey(projection.ActionType, projection.Category, index: null, projection.Id);
        AddTypedItemId(metadata, projection);
    }

    public static string InferActionTypeFromEntry(object? entry, string source)
        => InferActionType(InferCategory(entry, source));

    public static object? ResolveShopItem(object entry, string category)
    {
        if (category == "card_removal")
            return null;

        if (category.Contains("card", StringComparison.Ordinal))
        {
            object? creationResult = ReflectionUtil.GetMemberValue(entry, "CreationResult");
            return ReflectionUtil.GetMemberValue(creationResult, "Card")
                ?? ReflectionUtil.GetMemberValue(entry, "Card", "Item", "Reward");
        }

        if (category == "relic")
            return ReflectionUtil.GetMemberValue(entry, "Model", "Relic", "Item", "Reward");

        if (category == "potion")
            return ReflectionUtil.GetMemberValue(entry, "Model", "Potion", "Item", "Reward");

        return ReflectionUtil.GetMemberValue(entry, "Item", "Reward");
    }

    private static ShopEntryProjection ProjectEntry(
        object entry,
        string category,
        string actionType,
        object? player)
    {
        bool? isStocked = ReflectionUtil.GetBool(entry, "IsStocked", "Stocked");
        bool? used = ReflectionUtil.GetBool(entry, "Used", "WasUsed", "IsUsed");
        int? price = ReflectionUtil.GetInt(entry, "Cost", "Price", "GoldCost", "MerchantCost");
        bool? enoughGold = ReflectionUtil.GetBool(entry, "EnoughGold", "CanAfford");
        if (enoughGold == null)
        {
            int? gold = ReflectionUtil.GetInt(player, "Gold", "CurrentGold");
            if (gold != null && price != null)
                enoughGold = gold.Value >= price.Value;
        }

        object? item = ResolveShopItem(entry, category);
        string? id = GetEntityId(item) ?? GetEntityId(entry) ?? (category == "card_removal" ? "card_removal" : null);
        bool canBuy = isStocked != false && used != true && enoughGold != false;
        return new ShopEntryProjection(category, actionType, item, id, price, isStocked, enoughGold, used, canBuy);
    }

    private static string InferCategory(object? entry, string source)
    {
        string runtimeTypeName = entry?.GetType().Name ?? "";
        string combined = $"{source}.{runtimeTypeName}";
        if (combined.Contains("CardRemoval", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("card_removal", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("remove_card", StringComparison.OrdinalIgnoreCase))
        {
            return "card_removal";
        }

        if (combined.Contains("Relic", StringComparison.OrdinalIgnoreCase))
            return "relic";
        if (combined.Contains("Potion", StringComparison.OrdinalIgnoreCase))
            return "potion";
        if (combined.Contains("Card", StringComparison.OrdinalIgnoreCase))
            return "card";

        if (entry != null)
        {
            if (ResolveShopItem(entry, "card") != null)
                return "card";
            if (ResolveShopItem(entry, "relic") != null)
                return "relic";
            if (ResolveShopItem(entry, "potion") != null)
                return "potion";
        }

        return "shop_entry";
    }

    private static string InferActionType(string category)
        => category switch
        {
            "card" or "character_card" or "colorless_card" => "buy_shop_card",
            "relic" => "buy_shop_relic",
            "potion" => "buy_shop_potion",
            "card_removal" => "remove_card_at_shop",
            _ => "shop_purchase"
        };

    private static string NormalizePurchaseStatus(string purchaseStatus)
        => purchaseStatus switch
        {
            "Success" or "success" or "PurchaseStatus.Success" => "completed",
            _ => purchaseStatus
        };

    private static string BuildAvailability(ShopEntryProjection projection)
    {
        if (projection.IsStocked == false)
            return "not_stocked";
        if (projection.Used == true)
            return "used";
        if (projection.EnoughGold == false)
            return "insufficient_gold";
        if (projection.CanBuy)
            return "available";
        return "unavailable";
    }

    private static SortedDictionary<string, object?> BuildMatchKey(
        string actionType,
        string category,
        int? index,
        string? id)
    {
        var matchKey = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action_type"] = actionType,
            ["category"] = category,
            ["id"] = id
        };
        if (index != null)
            matchKey["index"] = index.Value;
        return matchKey;
    }

    private static void AddTypedItemId(
        IDictionary<string, object?> target,
        ShopEntryProjection projection)
    {
        switch (projection.ActionType)
        {
            case "buy_shop_card":
                target["card_id"] = projection.Id;
                break;
            case "buy_shop_relic":
                target["relic_id"] = projection.Id;
                break;
            case "buy_shop_potion":
                target["potion_id"] = projection.Id;
                break;
            case "remove_card_at_shop":
                target["removal_id"] = projection.Id;
                break;
        }
    }

    private static string? GetEntityId(object? value)
    {
        object? id = ReflectionUtil.GetMemberValue(value, "Id", "ID", "Key");
        return StableScalarText(id)
            ?? StableScalarText(ReflectionUtil.GetMemberValue(id, "Entry", "Value", "Id", "Key"));
    }

    private static string? StableScalarText(object? value)
    {
        if (value == null)
            return null;

        Type type = value.GetType();
        if (type.IsEnum)
            return value.ToString();

        return value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            bool boolValue => boolValue.ToString(),
            int intValue => intValue.ToString(),
            long longValue => longValue.ToString(),
            uint uintValue => uintValue.ToString(),
            ulong ulongValue => ulongValue.ToString(),
            _ => null
        };
    }

    private readonly record struct ShopEntryProjection(
        string Category,
        string ActionType,
        object? Item,
        string? Id,
        int? Price,
        bool? IsStocked,
        bool? EnoughGold,
        bool? Used,
        bool CanBuy);
}
