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

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Inventory icons (Minosoft's item GUI rendering): flat items use their "layer0" texture, block items are rendered
    /// once in 3D with the vanilla GUI transform (rotation 30/225, scale 0.625) into a small texture and cached.
    /// </summary>
    public sealed class ItemIcons : IDisposable
    {
        private const int IconSize = 64;

        private readonly ClientResources resources;
        private readonly GuiAssets assets;
        private readonly Material material;
        private readonly Dictionary<string, Texture> cache = new Dictionary<string, Texture>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        public ItemIcons(ClientResources resources, GuiAssets assets, Material blockMaterial)
        {
            this.resources = resources;
            this.assets = assets;
            material = blockMaterial;
        }

        /// <summary>Icon for an item name like "stone" or "diamond_sword"; null if there is none.</summary>
        /// <summary>Models of block entity items (chests, beds, banners, heads, shulker boxes, shields), set by the client.</summary>
        public System.Func<string, object, List<(Mesh mesh, Material material, Matrix4x4 matrix)>> SpecialParts;

        private Material guiGlint, worldGlint, armorGlint;

        private Material GlintMaterial(float scale, string texture = "enchanted_glint_item")
        {
            var shader = UnityEngine.Resources.Load<Shader>("WhiteFox/Glint");
            var sprite = assets.Get("assets/minecraft/textures/misc/" + texture + ".png");
            if (shader == null || sprite == null) return null;
            sprite.Texture.wrapMode = TextureWrapMode.Repeat;
            var glint = new Material(shader) { name = "Enchantment glint" };
            glint.SetTexture("_Glint", sprite.Texture);
            glint.SetFloat("_Scale", scale);
            owned.Add(glint);
            return glint;
        }

        public Texture MapBackground => assets.Get("assets/minecraft/textures/map/map_background.png")?.Texture;

        /// <summary>Extra material for enchanted held/dropped items and armor (drawn over the same mesh), or null.</summary>
        public Material WorldGlint => worldGlint != null ? worldGlint : worldGlint = GlintMaterial(1.0f);

        /// <summary>The glint on worn armor: larger, the armor uv covers the whole armor texture.</summary>
        public Material ArmorGlint => armorGlint != null ? armorGlint : armorGlint = GlintMaterial(0.5f, "enchanted_glint_entity");

        private readonly Dictionary<(Texture, bool), Material> glintVariants = new Dictionary<(Texture, bool), Material>();

        /// <summary>
        /// The glint drawn over a material (vanilla glint / armor_entity_glint render types): masked by its texture, so it
        /// stays inside the pixels of sprites and armor layers. Null without the glint texture.
        /// </summary>
        public Material GlintFor(Material baseMaterial, bool armor)
        {
            var glint = armor ? ArmorGlint : WorldGlint;
            if (glint == null) return null;
            var texture = baseMaterial != null && baseMaterial.HasProperty("_MainTex") ? baseMaterial.GetTexture("_MainTex") : null;
            if (texture == null) return glint;
            if (glintVariants.TryGetValue((texture, armor), out var variant) && variant != null) return variant;
            variant = new Material(glint) { name = glint.name + " " + texture.name };
            variant.SetTexture("_MainTex", texture);
            owned.Add(variant);
            glintVariants[(texture, armor)] = variant;
            return variant;
        }

        private readonly Dictionary<Texture, (RenderTexture overlay, int frame)> glintOverlays = new Dictionary<Texture, (RenderTexture, int)>();

        /// <summary>
        /// The enchantment glint over an inventory icon (call in the repaint event, after drawing the icon): the icon's
        /// shape with the scrolling glint is blitted into an overlay texture once per frame and drawn on top.
        /// </summary>
        public void DrawGlint(Rect rect, Texture icon)
        {
            if (icon == null || Event.current.type != EventType.Repaint) return;
            if (guiGlint == null) guiGlint = GlintMaterial(1.0f);
            if (guiGlint == null) return;
            if (!glintOverlays.TryGetValue(icon, out var entry) || entry.overlay == null)
            {
                entry = (new RenderTexture(IconSize, IconSize, 0, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Glint " + icon.name }, -1);
                entry.overlay.Create();
                owned.Add(entry.overlay);
            }
            if (entry.frame != Time.frameCount)
            {
                var active = RenderTexture.active;
                Graphics.Blit(icon, entry.overlay, guiGlint, 1);
                RenderTexture.active = active;
                entry.frame = Time.frameCount;
            }
            glintOverlays[icon] = entry;
            GUI.DrawTexture(rect, entry.overlay);
        }

        /// <summary>
        /// The durability bar under a damaged item (vanilla GuiGraphics.renderItemDecorations): 13 pixels, from green
        /// to red as it wears, on a black background.
        /// </summary>
        public static void DrawDurability(GuiRenderer gui, Rect slot, ItemStack stack)
        {
            if (stack == null || !stack.ShowsDurability || !gui.Repaint) return;
            var max = stack.Item.MaxDurability;
            var left = Mathf.Clamp01((max - stack.Damage) / (float)max);
            var width = Mathf.Round(13 - stack.Damage * 13f / max);
            var color = Color.HSVToRGB(left / 3, 1, 1);
            gui.Fill(new Rect(slot.x + 2, slot.y + 13, 13, 2), Color.black);
            gui.Fill(new Rect(slot.x + 2, slot.y + 13, Mathf.Max(0, width), 1), color);
        }

        /// <summary>Icon of a stack: banners and shields with patterns get their own icon, other items the one of the name.</summary>
        public Texture Get(ItemStack stack)
        {
            var name = stack?.Item?.Name.Path;
            var tag = PatternTag(stack);
            if (tag == null) return Get(name);
            var key = name + "|" + PatternKey(tag);
            if (cache.TryGetValue(key, out var cached)) return cached;
            Texture icon = null;
            try
            {
                var parts = SpecialParts?.Invoke(name, tag);
                if (parts != null && parts.Count > 0) icon = RenderParts(name, parts, GuiTransform(name));
            }
            catch (Exception exception)
            {
                Log.Warn("Can not create icon for " + key + ": " + exception.Message);
            }
            cache[key] = icon ?? Get(name);
            return cache[key];
        }

        /// <summary>The BlockEntityTag of a banner or shield that has patterns (or a shield base color), else null.</summary>
        public static object PatternTag(ItemStack stack)
        {
            var name = stack?.Item?.Name.Path;
            if (name == null || name != "shield" && !name.EndsWith("_banner")) return null;
            var tag = Core.Network.Nbt.Get(stack.Nbt, "BlockEntityTag");
            if (tag == null || Core.Network.Nbt.Get(tag, "Patterns") == null && Core.Network.Nbt.Get(tag, "Base") == null) return null;
            return tag;
        }

        public static string PatternKey(object tag)
        {
            var key = new System.Text.StringBuilder().Append(Core.Network.Nbt.GetInt(tag, "Base", -1));
            if (Core.Network.Nbt.Get(tag, "Patterns") is List<object> patterns)
            {
                foreach (var pattern in patterns) key.Append(',').Append(Core.Network.Nbt.GetString(pattern, "Pattern")).Append(Core.Network.Nbt.GetInt(pattern, "Color"));
            }
            return key.ToString();
        }

        public Texture Get(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            if (cache.TryGetValue(itemName, out var cached)) return cached;
            Texture icon = null;
            try
            {
                icon = Create(itemName);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not create icon for " + itemName + ": " + exception.Message);
            }
            cache[itemName] = icon;
            return icon;
        }

        private Texture Create(string itemName)
        {
            // item model: generated items have a layer0 texture, block items have a block model as parent
            if (resources.Assets.TryGetText("assets/minecraft/models/item/" + itemName + ".json", out var json))
            {
                var model = JObject.Parse(json);
                var parent = (string)model["parent"] ?? "";
                var layer0 = (string)model["textures"]?["layer0"];
                if (layer0 != null && !parent.Contains("block/"))
                {
                    var location = ResourceLocation.Of(layer0);
                    var sprite = assets.Get(location.ToAssetPath("textures/", ".png"));
                    if (sprite != null) return FirstFrame(sprite.Texture);
                }
            }
            var block = resources.Blocks.Get(itemName);
            if (resources.ItemBlockModels.TryGetValue(itemName, out var itemModel)) return RenderBlock(block, itemModel, GuiTransform(itemName));
            if (block?.DefaultState.Model is BlockRenderModel render && render.Models[0].Quads.Length > 0) return RenderBlock(block, render.Models[0], GuiTransform(itemName));
            var special = SpecialParts?.Invoke(itemName, null);
            if (special != null && special.Count > 0) return RenderParts(itemName, special, GuiTransform(itemName));
            return null;
        }

        /// <summary>Animated item textures (compass, clock) are vertical strips: use the first square frame.</summary>
        private Texture FirstFrame(Texture2D texture)
        {
            if (texture.height <= texture.width) return texture;
            var frame = new Texture2D(texture.width, texture.width, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            frame.SetPixels(texture.GetPixels(0, texture.height - texture.width, texture.width, texture.width));
            frame.Apply(false, false); // readable: held items are extruded from the pixels
            owned.Add(frame);
            return frame;
        }

        /// <summary>display.gui of the item model or its parents (rotation, translation in blocks, scale); vanilla block default [30, 225, 0] x 0.625.</summary>
        private (Vector3 rotation, Vector3 translation, float scale) GuiTransform(string itemName)
        {
            var path = "assets/minecraft/models/item/" + itemName + ".json";
            for (var depth = 0; depth < 16 && resources.Assets.TryGetText(path, out var text); depth++)
            {
                var json = JObject.Parse(text);
                if (json["display"]?["gui"] is JObject gui)
                {
                    var rotation = gui["rotation"] as JArray;
                    var scale = gui["scale"] as JArray;
                    var translation = gui["translation"] as JArray;
                    return (rotation != null && rotation.Count == 3 ? new Vector3((float)rotation[0], (float)rotation[1], (float)rotation[2]) : Vector3.zero,
                        translation != null && translation.Count == 3 ? new Vector3((float)translation[0], (float)translation[1], -(float)translation[2]) / 16 : Vector3.zero,
                        scale != null && scale.Count == 3 ? (float)scale[0] : 1);
                }
                var parent = (string)json["parent"];
                if (parent == null || parent.StartsWith("builtin/")) break;
                path = ResourceLocation.Of(parent).ToAssetPath("models/", ".json");
            }
            return (new Vector3(30, 225, 0), Vector3.zero, 0.625f);
        }

        /// <summary>"gui_light": "front" in the item model or its parents (flat lighting, vanilla Lighting.setupForFlatItems).</summary>
        private bool FrontLight(string itemName)
        {
            var path = "assets/minecraft/models/item/" + itemName + ".json";
            for (var depth = 0; depth < 16 && resources.Assets.TryGetText(path, out var text); depth++)
            {
                var json = JObject.Parse(text);
                var light = (string)json["gui_light"];
                if (light != null) return light == "front";
                var parent = (string)json["parent"];
                if (parent == null || parent.StartsWith("builtin/")) break;
                path = ResourceLocation.Of(parent).ToAssetPath("models/", ".json");
            }
            return false;
        }

        private Texture RenderBlock(Block block, BakedModel model, (Vector3 rotation, Vector3 translation, float scale) gui)
        {
            var mesh = BuildMesh(block, model);
            var target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Icon " + (block?.Name.ToString() ?? "item") };
            target.Create();

            // vanilla "gui" display transform: rotation [30, 225, 0] (turn first, then tilt toward the camera), scale 0.625, orthographic
            var modelMatrix = Matrix4x4.TRS(gui.translation, Quaternion.AngleAxis(-gui.rotation.x, Vector3.right) * Quaternion.AngleAxis(gui.rotation.y, Vector3.up) * Quaternion.AngleAxis(gui.rotation.z, Vector3.forward), Vector3.one * gui.scale) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f));
            var view = Matrix4x4.TRS(new Vector3(0, 0, -5), Quaternion.identity, Vector3.one).inverse;
            view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * view; // Unity cameras look down -z in view space
            // no render-into-texture flip: it mirrored the icon vertically and turned the winding around (back faces were drawn)
            var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, 0.1f, 20f), false);

            var day = Shader.GetGlobalFloat("_WF_DayLight");
            var commands = new CommandBuffer { name = "WhiteFox item icon" };
            commands.SetRenderTarget(target);
            commands.ClearRenderTarget(true, true, Color.clear);
            commands.SetViewProjectionMatrices(view, projection);
            commands.SetGlobalFloat("_WF_DayLight", 1.0f);
            var fog = Shader.IsKeywordEnabled("FOG_LINEAR");
            commands.DisableShaderKeyword("FOG_LINEAR");
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++) commands.DrawMesh(mesh, modelMatrix, material, submesh, 0);
            if (fog) commands.EnableShaderKeyword("FOG_LINEAR");
            commands.SetGlobalFloat("_WF_DayLight", day);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
            UnityEngine.Object.Destroy(mesh);
            owned.Add(target);
            return target;
        }

        /// <summary>The block's quads with full light and vanilla face shading, in the chunk vertex format.</summary>
        /// <summary>Icon of an entity model item (vanilla builtin/entity items) with the entity shader.</summary>
        private Texture RenderParts(string name, List<(Mesh mesh, Material material, Matrix4x4 matrix)> parts, (Vector3 rotation, Vector3 translation, float scale) gui)
        {
            // entity models draw both faces, so the depth test decides what is in front. The GPU projection of a reversed
            // depth platform puts near at 1, but this command buffer tests "less or equal": undo the reversal (z' = w - z)

            var target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Icon " + name };
            target.Create();
            var modelMatrix = Matrix4x4.TRS(gui.translation, Quaternion.AngleAxis(-gui.rotation.x, Vector3.right) * Quaternion.AngleAxis(gui.rotation.y, Vector3.up) * Quaternion.AngleAxis(gui.rotation.z, Vector3.forward), Vector3.one * gui.scale) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f));
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(new Vector3(0, 0, -5), Quaternion.identity, Vector3.one).inverse;
            var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, 0.1f, 20f), false);
            if (SystemInfo.usesReversedZBuffer) projection.SetRow(2, projection.GetRow(3) - projection.GetRow(2));
            var day = Shader.GetGlobalFloat("_WF_DayLight");
            var modern = Shader.GetGlobalFloat("_WF_ModernEntities");
            var commands = new CommandBuffer { name = "WhiteFox entity item icon" };
            commands.SetRenderTarget(target);
            commands.ClearRenderTarget(true, true, Color.clear);
            commands.SetViewProjectionMatrices(view, projection);
            commands.SetGlobalFloat("_WF_DayLight", 1.0f);
            commands.SetGlobalFloat("_WF_ModernEntities", 0);
            commands.SetGlobalFloat("_WF_ItemFrontLight", FrontLight(name) ? 1 : 0);
            commands.SetGlobalFloat("_WF_DynamicLightCount", 0);
            var fog = Shader.IsKeywordEnabled("FOG_LINEAR");
            commands.DisableShaderKeyword("FOG_LINEAR");
            foreach (var part in parts) commands.DrawMesh(part.mesh, modelMatrix * part.matrix, part.material, 0, 0);
            if (fog) commands.EnableShaderKeyword("FOG_LINEAR");
            commands.SetGlobalFloat("_WF_DayLight", day);
            commands.SetGlobalFloat("_WF_ModernEntities", modern);
            commands.SetGlobalFloat("_WF_ItemFrontLight", 0);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
            owned.Add(target);
            return target;
        }

        internal static Mesh BuildMesh(Block block, BakedModel model)
        {
            var vertices = new List<Core.Meshing.ChunkVertex>();
            var indices = new List<int>();
            var kind = block?.Tint ?? TintType.None;
            var tint = kind == TintType.Grass ? 0x7CBD6B : kind == TintType.Foliage ? 0x48B518 : kind == TintType.Water ? 0x3F76E4 : kind == TintType.Constant ? block.ConstantTint : 0xFFFFFF;
            foreach (var quad in model.Quads)
            {
                var shade = !quad.Shade ? 1.0f : quad.Face == Direction.Up ? 1.0f : quad.Face == Direction.Down ? 0.5f : quad.Face == Direction.North || quad.Face == Direction.South ? 0.8f : 0.6f;
                var color = quad.TintIndex >= 0 ? tint : 0xFFFFFF;
                var packed = (uint)((color >> 16) & 0xFF) | (uint)(color & 0xFF00) | (uint)((color & 0xFF) << 16) | ((uint)(shade * 255) << 24);
                var first = vertices.Count;
                // normals and tangents like the chunk meshes (the lit shader lights and maps the faces with them)
                System.Numerics.Vector3 Render(System.Numerics.Vector3 v) => new System.Numerics.Vector3(v.X, v.Y, -v.Z);
                System.Numerics.Vector2 Uv(int i) => new System.Numerics.Vector2(quad.UVs[i].X, 1 - quad.UVs[i].Y);
                Core.Meshing.SectionMesher.Frame(quad.Positions, Render(quad.Positions[0]), Render(quad.Positions[1]), Render(quad.Positions[2]),
                    Uv(0), Uv(1), Uv(2), (block?.EmitLight ?? 0) / 15.0f, quad.Face, out _, out var packedNormal, out var packedTangent);
                for (var i = 0; i < 4; i++)
                {
                    var p = quad.Positions[i];
                    vertices.Add(new Core.Meshing.ChunkVertex
                    {
                        X = p.X,
                        Y = p.Y,
                        Z = -p.Z,
                        Color = packed,
                        U = quad.UVs[i].X,
                        V = 1.0f - quad.UVs[i].Y,
                        Layer = quad.Texture.Layer,
                        AO = 1.0f,
                        BlockLight = 0,
                        SkyLight = 1,
                        Normal = packedNormal,
                        Tangent = packedTangent,
                    });
                }
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 1);
                indices.Add(first);
                indices.Add(first + 3);
                indices.Add(first + 2);
            }

            var mesh = new Mesh();
            mesh.SetVertexBufferParams(vertices.Count, WorldRenderer.Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, vertices.Count);
            mesh.SetIndexBufferParams(indices.Count, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Count);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Count));
            mesh.bounds = new Bounds(new Vector3(0.5f, 0.5f, -0.5f), Vector3.one * 2);
            return mesh;
        }

        public void Dispose()
        {
            foreach (var item in owned) UnityEngine.Object.Destroy(item);
            owned.Clear();
            guiGlint = worldGlint = armorGlint = null;
            glintVariants.Clear();
            glintOverlays.Clear();
            cache.Clear();
        }
    }
}
