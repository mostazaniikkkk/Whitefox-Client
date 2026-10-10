// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Data
{
    public sealed class EntityType
    {
        public int Id;
        public ResourceLocation Name;
        public string DisplayName;
        public float Width;
        public float Height;
        /// <summary>minecraft-data category: player, hostile, animal, passive, mob, living, ambient, water_creature, projectile, other.</summary>
        public string Category;

        public bool IsLiving => Category != "other" && Category != "projectile";

        public override string ToString() => Name.ToString();
    }

    /// <summary>Entity types with their network ids and hitbox sizes (minecraft-data entities.json, verified against 1.20.4).</summary>
    public sealed class EntityTypeRegistry
    {
        private readonly Dictionary<int, EntityType> byId = new Dictionary<int, EntityType>();
        private readonly Dictionary<string, EntityType> byName = new Dictionary<string, EntityType>();

        public EntityType Get(int id) => byId.TryGetValue(id, out var type) ? type : null;
        public EntityType Get(string name) => byName.TryGetValue(name, out var type) ? type : null;

        public static EntityTypeRegistry Load(string json)
        {
            var registry = new EntityTypeRegistry();
            foreach (var entry in JArray.Parse(json))
            {
                var type = new EntityType
                {
                    Id = (int)entry["id"],
                    Name = ResourceLocation.Minecraft((string)entry["name"]),
                    DisplayName = (string)entry["displayName"],
                    Width = (float?)entry["width"] ?? 0.5f,
                    Height = (float?)entry["height"] ?? 0.5f,
                    Category = (string)entry["type"] ?? "other",
                };
                registry.byId[type.Id] = type;
                registry.byName[type.Name.Path] = type;
            }
            return registry;
        }
    }
}
