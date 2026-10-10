// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Models;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;
using Random = UnityEngine.Random;

namespace WhiteFox.Client
{
    /// <summary>
    /// Particles (Minosoft's ParticleRenderer, vanilla ParticleEngine and the particle classes): the Particle packet,
    /// block break and digging particles, and the ambient particles vanilla spawns around the player (torches, fire,
    /// lava, portals, campfires...). Simulated at 20 TPS on the CPU, drawn as one camera facing mesh per frame.
    /// </summary>
    public sealed class ParticleEngine : IDisposable
    {
        private const int Max = 8192;
        private const int SpriteSize = 16;

        private struct Particle
        {
            public Vector3 Position, Previous, Velocity;
            public float Size;
            public int Age, Lifetime;
            public Color Color;
            public float Gravity, Friction;
            public int SpriteStart, SpriteCount;
            public bool Animated, Fullbright, Physics, OnGround, Block, Item, Shrink, Fade, Rise, NeedsWater;
            public int Layer;
            public float U, V, UvSize;
            public float BlockLight, SkyLight;
        }

        private readonly ClientResources resources;
        private readonly Particle[] particles = new Particle[Max];
        private int count;
        private readonly Dictionary<string, int[]> sprites = new Dictionary<string, int[]>();
        private readonly Texture2DArray array;
        // item sprites for item particles (eating, tools breaking), filled on demand
        private const int ItemLayers = 256;
        private readonly Texture2DArray itemArray;
        private readonly Dictionary<string, int> itemLayers = new Dictionary<string, int>();
        private readonly Material material;
        private readonly Mesh mesh;
        private readonly Vector3[] vertices = new Vector3[Max * 4];
        private readonly Color32[] colors = new Color32[Max * 4];
        private readonly List<Vector4> uv0 = new List<Vector4>(Max * 4);
        private readonly List<Vector2> uv1 = new List<Vector2>(Max * 4);
        private readonly System.Random random = new System.Random();

        public int Count => count;

        public ParticleEngine(ClientResources resources, Texture2DArray blocks)
        {
            this.resources = resources;
            array = LoadSprites();
            material = new Material(UnityEngine.Resources.Load<Shader>("WhiteFox/Particle")) { name = "WhiteFox particles" };
            material.SetTexture("_Particles", array);
            material.SetTexture("_Blocks", blocks);
            itemArray = new Texture2DArray(SpriteSize, SpriteSize, ItemLayers, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Item particle sprites" };
            material.SetTexture("_Items", itemArray);
            mesh = new Mesh { name = "Particles", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            var indices = new int[Max * 6];
            for (var i = 0; i < Max; i++)
            {
                indices[i * 6] = i * 4;
                indices[i * 6 + 1] = i * 4 + 1;
                indices[i * 6 + 2] = i * 4 + 2;
                indices[i * 6 + 3] = i * 4;
                indices[i * 6 + 4] = i * 4 + 2;
                indices[i * 6 + 5] = i * 4 + 3;
            }
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
        }

        public void SetBlockTextures(Texture2DArray blocks) => material.SetTexture("_Blocks", blocks);

        /// <summary>particles/{name}.json lists the sprites of every particle type; they all go into one 16x16 array.</summary>
        private Texture2DArray LoadSprites()
        {
            var layers = new List<RgbaImage>();
            var layerOf = new Dictionary<string, int>();
            foreach (var name in resources.ParticleTypes ?? Array.Empty<string>())
            {
                if (name == null || !resources.Assets.TryGetText("assets/minecraft/particles/" + name + ".json", out var json)) continue;
                var list = new List<int>();
                if (JObject.Parse(json)["textures"] is JArray textures)
                {
                    foreach (var texture in textures)
                    {
                        var location = ResourceLocation.Of((string)texture);
                        if (!layerOf.TryGetValue(location.Path, out var layer))
                        {
                            if (!resources.Assets.TryGet("assets/minecraft/textures/particle/" + location.Path + ".png", out var data)) continue;
                            var image = PngDecoder.Decode(data);
                            if (image.Width != SpriteSize || image.Height != SpriteSize) image = image.Scale(SpriteSize, SpriteSize);
                            layer = layers.Count;
                            layers.Add(image);
                            layerOf[location.Path] = layer;
                        }
                        list.Add(layer);
                    }
                }
                if (list.Count > 0) sprites[name] = list.ToArray();
            }
            var result = new Texture2DArray(SpriteSize, SpriteSize, Math.Max(1, layers.Count), TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Particle sprites" };
            var flipped = new byte[SpriteSize * SpriteSize * 4];
            for (var i = 0; i < layers.Count; i++)
            {
                var pixels = layers[i].Pixels;
                for (var y = 0; y < SpriteSize; y++) Buffer.BlockCopy(pixels, y * SpriteSize * 4, flipped, (SpriteSize - 1 - y) * SpriteSize * 4, SpriteSize * 4);
                result.SetPixelData(flipped, 0, i);
            }
            result.Apply(false, true);
            Log.Info("Loaded " + layers.Count + " particle sprites for " + sprites.Count + " particle types");
            return result;
        }

        private float Rand() => (float)random.NextDouble();
        private float Gaussian()
        {
            var u = 1.0 - random.NextDouble();
            var v = random.NextDouble();
            return (float)(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
        }

        // ------------------------------------------------------------------ spawning

        /// <summary>Particle packet semantics: count 0 = one particle moving along offset * speed, otherwise spread around.</summary>
        public void Spawn(ParticleRequest request)
        {
            var origin = new Vector3((float)request.Position.X, (float)request.Position.Y, (float)request.Position.Z);
            if (request.Name == "#break")
            {
                BreakBlock(request.Block, origin);
                return;
            }
            if (request.Name == "#crack")
            {
                Crack(request.Block, origin, (Direction)request.Count);
                return;
            }
            if (request.Name == "explosion_emitter")
            {
                for (var i = 0; i < 16; i++) Add("explosion", origin + new Vector3((Rand() - Rand()) * 4, (Rand() - Rand()) * 4, (Rand() - Rand()) * 4), Vector3.zero, request);
                return;
            }
            var offset = new Vector3((float)request.Offset.X, (float)request.Offset.Y, (float)request.Offset.Z);
            if (request.Count == 0)
            {
                Add(request.Name, origin, offset * request.Speed, request, true);
                return;
            }
            var amount = Math.Min(request.Count, 500);
            for (var i = 0; i < amount; i++)
            {
                var position = origin + new Vector3(Gaussian() * offset.x, Gaussian() * offset.y, Gaussian() * offset.z);
                var velocity = new Vector3(Gaussian(), Gaussian(), Gaussian()) * request.Speed;
                Add(request.Name, position, velocity, request);
            }
        }

        /// <summary>One particle of a type with vanilla-like lifetime, size, color and motion.</summary>
        private void Add(string name, Vector3 position, Vector3 velocity, ParticleRequest request, bool directional = false)
        {
            if (count >= Max) return;
            var p = new Particle
            {
                Position = position,
                Previous = position,
                Velocity = velocity,
                Size = 0.1f * (Rand() * 0.5f + 0.5f),
                Lifetime = (int)(4 / (Rand() * 0.9f + 0.1f)),
                Color = Color.white,
                Friction = 0.98f,
                Physics = true,
            };
            int Life(float baseTicks) => (int)(baseTicks / (Rand() * 0.8f + 0.2f));
            switch (name)
            {
                case "smoke":
                case "large_smoke":
                case "white_smoke":
                {
                    var gray = name == "white_smoke" ? 1 - Rand() * 0.1f : Rand() * 0.3f;
                    p.Color = new Color(gray, gray, gray);
                    p.Velocity = p.Velocity * (directional ? 1 : 0.1f) + new Vector3((Rand() * 2 - 1) * 0.01f, 0, (Rand() * 2 - 1) * 0.01f);
                    p.Lifetime = Life(name == "large_smoke" ? 20 : 8);
                    p.Size = 0.075f * (name == "large_smoke" ? 2.5f : 1) * (Rand() * 0.5f + 0.5f) * 2;
                    p.Animated = true;
                    p.Rise = true;
                    p.Friction = 0.96f;
                    break;
                }
                case "cloud":
                case "poof":
                case "sneeze":
                case "dust_plume":
                case "gust_dust":
                {
                    var gray = 1 - Rand() * 0.3f;
                    p.Color = name == "sneeze" ? new Color(0.784f, 0.984f, 0.639f) : new Color(gray, gray, gray);
                    p.Velocity *= 0.1f;
                    p.Lifetime = Life(name == "poof" ? 4 : 8) + 2;
                    p.Size = 0.15f * (Rand() * 0.5f + 0.5f) * 2;
                    p.Animated = true;
                    p.Rise = true;
                    p.Friction = 0.96f;
                    break;
                }
                case "campfire_cosy_smoke":
                case "campfire_signal_smoke":
                    p.Color = new Color(0.8f, 0.8f, 0.8f, 0.9f);
                    p.Velocity = new Vector3((Rand() - 0.5f) * 0.01f, 0.07f, (Rand() - 0.5f) * 0.01f);
                    p.Lifetime = name == "campfire_signal_smoke" ? 280 + random.Next(50) : 80 + random.Next(40);
                    p.Size = 0.75f * (Rand() * 0.3f + 0.7f);
                    p.Gravity = 0.000003f;
                    p.Friction = 0.99f;
                    p.Fade = true;
                    p.Physics = false;
                    break;
                case "flame":
                case "soul_fire_flame":
                case "small_flame":
                case "copper_fire_flame":
                    p.Velocity = p.Velocity * (directional ? 1 : 0.01f);
                    p.Lifetime = Life(8) + 4;
                    p.Size = 0.1f * (Rand() * 0.5f + 0.5f) * (name == "small_flame" ? 0.5f : 1);
                    p.Fullbright = true;
                    p.Shrink = true;
                    p.Friction = 0.96f;
                    p.Physics = false;
                    break;
                case "lava":
                    p.Velocity = new Vector3(p.Velocity.x * 0.8f, Rand() * 0.4f + 0.05f, p.Velocity.z * 0.8f);
                    p.Lifetime = Life(16);
                    p.Size = 0.1f * (Rand() * 2 + 0.2f);
                    p.Gravity = 0.75f;
                    p.Fullbright = true;
                    p.Shrink = true;
                    p.Friction = 0.999f;
                    break;
                case "crit":
                case "enchanted_hit":
                case "damage_indicator":
                    p.Velocity = p.Velocity * 0.1f + p.Velocity;
                    p.Lifetime = (int)(6 / (Rand() * 0.8f + 0.6f));
                    p.Size = 0.075f;
                    p.Gravity = 0.5f;
                    p.Friction = 0.7f;
                    if (name == "enchanted_hit") p.Color = new Color(0.3f, 0.8f, 1.0f) * (Rand() * 0.4f + 0.6f);
                    else p.Color = Color.white * (Rand() * 0.3f + 0.6f);
                    p.Color.a = 1;
                    p.Animated = true;
                    break;
                case "heart":
                case "angry_villager":
                    p.Velocity = new Vector3(p.Velocity.x * 0.01f, 0.1f, p.Velocity.z * 0.01f);
                    p.Lifetime = 16;
                    p.Size = 0.15f * 1.5f;
                    p.Friction = 0.86f;
                    p.Physics = false;
                    break;
                case "happy_villager":
                case "composter":
                case "mycelium":
                case "underwater":
                case "crimson_spore":
                case "warped_spore":
                    p.Velocity *= directional ? 1 : 0.02f;
                    p.Lifetime = (int)(20 / (Rand() * 0.8f + 0.2f));
                    p.Size = 0.05f * (Rand() * 0.6f + 0.6f);
                    p.Friction = 0.99f;
                    p.Physics = false;
                    if (name == "mycelium") p.Color = new Color(0.6f, 0.5f, 0.6f);
                    if (name == "crimson_spore") p.Color = new Color(0.9f, 0.4f, 0.5f);
                    if (name == "warped_spore") p.Color = new Color(0.1f, 0.1f, 0.3f);
                    break;
                case "note":
                {
                    // the note color comes from the offset x (pitch / 24) like vanilla
                    var hue = directional ? (float)request.Offset.X : Rand();
                    p.Color = Color.HSVToRGB(hue, 0.8f, 1);
                    p.Velocity = new Vector3(0, 0.2f, 0);
                    p.Lifetime = 6;
                    p.Size = 0.15f * 1.5f;
                    p.Friction = 0.66f;
                    p.Physics = false;
                    break;
                }
                case "portal":
                case "reverse_portal":
                case "enchant":
                    p.Color = name == "enchant" ? Color.white * (Rand() * 0.6f + 0.4f) : new Color(0.9f, 0.3f, 1.0f) * (Rand() * 0.6f + 0.4f);
                    p.Color.a = 1;
                    p.Lifetime = (int)(Rand() * 10) + 40;
                    p.Size = 0.1f * (Rand() * 0.2f + 0.5f);
                    p.Velocity *= 0.05f;
                    p.Friction = 1;
                    p.Physics = false;
                    p.Fullbright = name != "enchant";
                    break;
                case "witch":
                case "effect":
                case "instant_effect":
                case "entity_effect":
                case "ambient_entity_effect":
                    if ((name == "entity_effect" || name == "ambient_entity_effect") && directional) p.Color = new Color((float)request.Offset.X, (float)request.Offset.Y, (float)request.Offset.Z);
                    else if (name == "witch") p.Color = new Color(0.6f, 0.2f, 0.7f) * (Rand() * 0.5f + 0.5f);
                    if (name == "ambient_entity_effect") p.Color.a = 0.15f;
                    p.Velocity = new Vector3(p.Velocity.x * 0.1f, p.Velocity.y * 0.1f + 0.05f, p.Velocity.z * 0.1f);
                    p.Lifetime = Life(8);
                    p.Size = 0.075f;
                    p.Animated = true;
                    p.Friction = 0.96f;
                    p.Rise = true;
                    break;
                case "dust":
                case "dust_color_transition":
                {
                    var shade = Rand() * 0.4f + 0.6f;
                    p.Color = new Color(request.R * shade, request.G * shade, request.B * shade);
                    var scale = Mathf.Clamp(request.Scale, 0.01f, 4);
                    p.Lifetime = (int)(Life(8) * scale);
                    p.Size = 0.075f * scale;
                    p.Velocity *= 0.1f;
                    p.Friction = 0.96f;
                    p.Animated = true;
                    break;
                }
                case "explosion":
                {
                    var gray = Rand() * 0.6f + 0.4f;
                    p.Color = new Color(gray, gray, gray);
                    p.Lifetime = 6 + random.Next(4);
                    p.Size = 2 * (1 - Rand() * 0.5f);
                    p.Velocity = Vector3.zero;
                    p.Animated = true;
                    p.Fullbright = true;
                    p.Physics = false;
                    break;
                }
                case "splash":
                case "rain":
                case "fishing":
                case "bubble_pop":
                case "falling_water":
                case "dripping_water":
                case "dripping_dripstone_water":
                case "falling_dripstone_water":
                    p.Velocity = name == "splash" ? new Vector3(p.Velocity.x * 0.3f, Rand() * 0.2f + 0.1f, p.Velocity.z * 0.3f) : p.Velocity * 0.3f;
                    p.Lifetime = Life(8);
                    p.Size = 0.05f;
                    p.Gravity = 0.06f;
                    p.Color = name.Contains("water") ? new Color(0.2f, 0.3f, 1.0f) : Color.white;
                    break;
                case "bubble":
                case "bubble_column_up":
                case "current_down":
                    p.Velocity *= 0.2f;
                    p.Lifetime = Life(8);
                    p.Size = 0.1f * (Rand() * 0.6f + 0.2f);
                    p.Gravity = name == "current_down" ? 0.004f : -0.02f;
                    p.Friction = 0.85f;
                    p.NeedsWater = true;
                    break;
                case "end_rod":
                case "firework":
                case "totem_of_undying":
                case "glow":
                case "electric_spark":
                case "wax_on":
                case "wax_off":
                case "scrape":
                case "flash":
                case "soul":
                case "sculk_soul":
                    p.Lifetime = 40 + random.Next(20);
                    p.Size = 0.075f;
                    p.Gravity = name == "totem_of_undying" ? 1.25f * 0.1f : 0.0125f;
                    p.Friction = 0.91f;
                    p.Fullbright = true;
                    p.Fade = true;
                    p.Animated = true;
                    if (name == "totem_of_undying") p.Color = Rand() < 0.25f ? new Color(0.6f + Rand() * 0.2f, 0.6f + Rand() * 0.3f, Rand() * 0.2f) : new Color(0.1f + Rand() * 0.2f, 0.4f + Rand() * 0.3f, Rand() * 0.2f);
                    if (name == "flash") p.Size = 0.7f;
                    break;
                case "sweep_attack":
                    p.Size = 1 - Rand() * 0.5f;
                    p.Lifetime = 4;
                    p.Velocity = Vector3.zero;
                    p.Animated = true;
                    p.Fullbright = true;
                    p.Physics = false;
                    break;
                case "snowflake":
                case "cherry_leaves":
                case "falling_spore_blossom":
                case "white_ash":
                case "ash":
                case "spore_blossom_air":
                    p.Lifetime = 60 + random.Next(120);
                    p.Size = name == "cherry_leaves" ? 0.06f : 0.04f;
                    p.Gravity = name == "snowflake" ? 0.225f * 0.1f : 0.01f;
                    p.Velocity = p.Velocity * 0.2f;
                    p.Friction = 0.98f;
                    break;
                case "block":
                case "block_marker":
                case "falling_dust":
                case "item":
                {
                    var block = request.Block ?? (request.Item?.Item != null ? resources.Blocks.Get(request.Item.Item.Name)?.DefaultState : null);
                    if (name == "item" && request.Block == null && !(block?.Model is BlockRenderModel))
                    {
                        // a flat item: pieces of its sprite; the velocity of the request is kept (BreakingItemParticle)
                        if (!SetItemTexture(ref p, request.Item)) return;
                        p.Velocity = p.Velocity + new Vector3((Rand() * 2 - 1) * 0.04f, (Rand() * 2 - 1) * 0.04f, (Rand() * 2 - 1) * 0.04f);
                        break;
                    }
                    if (!SetBlockTexture(ref p, block)) return;
                    if (name == "block_marker")
                    {
                        p.Size = 0.5f;
                        p.Lifetime = 80;
                        p.Velocity = Vector3.zero;
                        p.Gravity = 0;
                    }
                    else if (name == "falling_dust")
                    {
                        p.Gravity = 0.06f;
                        p.Lifetime = (int)(32 / (Rand() * 0.8f + 0.2f));
                    }
                    break;
                }
            }
            if (sprites.TryGetValue(name, out var set))
            {
                p.SpriteStart = set[0];
                p.SpriteCount = set.Length;
                // non animated particles use one random sprite of their set
                if (!p.Animated && set.Length > 1) p.SpriteStart = set[random.Next(set.Length)];
                if (!p.Animated) p.SpriteCount = 1;
                p.Layer = p.SpriteStart;
                if (p.Animated) p.Layer = set[0];
                p.SpriteStart = p.Animated ? Array.IndexOf(spriteSets, set) : -1;
                if (p.Animated && p.SpriteStart < 0)
                {
                    spriteSetList.Add(set);
                    spriteSets = spriteSetList.ToArray();
                    p.SpriteStart = spriteSets.Length - 1;
                }
            }
            else if (!p.Block)
            {
                return; // no sprite (unknown type)
            }
            if (p.Lifetime <= 0) p.Lifetime = 1;
            particles[count++] = p;
        }

        private readonly List<int[]> spriteSetList = new List<int[]>();
        private int[][] spriteSets = Array.Empty<int[]>();

        /// <summary>Terrain particles: a random 4x4 pixel part of the block's texture, gravity 1 (vanilla TerrainParticle).</summary>
        /// <summary>Layer of an item sprite (the layer0 texture of its model) in the item array, -1 without one.</summary>
        private int ItemLayer(string item)
        {
            if (itemLayers.TryGetValue(item, out var layer)) return layer;
            layer = -1;
            if (itemLayers.Count < ItemLayers && resources.Assets.TryGetText("assets/minecraft/models/item/" + item + ".json", out var json))
            {
                var layer0 = (string)JObject.Parse(json)["textures"]?["layer0"];
                if (layer0 != null && resources.Assets.TryGet(ResourceLocation.Of(layer0).ToAssetPath("textures/", ".png"), out var data))
                {
                    var image = PngDecoder.Decode(data);
                    if (image.Height > image.Width) image = image.Crop(0, 0, image.Width, image.Width); // animated: first frame
                    if (image.Width != SpriteSize || image.Height != SpriteSize) image = image.Scale(SpriteSize, SpriteSize);
                    var flipped = new byte[SpriteSize * SpriteSize * 4];
                    for (var y = 0; y < SpriteSize; y++) Buffer.BlockCopy(image.Pixels, y * SpriteSize * 4, flipped, (SpriteSize - 1 - y) * SpriteSize * 4, SpriteSize * 4);
                    layer = itemLayers.Count;
                    itemArray.SetPixelData(flipped, 0, layer);
                    itemArray.Apply(false, false);
                }
            }
            itemLayers[item] = layer;
            return layer;
        }

        /// <summary>Vanilla BreakingItemParticle: a random quarter of the item sprite, half the usual size, falling.</summary>
        private bool SetItemTexture(ref Particle p, ItemStack stack)
        {
            var name = stack?.Item?.Name.Path;
            if (name == null) return false;
            var layer = ItemLayer(name);
            if (layer < 0) return false;
            p.Block = true;
            p.Item = true;
            p.Layer = layer;
            p.UvSize = 0.25f;
            p.U = random.Next(4) * 0.25f;
            p.V = random.Next(4) * 0.25f;
            p.Color = Color.white;
            p.Gravity = 1;
            p.Size = 0.1f * (Rand() * 0.5f + 0.5f) * 0.5f * 2;
            p.Lifetime = (int)(4 / (Rand() * 0.9f + 0.1f));
            return true;
        }

        private bool SetBlockTexture(ref Particle p, BlockState state)
        {
            if (!(state?.Model is BlockRenderModel render) || render.Models.Length == 0 || render.Models[0].Quads.Length == 0) return false;
            var quads = render.Models[0].Quads;
            var quad = quads[quads.Length > 1 ? random.Next(quads.Length) : 0];
            p.Block = true;
            p.Layer = quad.Texture.Layer;
            p.UvSize = 0.25f;
            p.U = Rand() * 0.75f;
            p.V = Rand() * 0.75f;
            var tint = state.Block.Tint == TintType.Grass ? resources.Colors.Grass : state.Block.Tint == TintType.Foliage ? resources.Colors.Foliage : state.Block.Tint == TintType.Constant ? state.Block.ConstantTint : 0xFFFFFF;
            if (quad.TintIndex < 0) tint = 0xFFFFFF;
            p.Color = new Color(((tint >> 16) & 0xFF) / 255f * 0.6f, ((tint >> 8) & 0xFF) / 255f * 0.6f, (tint & 0xFF) / 255f * 0.6f);
            p.Gravity = 1;
            p.Size = 0.1f * (Rand() * 0.5f + 0.5f) * 0.5f * 2;
            p.Lifetime = (int)(4 / (Rand() * 0.9f + 0.1f));
            return true;
        }

        /// <summary>Vanilla ParticleEngine.destroy: 4x4x4 particles flying out of the broken block.</summary>
        private void BreakBlock(BlockState state, Vector3 origin)
        {
            for (var x = 0; x < 4; x++)
            {
                for (var y = 0; y < 4; y++)
                {
                    for (var z = 0; z < 4; z++)
                    {
                        if (count >= Max) return;
                        var d = new Vector3((x + 0.5f) / 4, (y + 0.5f) / 4, (z + 0.5f) / 4);
                        var p = new Particle { Position = origin + d, Friction = 0.98f, Physics = true };
                        p.Previous = p.Position;
                        if (!SetBlockTexture(ref p, state)) return;
                        var velocity = d - new Vector3(0.5f, 0.5f, 0.5f) + new Vector3((Rand() * 2 - 1) * 0.4f, (Rand() * 2 - 1) * 0.4f, (Rand() * 2 - 1) * 0.4f);
                        var speed = (Rand() + Rand() + 1) * 0.15f;
                        velocity = velocity.normalized * speed * 0.4f + new Vector3(0, 0.1f, 0);
                        p.Velocity = velocity;
                        particles[count++] = p;
                    }
                }
            }
        }

        /// <summary>Vanilla ParticleEngine.crack: one small particle on the face being dug.</summary>
        private void Crack(BlockState state, Vector3 origin, Direction face)
        {
            if (count >= Max) return;
            var position = origin + new Vector3(Rand(), Rand(), Rand());
            const float outside = 0.1f;
            switch (face)
            {
                case Direction.Down: position.y = origin.y - outside; break;
                case Direction.Up: position.y = origin.y + 1 + outside; break;
                case Direction.North: position.z = origin.z - outside; break;
                case Direction.South: position.z = origin.z + 1 + outside; break;
                case Direction.West: position.x = origin.x - outside; break;
                case Direction.East: position.x = origin.x + 1 + outside; break;
            }
            var p = new Particle { Position = position, Previous = position, Friction = 0.98f, Physics = true };
            if (!SetBlockTexture(ref p, state)) return;
            p.Velocity = new Vector3((Rand() * 2 - 1) * 0.08f, 0.02f, (Rand() * 2 - 1) * 0.08f) * 0.2f;
            p.Size *= 0.6f;
            particles[count++] = p;
        }

        // ------------------------------------------------------------------ ticking

        public void Tick(World world)
        {
            var i = 0;
            while (i < count)
            {
                ref var p = ref particles[i];
                p.Previous = p.Position;
                p.Age++;
                if (p.Age >= p.Lifetime)
                {
                    particles[i] = particles[--count];
                    continue;
                }
                p.Velocity.y -= 0.04f * p.Gravity;
                if (p.Rise) p.Velocity.y += 0.004f;
                Move(world, ref p);
                p.Velocity *= p.Friction;
                if (p.OnGround)
                {
                    p.Velocity.x *= 0.7f;
                    p.Velocity.z *= 0.7f;
                }
                var x = Mathf.FloorToInt(p.Position.x);
                var y = Mathf.FloorToInt(p.Position.y);
                var z = Mathf.FloorToInt(p.Position.z);
                if (p.NeedsWater && Fluids.Get(world.GetBlock(x, y, z)) != FluidType.Water)
                {
                    particles[i] = particles[--count];
                    continue;
                }
                var light = world.GetLight(x, y, z);
                p.BlockLight = (light & 0x0F) / 15f;
                p.SkyLight = (light >> 4) / 15f;
                i++;
            }
        }

        /// <summary>Simple block collision (per axis against blocks with a collision shape).</summary>
        private static void Move(World world, ref Particle p)
        {
            var target = p.Position + p.Velocity;
            if (!p.Physics)
            {
                p.Position = target;
                return;
            }
            p.OnGround = false;
            if (Solid(world, p.Position.x, target.y, p.Position.z))
            {
                if (p.Velocity.y < 0) p.OnGround = true;
                p.Velocity.y = 0;
                target.y = p.Position.y;
            }
            if (Solid(world, target.x, target.y, p.Position.z))
            {
                p.Velocity.x = 0;
                target.x = p.Position.x;
            }
            if (Solid(world, target.x, target.y, target.z))
            {
                p.Velocity.z = 0;
                target.z = p.Position.z;
            }
            p.Position = target;
        }

        private static bool Solid(World world, float x, float y, float z)
        {
            var state = world.GetBlock(Mathf.FloorToInt(x), Mathf.FloorToInt(y), Mathf.FloorToInt(z));
            if (state == null || state.CollisionShape.Length == 0) return false;
            var lx = x - Mathf.Floor(x);
            var ly = y - Mathf.Floor(y);
            var lz = z - Mathf.Floor(z);
            foreach (var box in state.CollisionShape)
            {
                if (lx >= box.Min.X && lx <= box.Max.X && ly >= box.Min.Y && ly <= box.Max.Y && lz >= box.Min.Z && lz <= box.Max.Z) return true;
            }
            return false;
        }

        /// <summary>
        /// Vanilla ClientLevel.animateTick: 667 random blocks within 16 and 32 blocks of the player every tick, with
        /// their visual effects (Block.animateTick).
        /// </summary>
        public void TickAmbient(GameSession session)
        {
            var world = session.World;
            var center = session.Player.Position;
            ambientSession = session;
            for (var i = 0; i < 667; i++)
            {
                AnimateBlock(world, (int)Math.Floor(center.X) + random.Next(16) - random.Next(16), (int)Math.Floor(center.Y) + random.Next(16) - random.Next(16), (int)Math.Floor(center.Z) + random.Next(16) - random.Next(16));
                AnimateBlock(world, (int)Math.Floor(center.X) + random.Next(32) - random.Next(32), (int)Math.Floor(center.Y) + random.Next(32) - random.Next(32), (int)Math.Floor(center.Z) + random.Next(32) - random.Next(32));
            }
        }

        private GameSession ambientSession;

        /// <summary>A block sound vanilla plays on the client while animating (ClientLevel.playLocalSound).</summary>
        private void Sound(string name, float x, float y, float z, float volume, float pitch) =>
            ambientSession?.PlaySound(name, new Vec3d(x, y, z), volume, pitch, SoundCategory.Block);

        private void AnimateBlock(World world, int x, int y, int z)
        {
            var state = world.GetBlock(x, y, z);
            if (state == null) return;
            var name = state.Block.Name.Path;
            var none = new ParticleRequest { Scale = 1 };
            var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            switch (name)
            {
                case "torch":
                case "soul_torch":
                case "redstone_torch":
                case "wall_torch":
                case "soul_wall_torch":
                case "redstone_wall_torch":
                {
                    if (name.StartsWith("redstone") && state["lit"] == "false") return;
                    var position = new Vector3(x + 0.5f, y + 0.7f, z + 0.5f);
                    if (name.Contains("wall"))
                    {
                        var facing = Directions.Parse(state["facing"] ?? "north").Opposite();
                        position += new Vector3(facing.X() * 0.27f, 0.22f, facing.Z() * 0.27f);
                    }
                    if (name.StartsWith("redstone"))
                    {
                        if (random.Next(5) == 0) Add("dust", position + new Vector3((Rand() - 0.5f) * 0.2f, 0, (Rand() - 0.5f) * 0.2f), Vector3.zero, new ParticleRequest { R = 1, G = 0, B = 0, Scale = 1 });
                        return;
                    }
                    Add("smoke", position, Vector3.zero, none);
                    Add(name.StartsWith("soul") ? "soul_fire_flame" : "flame", position, Vector3.zero, none, true);
                    break;
                }
                case "campfire":
                case "soul_campfire":
                    if (state["lit"] == "true" && random.Next(10) == 0)
                    {
                        Sound("block.campfire.crackle", x + 0.5f, y + 0.5f, z + 0.5f, 0.5f + Rand(), Rand() * 0.7f + 0.6f);
                        Add("campfire_cosy_smoke", center + new Vector3((Rand() - 0.5f) * 0.6f, 0, (Rand() - 0.5f) * 0.6f), Vector3.zero, none);
                    }
                    break;
                case "fire":
                case "soul_fire":
                    if (random.Next(24) == 0)
                    {
                        Sound("block.fire.ambient", x + 0.5f, y + 0.5f, z + 0.5f, 1 + Rand(), Rand() * 0.7f + 0.3f);
                        Add("large_smoke", new Vector3(x + Rand(), y + Rand() * 0.5f + 0.5f, z + Rand()), Vector3.zero, none);
                    }
                    break;
                case "lava":
                    // LavaFluid.animateTick: pops with a spark, and the slow bubbling, under open air
                    if (world.GetBlock(x, y + 1, z) != null) break;
                    if (random.Next(100) == 0)
                    {
                        var spark = new Vector3(x + Rand(), y + 1, z + Rand());
                        Add("lava", spark, Vector3.zero, none);
                        Sound("block.lava.pop", spark.x, spark.y, spark.z, 0.2f + Rand() * 0.2f, 0.9f + Rand() * 0.15f);
                    }
                    if (random.Next(200) == 0) Sound("block.lava.ambient", x + 0.5f, y + 0.5f, z + 0.5f, 0.2f + Rand() * 0.2f, 0.9f + Rand() * 0.15f);
                    break;
                case "water":
                {
                    // WaterFluid.animateTick: flowing (not falling) water murmurs; sources and falls show the underwater specks
                    int.TryParse(state["level"], out var level);
                    if (level > 0 && level < 8)
                    {
                        if (random.Next(64) == 0) Sound("block.water.ambient", x + 0.5f, y + 0.5f, z + 0.5f, Rand() * 0.25f + 0.75f, Rand() + 0.5f);
                    }
                    break;
                }
                case "nether_portal":
                    for (var i = 0; i < 4; i++)
                    {
                        if (random.Next(100) != 0) continue;
                        Add("portal", new Vector3(x + Rand(), y + Rand(), z + Rand()), new Vector3((Rand() - 0.5f) * 0.5f, (Rand() - 0.5f) * 0.5f, (Rand() - 0.5f) * 0.5f), none, true);
                    }
                    break;
                case "end_rod":
                    if (random.Next(5) == 0) Add("end_rod", center + new Vector3((Rand() - 0.5f) * 0.3f, (Rand() - 0.5f) * 0.3f, (Rand() - 0.5f) * 0.3f), Vector3.zero, none);
                    break;
                case "furnace":
                case "blast_furnace":
                case "smoker":
                    if (state["lit"] == "true")
                    {
                        var facing = Directions.Parse(state["facing"] ?? "north");
                        var position = new Vector3(x + 0.5f + facing.X() * 0.52f, y + Rand() * 6 / 16f, z + 0.5f + facing.Z() * 0.52f);
                        if (random.Next(10) == 0) Sound(name == "furnace" ? "block.furnace.fire_crackle" : name == "smoker" ? "block.smoker.smoke" : "block.blastfurnace.fire_crackle", x + 0.5f, y, z + 0.5f, 1, 1);
                        Add("smoke", position, Vector3.zero, none);
                        if (name == "furnace") Add("flame", position, Vector3.zero, none, true);
                    }
                    break;
                case "mycelium":
                    if (random.Next(10) == 0) Add("mycelium", new Vector3(x + Rand(), y + 1.1f, z + Rand()), Vector3.zero, none);
                    break;
                case "spore_blossom":
                    if (random.Next(2) == 0) Add("falling_spore_blossom", new Vector3(x + Rand(), y - 0.1f, z + Rand()), Vector3.zero, none);
                    break;
            }
            if (name.EndsWith("candle") && state["lit"] == "true" && random.Next(3) == 0) Add("small_flame", new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), Vector3.zero, none, true);
        }

        /// <summary>Adds rain splashes (used by the weather).</summary>
        public void Splash(Vector3 position) => Add("rain", position, Vector3.zero, new ParticleRequest { Scale = 1 });

        // ------------------------------------------------------------------ rendering

        public void Render(Camera camera, float partial)
        {
            if (count == 0 || camera == null) return;
            var right = camera.transform.right;
            var up = camera.transform.up;
            uv0.Clear();
            uv1.Clear();
            for (var i = 0; i < count; i++)
            {
                ref var p = ref particles[i];
                var position = Vector3.LerpUnclamped(p.Previous, p.Position, partial);
                var center = RenderOrigin.ToRender(position.x, position.y, position.z);
                var age = (p.Age + partial) / p.Lifetime;
                var size = p.Size;
                if (p.Shrink) size *= 1 - age * age * 0.5f;
                var r = right * size;
                var u = up * size;
                var index = i * 4;
                vertices[index] = center - r - u;
                vertices[index + 1] = center + r - u;
                vertices[index + 2] = center + r + u;
                vertices[index + 3] = center - r + u;
                var color = p.Color;
                if (p.Fade) color.a *= 1 - Mathf.Clamp01(age);
                Color32 color32 = color;
                colors[index] = colors[index + 1] = colors[index + 2] = colors[index + 3] = color32;

                float u0 = 0, v0 = 0, u1 = 1, v1 = 1;
                var layer = p.Layer;
                if (p.Block)
                {
                    u0 = p.U;
                    v0 = p.V;
                    u1 = p.U + p.UvSize;
                    v1 = p.V + p.UvSize;
                }
                else if (p.Animated && p.SpriteStart >= 0 && p.SpriteStart < spriteSets.Length)
                {
                    var set = spriteSets[p.SpriteStart];
                    layer = set[Mathf.Clamp((int)(age * set.Length), 0, set.Length - 1)];
                }
                var source = p.Item ? 2 : p.Block ? 1 : 0;
                uv0.Add(new Vector4(u0, v0, layer, source));
                uv0.Add(new Vector4(u1, v0, layer, source));
                uv0.Add(new Vector4(u1, v1, layer, source));
                uv0.Add(new Vector4(u0, v1, layer, source));
                var light = p.Fullbright ? new Vector2(2, 1) : new Vector2(p.BlockLight, p.SkyLight);
                uv1.Add(light);
                uv1.Add(light);
                uv1.Add(light);
                uv1.Add(light);
            }
            for (var i = count * 4; i < vertices.Length && vertices[i] != Vector3.zero; i++) vertices[i] = Vector3.zero;
            for (var i = count * 4; i < Max * 4; i++)
            {
                uv0.Add(Vector4.zero);
                uv1.Add(Vector2.zero);
            }
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, count * 6), MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = new Bounds(camera.transform.position, Vector3.one * 2000);
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera, 0, null, ShadowCastingMode.Off, false);
        }

        public void Clear() => count = 0;

        public void Dispose()
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(array);
            UnityEngine.Object.Destroy(itemArray);
        }
    }
}
