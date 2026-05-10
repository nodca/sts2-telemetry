using System.Reflection;
using MegaCrit.Sts2.Core.Rewards;

namespace Sts2Telemetry;

internal static class PatchCallbacks
{
    public static void AfterSetUpSavedSinglePlayer()
        => Sts2TelemetryMod.OnSavedRunLoadedFromPatch("run_manager.set_up_saved_single_player");

    public static void AfterLoadRunSave()
        => Sts2TelemetryMod.OnSavePreviewFromPatch("save_manager.load_run_save");

    public static void AfterSaveRun()
        => Sts2TelemetryMod.OnSaveObservedFromPatch("save_run.postfix");

    public static void BeforeRunCleanUp(object[] __args)
    {
        Sts2TelemetryMod.OnRunSuspendedFromPatch("run_manager.cleanup", new Dictionary<string, object?>
        {
            ["graceful"] = __args.Length > 0 ? __args[0] : null
        });
    }

    public static void BeforeRunAbandon()
        => Sts2TelemetryMod.OnRunSuspendedFromPatch("run_manager.abandon", new Dictionary<string, object?>
        {
            ["reason"] = "abandon"
        });

    public static void BeforeRunEnded(object[] __args)
    {
        bool? isVictory = __args.Length > 0 && __args[0] is bool value ? value : null;
        Sts2TelemetryMod.OnRunEndedFromPatch("run_manager.on_ended", isVictory);
    }

    public static void AfterMainMenuReady()
        => Sts2TelemetryMod.OnMainMenuReadyFromPatch("main_menu.ready");

    public static void AfterMainMenuExit()
        => Sts2TelemetryMod.OnMainMenuExitFromPatch("main_menu.exit_tree");

    public static void BeforeUiDecision(object __instance, object[] __args, MethodBase __originalMethod)
    {
        string source = $"{__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}";
        Sts2TelemetryMod.OnUiDecisionFromPatch(source, __instance, __args);
    }

    public static void AfterRelicFlash(object __instance, object[] __args)
    {
        object? targets = __args.Length > 0 ? __args[0] : null;
        Sts2TelemetryMod.OnRelicTriggeredFromPatch("runtime.relic_model.flash", __instance, targets);
    }

    public static void AfterRelicFlashNoArgs(object __instance)
        => Sts2TelemetryMod.OnRelicTriggeredFromPatch("runtime.relic_model.flash_no_args", __instance, null);

    public static void AfterShopPurchaseCompleted(object[] __args)
    {
        object? entry = __args.Length > 0 ? __args[0] : null;
        Sts2TelemetryMod.OnShopPurchaseCompletedFromPatch(
            "runtime.shop.purchase_completed",
            entry,
            __args);
    }

    public static void AfterShopInventoryPurchaseCompleted(object[] __args)
    {
        object? entry = __args.Length > 1 ? __args[1] : null;
        Sts2TelemetryMod.OnShopPurchaseCompletedFromPatch(
            "runtime.shop.inventory_purchase_completed",
            entry,
            __args);
    }

    public static void BeforeCardRewardOpened(object __instance)
        => Sts2TelemetryMod.OnCardRewardOpenedFromPatch("runtime.card_reward.on_select", __instance);

    public static void BeforeRelicSelectOpened(object[] __args)
    {
        object? player = __args.Length > 0 ? __args[0] : null;
        object? relics = __args.Length > 1 ? __args[1] : null;
        Sts2TelemetryMod.OnRelicSelectOpenedFromPatch("runtime.relic_select.choose_a_relic", player, relics);
    }

    public static void BeforeBundleSelectOpened(object[] __args)
    {
        object? player = __args.Length > 0 ? __args[0] : null;
        object? bundles = __args.Length > 1 ? __args[1] : null;
        Sts2TelemetryMod.OnBundleSelectOpenedFromPatch("runtime.bundle_select.choose_a_bundle", player, bundles);
    }

    public static void AfterRewardsGenerated(object __instance, ref Task<List<Reward>> __result)
    {
        if (__result != null)
            __result = TrackRewardsGenerated(__instance, __result);
    }

    private static async Task<List<Reward>> TrackRewardsGenerated(object rewardsSet, Task<List<Reward>> original)
    {
        List<Reward> rewards = await original;
        Sts2TelemetryMod.OnRewardsGeneratedFromPatch(
            "runtime.rewards_set.generate_without_offering",
            rewardsSet,
            rewards);
        return rewards;
    }
}
