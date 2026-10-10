// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace WhiteFox.Core.Data
{
    /// <summary>
    /// Mining speeds of tools per block material (minecraft-data materials.json, Minosoft's ToolItem/MiningToolItem),
    /// edible items (foods.json) and other client side item properties.
    /// </summary>
    public sealed class ItemProperties
    {
        private readonly Dictionary<string, Dictionary<int, float>> materials = new Dictionary<string, Dictionary<int, float>>();
        private readonly HashSet<int> foods = new HashSet<int>();
        private readonly HashSet<int> alwaysEdible = new HashSet<int>();

        private static readonly HashSet<string> Usable = new HashSet<string>
        {
            "bow", "crossbow", "trident", "shield", "potion", "milk_bucket", "spyglass", "goat_horn", "honey_bottle", "brush",
        };

        private static readonly Dictionary<string, int[]> Armor = new Dictionary<string, int[]>
        {
            // helmet, chestplate, leggings, boots
            { "leather", new[] { 1, 3, 2, 1 } },
            { "chainmail", new[] { 2, 5, 4, 1 } },
            { "iron", new[] { 2, 6, 5, 2 } },
            { "golden", new[] { 2, 5, 3, 1 } },
            { "diamond", new[] { 3, 8, 6, 3 } },
            { "netherite", new[] { 3, 8, 6, 3 } },
        };

        public static ItemProperties Load(string materialsJson, string foodsJson, ItemRegistry items)
        {
            var properties = new ItemProperties();
            foreach (var material in JObject.Parse(materialsJson))
            {
                var speeds = new Dictionary<int, float>();
                foreach (var pair in (JObject)material.Value) speeds[int.Parse(pair.Key)] = (float)pair.Value;
                properties.materials[material.Key] = speeds;
            }
            foreach (var food in JArray.Parse(foodsJson))
            {
                var id = (int)food["id"];
                properties.foods.Add(id);
                var name = (string)food["name"];
                if (name == "golden_apple" || name == "enchanted_golden_apple" || name == "chorus_fruit" || name == "suspicious_stew") properties.alwaysEdible.Add(id);
            }
            return properties;
        }

        /// <summary>Destroy speed of the held item on a block (1 without a matching tool).</summary>
        public float ToolSpeed(Block block, Item held)
        {
            if (held == null || string.IsNullOrEmpty(block.Material)) return 1.0f;
            if (materials.TryGetValue(block.Material, out var speeds) && speeds.TryGetValue(held.Id, out var speed)) return speed;
            // composite materials ("plant;mineable/axe") are listed as a whole in materials.json; also try every part
            foreach (var part in block.Material.Split(';'))
            {
                if (materials.TryGetValue(part, out speeds) && speeds.TryGetValue(held.Id, out speed)) return speed;
            }
            return held.Name.Path == "shears" && (block.Name.Path.Contains("leaves") || block.Name.Path == "cobweb" || block.Name.Path.Contains("wool")) ? 15.0f : 1.0f;
        }

        /// <summary>Food of foods.json (eaten with crumbs and the eating sound).</summary>
        public bool IsFood(Item item) => item != null && foods.Contains(item.Id);

        /// <summary>Eating needs hunger (except golden apples and friends), the other usable items always work.</summary>
        public bool IsUsable(Item item, int food)
        {
            if (item == null) return false;
            if (foods.Contains(item.Id)) return food < 20 || alwaysEdible.Contains(item.Id);
            return Usable.Contains(item.Name.Path) || item.Name.Path.EndsWith("_potion");
        }

        /// <summary>Armor points of a worn piece (helmet/chestplate/leggings/boots, turtle helmet).</summary>
        public static int ArmorPoints(Item item)
        {
            if (item == null) return 0;
            var path = item.Name.Path;
            if (path == "turtle_helmet") return 2;
            var split = path.IndexOf('_');
            if (split < 0 || !Armor.TryGetValue(path.Substring(0, split), out var values)) return 0;
            if (path.EndsWith("_helmet")) return values[0];
            if (path.EndsWith("_chestplate")) return values[1];
            if (path.EndsWith("_leggings")) return values[2];
            if (path.EndsWith("_boots")) return values[3];
            return 0;
        }

        /// <summary>Level of an enchantment in the item NBT ({Enchantments: [{id: "minecraft:efficiency", lvl: 3s}]}).</summary>
        public static int EnchantmentLevel(ItemStack stack, string id)
        {
            if (!(stack?.Nbt is Dictionary<string, object> nbt) || !nbt.TryGetValue("Enchantments", out var list) || !(list is List<object> enchantments)) return 0;
            foreach (var entry in enchantments)
            {
                if (!(entry is Dictionary<string, object> enchantment)) continue;
                if (!(enchantment.TryGetValue("id", out var name) && (string)name == id)) continue;
                return enchantment.TryGetValue("lvl", out var level) ? Convert.ToInt32(level) : 1;
            }
            return 0;
        }

        /// <summary>
        /// Items drawn with the enchantment glint (vanilla Item.isFoil and its overrides): enchanted items and books,
        /// potions with effects, written books, enchanted golden apples, nether stars, bottles o' enchanting, end crystals,
        /// the debug stick and lodestone compasses.
        /// </summary>
        public static bool HasFoil(ItemStack stack)
        {
            var name = stack?.Item?.Name.Path;
            if (name == null) return false;
            switch (name)
            {
                case "enchanted_golden_apple":
                case "nether_star":
                case "experience_bottle":
                case "written_book":
                case "end_crystal":
                case "debug_stick":
                    return true;
            }
            if (!(stack.Nbt is Dictionary<string, object> nbt)) return false;
            if (nbt.TryGetValue("Enchantments", out var enchantments) && enchantments is List<object> list && list.Count > 0) return true;
            if (nbt.TryGetValue("StoredEnchantments", out var stored) && stored is List<object> storedList && storedList.Count > 0) return true;
            if (name == "compass" && nbt.ContainsKey("LodestonePos")) return true;
            if (name == "potion" || name == "splash_potion" || name == "lingering_potion")
            {
                if (nbt.TryGetValue("CustomPotionEffects", out var custom) && custom is List<object> effects && effects.Count > 0) return true;
                if (nbt.TryGetValue("Potion", out var potion) && potion is string id)
                {
                    id = id.Replace("minecraft:", "");
                    return id != "water" && id != "empty" && id != "mundane" && id != "thick" && id != "awkward";
                }
            }
            return false;
        }
    }
}
