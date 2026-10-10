// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Data
{
    public sealed class Item
    {
        public readonly int Id;
        public readonly ResourceLocation Name;
        public readonly string DisplayName;
        public readonly int StackSize;
        /// <summary>Uses before the item breaks (tools, weapons, armor...), 0 for items without durability.</summary>
        public readonly int MaxDurability;

        public Item(int id, ResourceLocation name, string displayName, int stackSize, int maxDurability = 0)
        {
            Id = id;
            Name = name;
            DisplayName = displayName;
            StackSize = stackSize;
            MaxDurability = maxDurability;
        }

        public override string ToString() => Name.ToString();
    }

    /// <summary>Item registry with the network ids of 1.20.4 (minecraft-data items.json, verified against vanilla).</summary>
    public sealed class ItemRegistry
    {
        private readonly Dictionary<int, Item> byId = new Dictionary<int, Item>();
        private readonly Dictionary<ResourceLocation, Item> byName = new Dictionary<ResourceLocation, Item>();

        public int Count => byId.Count;
        public IEnumerable<Item> All => byId.Values;

        public Item Get(int id) => byId.TryGetValue(id, out var item) ? item : null;
        public Item Get(ResourceLocation name) => byName.TryGetValue(name, out var item) ? item : null;

        public static ItemRegistry Load(string json)
        {
            var registry = new ItemRegistry();
            foreach (var entry in JArray.Parse(json))
            {
                var item = new Item((int)entry["id"], ResourceLocation.Minecraft((string)entry["name"]), (string)entry["displayName"], (int?)entry["stackSize"] ?? 64, (int?)entry["maxDurability"] ?? 0);
                registry.byId[item.Id] = item;
                registry.byName[item.Name] = item;
            }
            return registry;
        }
    }

    /// <summary>An item stack as sent by the server (Minosoft's ItemStack, without components).</summary>
    public sealed class ItemStack
    {
        public Item Item;
        public int Count;
        public object Nbt;

        /// <summary>Damage taken (NBT "Damage"); the durability bar shows when it is above 0 and the item is not unbreakable.</summary>
        public int Damage => Network.Nbt.GetInt(Nbt, "Damage");
        public bool ShowsDurability => Item != null && Item.MaxDurability > 0 && Damage > 0 && Network.Nbt.GetInt(Nbt, "Unbreakable") == 0;

        /// <summary>
        /// The name given in an anvil (NBT display.Name, a JSON text component) with § codes, italic unless the
        /// component says otherwise (like vanilla); null without one.
        /// </summary>
        public string CustomName
        {
            get
            {
                Parse();
                return customName;
            }
        }

        /// <summary>The custom name without formatting (what the anvil's name field starts with).</summary>
        public string CustomNamePlain
        {
            get
            {
                Parse();
                return customNamePlain;
            }
        }

        /// <summary>The name shown in tooltips and above the hotbar: the custom name, else the item's name.</summary>
        public string HoverName => CustomName ?? Item?.DisplayName;

        // the name is parsed once per NBT value (tooltips ask every frame)
        private object parsedNbt;
        private bool parsed;
        private string customName, customNamePlain;

        private void Parse()
        {
            if (parsed && ReferenceEquals(parsedNbt, Nbt)) return;
            parsed = true;
            parsedNbt = Nbt;
            customName = customNamePlain = null;
            var json = Network.Nbt.GetString(Network.Nbt.Get(Nbt, "display"), "Name");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var component = Network.TextComponent.FromJson(json);
                if (component == null) return;
                if (!component.Italic.HasValue) component.Italic = true;
                customName = component.ToLegacy();
                customNamePlain = component.ToPlain();
            }
            catch (System.Exception)
            {
                // not JSON: an old style plain name
                customName = "§o" + json;
                customNamePlain = json;
            }
        }

        public override string ToString() => Count > 1 ? Count + "x " + Item : Item?.ToString();
    }
}
