// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Models;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Entities
{
    /// <summary>Display contexts of item models (vanilla ItemDisplayContext).</summary>
    public enum ItemDisplay
    {
        Ground,
        ThirdPersonRightHand,
        FirstPersonRightHand,
        Fixed,
    }

    /// <summary>A renderable item: mesh in render space covering the unit cube, its material and display transforms.</summary>
    public sealed class ItemModel
    {
        public Mesh Mesh;
        public Material Material;
        public bool Block;
        /// <summary>Entity model items (chests, beds...): several meshes in render space instead of <see cref="Mesh"/>.</summary>
        public List<(Mesh mesh, Material material, Matrix4x4 matrix)> Parts;
        /// <summary>Per display: translation (in blocks), rotation (degrees, applied x, y, z) and scale.</summary>
        public readonly Dictionary<ItemDisplay, (Vector3 translation, Vector3 rotation, Vector3 scale)> Display = new Dictionary<ItemDisplay, (Vector3, Vector3, Vector3)>();

        public (Vector3 translation, Vector3 rotation, Vector3 scale) Get(ItemDisplay display) =>
            Display.TryGetValue(display, out var value) ? value : (Vector3.zero, Vector3.zero, Vector3.one);
    }

    /// <summary>
    /// Item models for dropped items, held items and the first person hand: block items use their baked block model,
    /// generated items a two sided sprite. Display transforms are read from the item model json and its parents.
    /// </summary>
    public sealed class ItemModels : IDisposable
    {
        private static readonly Dictionary<ItemDisplay, string> DisplayNames = new Dictionary<ItemDisplay, string>
        {
            { ItemDisplay.Ground, "ground" },
            { ItemDisplay.ThirdPersonRightHand, "thirdperson_righthand" },
            { ItemDisplay.FirstPersonRightHand, "firstperson_righthand" },
            { ItemDisplay.Fixed, "fixed" },
        };

        private readonly ClientResources resources;
        private readonly ItemIcons icons;
        private readonly Material blockMaterial;
        private readonly Shader shader;
        private readonly Dictionary<string, ItemModel> cache = new Dictionary<string, ItemModel>();

        public ItemModels(ClientResources resources, ItemIcons icons, Material blockMaterial)
        {
            this.resources = resources;
            this.icons = icons;
            this.blockMaterial = blockMaterial;
            shader = Resources.Load<Shader>("WhiteFox/Entity");
        }

        public ItemModel Get(string item)
        {
            if (string.IsNullOrEmpty(item)) return null;
            if (cache.TryGetValue(item, out var cached)) return cached;
            ItemModel model = null;
            try
            {
                model = Create(item);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not create item model for " + item + ": " + exception.Message);
            }
            cache[item] = model;
            return model;
        }

        private ItemModel Create(string item)
        {
            var model = new ItemModel();
            JObject json = null;
            if (resources.Assets.TryGetText("assets/minecraft/models/item/" + item + ".json", out var text)) json = JObject.Parse(text);
            var parent = (string)json?["parent"] ?? "";
            var layer0 = (string)json?["textures"]?["layer0"];
            var block = resources.Blocks.Get(item);
            if (item == "filled_map")
            {
                // maps show their own texture (set per renderer), on a flat square
                model.Mesh = SpriteMesh();
                model.Material = new Material(shader) { name = "Map" };
                model.Material.SetTexture("_MainTex", icons.Get(item));
            }
            else if (layer0 == null || parent.Contains("block/"))
            {
                BakedModel baked = null;
                if (resources.ItemBlockModels.TryGetValue(item, out var itemModel)) baked = itemModel;
                else if (block?.DefaultState.Model is BlockRenderModel render && render.Models[0].Quads.Length > 0) baked = render.Models[0];
                if (baked == null)
                {
                    var parts = icons.SpecialParts?.Invoke(item, null);
                    if (parts == null || parts.Count == 0) return null;
                    model.Parts = parts;
                }
                else
                {
                    model.Mesh = ItemIcons.BuildMesh(block, baked);
                }
                model.Material = blockMaterial;
                model.Block = true;
            }
            else
            {
                var icon = icons.Get(item);
                if (icon == null) return null;
                model.Mesh = (icon is Texture2D readable && readable.isReadable ? ExtrudedMesh(readable) : null) ?? SpriteMesh();
                model.Material = new Material(shader) { name = "Item " + item };
                model.Material.SetTexture("_MainTex", icon);
            }

            // display transforms: the first definition found walking up the parents
            var current = json;
            var depth = 0;
            while (current != null && depth++ < 16)
            {
                if (current["display"] is JObject display)
                {
                    foreach (var pair in DisplayNames)
                    {
                        if (model.Display.ContainsKey(pair.Key) || !(display[pair.Value] is JObject transform)) continue;
                        var translation = Vector(transform["translation"], Vector3.zero);
                        translation = new Vector3(Mathf.Clamp(translation.x, -80, 80), Mathf.Clamp(translation.y, -80, 80), Mathf.Clamp(translation.z, -80, 80)) / 16f;
                        model.Display[pair.Key] = (translation, Vector(transform["rotation"], Vector3.zero), Vector(transform["scale"], Vector3.one));
                    }
                }
                var next = (string)current["parent"];
                if (next == null || next.StartsWith("builtin/")) break;
                var location = ResourceLocation.Of(next);
                current = resources.Assets.TryGetText(location.ToAssetPath("models/", ".json"), out var parentText) ? JObject.Parse(parentText) : null;
            }
            return model;
        }

        private static Vector3 Vector(JToken token, Vector3 fallback)
        {
            if (!(token is JArray array) || array.Count < 3) return fallback;
            return new Vector3((float)array[0], (float)array[1], (float)array[2]);
        }

        /// <summary>Generated item: the sprite on both sides of a 1/16 thick slab (vanilla ItemModelGenerator, without the edges).</summary>
        private static Mesh SpriteMesh()
        {
            const float front = 8.5f / 16f;
            const float back = 7.5f / 16f;
            var mesh = new Mesh { name = "Item sprite" };
            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(0, 0, -front), new Vector3(1, 0, -front), new Vector3(1, 1, -front), new Vector3(0, 1, -front),
                new Vector3(0, 0, -back), new Vector3(1, 0, -back), new Vector3(1, 1, -back), new Vector3(0, 1, -back),
            });
            mesh.SetNormals(new List<Vector3>
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            });
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Generated item with thickness (vanilla ItemModelGenerator): the sprite on both sides of a 1/16 thick slab and
        /// a side face on every edge between an opaque and a transparent pixel, colored by that pixel.
        /// </summary>
        private static Mesh ExtrudedMesh(Texture2D texture)
        {
            int width = texture.width, height = Math.Min(texture.height, texture.width); // animated strips: first frame
            Color32[] pixels;
            try
            {
                pixels = texture.GetPixels32();
            }
            catch (Exception)
            {
                return null;
            }
            var frameOffset = texture.height - height; // rows are stored bottom up; the first frame is at the top
            bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && pixels[(frameOffset + y) * width + x].a > 25;

            const float front = 8.5f / 16f;
            const float back = 7.5f / 16f;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            float vMin = (float)frameOffset / texture.height, vMax = 1;

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
            {
                var first = vertices.Count;
                foreach (var corner in new[] { a, b, c, d }) vertices.Add(new Vector3(corner.x, corner.y, -corner.z));
                var n = new Vector3(normal.x, normal.y, -normal.z);
                normals.Add(n);
                normals.Add(n);
                normals.Add(n);
                normals.Add(n);
                uvs.Add(uvA);
                uvs.Add(uvB);
                uvs.Add(uvC);
                uvs.Add(uvD);
                indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }

            // the two big faces (minecraft space, front looks to +z)
            Quad(new Vector3(0, 0, front), new Vector3(1, 0, front), new Vector3(1, 1, front), new Vector3(0, 1, front), Vector3.forward,
                new Vector2(0, vMin), new Vector2(1, vMin), new Vector2(1, vMax), new Vector2(0, vMax));
            Quad(new Vector3(1, 0, back), new Vector3(0, 0, back), new Vector3(0, 1, back), new Vector3(1, 1, back), Vector3.back,
                new Vector2(1, vMin), new Vector2(0, vMin), new Vector2(0, vMax), new Vector2(1, vMax));

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!Opaque(x, y)) continue;
                    float x0 = (float)x / width, x1 = (float)(x + 1) / width, y0 = (float)y / height, y1 = (float)(y + 1) / height;
                    var uv = new Vector2((x + 0.5f) / width, (frameOffset + y + 0.5f) / texture.height); // the pixel's color
                    if (!Opaque(x - 1, y)) Quad(new Vector3(x0, y0, back), new Vector3(x0, y0, front), new Vector3(x0, y1, front), new Vector3(x0, y1, back), Vector3.left, uv, uv, uv, uv);
                    if (!Opaque(x + 1, y)) Quad(new Vector3(x1, y0, front), new Vector3(x1, y0, back), new Vector3(x1, y1, back), new Vector3(x1, y1, front), Vector3.right, uv, uv, uv, uv);
                    if (!Opaque(x, y - 1)) Quad(new Vector3(x0, y0, back), new Vector3(x1, y0, back), new Vector3(x1, y0, front), new Vector3(x0, y0, front), Vector3.down, uv, uv, uv, uv);
                    if (!Opaque(x, y + 1)) Quad(new Vector3(x0, y1, front), new Vector3(x1, y1, front), new Vector3(x1, y1, back), new Vector3(x0, y1, back), Vector3.up, uv, uv, uv, uv);
                }
            }

            var mesh = new Mesh { name = "Item " + texture.name };
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Minecraft space matrix of the display transform followed by vanilla ItemRenderer's translate(-0.5): translate,
        /// rotate x, y, z, scale. The meshes are in render space, z negated, which <see cref="EntityRenderer.Apply"/> handles.
        /// </summary>
        /// <summary>Creates the renderers of an item model under a parent: one mesh, or the parts of entity model items.</summary>
        public static List<Renderer> Instantiate(ItemModel model, Transform parent, string name)
        {
            var result = new List<Renderer>();
            if (model.Parts == null)
            {
                var gameObject = new GameObject("Item " + name, typeof(MeshFilter), typeof(MeshRenderer));
                gameObject.transform.SetParent(parent, false);
                gameObject.GetComponent<MeshFilter>().sharedMesh = model.Mesh;
                var renderer = gameObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = model.Material;
                result.Add(renderer);
                return result;
            }
            foreach (var part in model.Parts)
            {
                var gameObject = new GameObject("Item part", typeof(MeshFilter), typeof(MeshRenderer));
                gameObject.transform.SetParent(parent, false);
                var m = part.matrix;
                gameObject.transform.localPosition = m.GetColumn(3);
                gameObject.transform.localRotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                gameObject.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
                gameObject.GetComponent<MeshFilter>().sharedMesh = part.mesh;
                var renderer = gameObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = part.material;
                result.Add(renderer);
            }
            return result;
        }

        /// <summary>textures/map/map_background.png (the paper behind a map held with both hands).</summary>
        public Texture MapBackground => icons.MapBackground;

        /// <summary>The enchantment glint material (null without the glint texture).</summary>
        /// <summary>The glint for worn armor drawn with a material (masked by its texture).</summary>
        public Material ArmorGlint(Material armor) => icons.GlintFor(armor, true);

        /// <summary>Adds the glint as an extra material: the renderer draws its mesh a second time with it.</summary>
        public void AddGlint(IEnumerable<Renderer> renderers)
        {
            foreach (var renderer in renderers)
            {
                var glint = icons.GlintFor(renderer.sharedMaterial, false);
                if (glint == null) continue;
                var materials = new List<Material>(renderer.sharedMaterials) { glint };
                renderer.sharedMaterials = materials.ToArray();
            }
        }

        /// <summary>Item key with the glint: "name*" for items drawn with the enchantment glint.</summary>
        public static string Key(ItemStack stack) => stack?.Item == null ? null : stack.Item.Name.Path + (ItemProperties.HasFoil(stack) ? "*" : "");

        public static Matrix4x4 DisplayMatrix(ItemModel model, ItemDisplay display)
        {
            var (translation, rotation, scale) = model.Get(display);
            return Matrix4x4.Translate(translation)
                   * EntityRenderer.RotX(rotation.x * Mathf.Deg2Rad) * EntityRenderer.RotY(rotation.y * Mathf.Deg2Rad) * EntityRenderer.RotZ(rotation.z * Mathf.Deg2Rad)
                   * Matrix4x4.Scale(scale) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f));
        }

        public void Dispose()
        {
            foreach (var model in cache.Values)
            {
                if (model == null) continue;
                if (model.Mesh != null) UnityEngine.Object.Destroy(model.Mesh);
                if (model.Material != blockMaterial) UnityEngine.Object.Destroy(model.Material);
            }
            cache.Clear();
        }
    }
}
