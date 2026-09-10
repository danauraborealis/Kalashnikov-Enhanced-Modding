using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace KalashnikovEnhancedModding.Patches;

public sealed class GenerateWeaponByTplPatch(BotWeaponPools pools) : AbstractPatch
{
    private static BotWeaponPools? _pools;

    protected override MethodBase GetTargetMethod()
    {
        _pools = pools;
        return AccessTools.Method(typeof(BotWeaponGenerator), nameof(BotWeaponGenerator.GenerateWeaponByTpl));
    }

    [PatchPrefix, UsedImplicitly]
    private static void Prefix(MongoId weaponTpl, ref BotTypeInventory botTemplateInventory)
    {
        if (_pools is { } instance && instance.IsWeapon(weaponTpl))
        {
            botTemplateInventory = instance.Prepare(weaponTpl, botTemplateInventory);
        }
    }
}
