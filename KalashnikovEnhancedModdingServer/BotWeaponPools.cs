using System.Text.Json.Nodes;
using HarmonyLib;
using SPTarkov.Server.Core.Generators.Bot;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Bots;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace KalashnikovEnhancedModding;

/// <summary>Repairs the inventory actually handed to SPT, including pools supplied by later-loading mods.</summary>
public sealed class BotWeaponPools(TemplateTable templates, JsonObject rules, HashSet<MongoId> changedTemplates)
{
    private readonly HashSet<MongoId> _changedTemplates=changedTemplates;
    private readonly HashSet<MongoId> _weapons = rules["weaponIds"]!.AsArray()
        .Select(id => (MongoId)MigrationEngine.S(id)).ToHashSet();
    private static BotWeaponPools? _instance;

    public void Enable()
    {
        _instance = this;
        var harmony=new Harmony("com.baliston.kalashnikov-enhanced-modding.bot-pools");
        harmony.Patch(
            AccessTools.Method(typeof(BotWeaponGenerator), nameof(BotWeaponGenerator.GenerateWeaponByTpl)),
            prefix: new HarmonyMethod(typeof(BotWeaponPools), nameof(BeforeGenerate)));
        harmony.Patch(
            AccessTools.Method(typeof(BotEquipmentModGenerator), nameof(BotEquipmentModGenerator.GenerateModsForWeapon)),
            prefix: new HarmonyMethod(typeof(BotWeaponPools), nameof(BeforeGenerateAttachments)));
    }

    private static bool BeforeGenerateAttachments(ref GenerateWeaponRequest request, ref List<Item> __result)
    {
        if (_instance is not { } instance || request.ParentTemplate is not { } parent
            || !instance._changedTemplates.Contains(parent.Id) || request.ModPool is null || request.Weapon is not { } weapon) return true;
        // Dynamic PMC choices can reach parts absent from the initial traversal. Repair the actual
        // selected part at every recursion, including changed parts mounted on otherwise vanilla guns.
        var inventory=instance.Prepare(parent.Id,new BotTypeInventory { Mods=request.ModPool });
        request=request with { ModPool=inventory.Mods };
        if (inventory.Mods.TryGetValue(parent.Id,out var pool) && pool.Count==0)
        {
            // A split part may no longer have any child sockets. SPT must not recurse into its old pool.
            __result=weapon;
            return false;
        }
        return true;
    }

    private static void BeforeGenerate(MongoId weaponTpl, ref BotTypeInventory botTemplateInventory)
    {
        if (_instance is { } instance && instance._weapons.Contains(weaponTpl))
            botTemplateInventory = instance.Prepare(weaponTpl, botTemplateInventory);
    }

    public BotTypeInventory Prepare(MongoId weapon, BotTypeInventory inventory)
    {
        // SPT and other mods may reuse inventory dictionaries. Only modify request-owned copies.
        var pools = new Dictionary<MongoId, Dictionary<string, HashSet<MongoId>>>(inventory.Mods);
        var visited = new HashSet<MongoId>();
        var original = pools.GetValueOrDefault(weapon) ?? new();
        var relocated = new Dictionary<string, HashSet<MongoId>>();
        foreach (var name in new[] { "mod_gas_block", "mod_muzzle", "mod_sight_rear", "mod_launcher" })
            if (original.TryGetValue(name, out var choices)) relocated[name] = new(choices);

        // The old gas-block pool carries the lower handguard, which now belongs to the receiver.
        var lowers = new HashSet<MongoId>();
        if (original.TryGetValue("mod_gas_block", out var gases))
            foreach (var gas in gases)
                if (pools.TryGetValue(gas, out var gasPool) && gasPool.TryGetValue("mod_handguard", out var guards))
                    foreach (var guard in guards)
                        lowers.Add(rules["separatedLowers"]?[guard.ToString()] is { } lower
                            ? (MongoId)MigrationEngine.S(lower) : guard);

        Populate(weapon);
        return inventory with { Mods = pools };

        void Populate(MongoId tpl)
        {
            if (!visited.Add(tpl) || !templates.Items.TryGetValue(tpl, out var item)) return;
            var slots = item.Properties?.Slots?.ToArray() ?? [];
            var source = pools.GetValueOrDefault(tpl);
            if (slots.Length == 0 && !_changedTemplates.Contains(tpl)) return;
            var current = new Dictionary<string, HashSet<MongoId>>();
            // Cartridge/chamber pools are separate from attachment Slots.
            if (source is not null)
                foreach (var (name, choices) in source)
                    if (!name.StartsWith("mod_", StringComparison.Ordinal)) current[name] = new(choices);
            foreach (var slot in slots)
            {
                if (string.IsNullOrEmpty(slot.Name)) continue;
                var allowed = new HashSet<MongoId>();
                foreach (var filter in slot.Properties?.Filters ?? [])
                    foreach (var id in filter.Filter ?? [])
                        if (templates.Items.ContainsKey(id)) allowed.Add(id);
                var selected = new HashSet<MongoId>(source?.GetValueOrDefault(slot.Name) ?? []);
                selected.IntersectWith(allowed);
                if (selected.Count == 0 && tpl == weapon && slot.Name == "mod_handguard")
                {
                    selected.UnionWith(lowers);
                    selected.IntersectWith(allowed);
                }
                if (selected.Count == 0 && item.Parent.ToString() == "555ef6e44bdc2de9068b457e"
                    && relocated.TryGetValue(slot.Name, out var moved))
                {
                    selected.UnionWith(moved);
                    selected.IntersectWith(allowed);
                }
                if (selected.Count == 0 && slot.Name == "mod_barrel" && tpl == weapon
                    && rules["barrels"]?[weapon.ToString()] is { } barrel && allowed.Contains(MigrationEngine.S(barrel)))
                    selected.Add(MigrationEngine.S(barrel));
                if (selected.Count == 0 && (slot.Required == true || source is null || source.ContainsKey(slot.Name)))
                    selected.UnionWith(allowed);
                if (selected.Count == 0) continue;
                current[slot.Name] = selected;
                foreach (var child in selected) Populate(child);
            }
            pools[tpl] = current;
        }
    }
}
