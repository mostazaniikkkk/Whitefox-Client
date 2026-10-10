// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Client.Entities
{
    /// <summary>
    /// Block entity renderers (Minosoft's BlockEntityRenderer + vanilla ChestRenderer, SignRenderer,
    /// HangingSignRenderer, BedRenderer, BannerRenderer, SkullBlockRenderer, ShulkerBoxRenderer). The chunk mesher
    /// reports which blocks of a section need one; cave culling hides them together with their section.
    /// All transforms are written in Minecraft block space, like the vanilla renderers, and converted when applied.
    /// </summary>
    public sealed class BlockEntityRenderer : IDisposable
    {
        private static readonly int LightId = Shader.PropertyToID("_Light");

        private static readonly string[] DyeNames =
        {
            "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black",
        };

        /// <summary>DyeColor texture diffuse colors (banners).</summary>
        private static readonly int[] DyeDiffuse =
        {
            0xF9FFFE, 0xF9801D, 0xC74EBD, 0x3AB3DA, 0xFED83D, 0x80C71F, 0xF38BAA, 0x474F52, 0x9D9D97, 0x169C9C, 0x8932B8, 0x3C44AA, 0x835432, 0x5E7C16, 0xB02E26, 0x1D1D21,
        };

        /// <summary>DyeColor text colors (signs).</summary>
        private static readonly int[] DyeText =
        {
            0xFFFFFF, 0xFF681F, 0xFF00FF, 0x9AC0CD, 0xFFFF00, 0xBFFF00, 0xFF69B4, 0x808080, 0xD3D3D3, 0x00FFFF, 0xA020F0, 0x0000FF, 0x8B4513, 0x00FF00, 0xFF0000, 0x000000,
        };

        /// <summary>Banner pattern codes of the NBT (1.20.4) and their textures in entity/banner.</summary>
        private static readonly Dictionary<string, string> BannerPatterns = new Dictionary<string, string>
        {
            { "b", "base" }, { "bl", "square_bottom_left" }, { "br", "square_bottom_right" }, { "tl", "square_top_left" }, { "tr", "square_top_right" },
            { "bs", "stripe_bottom" }, { "ts", "stripe_top" }, { "ls", "stripe_left" }, { "rs", "stripe_right" }, { "cs", "stripe_center" },
            { "ms", "stripe_middle" }, { "drs", "stripe_downright" }, { "dls", "stripe_downleft" }, { "ss", "small_stripes" }, { "cr", "cross" },
            { "sc", "straight_cross" }, { "bt", "triangle_bottom" }, { "tt", "triangle_top" }, { "bts", "triangles_bottom" }, { "tts", "triangles_top" },
            { "ld", "diagonal_left" }, { "rd", "diagonal_up_right" }, { "lud", "diagonal_up_left" }, { "rud", "diagonal_right" }, { "mc", "circle" },
            { "mr", "rhombus" }, { "vh", "half_vertical" }, { "hh", "half_horizontal" }, { "vhr", "half_vertical_right" }, { "hhb", "half_horizontal_bottom" },
            { "bo", "border" }, { "cbo", "curly_border" }, { "gra", "gradient" }, { "gru", "gradient_up" }, { "bri", "bricks" }, { "glb", "globe" },
            { "cre", "creeper" }, { "sku", "skull" }, { "flo", "flower" }, { "moj", "mojang" }, { "pig", "piglin" },
        };

        private readonly GameSession session;
        private readonly WorldRenderer worldRenderer;
        private readonly GuiAssets assets;
        private readonly GuiFont font;
        private readonly SkinManager skins;
        private readonly Shader shader;
        private readonly Transform root;
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        private readonly Dictionary<SectionPos, List<BlockPos>> sections = new Dictionary<SectionPos, List<BlockPos>>();
        private readonly Dictionary<BlockPos, View> views = new Dictionary<BlockPos, View>();
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        public bool CastShadows;
        public int Count => views.Count;

        private sealed class View
        {
            public BlockState State;
            public BlockEntityKind Kind;
            public GameObject Root;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly Dictionary<string, (Transform transform, ModelPart part)> Parts = new Dictionary<string, (Transform, ModelPart)>();
            public Matrix4x4 Matrix;
            public int Version = -1;
            public string SkinUrl;
            public bool HasSkin;
            // owned per view: sign text meshes/textures, banner flag texture and material
            public readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
            public readonly List<GameObject> Dynamic = new List<GameObject>();
        }

        public BlockEntityRenderer(GameSession session, WorldRenderer worldRenderer, Transform parent, GuiAssets assets, GuiFont font, SkinManager skins)
        {
            this.session = session;
            this.worldRenderer = worldRenderer;
            this.assets = assets;
            this.font = font;
            this.skins = skins;
            shader = Resources.Load<Shader>("WhiteFox/Entity");
            root = new GameObject("Block entities").transform;
            root.SetParent(parent, false);
            worldRenderer.SpecialBlocksChanged += OnSpecialBlocks;
        }

        /// <summary>Model builder only (item icons and held items of chests, beds, banners, heads...): no world.</summary>
        public BlockEntityRenderer(GuiAssets assets, GuiFont font, Transform parent)
        {
            this.assets = assets;
            this.font = font;
            shader = Resources.Load<Shader>("WhiteFox/Entity");
            root = new GameObject("Block entity models").transform;
            root.SetParent(parent, false);
            root.gameObject.SetActive(false);
        }

        private readonly List<UnityEngine.Object> kept = new List<UnityEngine.Object>();

        /// <summary>
        /// The parts of the item form of a block entity (vanilla BlockEntityWithoutLevelRenderer) in render space with
        /// the block occupying 0..1 (z -1..0): meshes, materials and matrices. Beds get both halves, shields their model.
        /// </summary>
        /// <param name="tag">BlockEntityTag of the item (banner patterns, shield Base color and patterns), or null.</param>
        public List<(Mesh mesh, Material material, Matrix4x4 matrix)> ItemParts(ClientResources resources, string item, object tag = null)
        {
            var parts = new List<(Mesh, Material, Matrix4x4)>();
            if (item == "shield")
            {
                var layer = Layer(64, 64);
                layer.Parts.Add(new ModelPart("plate", 0, 0, 0).Box(0, 0, -6, -11, -2, 12, 22, 1));
                layer.Parts.Add(new ModelPart("handle", 0, 0, 0).Box(26, 0, -1, -3, -1, 2, 6, 6));
                var view = new View { Root = new GameObject("Shield") };
                view.Root.transform.SetParent(root, false);
                var material = MaterialFor("entity/shield_base_nopattern");
                // a shield with a Base color or patterns: the plain base and the banner layers (vanilla BannerRenderer.renderPatterns)
                if (tag != null && (Nbt.Get(tag, "Base") != null || Nbt.Get(tag, "Patterns") != null))
                {
                    var texture = CompositePatterns("shield", "entity/shield_base", Mathf.Clamp(Nbt.GetInt(tag, "Base"), 0, 15), tag);
                    material = new Material(shader) { name = "Shield" };
                    material.SetTexture("_MainTex", texture);
                    kept.Add(texture);
                    kept.Add(material);
                }
                Build(view, "shield", layer, material);
                // BlockEntityWithoutLevelRenderer: scale(1, -1, -1)
                EntityRenderer.Apply(view.Root.transform, Matrix4x4.Scale(new Vector3(1, -1, -1)));
                Collect(view, parts, Matrix4x4.identity);
                return parts;
            }
            var block = resources.Blocks.Get(item);
            if (block == null || block.Special == BlockEntityKind.None) return parts;
            var state = block.DefaultState;
            if (block.Special == BlockEntityKind.Bed)
            {
                var head = state.Block.States[0];
                foreach (var candidate in state.Block.States)
                {
                    if (candidate["part"] == "head" && candidate["facing"] == "south") head = candidate;
                }
                AddItemView(head, parts, Matrix4x4.identity);
                foreach (var candidate in state.Block.States)
                {
                    if (candidate["part"] == "foot" && candidate["facing"] == "south") AddItemView(candidate, parts, Matrix4x4.Translate(new Vector3(0, 0, -1)));
                }
                return parts;
            }
            if (block.Special == BlockEntityKind.Chest)
            {
                foreach (var candidate in state.Block.States)
                {
                    if (candidate["facing"] == "south" && (candidate["type"] ?? "single") == "single" && candidate["waterlogged"] != "true") state = candidate;
                }
            }
            AddItemView(state, parts, Matrix4x4.identity, tag);
            return parts;
        }

        private void AddItemView(BlockState state, List<(Mesh, Material, Matrix4x4)> parts, Matrix4x4 offset, object tag = null)
        {
            var view = CreateView(new BlockPos(0, 0, 0), state);
            if (view == null) return;
            if (view.Kind == BlockEntityKind.Banner || view.Kind == BlockEntityKind.WallBanner) BuildBannerFlag(view, tag);
            EntityRenderer.Apply(view.Root.transform, offset * view.Matrix);
            Animate(view, null, new BlockPos(0, 0, 0), 0, 0);
            Collect(view, parts, Matrix4x4.identity);
        }

        private void Collect(View view, List<(Mesh, Material, Matrix4x4)> parts, Matrix4x4 extra)
        {
            foreach (var renderer in view.Root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                for (var i = 0; i < renderer.sharedMaterials.Length && i < Mathf.Max(1, filter.sharedMesh.subMeshCount); i++)
                {
                    parts.Add((filter.sharedMesh, renderer.sharedMaterials[i], extra * matrix));
                }
            }
            kept.AddRange(view.Owned);
            view.Owned.Clear();
            UnityEngine.Object.Destroy(view.Root);
        }

        private void OnSpecialBlocks(SectionPos section, List<BlockPos> positions)
        {
            if (sections.TryGetValue(section, out var old))
            {
                foreach (var position in old)
                {
                    if (!positions.Contains(position)) RemoveView(position);
                }
            }
            if (positions.Count == 0)
            {
                sections.Remove(section);
                return;
            }
            sections[section] = new List<BlockPos>(positions);
            foreach (var position in positions)
            {
                var state = session.World.GetBlock(position);
                if (views.TryGetValue(position, out var view) && view.State == state) continue;
                RemoveView(position);
                if (state == null || state.Block.Special == BlockEntityKind.None) continue;
                view = CreateView(position, state);
                if (view != null) views[position] = view;
            }
        }

        private void RemoveView(BlockPos position)
        {
            if (!views.TryGetValue(position, out var view)) return;
            views.Remove(position);
            foreach (var owned in view.Owned) UnityEngine.Object.Destroy(owned);
            UnityEngine.Object.Destroy(view.Root);
        }

        // ------------------------------------------------------------------ models

        private Material MaterialFor(string texture)
        {
            if (materials.TryGetValue(texture, out var material)) return material;
            material = new Material(shader) { name = texture };
            var sprite = assets.Get("assets/minecraft/textures/" + texture + ".png");
            material.SetTexture("_MainTex", sprite != null ? sprite.Texture : Texture2D.whiteTexture);
            materials[texture] = material;
            return material;
        }

        private static ModelLayer Layer(int width, int height) => new ModelLayer { TextureWidth = width, TextureHeight = height };

        /// <summary>Adds the parts of a model layer below the view root; meshes are shared between views by key.</summary>
        private void Build(View view, string key, ModelLayer layer, Material material, Transform parent = null)
        {
            foreach (var part in layer.Parts) BuildPart(view, key, layer, part, material, parent ?? view.Root.transform);
        }

        private void BuildPart(View view, string key, ModelLayer layer, ModelPart part, Material material, Transform parent)
        {
            var meshKey = key + "/" + part.Name;
            if (!meshes.TryGetValue(meshKey, out var mesh)) meshes[meshKey] = mesh = EntityRenderer.PartMesh(part, layer);
            var gameObject = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(parent, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.receiveShadows = true;
            view.Renderers.Add(renderer);
            view.Parts[part.Name] = (gameObject.transform, part);
            SetPart(view, part.Name, part.Pivot, part.Rotation);
            foreach (var child in part.Children) BuildPart(view, key, layer, child, material, gameObject.transform);
        }

        private static void SetPart(View view, string name, Vector3 pivot, Vector3 rotation)
        {
            if (!view.Parts.TryGetValue(name, out var entry)) return;
            EntityRenderer.Apply(entry.transform, Matrix4x4.Translate(pivot / 16f) * EntityRenderer.RotZ(rotation.z) * EntityRenderer.RotY(rotation.y) * EntityRenderer.RotX(rotation.x), true);
        }

        private static float YRot(string facing)
        {
            switch (facing)
            {
                case "west": return 90;
                case "north": return 180;
                case "east": return 270;
                default: return 0;
            }
        }

        private static Matrix4x4 Deg(Func<float, Matrix4x4> rotation, float degrees) => rotation(degrees * Mathf.Deg2Rad);

        private static int Rotation16(BlockState state) => int.TryParse(state["rotation"], out var rotation) ? rotation : 0;

        private View CreateView(BlockPos position, BlockState state)
        {
            var view = new View { State = state, Kind = state.Block.Special };
            view.Root = new GameObject(view.Kind + " " + position);
            view.Root.transform.SetParent(root, false);
            var name = state.Block.Name.Path;
            var ok = true;
            switch (view.Kind)
            {
                case BlockEntityKind.Chest:
                    CreateChest(view, name, state);
                    break;
                case BlockEntityKind.Sign:
                case BlockEntityKind.WallSign:
                    CreateSign(view, name, state);
                    break;
                case BlockEntityKind.HangingSign:
                case BlockEntityKind.WallHangingSign:
                    CreateHangingSign(view, name, state);
                    break;
                case BlockEntityKind.Bed:
                    CreateBed(view, name, state);
                    break;
                case BlockEntityKind.Banner:
                case BlockEntityKind.WallBanner:
                    CreateBanner(view, state);
                    break;
                case BlockEntityKind.Skull:
                case BlockEntityKind.WallSkull:
                    ok = CreateSkull(view, name, state);
                    break;
                case BlockEntityKind.ShulkerBox:
                    CreateShulker(view, name, state);
                    break;
            }
            if (!ok)
            {
                UnityEngine.Object.Destroy(view.Root);
                return null;
            }
            return view;
        }

        // ------------------------------------------------------------------ chest

        private void CreateChest(View view, string name, BlockState state)
        {
            var type = state["type"] ?? "single";
            var texture = name == "ender_chest" ? "ender" : name == "trapped_chest" ? "trapped" : "normal";
            var layer = Layer(64, 64);
            if (type == "single" || name == "ender_chest")
            {
                type = "single";
                layer.Parts.Add(new ModelPart("bottom", 0, 0, 0).Box(0, 19, 1, 0, 1, 14, 10, 14));
                layer.Parts.Add(new ModelPart("lid", 0, 9, 1).Box(0, 0, 1, 0, 0, 14, 5, 14));
                layer.Parts.Add(new ModelPart("lock", 0, 9, 1).Box(0, 0, 7, -2, 14, 2, 4, 1));
            }
            else if (type == "left")
            {
                texture += "_left";
                layer.Parts.Add(new ModelPart("bottom", 0, 0, 0).Box(0, 19, 0, 0, 1, 15, 10, 14));
                layer.Parts.Add(new ModelPart("lid", 0, 9, 1).Box(0, 0, 0, 0, 0, 15, 5, 14));
                layer.Parts.Add(new ModelPart("lock", 0, 9, 1).Box(0, 0, 0, -2, 14, 1, 4, 1));
            }
            else
            {
                texture += "_right";
                layer.Parts.Add(new ModelPart("bottom", 0, 0, 0).Box(0, 19, 1, 0, 1, 15, 10, 14));
                layer.Parts.Add(new ModelPart("lid", 0, 9, 1).Box(0, 0, 1, 0, 0, 15, 5, 14));
                layer.Parts.Add(new ModelPart("lock", 0, 9, 1).Box(0, 0, 15, -2, 14, 1, 4, 1));
            }
            Build(view, "chest_" + type, layer, MaterialFor("entity/chest/" + texture));
            view.Matrix = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * Deg(EntityRenderer.RotY, -YRot(state["facing"])) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f));
        }

        // ------------------------------------------------------------------ signs

        private static string Wood(string name, params string[] suffixes)
        {
            foreach (var suffix in suffixes)
            {
                if (name.EndsWith(suffix)) return name.Substring(0, name.Length - suffix.Length);
            }
            return "oak";
        }

        private void CreateSign(View view, string name, BlockState state)
        {
            var wall = view.Kind == BlockEntityKind.WallSign;
            var layer = Layer(64, 32);
            layer.Parts.Add(new ModelPart("board", 0, 0, 0).Box(0, 0, -12, -14, -1, 24, 12, 2));
            if (!wall) layer.Parts.Add(new ModelPart("stick", 0, 0, 0).Box(0, 14, -1, -2, -1, 2, 14, 2));
            var holder = new GameObject("model").transform;
            holder.SetParent(view.Root.transform, false);
            EntityRenderer.Apply(holder, Matrix4x4.Scale(new Vector3(0.6666667f, -0.6666667f, -0.6666667f)), true);
            Build(view, wall ? "wall_sign" : "sign", layer, MaterialFor("entity/signs/" + Wood(name, "_wall_sign", "_sign")), holder);

            var angle = wall ? YRot(state["facing"]) : Rotation16(state) * 22.5f;
            view.Matrix = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * Deg(EntityRenderer.RotY, -angle);
            if (wall) view.Matrix *= Matrix4x4.Translate(new Vector3(0, -0.3125f, -0.4375f));
        }

        private void CreateHangingSign(View view, string name, BlockState state)
        {
            var wall = view.Kind == BlockEntityKind.WallHangingSign;
            var attached = state["attached"] == "true";
            var layer = Layer(64, 32);
            layer.Parts.Add(new ModelPart("board", 0, 0, 0).Box(0, 12, -7, 0, -1, 14, 10, 2));
            if (wall) layer.Parts.Add(new ModelPart("plank", 0, 0, 0).Box(0, 0, -8, -6, -2, 16, 2, 4));
            if (!wall && attached)
            {
                layer.Parts.Add(new ModelPart("v_chains", 0, 0, 0).Box(14, 6, -6, -6, 0, 12, 6, 0));
            }
            else
            {
                const float quarter = Mathf.PI / 4;
                layer.Parts.Add(new ModelPart("left_chain_1", -5, -6, 0, 0, -quarter).Box(0, 6, -1.5f, 0, 0, 3, 6, 0));
                layer.Parts.Add(new ModelPart("left_chain_2", -5, -6, 0, 0, quarter).Box(6, 6, -1.5f, 0, 0, 3, 6, 0));
                layer.Parts.Add(new ModelPart("right_chain_1", 5, -6, 0, 0, -quarter).Box(0, 6, -1.5f, 0, 0, 3, 6, 0));
                layer.Parts.Add(new ModelPart("right_chain_2", 5, -6, 0, 0, quarter).Box(6, 6, -1.5f, 0, 0, 3, 6, 0));
            }
            var holder = new GameObject("model").transform;
            holder.SetParent(view.Root.transform, false);
            EntityRenderer.Apply(holder, Matrix4x4.Scale(new Vector3(1, -1, -1)), true);
            var key = "hanging_sign" + (wall ? "_wall" : attached ? "_attached" : "");
            Build(view, key, layer, MaterialFor("entity/signs/hanging/" + Wood(name, "_wall_hanging_sign", "_hanging_sign")), holder);

            var angle = wall ? YRot(state["facing"]) : Rotation16(state) * 22.5f;
            view.Matrix = Matrix4x4.Translate(new Vector3(0.5f, 0.9375f, 0.5f)) * Deg(EntityRenderer.RotY, -angle) * Matrix4x4.Translate(new Vector3(0, -0.3125f, 0));
        }

        /// <summary>
        /// Sign text (vanilla SignRenderer.renderSignText): four centered lines drawn into a texture, on a quad in front of
        /// (and behind) the board. Not glowing text is darkened to 40%; glowing text ignores the light.
        /// </summary>
        private void BuildSignText(View view, BlockEntity entity)
        {
            foreach (var gameObject in view.Dynamic)
            {
                view.Renderers.Remove(gameObject.GetComponent<Renderer>());
                UnityEngine.Object.Destroy(gameObject);
            }
            view.Dynamic.Clear();
            foreach (var owned in view.Owned) UnityEngine.Object.Destroy(owned);
            view.Owned.Clear();
            if (entity?.Nbt == null) return;

            var hanging = view.Kind == BlockEntityKind.HangingSign || view.Kind == BlockEntityKind.WallHangingSign;
            var lineHeight = hanging ? 9 : 10;
            var maxWidth = hanging ? 60 : 90;
            var scale = hanging ? 0.015625f * 0.9f : 0.010416667f;
            var offset = hanging ? new Vector3(0, -0.32f, 0.073f) : new Vector3(0, 0.33333334f, 0.046666667f);
            AddSignSide(view, Nbt.Get(entity.Nbt, "front_text"), true, lineHeight, maxWidth, scale, offset);
            AddSignSide(view, Nbt.Get(entity.Nbt, "back_text"), false, lineHeight, maxWidth, scale, offset);
        }

        private void AddSignSide(View view, object text, bool front, int lineHeight, int maxWidth, float scale, Vector3 offset)
        {
            if (!(Nbt.Get(text, "messages") is List<object> messages)) return;
            var lines = new string[4];
            var any = false;
            for (var i = 0; i < 4 && i < messages.Count; i++)
            {
                lines[i] = PlainText(messages[i] as string);
                any |= !string.IsNullOrWhiteSpace(LegacyFormatting.Strip(lines[i]));
            }
            if (!any) return;

            var dye = Array.IndexOf(DyeNames, Nbt.GetString(text, "color") ?? "black");
            var glowing = Nbt.GetInt(text, "has_glowing_text") != 0;
            var rgb = DyeText[Mathf.Max(0, dye)];
            var factor = glowing ? 1.0f : 0.4f;
            var color = new Color32((byte)(((rgb >> 16) & 0xFF) * factor), (byte)(((rgb >> 8) & 0xFF) * factor), (byte)((rgb & 0xFF) * factor), 255);

            int width = maxWidth, height = lineHeight * 4;
            var pixels = new Color32[width * height];
            for (var i = 0; i < 4; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) continue;
                font.Rasterize(lines[i], pixels, width, height, (width - font.Width(lines[i])) / 2, i * lineHeight, color);
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Sign text" };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var material = new Material(shader) { name = "Sign text" };
            material.SetTexture("_MainTex", texture);
            material.SetFloat("_Fullbright", glowing ? 1 : 0);

            // quad in sign space: x from -width/2 to width/2, y from +height/2 (top) to -height/2, at the text offset
            float halfWidth = width / 2f * scale, halfHeight = height / 2f * scale;
            var z = front ? offset.z : -offset.z;
            var left = front ? -halfWidth : halfWidth;
            var right = -left;
            var corners = new[]
            {
                new Vector3(left, offset.y - halfHeight, z), new Vector3(right, offset.y - halfHeight, z),
                new Vector3(right, offset.y + halfHeight, z), new Vector3(left, offset.y + halfHeight, z),
            };
            var mesh = new Mesh { name = "Sign text" };
            var vertices = new List<Vector3>();
            foreach (var corner in corners) vertices.Add(new Vector3(corner.x, corner.y, -corner.z)); // render space
            mesh.SetVertices(vertices);
            var normal = new Vector3(0, 0, front ? -1 : 1);
            mesh.SetNormals(new List<Vector3> { normal, normal, normal, normal });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();

            var gameObject = new GameObject(front ? "Front text" : "Back text", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            view.Renderers.Add(renderer);
            view.Dynamic.Add(gameObject);
            view.Owned.Add(texture);
            view.Owned.Add(material);
            view.Owned.Add(mesh);
        }

        private static string PlainText(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                return TextComponent.FromJson(json).ToPlain();
            }
            catch (Exception)
            {
                return json;
            }
        }

        // ------------------------------------------------------------------ bed

        private void CreateBed(View view, string name, BlockState state)
        {
            var head = state["part"] == "head";
            var layer = Layer(64, 64);
            if (head)
            {
                layer.Parts.Add(new ModelPart("main", 0, 0, 0).Box(0, 0, 0, 0, 0, 16, 16, 6));
                layer.Parts.Add(new ModelPart("left_leg", 0, 0, 0, Mathf.PI / 2, 0, Mathf.PI / 2).Box(50, 6, 0, 6, 0, 3, 3, 3));
                layer.Parts.Add(new ModelPart("right_leg", 0, 0, 0, Mathf.PI / 2, 0, Mathf.PI).Box(50, 18, -16, 6, 0, 3, 3, 3));
            }
            else
            {
                layer.Parts.Add(new ModelPart("main", 0, 0, 0).Box(0, 22, 0, 0, 0, 16, 16, 6));
                layer.Parts.Add(new ModelPart("left_leg", 0, 0, 0, Mathf.PI / 2).Box(50, 0, 0, 6, -16, 3, 3, 3));
                layer.Parts.Add(new ModelPart("right_leg", 0, 0, 0, Mathf.PI / 2, 0, Mathf.PI * 1.5f).Box(50, 12, -16, 6, -16, 3, 3, 3));
            }
            Build(view, head ? "bed_head" : "bed_foot", layer, MaterialFor("entity/bed/" + Wood(name, "_bed")));
            view.Matrix = Matrix4x4.Translate(new Vector3(0, 0.5625f, 0)) * Deg(EntityRenderer.RotX, 90)
                          * Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * Deg(EntityRenderer.RotZ, 180 + YRot(state["facing"])) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f));
        }

        // ------------------------------------------------------------------ banner

        private void CreateBanner(View view, BlockState state)
        {
            var wall = view.Kind == BlockEntityKind.WallBanner;
            var layer = Layer(64, 64);
            if (!wall) layer.Parts.Add(new ModelPart("pole", 0, 0, 0).Box(44, 0, -1, -30, -1, 2, 42, 2));
            layer.Parts.Add(new ModelPart("bar", 0, 0, 0).Box(0, 42, -10, -32, -1, 20, 2, 2));
            var holder = new GameObject("model").transform;
            holder.SetParent(view.Root.transform, false);
            EntityRenderer.Apply(holder, Matrix4x4.Scale(new Vector3(0.6666667f, -0.6666667f, -0.6666667f)), true);
            Build(view, wall ? "wall_banner" : "banner", layer, MaterialFor("entity/banner_base"), holder);

            var angle = wall ? YRot(state["facing"]) : Rotation16(state) * 22.5f;
            view.Matrix = wall
                ? Matrix4x4.Translate(new Vector3(0.5f, -0.16666667f, 0.5f)) * Deg(EntityRenderer.RotY, -angle) * Matrix4x4.Translate(new Vector3(0, -0.3125f, -0.4375f))
                : Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * Deg(EntityRenderer.RotY, -angle);
        }

        /// <summary>The flag with its patterns composed into one texture (vanilla renders one layer per pattern).</summary>
        private void BuildBannerFlag(View view, BlockEntity entity) => BuildBannerFlag(view, entity?.Nbt);

        /// <summary>
        /// Banner layers composited into one texture: an optional untinted background (the shield's wood), the base color
        /// and every pattern of the Patterns list tinted with its dye (masks of textures/entity/banner or entity/shield).
        /// </summary>
        private Texture2D CompositePatterns(string folder, string background, int baseColor, object nbt)
        {
            const int size = 64;
            var pixels = new Color[size * size];
            if (background != null)
            {
                var plain = assets.Get("assets/minecraft/textures/" + background + ".png");
                if (plain != null && plain.Texture.width == size && plain.Texture.height == size) pixels = plain.Texture.GetPixels();
            }
            var layers = new List<(string texture, int color)> { ("base", baseColor) };
            if (Nbt.Get(nbt, "Patterns") is List<object> patterns)
            {
                foreach (var pattern in patterns)
                {
                    var code = Nbt.GetString(pattern, "Pattern");
                    if (code != null && BannerPatterns.TryGetValue(code, out var texture)) layers.Add((texture, Mathf.Clamp(Nbt.GetInt(pattern, "Color"), 0, 15)));
                }
            }
            foreach (var (texture, color) in layers)
            {
                var sprite = assets.Get("assets/minecraft/textures/entity/" + folder + "/" + texture + ".png");
                if (sprite == null || sprite.Texture.width != size || sprite.Texture.height != size) continue;
                var mask = sprite.Texture.GetPixels();
                var rgb = DyeDiffuse[color];
                var tint = new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
                for (var i = 0; i < pixels.Length; i++)
                {
                    var alpha = mask[i].a;
                    if (alpha <= 0) continue;
                    var value = new Color(mask[i].r * tint.r, mask[i].g * tint.g, mask[i].b * tint.b, 1);
                    pixels[i] = Color.Lerp(pixels[i], value, alpha);
                    pixels[i].a = Mathf.Max(pixels[i].a, alpha);
                }
            }
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = folder };
            result.SetPixels(pixels);
            result.Apply(false, true);
            return result;
        }

        private void BuildBannerFlag(View view, object nbt)
        {
            foreach (var gameObject in view.Dynamic)
            {
                view.Renderers.Remove(gameObject.GetComponent<Renderer>());
                UnityEngine.Object.Destroy(gameObject);
            }
            view.Dynamic.Clear();
            foreach (var owned in view.Owned) UnityEngine.Object.Destroy(owned);
            view.Owned.Clear();

            var baseColor = Array.IndexOf(DyeNames, Wood(view.State.Block.Name.Path, "_wall_banner", "_banner"));
            var flagTexture = CompositePatterns("banner", null, Mathf.Max(0, baseColor), nbt);
            var material = new Material(shader) { name = "Banner" };
            material.SetTexture("_MainTex", flagTexture);
            view.Owned.Add(flagTexture);
            view.Owned.Add(material);

            var layer = Layer(64, 64);
            var flag = new ModelPart("flag", 0, 0, 0).Box(0, 0, -10, 0, -2, 20, 40, 1);
            layer.Parts.Add(flag);
            var holder = view.Root.transform.Find("model");
            var before = view.Renderers.Count;
            Build(view, "banner_flag", layer, material, holder);
            for (var i = before; i < view.Renderers.Count; i++) view.Dynamic.Add(view.Renderers[i].gameObject);
        }

        // ------------------------------------------------------------------ skull

        private bool CreateSkull(View view, string name, BlockState state)
        {
            var wall = view.Kind == BlockEntityKind.WallSkull;
            var type = Wood(name, "_wall_skull", "_wall_head", "_skull", "_head");
            var layer = Layer(64, 32);
            string texture;
            switch (type)
            {
                case "skeleton":
                    texture = "entity/skeleton/skeleton";
                    break;
                case "wither_skeleton":
                    texture = "entity/skeleton/wither_skeleton";
                    break;
                case "creeper":
                    texture = "entity/creeper/creeper";
                    break;
                case "zombie":
                case "player":
                    texture = type == "zombie" ? "entity/zombie/zombie" : "entity/player/wide/steve";
                    layer = Layer(64, 64);
                    break;
                case "piglin":
                    texture = "entity/piglin/piglin";
                    layer = Layer(64, 64);
                    break;
                default:
                    return false; // dragon head: not modelled yet
            }
            var head = new ModelPart("head", 0, 0, 0);
            if (type == "piglin")
            {
                head.Box(0, 0, -5, -8, -4, 10, 8, 8);
                head.Child(new ModelPart("left_ear", 4.5f, -6, 0, 0, 0, -Mathf.PI / 6).Box(51, 6, 0, 0, -2, 1, 5, 4));
                head.Child(new ModelPart("right_ear", -4.5f, -6, 0, 0, 0, Mathf.PI / 6).Box(39, 6, -1, 0, -2, 1, 5, 4));
            }
            else
            {
                head.Box(0, 0, -4, -8, -4, 8, 8, 8);
                if (layer.TextureHeight == 64) head.Child(new ModelPart("hat", 0, 0, 0).Box(32, 0, -4, -8, -4, 8, 8, 8, 0.25f));
            }
            layer.Parts.Add(head);
            var material = MaterialFor(texture);
            if (type == "player")
            {
                material = new Material(material) { name = "Player head" };
                view.Owned.Add(material);
                view.HasSkin = true;
            }
            Build(view, "skull_" + type, layer, material);

            float rotation;
            if (wall)
            {
                var facing = state["facing"];
                rotation = YRot(facing) + 180; // segment of the opposite direction
                var step = facing == "north" ? new Vector2(0, -1) : facing == "south" ? new Vector2(0, 1) : facing == "west" ? new Vector2(-1, 0) : new Vector2(1, 0);
                view.Matrix = Matrix4x4.Translate(new Vector3(0.5f - step.x * 0.25f, 0.25f, 0.5f - step.y * 0.25f));
            }
            else
            {
                rotation = Rotation16(state) * 22.5f;
                view.Matrix = Matrix4x4.Translate(new Vector3(0.5f, 0, 0.5f));
            }
            view.Matrix *= Matrix4x4.Scale(new Vector3(-1, -1, 1));
            SetPart(view, "head", Vector3.zero, new Vector3(0, rotation * Mathf.Deg2Rad, 0));
            return true;
        }

        /// <summary>Player head skin: SkullOwner.Properties.textures[0].Value is base64 JSON with textures.SKIN.url.</summary>
        private static string SkullSkinUrl(BlockEntity entity)
        {
            var owner = Nbt.Get(entity?.Nbt, "SkullOwner");
            if (!(Nbt.Get(Nbt.Get(owner, "Properties"), "textures") is List<object> textures) || textures.Count == 0) return null;
            var value = Nbt.GetString(textures[0], "Value");
            if (value == null) return null;
            try
            {
                var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                return (string)json["textures"]?["SKIN"]?["url"];
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ shulker box

        private void CreateShulker(View view, string name, BlockState state)
        {
            var color = name == "shulker_box" ? null : Wood(name, "_shulker_box");
            var layer = Layer(64, 64);
            layer.Parts.Add(new ModelPart("base", 0, 24, 0).Box(0, 28, -8, -8, -8, 16, 8, 16));
            layer.Parts.Add(new ModelPart("lid", 0, 24, 0).Box(0, 0, -8, -16, -8, 16, 12, 16));
            Build(view, "shulker", layer, MaterialFor("entity/shulker/shulker" + (color != null ? "_" + color : "")));

            Matrix4x4 direction;
            switch (state["facing"])
            {
                case "down":
                    direction = EntityRenderer.RotX(Mathf.PI);
                    break;
                case "north":
                    direction = EntityRenderer.RotX(Mathf.PI / 2) * EntityRenderer.RotZ(Mathf.PI);
                    break;
                case "south":
                    direction = EntityRenderer.RotX(Mathf.PI / 2);
                    break;
                case "west":
                    direction = EntityRenderer.RotX(Mathf.PI / 2) * EntityRenderer.RotZ(Mathf.PI / 2);
                    break;
                case "east":
                    direction = EntityRenderer.RotX(Mathf.PI / 2) * EntityRenderer.RotZ(-Mathf.PI / 2);
                    break;
                default:
                    direction = Matrix4x4.identity;
                    break;
            }
            view.Matrix = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f)) * Matrix4x4.Scale(Vector3.one * 0.9995f) * direction
                          * Matrix4x4.Scale(new Vector3(1, -1, -1)) * Matrix4x4.Translate(new Vector3(0, -1, 0));
        }

        // ------------------------------------------------------------------ per frame

        public void Update(float partial)
        {
            var world = session.World;
            var time = session.Ticks;
            foreach (var pair in views)
            {
                var position = pair.Key;
                var view = pair.Value;
                var section = new SectionPos(position.X >> 4, position.Y >> 4, position.Z >> 4);
                var visible = worldRenderer.IsVisible(section);
                if (view.Root.activeSelf != visible) view.Root.SetActive(visible);
                if (!visible) continue;

                var entity = world.BlockEntities.Get(position);
                var version = entity?.Version ?? 0;
                if (version != view.Version)
                {
                    view.Version = version;
                    if (view.Kind == BlockEntityKind.Sign || view.Kind == BlockEntityKind.WallSign || view.Kind == BlockEntityKind.HangingSign || view.Kind == BlockEntityKind.WallHangingSign) BuildSignText(view, entity);
                    else if (view.Kind == BlockEntityKind.Banner || view.Kind == BlockEntityKind.WallBanner) BuildBannerFlag(view, entity);
                    else if (view.HasSkin) view.SkinUrl = SkullSkinUrl(entity);
                }

                EntityRenderer.Apply(view.Root.transform, Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z)) * view.Matrix);
                Animate(view, entity, position, partial, time);

                var light = world.GetLight(position.X, position.Y, position.Z);
                properties.Clear();
                properties.SetVector(LightId, new Vector4((light & 0x0F) / 15f, (light >> 4) / 15f, 0, 0));
                var shadows = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                foreach (var renderer in view.Renderers)
                {
                    renderer.SetPropertyBlock(properties);
                    if (renderer.shadowCastingMode != shadows && !view.Dynamic.Contains(renderer.gameObject)) renderer.shadowCastingMode = shadows;
                }
            }
        }

        private void Animate(View view, BlockEntity entity, BlockPos position, float partial, long time)
        {
            switch (view.Kind)
            {
                case BlockEntityKind.Chest:
                {
                    var open = entity?.LerpOpenness(partial) ?? 0;
                    open = 1 - open;
                    open = 1 - open * open * open;
                    var angle = -(open * Mathf.PI / 2);
                    SetPart(view, "lid", new Vector3(0, 9, 1), new Vector3(angle, 0, 0));
                    SetPart(view, "lock", new Vector3(0, 9, 1), new Vector3(angle, 0, 0));
                    break;
                }
                case BlockEntityKind.ShulkerBox:
                {
                    var progress = entity?.LerpOpenness(partial) ?? 0;
                    SetPart(view, "lid", new Vector3(0, 24 - progress * 0.5f * 16, 0), new Vector3(0, 270 * progress * Mathf.Deg2Rad, 0));
                    break;
                }
                case BlockEntityKind.Banner:
                case BlockEntityKind.WallBanner:
                {
                    var phase = (((long)(position.X * 7 + position.Y * 9 + position.Z * 13) + time) % 100 + 100) % 100;
                    var h = (phase + partial) / 100f;
                    SetPart(view, "flag", new Vector3(0, -32, 0), new Vector3((-0.0125f + 0.01f * Mathf.Cos(Mathf.PI * 2 * h)) * Mathf.PI, 0, 0));
                    break;
                }
                default:
                    if (view.HasSkin && view.SkinUrl != null && view.Renderers.Count > 0)
                    {
                        var skin = skins.GetUrl(view.SkinUrl);
                        if (skin != null && view.Renderers[0].sharedMaterial.mainTexture != skin) view.Renderers[0].sharedMaterial.SetTexture("_MainTex", skin);
                    }
                    break;
            }
        }

        public void Dispose()
        {
            foreach (var owned in kept) UnityEngine.Object.Destroy(owned);
            if (worldRenderer != null) worldRenderer.SpecialBlocksChanged -= OnSpecialBlocks;
            foreach (var position in new List<BlockPos>(views.Keys)) RemoveView(position);
            foreach (var material in materials.Values) UnityEngine.Object.Destroy(material);
            foreach (var mesh in meshes.Values) UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(root.gameObject);
        }
    }
}
