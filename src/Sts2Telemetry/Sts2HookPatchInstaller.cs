using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace Sts2Telemetry;

internal static class Sts2HookPatchInstaller
{
    private static readonly UiDecisionPatchTarget[] UiDecisionPatchTargets =
    {
        new(
            "MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteRoom",
            "AfterSelectingOption",
            "ui.rest.after_selecting_option",
            null),
        new(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom",
            "AfterSelectingOption",
            "ui.rest.after_selecting_option",
            null),
        new(
            "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry",
            "OnTryPurchaseWrapper",
            "ui.shop.on_try_purchase",
            new[] { typeof(MerchantInventory), typeof(bool) }),
        new(
            "MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry",
            "OnTryPurchaseWrapper",
            "ui.shop.card_removal.on_try_purchase",
            new[] { typeof(MerchantInventory), typeof(bool), typeof(bool) }),
        new(
            "MegaCrit.Sts2.Core.Rewards.Reward",
            "OnSelectWrapper",
            "ui.reward.on_select_wrapper",
            null),
        new(
            "MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            "ChooseLocalOption",
            "runtime.event.choose_local_option",
            new[] { typeof(int) }),
        new(
            "MegaCrit.Sts2.Core.Multiplayer.Game.RestSiteSynchronizer",
            "ChooseLocalOption",
            "runtime.rest.choose_local_option",
            new[] { typeof(int) }),
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen",
            "CardsSelected",
            "ui.card_reward.cards_selected",
            null),
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen",
            "SelectCard",
            "runtime.card_reward.select_card",
            new[] { typeof(NCardHolder) }),
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChoiceSelectionSkipButton",
            "OnPress",
            "ui.card_selection.skip_pressed",
            null)
    };

    private static readonly RuntimeSignalPatchTarget[] RuntimeSignalPatchTargets =
    {
        new(
            "MegaCrit.Sts2.Core.Rewards.RewardsSet",
            "GenerateWithoutOffering",
            "runtime.rewards_set.generate_without_offering",
            nameof(PatchCallbacks.AfterRewardsGenerated),
            Array.Empty<Type>()),
        new(
            "MegaCrit.Sts2.Core.Models.RelicModel",
            "Flash",
            "runtime.relic_model.flash_no_args",
            nameof(PatchCallbacks.AfterRelicFlashNoArgs),
            Array.Empty<Type>()),
        new(
            "MegaCrit.Sts2.Core.Models.RelicModel",
            "Flash",
            "runtime.relic_model.flash",
            nameof(PatchCallbacks.AfterRelicFlash),
            new[] { typeof(IEnumerable<Creature>) }),
        new(
            "MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry",
            "InvokePurchaseCompleted",
            "runtime.shop.purchase_completed",
            nameof(PatchCallbacks.AfterShopPurchaseCompleted),
            new[] { typeof(MerchantEntry) }),
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory",
            "OnPurchaseCompleted",
            "runtime.shop.inventory_purchase_completed",
            nameof(PatchCallbacks.AfterShopInventoryPurchaseCompleted),
            new[] { typeof(PurchaseStatus), typeof(MerchantEntry) })
    };

    private static readonly RuntimeSignalPatchTarget[] RuntimeOpeningPatchTargets =
    {
        new(
            "MegaCrit.Sts2.Core.Rewards.CardReward",
            "OnSelect",
            "runtime.card_reward.on_select",
            nameof(PatchCallbacks.BeforeCardRewardOpened),
            Array.Empty<Type>()),
        new(
            "MegaCrit.Sts2.Core.Commands.RelicSelectCmd",
            "FromChooseARelicScreen",
            "runtime.relic_select.choose_a_relic",
            nameof(PatchCallbacks.BeforeRelicSelectOpened),
            new[] { typeof(Player), typeof(IReadOnlyList<RelicModel>) }),
        new(
            "MegaCrit.Sts2.Core.Commands.CardSelectCmd",
            "FromChooseABundleScreen",
            "runtime.bundle_select.choose_a_bundle",
            nameof(PatchCallbacks.BeforeBundleSelectOpened),
            new[] { typeof(Player), typeof(IReadOnlyList<IReadOnlyList<CardModel>>) })
    };

    private static readonly RuntimeSignalPatchTarget[] MainMenuUiPatchTargets =
    {
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu",
            "_Ready",
            "main_menu.ready",
            nameof(PatchCallbacks.AfterMainMenuReady),
            Array.Empty<Type>()),
        new(
            "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu",
            "_ExitTree",
            "main_menu.exit_tree",
            nameof(PatchCallbacks.AfterMainMenuExit),
            Array.Empty<Type>())
    };

    public static PatchInstallReport Install(Harmony harmony)
    {
        var results = new List<PatchInstallResult>();
        results.AddRange(PatchLifecyclePostfix(harmony, "MegaCrit.Sts2.Core.Runs.RunManager", "SetUpSavedSinglePlayer",
            nameof(PatchCallbacks.AfterSetUpSavedSinglePlayer)));
        results.AddRange(PatchLifecyclePrefix(harmony, "MegaCrit.Sts2.Core.Runs.RunManager", "CleanUp",
            nameof(PatchCallbacks.BeforeRunCleanUp)));
        results.AddRange(PatchLifecyclePrefix(harmony, "MegaCrit.Sts2.Core.Runs.RunManager", "Abandon",
            nameof(PatchCallbacks.BeforeRunAbandon)));
        results.AddRange(PatchLifecyclePrefix(harmony, "MegaCrit.Sts2.Core.Runs.RunManager", "OnEnded",
            nameof(PatchCallbacks.BeforeRunEnded)));

        results.AddRange(PatchLifecyclePostfix(harmony, "MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager", "SaveRun",
            nameof(PatchCallbacks.AfterSaveRun)));
        results.AddRange(PatchLifecyclePostfix(harmony, "MegaCrit.Sts2.Core.Saves.SaveManager", "SaveRun",
            nameof(PatchCallbacks.AfterSaveRun)));
        results.AddRange(PatchLifecyclePostfix(harmony, "MegaCrit.Sts2.Core.Saves.SaveManager", "LoadRunSave",
            nameof(PatchCallbacks.AfterLoadRunSave)));

        foreach (UiDecisionPatchTarget target in UiDecisionPatchTargets)
        {
            if (target.ParameterTypes == null)
                results.AddRange(PatchUiDecisionPrefix(harmony, target.TypeName, target.MethodName, target.Source));
            else
                results.AddRange(PatchUiDecisionPrefix(harmony, target.TypeName, target.MethodName, target.Source, target.ParameterTypes));
        }

        foreach (RuntimeSignalPatchTarget target in RuntimeSignalPatchTargets)
            results.AddRange(PatchRuntimeSignalPostfix(harmony, target.TypeName, target.MethodName, target.CallbackName, target.ParameterTypes));

        foreach (RuntimeSignalPatchTarget target in RuntimeOpeningPatchTargets)
            results.AddRange(PatchRuntimeSignalPrefix(harmony, target.TypeName, target.MethodName, target.CallbackName, target.ParameterTypes));

        foreach (RuntimeSignalPatchTarget target in MainMenuUiPatchTargets)
            results.AddRange(PatchRuntimeSignalPostfix(harmony, target.TypeName, target.MethodName, target.CallbackName, target.ParameterTypes));

        return new PatchInstallReport(results);
    }

    internal static IReadOnlyList<UiDecisionPatchTarget> UiDecisionPatchTargetsForTests()
        => UiDecisionPatchTargets;

    internal static IReadOnlyList<RuntimeSignalPatchTarget> RuntimeSignalPatchTargetsForTests()
        => RuntimeSignalPatchTargets;

    internal static IReadOnlyList<RuntimeSignalPatchTarget> RuntimeOpeningPatchTargetsForTests()
        => RuntimeOpeningPatchTargets;

    internal static IReadOnlyList<RuntimeSignalPatchTarget> MainMenuUiPatchTargetsForTests()
        => MainMenuUiPatchTargets;

    private static IReadOnlyList<PatchInstallResult> PatchLifecyclePrefix(Harmony harmony, string typeName, string methodName, string callbackName)
        => PatchAllMatching(harmony, typeName, methodName, prefixName: callbackName, postfixName: null, source: null);

    private static IReadOnlyList<PatchInstallResult> PatchLifecyclePostfix(Harmony harmony, string typeName, string methodName, string callbackName)
        => PatchAllMatching(harmony, typeName, methodName, prefixName: null, postfixName: callbackName, source: null);

    private static IReadOnlyList<PatchInstallResult> PatchRuntimeSignalPostfix(
        Harmony harmony,
        string typeName,
        string methodName,
        string callbackName,
        IReadOnlyList<Type> parameterTypes)
        => PatchAllMatching(
            harmony,
            typeName,
            methodName,
            prefixName: null,
            postfixName: callbackName,
            source: null,
            parameterTypes: parameterTypes);

    private static IReadOnlyList<PatchInstallResult> PatchRuntimeSignalPrefix(
        Harmony harmony,
        string typeName,
        string methodName,
        string callbackName,
        IReadOnlyList<Type> parameterTypes)
        => PatchAllMatching(
            harmony,
            typeName,
            methodName,
            prefixName: callbackName,
            postfixName: null,
            source: null,
            parameterTypes: parameterTypes);

    private static IReadOnlyList<PatchInstallResult> PatchUiDecisionPrefix(Harmony harmony, string typeName, string methodName, string source)
        => PatchAllMatching(harmony, typeName, methodName, nameof(PatchCallbacks.BeforeUiDecision), null, source, parameterTypes: null);

    private static IReadOnlyList<PatchInstallResult> PatchUiDecisionPrefix(
        Harmony harmony,
        string typeName,
        string methodName,
        string source,
        params Type[] parameterTypes)
        => PatchAllMatching(harmony, typeName, methodName, nameof(PatchCallbacks.BeforeUiDecision), null, source, parameterTypes);

    private static IReadOnlyList<PatchInstallResult> PatchAllMatching(
        Harmony harmony,
        string typeName,
        string methodName,
        string? prefixName,
        string? postfixName,
        string? source,
        IReadOnlyList<Type>? parameterTypes = null)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type == null)
            return new[]
            {
                PatchInstallResult.MissingType(typeName, methodName, source, prefixName, postfixName, parameterTypes)
            };

        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName
                && !method.IsGenericMethod
                && ParametersMatch(method, parameterTypes))
            .Cast<MethodBase>()
            .ToList();

        if (methods.Count == 0)
            return new[]
            {
                PatchInstallResult.MissingMethod(typeName, methodName, source, prefixName, postfixName, parameterTypes)
            };

        HarmonyMethod? prefix;
        HarmonyMethod? postfix;
        try
        {
            prefix = prefixName == null ? null : BuildHarmonyMethod(prefixName);
            postfix = postfixName == null ? null : BuildHarmonyMethod(postfixName);
        }
        catch (Exception ex)
        {
            return methods
                .Select(method => PatchInstallResult.Failed(typeName, methodName, method, source, prefixName, postfixName, parameterTypes, ex))
                .ToArray();
        }

        var results = new List<PatchInstallResult>();
        foreach (MethodBase method in methods)
        {
            try
            {
                harmony.Patch(method, prefix, postfix);
                results.Add(PatchInstallResult.Patched(
                    typeName,
                    methodName,
                    method,
                    source,
                    prefixName,
                    postfixName,
                    parameterTypes));
            }
            catch (Exception ex)
            {
                results.Add(PatchInstallResult.Failed(
                    typeName,
                    methodName,
                    method,
                    source,
                    prefixName,
                    postfixName,
                    parameterTypes,
                    ex));
            }
        }

        return results;
    }

    private static HarmonyMethod BuildHarmonyMethod(string callbackName)
    {
        MethodInfo method = AccessTools.Method(typeof(PatchCallbacks), callbackName)
            ?? throw new MissingMethodException(nameof(PatchCallbacks), callbackName);
        return new HarmonyMethod(method);
    }

    private static bool ParametersMatch(MethodInfo method, IReadOnlyList<Type>? parameterTypes)
    {
        if (parameterTypes == null)
            return true;

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != parameterTypes.Count)
            return false;

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType != parameterTypes[i])
                return false;
        }

        return true;
    }

    internal readonly record struct UiDecisionPatchTarget(
        string TypeName,
        string MethodName,
        string Source,
        Type[]? ParameterTypes);

    internal readonly record struct RuntimeSignalPatchTarget(
        string TypeName,
        string MethodName,
        string Source,
        string CallbackName,
        Type[] ParameterTypes);

    internal sealed class PatchInstallReport
    {
        public PatchInstallReport(IReadOnlyList<PatchInstallResult> results)
        {
            Results = results;
        }

        public IReadOnlyList<PatchInstallResult> Results { get; }

        public int PatchedMethodCount => Results.Count(result => result.Status == "patched");

        public int MissingTargetCount => Results.Count(result => result.Status is "type_missing" or "method_missing");

        public int FailedPatchCount => Results.Count(result => result.Status == "patch_failed");

        public IReadOnlyDictionary<string, object?> ToRecord()
            => new Dictionary<string, object?>
            {
                ["source"] = "harmony.patch_installer",
                ["patched_method_count"] = PatchedMethodCount,
                ["missing_target_count"] = MissingTargetCount,
                ["failed_patch_count"] = FailedPatchCount,
                ["target_count"] = Results.Count,
                ["targets"] = Results.Select(result => result.ToRecord()).ToArray()
            };
    }

    internal readonly record struct PatchInstallResult(
        string TypeName,
        string MethodName,
        string? Source,
        string? PrefixCallback,
        string? PostfixCallback,
        string ParameterSignature,
        string? PatchedMethod,
        string Status,
        string? ErrorType,
        string? ErrorMessage)
    {
        public static PatchInstallResult Patched(
            string typeName,
            string methodName,
            MethodBase method,
            string? source,
            string? prefixName,
            string? postfixName,
            IReadOnlyList<Type>? parameterTypes)
            => new(
                typeName,
                methodName,
                source,
                prefixName,
                postfixName,
                BuildParameterSignature(parameterTypes),
                DescribeMethod(method),
                "patched",
                null,
                null);

        public static PatchInstallResult MissingType(
            string typeName,
            string methodName,
            string? source,
            string? prefixName,
            string? postfixName,
            IReadOnlyList<Type>? parameterTypes)
            => new(
                typeName,
                methodName,
                source,
                prefixName,
                postfixName,
                BuildParameterSignature(parameterTypes),
                null,
                "type_missing",
                null,
                null);

        public static PatchInstallResult MissingMethod(
            string typeName,
            string methodName,
            string? source,
            string? prefixName,
            string? postfixName,
            IReadOnlyList<Type>? parameterTypes)
            => new(
                typeName,
                methodName,
                source,
                prefixName,
                postfixName,
                BuildParameterSignature(parameterTypes),
                null,
                "method_missing",
                null,
                null);

        public static PatchInstallResult Failed(
            string typeName,
            string methodName,
            MethodBase method,
            string? source,
            string? prefixName,
            string? postfixName,
            IReadOnlyList<Type>? parameterTypes,
            Exception exception)
            => new(
                typeName,
                methodName,
                source,
                prefixName,
                postfixName,
                BuildParameterSignature(parameterTypes),
                DescribeMethod(method),
                "patch_failed",
                exception.GetType().FullName,
                exception.Message);

        public IReadOnlyDictionary<string, object?> ToRecord()
            => new Dictionary<string, object?>
            {
                ["type_name"] = TypeName,
                ["method_name"] = MethodName,
                ["source"] = Source,
                ["prefix_callback"] = PrefixCallback,
                ["postfix_callback"] = PostfixCallback,
                ["parameter_signature"] = ParameterSignature,
                ["patched_method"] = PatchedMethod,
                ["status"] = Status,
                ["error_type"] = ErrorType,
                ["error_message"] = ErrorMessage
            };

        private static string BuildParameterSignature(IReadOnlyList<Type>? parameterTypes)
            => parameterTypes == null
                ? "*"
                : string.Join(",", parameterTypes.Select(type => type.FullName ?? type.Name));

        private static string DescribeMethod(MethodBase method)
            => $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name))})";
    }
}
