using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;

namespace KalashnikovEnhancedModding.Patches;

public sealed class GenerateModsForWeaponPatch(BotWeaponPools pools) : AbstractPatch
{
    private static BotWeaponPools? _pools;

    protected override MethodBase GetTargetMethod()
    {
        _pools = pools;
        return AccessTools.Method(typeof(BotEquipmentModGenerator), nameof(BotEquipmentModGenerator.GenerateModsForWeapon));
    }

    [PatchPrefix, UsedImplicitly]
    private static bool Prefix(ref GenerateWeaponRequest request, ref List<Item> __result)
    {
        if (_pools is not { } instance || request.ParentTemplate is not { } parent
            || !instance.IsChangedTemplate(parent.Id) || request.ModPool is null || request.Weapon is not { } weapon)
        {
            return true;
        }

        // Dynamic PMC selections can reach changed parts after the initial weapon traversal.
        // Repair each selected part, including parts fitted to otherwise vanilla weapons.
        var inventory = instance.Prepare(parent.Id, new BotTypeInventory { Mods = request.ModPool });
        request = request with { ModPool = inventory.Mods };

        if (inventory.Mods.TryGetValue(parent.Id, out var pool) && pool.Count == 0)
        {
            // Split parts with no remaining child sockets must not recurse into their old pools.
            __result = weapon;
            return false;
        }

        return true;
    }
}
