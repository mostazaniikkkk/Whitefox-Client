// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using WhiteFox.Core.Network;

namespace WhiteFox.Core.Worlds
{
    /// <summary>A biome of the server's minecraft:worldgen/biome registry (Minosoft's Biome): climate and colors.</summary>
    public sealed class Biome
    {
        public string Name = "minecraft:plains";
        public float Temperature = 0.8f;
        public float Downfall = 0.4f;
        public bool HasPrecipitation = true;
        /// <summary>Explicit colors of the biome effects, -1 when the color map is used.</summary>
        public int GrassColorOverride = -1;
        public int FoliageColorOverride = -1;
        public int WaterColor = 0x3F76E4;
        public int WaterFogColor = 0x050533;
        public int FogColor = 0xC0D8FF;
        public int SkyColor = 0x78A7FF;
        /// <summary>none, dark_forest or swamp.</summary>
        public string GrassModifier = "none";
        /// <summary>Background music of the biome effects (sound event without namespace), or null.</summary>
        public string Music;
        public int MusicMinDelay = 12000;
        public int MusicMaxDelay = 24000;
        public bool MusicReplaces;

        /// <summary>Final tint colors (color map or override, modifier applied); set by <see cref="Meshing.BiomeColors.Apply"/>.</summary>
        public int Grass = 0x91BD59;
        public int Foliage = 0x77AB2F;

        /// <summary>Snow instead of rain (vanilla Biome.coldEnoughToSnow without the height noise: base temperature below 0.15).</summary>
        public bool Snows(int y) => HasPrecipitation && Temperature - (y > 80 ? (y - 80) * 0.0016666667f : 0) < 0.15f;
    }

    /// <summary>The biomes by network id.</summary>
    public sealed class BiomeRegistry
    {
        public static readonly Biome Plains = new Biome();

        public readonly Dictionary<int, Biome> ById = new Dictionary<int, Biome>();

        public Biome Get(int id) => ById.TryGetValue(id, out var biome) ? biome : Plains;

        /// <summary>Registry Data "minecraft:worldgen/biome": entries with name, id and element (climate + effects).</summary>
        public static BiomeRegistry FromNbt(List<object> entries)
        {
            var registry = new BiomeRegistry();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var element = Nbt.Get(entry, "element");
                var effects = Nbt.Get(element, "effects");
                var biome = new Biome
                {
                    Name = Nbt.GetString(entry, "name") ?? "minecraft:plains",
                    Temperature = GetFloat(element, "temperature", 0.8f),
                    Downfall = GetFloat(element, "downfall", 0.4f),
                    HasPrecipitation = Nbt.GetInt(element, "has_precipitation", 1) != 0,
                    GrassColorOverride = Nbt.GetInt(effects, "grass_color", -1),
                    FoliageColorOverride = Nbt.GetInt(effects, "foliage_color", -1),
                    WaterColor = Nbt.GetInt(effects, "water_color", 0x3F76E4),
                    WaterFogColor = Nbt.GetInt(effects, "water_fog_color", 0x050533),
                    FogColor = Nbt.GetInt(effects, "fog_color", 0xC0D8FF),
                    SkyColor = Nbt.GetInt(effects, "sky_color", 0x78A7FF),
                    GrassModifier = (Nbt.GetString(effects, "grass_color_modifier") ?? "none").Replace("minecraft:", ""),
                };
                var music = Nbt.Get(effects, "music");
                if (music != null)
                {
                    // sound: a sound event id, or a compound with sound_id
                    var sound = Nbt.Get(music, "sound");
                    var name = sound as string ?? Nbt.GetString(sound, "sound_id");
                    if (name != null) biome.Music = name.Replace("minecraft:", "");
                    biome.MusicMinDelay = Nbt.GetInt(music, "min_delay", 12000);
                    biome.MusicMaxDelay = Nbt.GetInt(music, "max_delay", 24000);
                    biome.MusicReplaces = Nbt.GetInt(music, "replace_current_music", 0) != 0;
                }
                registry.ById[Nbt.GetInt(entry, "id", index)] = biome;
            }
            return registry;
        }

        private static float GetFloat(object compound, string key, float fallback)
        {
            switch (Nbt.Get(compound, key))
            {
                case float f: return f;
                case double d: return (float)d;
                case int i: return i;
                default: return fallback;
            }
        }
    }
}
