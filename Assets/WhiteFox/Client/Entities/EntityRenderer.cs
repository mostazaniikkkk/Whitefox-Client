// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Entities;
using WhiteFox.Core.Game;
using WhiteFox.Core.Models;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Entities
{
    /// <summary>
    /// Renders the entities of the session (Minosoft's EntitiesRenderer + EntityRenderer features): model entities
    /// with vanilla animations, dropped items, experience orbs, falling blocks and hitboxes for everything without a
    /// model. Minecraft space is converted to render space by negating z (conjugating every transform).
    /// </summary>
    public sealed partial class EntityRenderer : IDisposable
    {
        private static readonly Matrix4x4 Flip = Matrix4x4.Scale(new Vector3(1, 1, -1));
        private static readonly int LightId = Shader.PropertyToID("_Light");
        private static readonly int OverlayId = Shader.PropertyToID("_Overlay");

        private readonly GameSession session;
        private readonly Transform root;
        private readonly GuiAssets assets;
        private readonly ItemModels items;
        private readonly SkinManager skins;
        private readonly Material blockMaterial;
        private readonly Shader shader;
        private readonly Material lineMaterial;
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly Dictionary<int, EntityView> views = new Dictionary<int, EntityView>();
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private readonly Entity localEntity = new Entity { Id = int.MinValue };
        private EntityView localView;

        public bool ShowLocalPlayer;
        /// <summary>Map textures for filled maps in item frames.</summary>
        public MapTextures Maps;
        public bool CastShadows;
        public int Count => views.Count;

        public EntityRenderer(GameSession session, Transform parent, GuiAssets assets, ItemModels items, SkinManager skins, Material blockMaterial)
        {
            this.session = session;
            this.assets = assets;
            this.items = items;
            this.skins = skins;
            this.blockMaterial = blockMaterial;
            root = new GameObject("Entities").transform;
            root.SetParent(parent, false);
            shader = Resources.Load<Shader>("WhiteFox/Entity");
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { color = new Color(1, 1, 1, 0.6f) };
            session.Entities.Removed += OnRemoved;
            localEntity.Type = session.Resources.EntityTypes.Get("player");
        }

        private void OnRemoved(Entity entity)
        {
            if (!views.TryGetValue(entity.Id, out var view)) return;
            view.Destroy();
            views.Remove(entity.Id);
        }

        public void Update(float partialTick)
        {
            foreach (var entity in session.Entities.Entities.Values)
            {
                if (views.TryGetValue(entity.Id, out var view) && view is ModelView built && built.VariantKey != VariantKey(entity))
                {
                    // the variant arrived (or changed) after the spawn: build the model again
                    view.Destroy();
                    views.Remove(entity.Id);
                    view = null;
                }
                if (view == null)
                {
                    view = CreateView(entity);
                    if (view is ModelView modelView) modelView.VariantKey = VariantKey(entity);
                    views[entity.Id] = view;
                }
                UpdateView(view, entity, partialTick);
            }

            UpdateLeads(partialTick);

            // the own player, for the third person view
            UpdateLocalEntity(partialTick);
            if (ShowLocalPlayer)
            {
                if (localView == null) localView = CreateView(localEntity);
                localView.Root.SetActive(true);
                UpdateView(localView, localEntity, partialTick);
            }
            else if (localView != null)
            {
                localView.Root.SetActive(false);
            }
        }

        private void UpdateLocalEntity(float partialTick)
        {
            var player = session.Player;
            localEntity.Uuid = session.Server?.Uuid ?? Guid.Empty;
            localEntity.PreviousPosition = player.PreviousPosition;
            localEntity.Position = player.Position;
            localEntity.PreviousYaw = localEntity.Yaw;
            localEntity.Yaw = player.Yaw;
            localEntity.PreviousPitch = localEntity.Pitch = player.Pitch;
            localEntity.Flags = (byte)((player.Pose == Core.Physics.Pose.Sneaking ? 0x02 : 0) | (player.Sprinting ? 0x08 : 0));
            localEntity.PoseId = player.Pose == Core.Physics.Pose.Swimming ? 3 : player.Pose == Core.Physics.Pose.Sneaking ? 5 : player.Pose == Core.Physics.Pose.FallFlying ? 1 : 0;
            localEntity.SwingTime = player.SwingTime;
            localEntity.Vehicle = player.Vehicle;
            localEntity.Equipment[0] = session.Inventory.Player.Slots[36 + session.SelectedSlot];
            var slots = session.Inventory.Player.Slots;
            for (var i = 0; i < 4; i++) localEntity.Equipment[5 - i] = slots[5 + i]; // helmet, chestplate, leggings, boots
            // walk animation, normally done by Entity.Tick
            if (localEntity.Age != (int)session.Ticks)
            {
                localEntity.Age = (int)session.Ticks;
                localEntity.PreviousLimbSwingAmount = localEntity.LimbSwingAmount;
                localEntity.PreviousBodyYaw = localEntity.BodyYaw;
                var dx = player.Position.X - player.PreviousPosition.X;
                var dz = player.Position.Z - player.PreviousPosition.Z;
                var distance = Mathf.Min(1, (float)Math.Sqrt(dx * dx + dz * dz) * 4);
                localEntity.LimbSwingAmount += (distance - localEntity.LimbSwingAmount) * 0.4f;
                localEntity.LimbSwing += localEntity.LimbSwingAmount;
                var difference = MathUtil.WrapDegrees(player.Yaw - localEntity.BodyYaw);
                if (dx * dx + dz * dz > 0.0025) localEntity.BodyYaw += MathUtil.WrapDegrees((float)(Math.Atan2(dz, dx) * 180 / Math.PI) - 90 - localEntity.BodyYaw) * 0.3f;
                else if (Math.Abs(difference) > 50) localEntity.BodyYaw += difference - Math.Sign(difference) * 50;
            }
        }

        // ------------------------------------------------------------------ views

        private abstract class EntityView
        {
            public GameObject Root;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Mesh> OwnedMeshes = new List<Mesh>();
            public readonly List<Material> OwnedMaterials = new List<Material>();
            public void Destroy()
            {
                foreach (var mesh in OwnedMeshes) UnityEngine.Object.Destroy(mesh);
                foreach (var material in OwnedMaterials) UnityEngine.Object.Destroy(material);
                UnityEngine.Object.Destroy(Root);
            }
        }

        private sealed class ModelView : EntityView
        {
            public EntityModel Model;
            public readonly Dictionary<string, List<KeyValuePair<Transform, ModelPart>>> Parts = new Dictionary<string, List<KeyValuePair<Transform, ModelPart>>>();
            public Material SkinMaterial;
            public bool Slim;
            public string HeldName;
            public Transform Held;
            /// <summary>Variant the model was built for (texture variants rebuild the view when they change).</summary>
            public int VariantKey;
            public int Variant = -1;
            public readonly string[] ArmorNames = new string[6];
            public readonly List<GameObject> Armor = new List<GameObject>();
            public readonly List<Renderer> SecondLayer = new List<Renderer>();
        }

        private sealed class SimpleView : EntityView
        {
            public string Kind;
            public string ItemName;
            public ItemModel ItemModel;
            public Transform ItemTransform;
            public Texture MapTexture;
            /// <summary>Object renderers: their model parts and the fishing line.</summary>
            public readonly List<Transform> Pieces = new List<Transform>();
            public LineRenderer Line;
        }

        private static int VariantKey(Entity entity)
        {
            var key = entity.Variant;
            if (entity.VillagerData != null) key = key * 31 + entity.VillagerData[0] * 997 + entity.VillagerData[1] * 31 + entity.VillagerData[2];
            return key;
        }

        private EntityView CreateView(Entity entity)
        {
            var path = entity.Type?.Name.Path ?? "";
            var rootObject = new GameObject("Entity " + entity.Id + " " + path);
            rootObject.transform.SetParent(root, false);

            if (path == "item" || path == "experience_orb" || path == "falling_block")
            {
                var view = new SimpleView { Root = rootObject, Kind = path };
                return view;
            }
            if (path == "arrow" || path == "spectral_arrow")
            {
                var arrow = new SimpleView { Root = rootObject, Kind = "arrow" };
                var filter = rootObject.AddComponent<MeshFilter>();
                filter.sharedMesh = ArrowMesh();
                var renderer = rootObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = MaterialFor("assets/minecraft/textures/entity/projectiles/" + path + ".png");
                arrow.Renderers.Add(renderer);
                return arrow;
            }
            if (Core.Game.GameSession.ThrownItems.Contains(path)) return new SimpleView { Root = rootObject, Kind = "thrown" };
            if (ObjectKinds.Contains(path)) return CreateObject(entity, path, rootObject);
            if (path == "item_frame" || path == "glow_item_frame")
            {
                var frame = new SimpleView { Root = rootObject, Kind = path };
                var frameObject = new GameObject("Frame", typeof(MeshFilter), typeof(MeshRenderer));
                frameObject.transform.SetParent(rootObject.transform, false);
                EntityRenderer.Apply(frameObject.transform, Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f)), true);
                frameObject.GetComponent<MeshFilter>().sharedMesh = FrameMesh();
                var renderer = frameObject.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { MaterialFor("assets/minecraft/textures/block/birch_planks.png"), MaterialFor("assets/minecraft/textures/block/" + path + ".png") };
                frame.Renderers.Add(renderer);
                return frame;
            }
            if (path == "painting") return new SimpleView { Root = rootObject, Kind = path };
            if (path == "lightning_bolt")
            {
                var bolt = new SimpleView { Root = rootObject, Kind = path };
                var filter = rootObject.AddComponent<MeshFilter>();
                filter.sharedMesh = LightningMesh(entity.Id);
                bolt.OwnedMeshes.Add(filter.sharedMesh);
                var renderer = rootObject.AddComponent<MeshRenderer>();
                var material = new Material(shader) { name = "Lightning" };
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Tint", new Color(0.75f, 0.75f, 0.9f));
                material.SetFloat("_Fullbright", 1);
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                bolt.Renderers.Add(renderer);
                return bolt;
            }

            var slim = false;
            if (entity.IsPlayer) slim = skins.IsSlim(entity.Uuid);
            var model = EntityModels.Create(path, slim, entity.Variant, entity.VillagerData);
            if (model == null)
            {
                var box = new SimpleView { Root = rootObject, Kind = "box" };
                var filter = rootObject.AddComponent<MeshFilter>();
                filter.sharedMesh = BoxMesh(entity.Width, entity.Height);
                box.OwnedMeshes.Add(filter.sharedMesh);
                var renderer = rootObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = lineMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                box.Renderers.Add(renderer);
                return box;
            }

            var modelView = new ModelView { Root = rootObject, Model = model, Slim = slim };
            for (var i = 0; i < model.Layers.Count; i++)
            {
                var layer = model.Layers[i];
                Material material;
                if (entity.IsPlayer && i == 0)
                {
                    material = new Material(shader) { name = "Skin " + entity.Uuid };
                    modelView.SkinMaterial = material;
                    material.SetTexture("_MainTex", skins.Get(entity.Uuid));
                }
                else
                {
                    material = MaterialFor(layer.Texture);
                }
                if (layer.Tint != Color.white)
                {
                    var key = layer.Texture + "#" + ColorUtility.ToHtmlStringRGB(layer.Tint);
                    if (!materials.TryGetValue(key, out var tinted))
                    {
                        tinted = new Material(material) { name = key };
                        tinted.SetColor("_Tint", layer.Tint);
                        materials[key] = tinted;
                    }
                    material = tinted;
                }
                var layerRoot = new GameObject("Layer " + i).transform;
                layerRoot.SetParent(rootObject.transform, false);
                var first = modelView.Renderers.Count;
                foreach (var part in layer.Parts) BuildPart(modelView, layer, part, layerRoot, material);
                if (i == 1) modelView.SecondLayer.AddRange(modelView.Renderers.GetRange(first, modelView.Renderers.Count - first));
            }
            return modelView;
        }

        private void BuildPart(ModelView view, ModelLayer layer, ModelPart part, Transform parent, Material material)
        {
            var gameObject = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(parent, false);
            var mesh = PartMesh(part, layer);
            view.OwnedMeshes.Add(mesh);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            view.Renderers.Add(renderer);
            if (!view.Parts.TryGetValue(part.Name, out var list)) view.Parts[part.Name] = list = new List<KeyValuePair<Transform, ModelPart>>();
            list.Add(new KeyValuePair<Transform, ModelPart>(gameObject.transform, part));
            foreach (var child in part.Children) BuildPart(view, layer, child, gameObject.transform, material);
        }

        private Material MaterialFor(string texture)
        {
            if (materials.TryGetValue(texture, out var material)) return material;
            material = new Material(shader) { name = texture };
            var sprite = assets.Get(texture);
            material.SetTexture("_MainTex", sprite != null ? sprite.Texture : Texture2D.whiteTexture);
            materials[texture] = material;
            return material;
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>Box UV mesh of a part (vanilla ModelPart.Cube), vertices in part local render space.</summary>
        internal static Mesh PartMesh(ModelPart part, ModelLayer layer)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            foreach (var cube in part.Cubes)
            {
                var from = cube.From - Vector3.one * cube.Inflate;
                var to = cube.From + cube.Size + Vector3.one * cube.Inflate;
                float dx = cube.Size.x, dy = cube.Size.y, dz = cube.Size.z;
                float u = cube.U, v = cube.V;

                // (region u, v, width, height), outward normal, corners for uv (0,0) (w,0) (w,h) (0,h)
                var rightRegion = new Vector4(u, v + dz, dz, dy);
                var leftRegion = new Vector4(u + dz + dx, v + dz, dz, dy);
                if (cube.Mirror)
                {
                    var swap = rightRegion;
                    rightRegion = leftRegion;
                    leftRegion = swap;
                }
                Face(new Vector4(u + dz, v, dx, dz), new Vector3(0, -1, 0), new Vector3(from.x, from.y, to.z), new Vector3(to.x, from.y, to.z), new Vector3(to.x, from.y, from.z), new Vector3(from.x, from.y, from.z));
                Face(new Vector4(u + dz + dx, v, dx, dz), new Vector3(0, 1, 0), new Vector3(from.x, to.y, from.z), new Vector3(to.x, to.y, from.z), new Vector3(to.x, to.y, to.z), new Vector3(from.x, to.y, to.z));
                Face(new Vector4(u + dz, v + dz, dx, dy), new Vector3(0, 0, -1), new Vector3(from.x, from.y, from.z), new Vector3(to.x, from.y, from.z), new Vector3(to.x, to.y, from.z), new Vector3(from.x, to.y, from.z));
                Face(new Vector4(u + dz + dx + dz, v + dz, dx, dy), new Vector3(0, 0, 1), new Vector3(to.x, from.y, to.z), new Vector3(from.x, from.y, to.z), new Vector3(from.x, to.y, to.z), new Vector3(to.x, to.y, to.z));
                Face(rightRegion, new Vector3(-1, 0, 0), new Vector3(from.x, from.y, to.z), new Vector3(from.x, from.y, from.z), new Vector3(from.x, to.y, from.z), new Vector3(from.x, to.y, to.z));
                Face(leftRegion, new Vector3(1, 0, 0), new Vector3(to.x, from.y, from.z), new Vector3(to.x, from.y, to.z), new Vector3(to.x, to.y, to.z), new Vector3(to.x, to.y, from.z));

                void Face(Vector4 region, Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
                {
                    if (region.z <= 0 || region.w <= 0) return;
                    var first = vertices.Count;
                    var corners = new[] { a, b, c, d };
                    var cornerUV = new[] { new Vector2(0, 0), new Vector2(region.z, 0), new Vector2(region.z, region.w), new Vector2(0, region.w) };
                    for (var i = 0; i < 4; i++)
                    {
                        var p = corners[i] / 16.0f;
                        vertices.Add(new Vector3(p.x, p.y, -p.z));
                        normals.Add(new Vector3(normal.x, normal.y, -normal.z));
                        var local = cornerUV[i];
                        if (cube.Mirror) local.x = region.z - local.x;
                        uvs.Add(new Vector2((region.x + local.x) / layer.TextureWidth, 1 - (region.y + local.y) / layer.TextureHeight));
                    }
                    indices.Add(first);
                    indices.Add(first + 1);
                    indices.Add(first + 2);
                    indices.Add(first);
                    indices.Add(first + 2);
                    indices.Add(first + 3);
                }
            }
            var mesh = new Mesh { name = part.Name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BoxMesh(float width, float height)
        {
            var vertices = new List<Vector3>();
            var half = width / 2;
            for (var i = 0; i < 8; i++)
            {
                vertices.Add(new Vector3((i & 1) == 0 ? -half : half, (i & 2) == 0 ? 0 : height, (i & 4) == 0 ? -half : half));
            }
            var mesh = new Mesh();
            mesh.SetVertices(vertices);
            mesh.SetIndices(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 }, MeshTopology.Lines, 0);
            return mesh;
        }

        private static Mesh QuadMesh(Rect uv, Vector2 size, float y)
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(-size.x / 2, y, 0), new Vector3(size.x / 2, y, 0), new Vector3(size.x / 2, y + size.y, 0), new Vector3(-size.x / 2, y + size.y, 0) });
            mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMin, uv.yMax) });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ per frame

        // ------------------------------------------------------------------ inventory doll

        private ModelView dollView;
        private bool dollSlim;
        private readonly Entity dollEntity = new Entity { Id = int.MinValue + 1 };
        private RenderTexture dollTexture;
        private readonly MaterialPropertyBlock dollProperties = new MaterialPropertyBlock();

        /// <summary>
        /// The own player in the inventory (vanilla InventoryScreen.renderEntityInInventoryFollowsMouse): turned toward
        /// the mouse, drawn into a texture of the given size. mouse is relative to the doll center (gui pixels).
        /// </summary>
        public Texture RenderDoll(int width, int height, Vector2 mouse)
        {
            if (width <= 0 || height <= 0) return null;
            var slim = skins.IsSlim(localEntity.Uuid);
            if (dollView == null || dollSlim != slim)
            {
                dollView?.Destroy();
                dollSlim = slim;
                dollEntity.Type = localEntity.Type;
                dollEntity.Uuid = localEntity.Uuid;
                dollView = (ModelView)CreateView(dollEntity);
            }
            if (dollTexture == null || dollTexture.width != width || dollTexture.height != height)
            {
                if (dollTexture != null) dollTexture.Release();
                dollTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Point, name = "Inventory player" };
                dollTexture.Create();
            }

            // pose: body follows the mouse a little, the head more (atan(mouse / 40) * 20 degrees)
            var yaw = Mathf.Atan(mouse.x / 40f) * 20;
            var pitch = Mathf.Atan(mouse.y / 40f) * 20;
            dollEntity.Uuid = localEntity.Uuid;
            for (var i = 0; i < dollEntity.Equipment.Length; i++) dollEntity.Equipment[i] = localEntity.Equipment[i];
            dollEntity.PreviousBodyYaw = dollEntity.BodyYaw = -yaw;
            dollEntity.PreviousYaw = dollEntity.Yaw = -yaw * 2;
            dollEntity.PreviousPitch = dollEntity.Pitch = pitch;
            dollEntity.Flags = localEntity.Flags;
            dollEntity.SwingTime = -1;
            var origin = new Vec3d(RenderOrigin.X, 0, RenderOrigin.Z);
            dollEntity.Position = dollEntity.PreviousPosition = origin;
            UpdateModel(dollView, dollEntity, origin, 0);
            foreach (var renderer in dollView.Renderers) renderer.enabled = false;

            // orthographic view of the 2 blocks around the model, looking at its front
            var aspect = (float)width / height;
            var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(new Vector3(0, 0.95f, -5), Quaternion.identity, Vector3.one).inverse;
            var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1.05f * aspect, 1.05f * aspect, -1.05f, 1.05f, 0.1f, 20f), false);
            if (SystemInfo.usesReversedZBuffer) projection.SetRow(2, projection.GetRow(3) - projection.GetRow(2));
            var modern = Shader.GetGlobalFloat("_WF_ModernEntities");
            var day = Shader.GetGlobalFloat("_WF_DayLight");
            var commands = new CommandBuffer { name = "WhiteFox inventory player" };
            commands.SetRenderTarget(dollTexture);
            commands.ClearRenderTarget(true, true, Color.clear);
            commands.SetViewProjectionMatrices(view, projection);
            commands.SetGlobalFloat("_WF_ModernEntities", 0);
            commands.SetGlobalFloat("_WF_DayLight", 1);
            commands.SetGlobalFloat("_WF_DynamicLightCount", 0);
            dollProperties.Clear();
            dollProperties.SetVector(LightId, new Vector4(1, 1, 0, 0));
            var rootInverse = dollView.Root.transform.parent.worldToLocalMatrix;
            foreach (var renderer in dollView.Renderers)
            {
                if (!renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                commands.DrawMesh(filter.sharedMesh, rootInverse * renderer.transform.localToWorldMatrix, renderer.sharedMaterial, 0, 0, dollProperties);
            }
            commands.SetGlobalFloat("_WF_ModernEntities", modern);
            commands.SetGlobalFloat("_WF_DayLight", day);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Release();
            return dollTexture;
        }

        private void UpdateView(EntityView view, Entity entity, float partial)
        {
            var visible = !entity.Invisible && (entity != localEntity || ShowLocalPlayer) && !(entity.Id == session.Server?.EntityId);
            var position = entity.Interpolated(partial);
            var light = session.World.GetLight((int)Math.Floor(position.X), (int)Math.Floor(position.Y + entity.Height * 0.5), (int)Math.Floor(position.Z));
            properties.Clear();
            properties.SetVector(LightId, new Vector4((light & 0x0F) / 15f, (light >> 4) / 15f, 0, 0));
            var hurt = entity.HurtTime > 0 || entity.DeathTime > 0;
            properties.SetColor(OverlayId, hurt ? new Color(1, 0, 0, 0.3f) : Color.clear);

            switch (view)
            {
                case ModelView model:
                    UpdateModel(model, entity, position, partial);
                    break;
                case SimpleView simple:
                    UpdateSimple(simple, entity, position, partial);
                    break;
            }
            if (view.Root.activeSelf != visible) view.Root.SetActive(visible);
            foreach (var renderer in view.Renderers)
            {
                renderer.SetPropertyBlock(properties);
                renderer.shadowCastingMode = CastShadows && !(view is SimpleView s && s.Kind == "box") ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
            if (view is SimpleView framed && framed.MapTexture != null && framed.ItemTransform != null)
            {
                properties.SetTexture("_MainTex", framed.MapTexture);
                foreach (var renderer in framed.ItemTransform.GetComponentsInChildren<Renderer>()) renderer.SetPropertyBlock(properties);
            }
            if (view is ModelView sheep && entity.Type?.Name.Path == "sheep")
            {
                // wool color (vanilla DyeColor texture diffuse colors)
                properties.SetColor("_Tint", DyeColors[entity.SheepWool & 0x0F]);
                foreach (var renderer in sheep.SecondLayer) renderer.SetPropertyBlock(properties);
            }
        }

        /// <summary>Root transform: position, body yaw, vanilla scale(-1,-1,1) and translate(0,-1.501,0), death flip.</summary>
        private void UpdateModel(ModelView view, Entity entity, Vec3d position, float partial)
        {
            if (entity.IsPlayer && view.SkinMaterial != null)
            {
                var skin = skins.Get(entity.Uuid);
                if (view.SkinMaterial.mainTexture != skin) view.SkinMaterial.SetTexture("_MainTex", skin);
            }

            var bodyYaw = MathUtil.LerpYaw(entity.PreviousBodyYaw, entity.BodyYaw, partial);
            var origin = Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z));
            if (view.Model.Kind == ModelKind.Boat || view.Model.Kind == ModelKind.Minecart)
            {
                // vanilla BoatRenderer / MinecartRenderer
                var yaw = MathUtil.LerpYaw(entity.PreviousYaw, entity.Yaw, partial);
                var vehicle = origin * Matrix4x4.Translate(new Vector3(0, 0.375f, 0)) * RotY((180 - yaw) * Mathf.Deg2Rad);
                if (view.Model.Kind == ModelKind.Boat)
                {
                    vehicle *= Matrix4x4.Scale(new Vector3(-1, -1, 1)) * RotY(Mathf.PI / 2);
                    if (entity.Variant != view.Variant)
                    {
                        view.Variant = entity.Variant;
                        var material = MaterialFor("assets/minecraft/textures/entity/boat/" + BoatTypes[Mathf.Clamp(entity.Variant, 0, BoatTypes.Length - 1)] + ".png");
                        foreach (var renderer in view.Renderers) renderer.sharedMaterial = material;
                    }
                }
                else
                {
                    vehicle *= RotZ(-entity.Pitch * Mathf.Deg2Rad) * Matrix4x4.Scale(new Vector3(-1, -1, 1));
                }
                Apply(view.Root.transform, vehicle);
                Animate(view, entity, partial, yaw);
                return;
            }
            var matrix = origin * RotY((180 - bodyYaw) * Mathf.Deg2Rad);
            if (entity.DeathTime > 0)
            {
                var flip = Mathf.Min(1, Mathf.Sqrt((entity.DeathTime + partial - 1) / 20f * 1.6f));
                matrix *= RotZ(flip * 90 * Mathf.Deg2Rad);
            }
            if (entity.Swimming || entity.PoseId == 1) matrix *= RotX(-(90 + entity.Pitch) * Mathf.Deg2Rad) * Matrix4x4.Translate(new Vector3(0, -1, 0.3f));
            var scale = view.Model.Scale * (entity.Baby ? 0.5f : 1) * (view.Model.Kind == ModelKind.Slime ? entity.SlimeSize : 1)
                        * (view.Model.Kind == ModelKind.ArmorStand && (entity.StandFlags & 0x01) != 0 ? 0.5f : 1)
                        * (view.Model.Kind == ModelKind.Phantom ? 1 + 0.15f * entity.PhantomSize : 1);
            matrix *= Matrix4x4.Scale(new Vector3(-1, -1, 1)) * Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(view.Model.Offset) * Matrix4x4.Translate(new Vector3(0, -1.501f, 0));
            Apply(view.Root.transform, matrix);

            // sheep: sheared hides the fur; pig: the saddle layer
            if (view.Root.transform.childCount > 1)
            {
                var path = entity.Type?.Name.Path;
                var second = path == "sheep" ? (entity.SheepWool & 0x10) == 0 : path == "pig" || path == "strider" ? entity.Saddled : true;
                var layer = view.Root.transform.GetChild(1).gameObject;
                if (layer.activeSelf != second) layer.SetActive(second);
            }

            Animate(view, entity, partial, bodyYaw);

            UpdateArmor(view, entity);

            // held item (vanilla ItemInHandLayer): attached to the right arm
            if (view.Model.Kind == ModelKind.Biped || view.Model.Kind == ModelKind.Zombie || view.Model.Kind == ModelKind.ArmorStand)
            {
                var name = ItemModels.Key(entity.Equipment[0]);
                if (name != view.HeldName && view.Parts.TryGetValue("right_arm", out var arms))
                {
                    view.HeldName = name;
                    SetItem(arms[0].Key, ref view.Held, view.Renderers, name);
                }
                var model = items.Get(name?.TrimEnd('*'));
                if (view.Held != null && model != null)
                {
                    var hand = RotX(-90 * Mathf.Deg2Rad) * RotY(Mathf.PI) * Matrix4x4.Translate(new Vector3(1 / 16f, 0.125f, -0.625f));
                    Apply(view.Held, hand * ItemModels.DisplayMatrix(model, ItemDisplay.ThirdPersonRightHand), true);
                }
            }
        }

        private static readonly string[] BoatTypes = { "oak", "spruce", "birch", "jungle", "acacia", "cherry", "dark_oak", "mangrove", "bamboo" };

        private static readonly Color[] DyeColors =
        {
            Rgb(0xF9FFFE), Rgb(0xF9801D), Rgb(0xC74EBD), Rgb(0x3AB3DA), Rgb(0xFED83D), Rgb(0x80C71F), Rgb(0xF38BAA), Rgb(0x474F52),
            Rgb(0x9D9D97), Rgb(0x169C9C), Rgb(0x8932B8), Rgb(0x3C44AA), Rgb(0x835432), Rgb(0x5E7C16), Rgb(0xB02E26), Rgb(0x1D1D21),
        };

        private static Color Rgb(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

        private static readonly Vec3d?[] DefaultStandPose =
        {
            new Vec3d(0, 0, 0), new Vec3d(0, 0, 0), new Vec3d(-10, 0, -10), new Vec3d(-15, 0, 10), new Vec3d(-1, 0, -1), new Vec3d(1, 0, 1),
        };

        private static void SetPartVisible(ModelView view, string name, bool visible)
        {
            if (!view.Parts.TryGetValue(name, out var list)) return;
            foreach (var entry in list)
            {
                if (entry.Key.gameObject.activeSelf != visible) entry.Key.gameObject.SetActive(visible);
            }
        }

        private static readonly Dictionary<string, Vector3> PoseRotation = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, Vector3> PosePivot = new Dictionary<string, Vector3>();

        /// <summary>Vanilla setupAnim of the model kinds: head look, walking limbs, arm swing, sneaking, riding.</summary>
        private void Animate(ModelView view, Entity entity, float partial, float bodyYaw)
        {
            PoseRotation.Clear();
            PosePivot.Clear();
            var limbSwing = entity.LimbSwing - entity.LimbSwingAmount * (1 - partial);
            var amount = Mathf.Min(1, Mathf.Lerp(entity.PreviousLimbSwingAmount, entity.LimbSwingAmount, partial));
            var age = entity.Age + partial;
            var headYaw = entity.IsPlayer || entity == localEntity ? MathUtil.LerpYaw(entity.PreviousYaw, entity.Yaw, partial) : MathUtil.LerpYaw(entity.PreviousHeadYaw, entity.HeadYaw, partial);
            var relativeHead = MathUtil.WrapDegrees(headYaw - bodyYaw) * Mathf.Deg2Rad;
            var pitch = Mathf.Lerp(entity.PreviousPitch, entity.Pitch, partial) * Mathf.Deg2Rad;
            var swing = entity.SwingTime >= 0 ? (entity.SwingTime + partial) / 6f : 0;

            void Rotate(string part, float x, float y, float z) => PoseRotation[part] = new Vector3(x, y, z);
            float Cos(float value) => Mathf.Cos(value);
            const float walk = 0.6662f;

            switch (view.Model.Kind)
            {
                case ModelKind.Biped:
                case ModelKind.Zombie:
                case ModelKind.Villager:
                {
                    Rotate("head", pitch, relativeHead, 0);
                    var rightArm = Cos(limbSwing * walk + Mathf.PI) * 2 * amount * 0.5f;
                    var leftArm = Cos(limbSwing * walk) * 2 * amount * 0.5f;
                    var rightArmZ = Cos(age * 0.09f) * 0.05f + 0.05f;
                    var leftArmZ = -(Cos(age * 0.09f) * 0.05f + 0.05f);
                    rightArm += Mathf.Sin(age * 0.067f) * 0.05f;
                    leftArm -= Mathf.Sin(age * 0.067f) * 0.05f;
                    if (view.Model.Kind == ModelKind.Zombie)
                    {
                        rightArm = leftArm = -Mathf.PI / 2.25f;
                    }
                    if (entity.Equipment[0] != null && view.Model.Kind == ModelKind.Biped) rightArm = rightArm * 0.5f - Mathf.PI / 10;
                    var bodyY = 0f;
                    if (swing > 0)
                    {
                        var progress = 1 - swing;
                        progress = 1 - progress * progress * progress * progress;
                        bodyY = Mathf.Sin(Mathf.Sqrt(swing) * Mathf.PI * 2) * 0.2f;
                        rightArm -= Mathf.Sin(progress * Mathf.PI) * 1.2f + Mathf.Sin(swing * Mathf.PI) * -(pitch - 0.7f) * 0.75f;
                    }
                    var rightLeg = Cos(limbSwing * walk) * 1.4f * amount;
                    var leftLeg = Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount;
                    var legY = 0f;
                    if (entity.Vehicle >= 0)
                    {
                        rightArm -= Mathf.PI / 5;
                        leftArm -= Mathf.PI / 5;
                        rightLeg = leftLeg = -1.4137167f;
                        legY = Mathf.PI / 10;
                    }
                    Rotate("body", 0, bodyY, 0);
                    Rotate("right_arm", rightArm, 0, rightArmZ);
                    Rotate("left_arm", leftArm, 0, leftArmZ);
                    Rotate("right_leg", rightLeg, legY, 0);
                    Rotate("left_leg", leftLeg, -legY, 0);
                    if (entity.Sneaking && view.Model.Kind == ModelKind.Biped)
                    {
                        Rotate("body", 0.5f, bodyY, 0);
                        Rotate("right_arm", rightArm + 0.4f, 0, rightArmZ);
                        Rotate("left_arm", leftArm + 0.4f, 0, leftArmZ);
                        PosePivot["right_leg"] = new Vector3(-1.9f, 12.2f, 4);
                        PosePivot["left_leg"] = new Vector3(1.9f, 12.2f, 4);
                        PosePivot["head"] = new Vector3(0, 4.2f, 0);
                        PosePivot["body"] = new Vector3(0, 3.2f, 0);
                        var armY = view.Slim ? 5.7f : 5.2f;
                        PosePivot["right_arm"] = new Vector3(-5, armY, 0);
                        PosePivot["left_arm"] = new Vector3(5, armY, 0);
                    }
                    break;
                }
                case ModelKind.Quadruped:
                    Rotate("head", pitch, relativeHead, 0);
                    Rotate("right_hind_leg", Cos(limbSwing * walk) * 1.4f * amount, 0, 0);
                    Rotate("left_hind_leg", Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount, 0, 0);
                    Rotate("right_front_leg", Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount, 0, 0);
                    Rotate("left_front_leg", Cos(limbSwing * walk) * 1.4f * amount, 0, 0);
                    break;
                case ModelKind.Chicken:
                    Rotate("head", pitch, relativeHead, 0);
                    Rotate("right_leg", Cos(limbSwing * walk) * 1.4f * amount, 0, 0);
                    Rotate("left_leg", Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount, 0, 0);
                    var flap = entity.OnGround ? 0 : (Mathf.Sin(age * 1.2f) + 1) * 0.6f;
                    Rotate("right_wing", 0, 0, flap);
                    Rotate("left_wing", 0, 0, -flap);
                    break;
                case ModelKind.Spider:
                    Rotate("head", pitch, relativeHead, 0);
                    break;
                case ModelKind.Golem:
                {
                    // IronGolemModel: triangle wave walk, arms opposite to the legs
                    float Triangle(float value, float period) => (Mathf.Abs(value % period - period * 0.5f) - period * 0.25f) / (period * 0.25f);
                    var wave = Triangle(limbSwing, 13);
                    Rotate("head", pitch, relativeHead, 0);
                    Rotate("right_leg", -1.5f * wave * amount, 0, 0);
                    Rotate("left_leg", 1.5f * wave * amount, 0, 0);
                    if (entity.Type?.Name.Path == "iron_golem" || entity.Type?.Name.Path == "warden")
                    {
                        Rotate("right_arm", (-0.2f + 1.5f * wave) * amount, 0, 0);
                        Rotate("left_arm", (-0.2f - 1.5f * wave) * amount, 0, 0);
                    }
                    break;
                }
                case ModelKind.Horse:
                    Rotate("head", pitch, relativeHead, 0);
                    Rotate("right_hind_leg", Cos(limbSwing * walk) * 1.4f * amount, 0, 0);
                    Rotate("left_hind_leg", Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount, 0, 0);
                    Rotate("right_front_leg", Cos(limbSwing * walk + Mathf.PI) * 1.4f * amount, 0, 0);
                    Rotate("left_front_leg", Cos(limbSwing * walk) * 1.4f * amount, 0, 0);
                    SetPartVisible(view, "saddle", entity.Saddled);
                    break;
                case ModelKind.Bee:
                {
                    var wing = Cos(age * 2.1f) * Mathf.PI * 0.15f;
                    Rotate("right_wing", 0, 0, wing);
                    Rotate("left_wing", 0, 0, -wing);
                    Rotate("bone", 0.1f * Mathf.Sin(age * 0.18f), 0, 0);
                    break;
                }
                case ModelKind.Fish:
                    Rotate("tail_fin", 0, -0.45f * Mathf.Sin(0.6f * age), 0);
                    break;
                case ModelKind.Allay:
                {
                    // AllayModel / VexModel: the wings beat, the head looks
                    Rotate("head", pitch, relativeHead, 0);
                    var beat = Cos(age * 20 * Mathf.Deg2Rad + limbSwing) * Mathf.PI * 0.15f;
                    Rotate("right_wing", 0.43633232f, -Mathf.PI / 4 + beat, 0);
                    Rotate("left_wing", 0.43633232f, Mathf.PI / 4 - beat, 0);
                    Rotate("right_arm", 0, 0, -Mathf.PI / 8);
                    Rotate("left_arm", 0, 0, Mathf.PI / 8);
                    break;
                }
                case ModelKind.Segments:
                {
                    // EndermiteModel / SilverfishModel: the segments wiggle sideways
                    var silverfish = entity.Type?.Name.Path == "silverfish";
                    var count = silverfish ? 7 : 4;
                    var turn = silverfish ? 0.05f : 0.01f;
                    var sway = silverfish ? 0.2f : 0.1f;
                    for (var i = 0; i < count; i++)
                    {
                        var phase = age * 0.9f + i * 0.15f * Mathf.PI;
                        Rotate("segment" + i, 0, Cos(phase) * Mathf.PI * turn * (1 + Mathf.Abs(i - 2)), 0);
                        if (view.Parts.TryGetValue("segment" + i, out var segment))
                        {
                            var pivot = segment[0].Value.Pivot;
                            PosePivot["segment" + i] = new Vector3(Mathf.Sin(phase) * Mathf.PI * sway * Mathf.Abs(i - 2), pivot.y, pivot.z);
                        }
                    }
                    if (silverfish)
                    {
                        // the fur follows segments 2, 4 and 1
                        foreach (var (fur, segment) in new[] { ("layer0", 2), ("layer1", 4), ("layer2", 1) })
                        {
                            if (!view.Parts.TryGetValue(fur, out var parts) || !PosePivot.TryGetValue("segment" + segment, out var moved)) continue;
                            PoseRotation[fur] = PoseRotation["segment" + segment];
                            PosePivot[fur] = new Vector3(moved.x, parts[0].Value.Pivot.y, parts[0].Value.Pivot.z);
                        }
                    }
                    break;
                }
                case ModelKind.Phantom:
                {
                    // PhantomModel: wings flap, the tail follows (the rest pose has the wings at z +-0.1)
                    var beatPhase = (entity.Id * 3 + age) * 7.448451f * Mathf.Deg2Rad;
                    var wing = Cos(beatPhase) * 16 * Mathf.Deg2Rad;
                    Rotate("left_wing_base", 0, 0, wing - 0.1f);
                    Rotate("left_wing_tip", 0, 0, wing - 0.1f);
                    Rotate("right_wing_base", 0, 0, -wing + 0.1f);
                    Rotate("right_wing_tip", 0, 0, -wing + 0.1f);
                    var tail = -(5 + Cos(beatPhase * 2) * 5) * Mathf.Deg2Rad;
                    Rotate("tail_base", tail, 0, 0);
                    Rotate("tail_tip", tail, 0, 0);
                    break;
                }
                case ModelKind.Hoglin:
                {
                    if (entity.Type?.Name.Path == "ravager")
                    {
                        // RavagerModel: the head looks, the legs swing
                        Rotate("head", pitch, relativeHead, 0);
                        var step = 0.4f * amount;
                        Rotate("right_hind_leg", Cos(limbSwing * walk) * step, 0, 0);
                        Rotate("left_hind_leg", Cos(limbSwing * walk + Mathf.PI) * step, 0, 0);
                        Rotate("right_front_leg", Cos(limbSwing * walk + Mathf.PI) * step, 0, 0);
                        Rotate("left_front_leg", Cos(limbSwing * walk) * step, 0, 0);
                    }
                    else
                    {
                        // HoglinModel: the head turns (its pitch is fixed), the ears wobble, the legs swing
                        Rotate("head", 0, relativeHead, 0);
                        Rotate("right_ear", 0, 0, -0.1f * Cos(limbSwing * 0.6f) * amount);
                        Rotate("left_ear", 0, 0, 0.1f * Cos(limbSwing * 0.6f) * amount);
                        var front = Cos(limbSwing) * 1.2f * amount;
                        var back = Cos(limbSwing + Mathf.PI) * 1.2f * amount;
                        Rotate("right_front_leg", front, 0, 0);
                        Rotate("left_front_leg", back, 0, 0);
                        Rotate("right_hind_leg", back, 0, 0);
                        Rotate("left_hind_leg", front, 0, 0);
                    }
                    break;
                }
                case ModelKind.Strider:
                {
                    // StriderModel: the body is the head, long legs step, the bristles wave
                    Rotate("body", pitch, relativeHead, 0);
                    var stride = Mathf.Min(amount, 0.25f);
                    Rotate("right_leg", Mathf.Sin(limbSwing * 0.5f) * 2 * stride, 0, 0.17453292f * Cos(limbSwing * 0.5f) * stride);
                    Rotate("left_leg", Mathf.Sin(limbSwing * 0.5f + Mathf.PI) * 2 * stride, 0, 0.17453292f * Cos(limbSwing * 0.5f + Mathf.PI) * stride);
                    var wave = Mathf.Sin(age * 0.4f) * 0.1f + amount * 0.2f;
                    foreach (var side in new[] { "top", "middle", "bottom" })
                    {
                        Rotate("right_" + side + "_bristle", 0, 0, -wave);
                        Rotate("left_" + side + "_bristle", 0, 0, wave);
                    }
                    break;
                }
                case ModelKind.Shulker:
                {
                    // ShulkerModel: the lid rises (and turns a little) with the peek amount, the head looks out
                    entity.PeekClient = Mathf.MoveTowards(entity.PeekClient, entity.Peek / 100f, Time.deltaTime);
                    var f1 = (0.5f + entity.PeekClient) * Mathf.PI;
                    var f2 = -1 + Mathf.Sin(f1);
                    var shake = f1 > Mathf.PI ? Mathf.Sin(age * 0.1f) * 0.7f : 0;
                    PosePivot["lid"] = new Vector3(0, 16 + Mathf.Sin(f1) * 8 + shake, 0);
                    Rotate("lid", 0, entity.PeekClient > 0.3f ? f2 * f2 * f2 * f2 * Mathf.PI * 0.125f : 0, 0);
                    Rotate("head", pitch, relativeHead, 0);
                    break;
                }
                case ModelKind.Breeze:
                    // BreezeModel: the rods circle the body, the head looks
                    Rotate("rods", 0, age * Mathf.PI * -0.1f, 0);
                    Rotate("head", pitch, relativeHead, 0);
                    break;
                case ModelKind.Guardian:
                {
                    // GuardianModel.setupAnim: spikes in while swimming (Guardian.aiStep spike animation), tail wave
                    entity.Spikes = Mathf.MoveTowards(entity.Spikes, entity.Moving ? 0 : 1, Time.deltaTime * (entity.Moving ? 5 : 1.2f));
                    var extension = (1 - entity.Spikes) * 0.55f;
                    for (var i = 0; i < 12; i++)
                    {
                        var offset = 1 + Cos(age * 1.5f + i) * 0.01f - extension;
                        PosePivot["spike" + i] = new Vector3(EntityModels.SpikeX[i] * offset, 16 + EntityModels.SpikeY[i] * offset, EntityModels.SpikeZ[i] * offset);
                    }
                    Rotate("head", pitch, relativeHead, 0);
                    var tail = age * (entity.Moving ? 0.3f : 0.1f);
                    Rotate("tail0", 0, Mathf.Sin(tail) * Mathf.PI * 0.05f, 0);
                    Rotate("tail1", 0, Mathf.Sin(tail) * Mathf.PI * 0.1f, 0);
                    Rotate("tail_fin", 0, Mathf.Sin(tail) * Mathf.PI * 0.15f, 0);
                    break;
                }
                case ModelKind.Wither:
                {
                    // WitherBossModel.setupAnim: the ribcage and tail breathe, the heads look around
                    var breath = Cos(age * 0.1f);
                    var ribcage = (0.065f + 0.05f * breath) * Mathf.PI;
                    Rotate("ribcage", ribcage - 0.2f, 0, 0);
                    PosePivot["tail"] = new Vector3(-2, 6.9f + Cos(ribcage) * 10, -0.5f + Mathf.Sin(ribcage) * 10);
                    Rotate("tail", (0.265f + 0.1f * breath) * Mathf.PI - 0.83f, 0, 0);
                    Rotate("head", pitch, relativeHead, 0);
                    Rotate("right_head", pitch * 0.5f + Mathf.Sin(age * 0.05f) * 0.2f, relativeHead * 0.5f + Mathf.Sin(age * 0.03f) * 0.4f, 0);
                    Rotate("left_head", pitch * 0.5f + Mathf.Sin(age * 0.06f + 1) * 0.2f, relativeHead * 0.5f + Mathf.Sin(age * 0.04f + 2) * 0.4f, 0);
                    break;
                }
                case ModelKind.Dragon:
                {
                    // DragonModel.prepareMobModel / renderToBuffer: flapping wings and tips, jaw, legs, swaying neck and tail
                    var f9 = age * 0.07f * Mathf.PI * 2;
                    Rotate("jaw", (Mathf.Sin(f9) + 1) * 0.2f, 0, 0);
                    var wingX = 0.125f - Cos(f9) * 0.2f;
                    var wingZ = -(Mathf.Sin(f9) + 0.125f) * 0.8f;
                    var tipZ = (Mathf.Sin(f9 + 2) + 0.5f) * 0.75f;
                    // the model rest pose has the wings at z -0.3 / 0.3 and the tips at 0.6 / -0.6
                    Rotate("left_wing", wingX, -0.25f, wingZ + 0.3f);
                    Rotate("left_wing_tip", 0, 0, tipZ - 0.6f);
                    Rotate("right_wing", wingX, 0.25f, -wingZ - 0.3f);
                    Rotate("right_wing_tip", 0, 0, -tipZ + 0.6f);
                    var legs = Mathf.Sin(f9) * 0.1f;
                    foreach (var leg in new[] { "left_front_leg", "right_front_leg", "left_hind_leg", "right_hind_leg" })
                    {
                        Rotate(leg, legs, 0, 0);
                        Rotate(leg + "_tip", legs, 0, 0);
                    }
                    for (var i = 0; i < 5; i++) Rotate("neck" + i, Mathf.Sin(f9 * 0.5f + i * 0.4f) * 0.04f, Mathf.Sin(age * 0.02f + i * 0.3f) * 0.05f, 0);
                    Rotate("head", Mathf.Sin(f9 * 0.5f + 2) * 0.05f, Mathf.Sin(age * 0.02f + 1.5f) * 0.1f, 0);
                    for (var i = 0; i < 12; i++) Rotate("tail" + i, Mathf.Sin(f9 * 0.5f + i * 0.45f) * 0.05f, Mathf.Sin(age * 0.03f + i * 0.3f) * 0.06f, 0);
                    break;
                }
                case ModelKind.Squid:
                    for (var i = 0; i < 8; i++) Rotate("tentacle" + i, 0.35f + 0.25f * Mathf.Sin(age * 0.2f), 0, 0);
                    break;
                case ModelKind.Bat:
                {
                    var wing = Cos(age * 1.3f) * Mathf.PI * 0.25f;
                    Rotate("right_wing", 0, wing, 0);
                    Rotate("left_wing", 0, -wing, 0);
                    Rotate("right_wing_tip", 0, wing * 0.5f, 0);
                    Rotate("left_wing_tip", 0, -wing * 0.5f, 0);
                    break;
                }
                case ModelKind.Ghast:
                    for (var i = 0; i < 9; i++) Rotate("tentacle" + i, 0.2f * Mathf.Sin(age * 0.3f + i) + 0.4f, 0, 0);
                    break;
                case ModelKind.Blaze:
                {
                    // BlazeModel.setupAnim: three rings of rods turning around the head
                    Rotate("head", pitch, relativeHead, 0);
                    var angle = age * Mathf.PI * -0.1f;
                    for (var i = 0; i < 4; i++, angle++) PosePivot["rod" + i] = new Vector3(Cos(angle) * 9, -2 + Cos((i * 2 + age) * 0.25f), Mathf.Sin(angle) * 9);
                    angle = Mathf.PI / 4 + age * Mathf.PI * 0.03f;
                    for (var i = 4; i < 8; i++, angle++) PosePivot["rod" + i] = new Vector3(Cos(angle) * 7, 2 + Cos((i * 2 + age) * 0.25f), Mathf.Sin(angle) * 7);
                    angle = 0.47123894f + age * Mathf.PI * -0.05f;
                    for (var i = 8; i < 12; i++, angle++) PosePivot["rod" + i] = new Vector3(Cos(angle) * 5, 11 + Cos((i * 1.5f + age) * 0.5f), Mathf.Sin(angle) * 5);
                    break;
                }
                case ModelKind.ArmorStand:
                {
                    // the pose of the entity data (degrees), arms and base plate depend on the flags
                    string[] names = { "head", "body", "left_arm", "right_arm", "left_leg", "right_leg" };
                    for (var i = 0; i < 6; i++)
                    {
                        var rotation = (entity.Pose[i] ?? DefaultStandPose[i]).Value;
                        Rotate(names[i], (float)rotation.X * Mathf.Deg2Rad, (float)rotation.Y * Mathf.Deg2Rad, (float)rotation.Z * Mathf.Deg2Rad);
                    }
                    SetPartVisible(view, "left_arm", (entity.StandFlags & 0x04) != 0);
                    SetPartVisible(view, "right_arm", (entity.StandFlags & 0x04) != 0);
                    SetPartVisible(view, "base_plate", (entity.StandFlags & 0x08) == 0);
                    break;
                }
            }

            foreach (var pair in view.Parts)
            {
                foreach (var entry in pair.Value)
                {
                    var part = entry.Value;
                    var rotation = part.Rotation + (PoseRotation.TryGetValue(pair.Key, out var animated) ? animated : Vector3.zero);
                    if (view.Model.Kind == ModelKind.Spider && pair.Key.EndsWith("_leg"))
                    {
                        // walking: legs swing around y and lift around z, alternating (vanilla SpiderModel)
                        var index = pair.Key.Contains("hind") && !pair.Key.Contains("middle") ? 0 : pair.Key.Contains("middle_hind") ? 1 : pair.Key.Contains("middle_front") ? 2 : 3;
                        var sign = pair.Key.StartsWith("right") ? 1 : -1;
                        var swingY = -(Cos(limbSwing * walk * 2 + index * Mathf.PI / 2 + (index % 2) * Mathf.PI) * 0.4f) * amount;
                        var swingZ = Mathf.Abs(Mathf.Sin(limbSwing * walk + index * Mathf.PI / 2 + (index % 2) * Mathf.PI) * 0.4f) * amount;
                        rotation += new Vector3(0, swingY * sign, swingZ * sign);
                    }
                    var pivot = PosePivot.TryGetValue(pair.Key, out var moved) ? moved : part.Pivot;
                    var local = Matrix4x4.Translate(pivot / 16f) * RotZ(rotation.z) * RotY(rotation.y) * RotX(rotation.x);
                    Apply(entry.Key, local, true);
                }
            }
        }

        private void UpdateSimple(SimpleView view, Entity entity, Vec3d position, float partial)
        {
            if (ObjectKinds.Contains(view.Kind))
            {
                UpdateObject(view, entity, position, partial);
                return;
            }
            var path = view.Kind;
            switch (path)
            {
                case "item":
                {
                    var name = ItemModels.Key(entity.Item);
                    if (name != view.ItemName)
                    {
                        view.ItemName = name;
                        SetItem(view.Root.transform, ref view.ItemTransform, view.Renderers, name);
                        view.ItemModel = items.Get(name?.TrimEnd('*'));
                    }
                    // vanilla ItemEntityRenderer: bobbing, spinning and the ground display transform
                    var age = entity.Age + partial;
                    var bob = Mathf.Sin(age / 10f + entity.Id) * 0.1f + 0.1f;
                    var spin = age / 20f + entity.Id;
                    var groundScale = view.ItemModel?.Get(ItemDisplay.Ground).scale.y ?? 1;
                    var matrix = Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y + bob + 0.25f * groundScale, position.Z)) * RotY(spin);
                    Apply(view.Root.transform, matrix);
                    if (view.ItemTransform != null) Apply(view.ItemTransform, ItemModels.DisplayMatrix(view.ItemModel, ItemDisplay.Ground), true);
                    break;
                }
                case "experience_orb":
                {
                    if (view.Renderers.Count == 0) BuildOrb(view, entity.ExperienceCount);
                    view.Root.transform.localPosition = RenderOrigin.ToRender(position.X, position.Y + 0.1, position.Z);
                    var camera = Camera.main;
                    if (camera != null) view.Root.transform.rotation = camera.transform.rotation;
                    var time = (entity.Age + partial) / 2f;
                    var color = new Color((Mathf.Sin(time) + 1) * 0.5f, 1, (Mathf.Sin(time + 4.1887903f) + 1) * 0.1f, 1);
                    properties.SetColor("_Tint", color);
                    break;
                }
                case "arrow":
                {
                    // ArrowRenderer: yaw - 90 around y, pitch around z, 45 degrees around x, 0.05625 scale
                    var yaw = MathUtil.LerpYaw(entity.PreviousYaw, entity.Yaw, partial);
                    var pitch = Mathf.Lerp(entity.PreviousPitch, entity.Pitch, partial);
                    var matrix = Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z)) * RotY((yaw - 90) * Mathf.Deg2Rad) * RotZ(pitch * Mathf.Deg2Rad)
                                 * RotX(Mathf.PI / 4) * Matrix4x4.Scale(Vector3.one * 0.05625f) * Matrix4x4.Translate(new Vector3(-4, 0, 0));
                    Apply(view.Root.transform, matrix);
                    break;
                }
                case "thrown":
                {
                    // ThrownItemRenderer: the item facing the camera
                    var name = ItemModels.Key(entity.Item) ?? ThrownDefault(entity.Type.Name.Path);
                    if (name != view.ItemName)
                    {
                        view.ItemName = name;
                        SetItem(view.Root.transform, ref view.ItemTransform, view.Renderers, name);
                        view.ItemModel = items.Get(name?.TrimEnd('*'));
                    }
                    view.Root.transform.localPosition = RenderOrigin.ToRender(position.X, position.Y + entity.Height / 2, position.Z);
                    var camera = Camera.main;
                    if (camera != null) view.Root.transform.rotation = camera.transform.rotation;
                    if (view.ItemTransform != null && view.ItemModel != null) Apply(view.ItemTransform, ItemModels.DisplayMatrix(view.ItemModel, ItemDisplay.Ground), true);
                    break;
                }
                case "item_frame":
                case "glow_item_frame":
                {
                    // ItemFrameRenderer: the frame on the wall and the item rotated in 45 degree steps
                    var direction = (Direction)Mathf.Clamp(entity.Data, 0, 5);
                    var center = new Vec3d(position.X + Directions.X(direction) * 0.46875, position.Y + Directions.Y(direction) * 0.46875, position.Z + Directions.Z(direction) * 0.46875);
                    var matrix = Matrix4x4.Translate(RenderOrigin.Relative(center.X, center.Y, center.Z)) * RotX(entity.Pitch * Mathf.Deg2Rad) * RotY((180 - entity.Yaw) * Mathf.Deg2Rad);
                    Apply(view.Root.transform, matrix);
                    var name = ItemModels.Key(entity.Item);
                    if (name != view.ItemName)
                    {
                        view.ItemName = name;
                        SetItem(view.Root.transform, ref view.ItemTransform, view.Renderers, name);
                        view.ItemModel = items.Get(name?.TrimEnd('*'));
                    }
                    view.MapTexture = name == "filled_map" ? Maps?.Get(session, MapTextures.MapId(entity.Item)) : null;
                    if (view.ItemTransform != null && view.MapTexture != null)
                    {
                        // ItemFrameRenderer: maps fill the frame, rotated in 90 degree steps
                        var map = Matrix4x4.Translate(new Vector3(0, 0, 0.4375f)) * RotZ(entity.FrameRotation % 4 * Mathf.PI / 2) * RotY(Mathf.PI) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f));
                        Apply(view.ItemTransform, map, true);
                    }
                    else if (view.ItemTransform != null && view.ItemModel != null)
                    {
                        var item = Matrix4x4.Translate(new Vector3(0, 0, 0.4375f)) * RotZ(entity.FrameRotation % 8 * Mathf.PI / 4) * Matrix4x4.Scale(Vector3.one * 0.5f);
                        Apply(view.ItemTransform, item * ItemModels.DisplayMatrix(view.ItemModel, ItemDisplay.Fixed), true);
                    }
                    if (path == "glow_item_frame") properties.SetVector(LightId, new Vector4(1, 1, 0, 0));
                    break;
                }
                case "painting":
                {
                    if (view.Renderers.Count == 0 || view.ItemName != entity.Variant.ToString()) BuildPainting(view, entity.Variant);
                    Apply(view.Root.transform, Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z)) * RotY((180 - entity.Yaw) * Mathf.Deg2Rad));
                    break;
                }
                case "falling_block":
                {
                    if (view.Renderers.Count == 0 && entity.FallingBlock?.Model is BlockRenderModel render && render.Models[0].Quads.Length > 0)
                    {
                        var filter = view.Root.AddComponent<MeshFilter>();
                        filter.sharedMesh = ItemIcons.BuildMesh(entity.FallingBlock.Block, render.Models[0]);
                        view.OwnedMeshes.Add(filter.sharedMesh);
                        var renderer = view.Root.AddComponent<MeshRenderer>();
                        renderer.sharedMaterial = blockMaterial;
                        view.Renderers.Add(renderer);
                    }
                    view.Root.transform.localPosition = RenderOrigin.ToRender(position.X - 0.5, position.Y, position.Z - 0.5);
                    break;
                }
                default:
                    view.Root.transform.localPosition = RenderOrigin.ToRender(position.X, position.Y, position.Z);
                    break;
            }
        }

        private readonly Dictionary<string, Mesh> armorMeshes = new Dictionary<string, Mesh>();

        /// <summary>Slots of the equipment (2 feet .. 5 head) and the humanoid parts their armor covers (HumanoidArmorLayer).</summary>
        private static readonly string[][] ArmorParts =
        {
            null, null,
            new[] { "right_leg", "left_leg" },
            new[] { "body", "right_leg", "left_leg" },
            new[] { "body", "right_arm", "left_arm" },
            new[] { "head" },
        };

        private static readonly string[] ArmorSuffixes = { null, null, "_boots", "_leggings", "_chestplate", "_helmet" };

        /// <summary>Worn armor (vanilla HumanoidArmorLayer): rebuilt when the equipment changes, attached to the animated parts.</summary>
        private void UpdateArmor(ModelView view, Entity entity)
        {
            if (view.Model.Kind != ModelKind.Biped && view.Model.Kind != ModelKind.Zombie && view.Model.Kind != ModelKind.ArmorStand) return;
            var changed = false;
            for (var slot = 2; slot <= 5; slot++)
            {
                var stack = entity.Equipment[slot];
                var name = stack?.Item?.Name.Path;
                if (name != null && !name.EndsWith(ArmorSuffixes[slot])) name = null;
                if (name != null && name.StartsWith("leather_")) name += "#" + LeatherColor(stack).ToString("X6");
                if (name != null && Core.Data.ItemProperties.HasFoil(stack)) name += "*";
                if (name == view.ArmorNames[slot]) continue;
                view.ArmorNames[slot] = name;
                changed = true;
            }
            if (!changed) return;

            foreach (var gameObject in view.Armor)
            {
                view.Renderers.Remove(gameObject.GetComponent<Renderer>());
                UnityEngine.Object.Destroy(gameObject);
            }
            view.Armor.Clear();
            for (var slot = 2; slot <= 5; slot++)
            {
                var name = view.ArmorNames[slot];
                if (name == null) continue;
                var glint = name.EndsWith("*");
                if (glint) name = name.Substring(0, name.Length - 1);
                var hash = name.IndexOf('#');
                var color = hash >= 0 ? Convert.ToInt32(name.Substring(hash + 1), 16) : -1;
                if (hash >= 0) name = name.Substring(0, hash);
                var material = name.Substring(0, name.Length - ArmorSuffixes[slot].Length);
                if (material == "golden") material = "gold";
                if (name == "turtle_helmet") material = "turtle";
                var inner = slot == 3;
                var texture = "assets/minecraft/textures/models/armor/" + material + "_layer_" + (inner ? 2 : 1);
                var armorMaterial = TintedMaterial(texture + ".png", color);
                AddArmor(view, slot, inner, armorMaterial);
                if (color >= 0) AddArmor(view, slot, inner, MaterialFor(texture + "_overlay.png"));
                var armorGlint = glint ? items.ArmorGlint(armorMaterial) : null;
                if (armorGlint != null) AddArmor(view, slot, inner, armorGlint);
            }
        }

        private void AddArmor(ModelView view, int slot, bool inner, Material material)
        {
            var layer = EntityModels.Armor(inner);
            foreach (var partName in ArmorParts[slot])
            {
                if (!view.Parts.TryGetValue(partName, out var targets)) continue;
                var part = layer.Parts.Find(p => p.Name == partName);
                if (part == null) continue;
                AddArmorPart(view, targets[0].Key, part, layer, inner, material);
            }
        }

        private void AddArmorPart(ModelView view, Transform parent, ModelPart part, ModelLayer layer, bool inner, Material material)
        {
            var key = (inner ? "inner/" : "outer/") + part.Name;
            if (!armorMeshes.TryGetValue(key, out var mesh)) armorMeshes[key] = mesh = PartMesh(part, layer);
            var gameObject = new GameObject("Armor " + part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(parent, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            view.Renderers.Add(renderer);
            view.Armor.Add(gameObject);
            // the hat of the helmet follows the head like the base model's hat
            foreach (var child in part.Children)
            {
                if (child.Name == "hat") AddArmorPart(view, gameObject.transform, child, layer, inner, material);
            }
        }

        /// <summary>Dyed leather armor: display.color of the item NBT, default brown.</summary>
        private static int LeatherColor(Core.Data.ItemStack stack)
        {
            var display = Core.Network.Nbt.Get(stack?.Nbt, "display");
            return Core.Network.Nbt.Get(display, "color") is int color ? color & 0xFFFFFF : 0xA06540;
        }

        private Material TintedMaterial(string texture, int color)
        {
            if (color < 0) return MaterialFor(texture);
            var key = texture + "#" + color.ToString("X6");
            if (materials.TryGetValue(key, out var material)) return material;
            material = new Material(MaterialFor(texture)) { name = key };
            material.SetColor("_Tint", new Color32((byte)(color >> 16), (byte)(color >> 8), (byte)color, 255));
            materials[key] = material;
            return material;
        }

        /// <summary>Replaces the item mesh child of a parent (dropped item or held item).</summary>
        private void SetItem(Transform parent, ref Transform child, List<Renderer> renderers, string name)
        {
            if (child != null)
            {
                foreach (var old in child.GetComponentsInChildren<Renderer>(true)) renderers.Remove(old);
                UnityEngine.Object.Destroy(child.gameObject);
                child = null;
            }
            if (name == null) return;
            var glint = name.EndsWith("*");
            if (glint) name = name.Substring(0, name.Length - 1);
            var model = items.Get(name);
            if (model == null) return;
            // a container: the display transform goes here, the item meshes (one, or entity model parts) below
            var container = new GameObject("Item " + name).transform;
            container.SetParent(parent, false);
            var created = ItemModels.Instantiate(model, container, name);
            if (glint) items.AddGlint(created);
            renderers.AddRange(created);
            child = container;
        }

        /// <summary>LightningBoltRenderer: a jagged column of thin boxes 128 blocks high, random per bolt.</summary>
        private static Mesh LightningMesh(int seed)
        {
            var random = new System.Random(seed);
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            float x = 0, z = 0;
            for (var segment = 0; segment < 8; segment++)
            {
                var y0 = 128 - segment * 16f;
                var y1 = y0 - 16;
                var nx = segment == 7 ? 0 : x + (float)(random.NextDouble() * 2 - 1) * 3;
                var nz = segment == 7 ? 0 : z + (float)(random.NextDouble() * 2 - 1) * 3;
                for (var layer = 0; layer < 2; layer++)
                {
                    var width = layer == 0 ? 0.1f : 0.3f;
                    var first = vertices.Count;
                    // two crossed quads per segment (render space, z negated)
                    vertices.Add(new Vector3(x - width, y0, -z));
                    vertices.Add(new Vector3(x + width, y0, -z));
                    vertices.Add(new Vector3(nx + width, y1, -nz));
                    vertices.Add(new Vector3(nx - width, y1, -nz));
                    vertices.Add(new Vector3(x, y0, -(z - width)));
                    vertices.Add(new Vector3(x, y0, -(z + width)));
                    vertices.Add(new Vector3(nx, y1, -(nz + width)));
                    vertices.Add(new Vector3(nx, y1, -(nz - width)));
                    indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3, first + 4, first + 5, first + 6, first + 4, first + 6, first + 7 });
                }
                x = nx;
                z = nz;
            }
            var mesh = new Mesh { name = "Lightning" };
            mesh.SetVertices(vertices);
            var normals = new Vector3[vertices.Count];
            for (var i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.uv = new Vector2[vertices.Count];
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A lightning bolt exists: the sky flashes (vanilla ClientLevel.skyFlashTime).</summary>
        public bool Lightning
        {
            get
            {
                foreach (var entity in session.Entities.Entities.Values)
                {
                    if (entity.Type?.Name.Path == "lightning_bolt") return true;
                }
                return false;
            }
        }

        private static string ThrownDefault(string type)
        {
            switch (type)
            {
                case "potion": return "splash_potion";
                case "eye_of_ender": return "ender_eye";
                case "fireball":
                case "small_fireball": return "fire_charge";
                default: return type;
            }
        }

        private Mesh arrowMesh;
        private Mesh frameMesh;
        private readonly Dictionary<int, Mesh> paintingMeshes = new Dictionary<int, Mesh>();

        /// <summary>ArrowRenderer quads: the back square and two crossed shaft planes (pixel units, both sides drawn).</summary>
        private Mesh ArrowMesh()
        {
            if (arrowMesh != null) return arrowMesh;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Rect uv, Vector3 normal)
            {
                foreach (var v in new[] { a, b, c, d })
                {
                    vertices.Add(new Vector3(v.x, v.y, -v.z));
                    normals.Add(new Vector3(normal.x, normal.y, -normal.z));
                }
                uvs.Add(new Vector2(uv.xMin, 1 - uv.yMin));
                uvs.Add(new Vector2(uv.xMax, 1 - uv.yMin));
                uvs.Add(new Vector2(uv.xMax, 1 - uv.yMax));
                uvs.Add(new Vector2(uv.xMin, 1 - uv.yMax));
            }
            Quad(new Vector3(-7, -2, -2), new Vector3(-7, -2, 2), new Vector3(-7, 2, 2), new Vector3(-7, 2, -2), new Rect(0, 0.15625f, 0.15625f, 0.15625f), Vector3.left);
            Quad(new Vector3(-8, -2, 0), new Vector3(8, -2, 0), new Vector3(8, 2, 0), new Vector3(-8, 2, 0), new Rect(0, 0, 0.5f, 0.15625f), Vector3.forward);
            Quad(new Vector3(-8, 0, -2), new Vector3(8, 0, -2), new Vector3(8, 0, 2), new Vector3(-8, 0, 2), new Rect(0, 0, 0.5f, 0.15625f), Vector3.up);
            var indices = new List<int>();
            for (var i = 0; i < vertices.Count; i += 4) indices.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            arrowMesh = new Mesh { name = "Arrow" };
            arrowMesh.SetVertices(vertices);
            arrowMesh.SetNormals(normals);
            arrowMesh.SetUVs(0, uvs);
            arrowMesh.SetTriangles(indices, 0);
            arrowMesh.RecalculateBounds();
            return arrowMesh;
        }

        /// <summary>block/item_frame.json: the back plate (submesh 1) and the four birch borders (submesh 0), in block units.</summary>
        private Mesh FrameMesh()
        {
            if (frameMesh != null) return frameMesh;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var wood = new List<int>();
            var back = new List<int>();
            void Box(Vector3 from, Vector3 to, List<int> indices)
            {
                from /= 16;
                to /= 16;
                var faces = new[]
                {
                    (new Vector3(0, 0, -1), new[] { new Vector3(to.x, from.y, from.z), new Vector3(from.x, from.y, from.z), new Vector3(from.x, to.y, from.z), new Vector3(to.x, to.y, from.z) }),
                    (new Vector3(0, 0, 1), new[] { new Vector3(from.x, from.y, to.z), new Vector3(to.x, from.y, to.z), new Vector3(to.x, to.y, to.z), new Vector3(from.x, to.y, to.z) }),
                    (new Vector3(-1, 0, 0), new[] { new Vector3(from.x, from.y, from.z), new Vector3(from.x, from.y, to.z), new Vector3(from.x, to.y, to.z), new Vector3(from.x, to.y, from.z) }),
                    (new Vector3(1, 0, 0), new[] { new Vector3(to.x, from.y, to.z), new Vector3(to.x, from.y, from.z), new Vector3(to.x, to.y, from.z), new Vector3(to.x, to.y, to.z) }),
                    (new Vector3(0, 1, 0), new[] { new Vector3(from.x, to.y, to.z), new Vector3(to.x, to.y, to.z), new Vector3(to.x, to.y, from.z), new Vector3(from.x, to.y, from.z) }),
                    (new Vector3(0, -1, 0), new[] { new Vector3(from.x, from.y, from.z), new Vector3(to.x, from.y, from.z), new Vector3(to.x, from.y, to.z), new Vector3(from.x, from.y, to.z) }),
                };
                foreach (var (normal, corners) in faces)
                {
                    var first = vertices.Count;
                    foreach (var corner in corners)
                    {
                        vertices.Add(new Vector3(corner.x, corner.y, -corner.z));
                        normals.Add(new Vector3(normal.x, normal.y, -normal.z));
                        // texture coordinates from the position on the face's plane (like vanilla's automatic uv)
                        var u = normal.x != 0 ? corner.z : corner.x;
                        var v = normal.y != 0 ? corner.z : corner.y;
                        uvs.Add(new Vector2(u, v));
                    }
                    indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }
            }
            Box(new Vector3(3, 3, 15.5f), new Vector3(13, 13, 16), back);
            Box(new Vector3(2, 2, 15), new Vector3(14, 3, 16), wood);
            Box(new Vector3(2, 13, 15), new Vector3(14, 14, 16), wood);
            Box(new Vector3(2, 3, 15), new Vector3(3, 13, 16), wood);
            Box(new Vector3(13, 3, 15), new Vector3(14, 13, 16), wood);
            frameMesh = new Mesh { name = "Item frame", subMeshCount = 2 };
            frameMesh.SetVertices(vertices);
            frameMesh.SetNormals(normals);
            frameMesh.SetUVs(0, uvs);
            frameMesh.SetTriangles(wood, 0);
            frameMesh.SetTriangles(back, 1);
            frameMesh.RecalculateBounds();
            return frameMesh;
        }

        /// <summary>minecraft:painting_variant (1.20.4 registration order) with its size in pixels.</summary>
        private static readonly (string name, int width, int height)[] Paintings =
        {
            ("kebab", 16, 16), ("aztec", 16, 16), ("alban", 16, 16), ("aztec2", 16, 16), ("bomb", 16, 16), ("plant", 16, 16), ("wasteland", 16, 16),
            ("pool", 32, 16), ("courbet", 32, 16), ("sea", 32, 16), ("sunset", 32, 16), ("creebet", 32, 16), ("wanderer", 16, 32), ("graham", 16, 32),
            ("match", 32, 32), ("bust", 32, 32), ("stage", 32, 32), ("void", 32, 32), ("skull_and_roses", 32, 32), ("wither", 32, 32), ("fighters", 64, 32),
            ("pointer", 64, 64), ("pigscene", 64, 64), ("burning_skull", 64, 64), ("skeleton", 64, 48), ("earth", 32, 32), ("wind", 32, 32), ("water", 32, 32),
            ("fire", 32, 32), ("donkey_kong", 64, 48),
        };

        /// <summary>PaintingRenderer: the picture on the front (model -z), the back texture behind it.</summary>
        private void BuildPainting(SimpleView view, int variant)
        {
            foreach (var renderer in view.Renderers) UnityEngine.Object.Destroy(renderer.gameObject == view.Root ? (UnityEngine.Object)renderer : renderer.gameObject);
            view.Renderers.Clear();
            view.ItemName = variant.ToString();
            var (name, width, height) = Paintings[Mathf.Clamp(variant, 0, Paintings.Length - 1)];
            if (!paintingMeshes.TryGetValue(variant, out var mesh))
            {
                float halfWidth = width / 32f, halfHeight = height / 32f, front = -0.5f / 16, back = 0.5f / 16;
                mesh = new Mesh { name = "Painting " + name, subMeshCount = 2 };
                mesh.SetVertices(new List<Vector3>
                {
                    // render space (z negated): the front at model z -0.5/16
                    new Vector3(halfWidth, -halfHeight, -front), new Vector3(-halfWidth, -halfHeight, -front), new Vector3(-halfWidth, halfHeight, -front), new Vector3(halfWidth, halfHeight, -front),
                    new Vector3(-halfWidth, -halfHeight, -back), new Vector3(halfWidth, -halfHeight, -back), new Vector3(halfWidth, halfHeight, -back), new Vector3(-halfWidth, halfHeight, -back),
                });
                mesh.SetNormals(new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.back, Vector3.back, Vector3.back, Vector3.back });
                mesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                    new Vector2(0, 0), new Vector2(width / 16f, 0), new Vector2(width / 16f, height / 16f), new Vector2(0, height / 16f),
                });
                mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                mesh.SetTriangles(new[] { 4, 5, 6, 4, 6, 7 }, 1);
                mesh.RecalculateBounds();
                paintingMeshes[variant] = mesh;
            }
            var gameObject = new GameObject("Painting " + name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = gameObject.GetComponent<MeshRenderer>();
            var backMaterial = MaterialFor("assets/minecraft/textures/painting/back.png");
            backMaterial.mainTexture.wrapMode = TextureWrapMode.Repeat;
            meshRenderer.sharedMaterials = new[] { MaterialFor("assets/minecraft/textures/painting/" + name + ".png"), backMaterial };
            view.Renderers.Add(meshRenderer);
        }

        /// <summary>Experience orb: billboard with the size dependent icon of experience_orb.png (4x4 icons).</summary>
        private void BuildOrb(SimpleView view, int value)
        {
            var icon = value >= 2477 ? 10 : value >= 1237 ? 9 : value >= 617 ? 8 : value >= 307 ? 7 : value >= 149 ? 6 : value >= 73 ? 5 : value >= 37 ? 4 : value >= 17 ? 3 : value >= 7 ? 2 : value >= 3 ? 1 : 0;
            var uv = new Rect(icon % 4 * 16 / 64f, 1 - (icon / 4 * 16 + 16) / 64f, 16 / 64f, 16 / 64f);
            var gameObject = new GameObject("Orb", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            var quad = QuadMesh(uv, new Vector2(0.3f, 0.3f), -0.15f);
            view.OwnedMeshes.Add(quad);
            gameObject.GetComponent<MeshFilter>().sharedMesh = quad;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            var material = MaterialFor("assets/minecraft/textures/entity/experience_orb.png");
            var orb = new Material(material) { name = "Orb" };
            orb.SetFloat("_Fullbright", 1);
            renderer.sharedMaterial = orb;
            view.Renderers.Add(renderer);
        }

        // ------------------------------------------------------------------ name tags (projected in the GUI)

        /// <summary>Player names and visible custom names above the entities (vanilla EntityRenderer.renderNameTag).</summary>
        public void DrawNameTags(GuiRenderer gui, Camera camera, float partial)
        {
            if (camera == null) return;
            foreach (var entity in session.Entities.Entities.Values)
            {
                string name = null;
                string below = null;
                var ui = session.ServerUi;
                if (entity.IsPlayer && session.Entities.Players.TryGetValue(entity.Uuid, out var info))
                {
                    name = info.DisplayName ?? info.Name;
                    if (info.Name != null && ui.TeamOf.TryGetValue(info.Name, out var team))
                    {
                        if (team.NameTagVisibility == "never") continue;
                        if (info.DisplayName == null) name = team.Decorate(info.Name);
                    }
                    var belowName = ui.DisplayObjective(2);
                    var score = belowName != null && info.Name != null ? ui.GetScore(belowName.Name, info.Name) : null;
                    if (score != null) below = score.Value + " " + belowName.DisplayName;
                }
                if (entity.CustomName != null && (entity.CustomNameVisible || session.TargetEntity == entity)) name = entity.CustomName;
                if (string.IsNullOrEmpty(name) || entity.Invisible) continue;
                var position = entity.Interpolated(partial);
                var world = RenderOrigin.ToRender(position.X, position.Y + entity.Height + 0.5, position.Z);
                if ((camera.transform.position - world).sqrMagnitude > 64 * 64) continue;
                var screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0) continue;
                var x = screen.x / gui.Scale;
                var y = (Screen.height - screen.y) / gui.Scale;
                if (below != null)
                {
                    var belowWidth = gui.Font.Width(below);
                    gui.Fill(new Rect(x - belowWidth / 2 - 1, y - 1, belowWidth + 2, 10), new Color(0, 0, 0, 0.25f));
                    gui.TextCentered(below, x, y, Color.white, false);
                    y -= 10;
                }
                var width = gui.Font.Width(name);
                gui.Fill(new Rect(x - width / 2 - 1, y - 1, width + 2, 10), new Color(0, 0, 0, 0.25f));
                gui.TextCentered(name, x, y, entity.Sneaking ? new Color(1, 1, 1, 0.5f) : Color.white, false);
            }
        }

        // ------------------------------------------------------------------ math

        internal static Matrix4x4 RotX(float a)
        {
            var m = Matrix4x4.identity;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            m.m11 = c;
            m.m12 = -s;
            m.m21 = s;
            m.m22 = c;
            return m;
        }

        internal static Matrix4x4 RotY(float a)
        {
            var m = Matrix4x4.identity;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            m.m00 = c;
            m.m02 = s;
            m.m20 = -s;
            m.m22 = c;
            return m;
        }

        internal static Matrix4x4 RotZ(float a)
        {
            var m = Matrix4x4.identity;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            m.m00 = c;
            m.m01 = -s;
            m.m10 = s;
            m.m11 = c;
            return m;
        }

        /// <summary>Sets a transform from a minecraft space matrix (conjugated with the z flip).</summary>
        internal static void Apply(Transform transform, Matrix4x4 minecraft, bool local = false)
        {
            var m = Flip * minecraft * Flip;
            var position = (Vector3)m.GetColumn(3);
            var forward = (Vector3)m.GetColumn(2);
            var up = (Vector3)m.GetColumn(1);
            var scale = ((Vector3)m.GetColumn(0)).magnitude;
            transform.localPosition = position;
            transform.localRotation = Quaternion.LookRotation(forward, up);
            transform.localScale = Vector3.one * scale;
        }

        public void Dispose()
        {
            session.Entities.Removed -= OnRemoved;
            foreach (var lead in leads.Values)
            {
                if (lead != null) UnityEngine.Object.Destroy(lead.gameObject);
            }
            leads.Clear();
            foreach (var material in lineMaterials.Values) UnityEngine.Object.Destroy(material);
            lineMaterials.Clear();
            foreach (var view in views.Values) view.Destroy();
            views.Clear();
            localView?.Destroy();
            dollView?.Destroy();
            if (dollTexture != null) dollTexture.Release();
            foreach (var material in materials.Values) UnityEngine.Object.Destroy(material);
            foreach (var mesh in armorMeshes.Values) UnityEngine.Object.Destroy(mesh);
            foreach (var mesh in paintingMeshes.Values) UnityEngine.Object.Destroy(mesh);
            if (arrowMesh != null) UnityEngine.Object.Destroy(arrowMesh);
            if (frameMesh != null) UnityEngine.Object.Destroy(frameMesh);
            UnityEngine.Object.Destroy(lineMaterial);
            UnityEngine.Object.Destroy(root.gameObject);
        }
    }

    /// <summary>
    /// Player skins: the default skins of the game chosen from the UUID like vanilla (DefaultPlayerSkin), or the skin
    /// of the textures property (online mode), downloaded once from textures.minecraft.net.
    /// </summary>
    public sealed class SkinManager : IDisposable
    {
        private static readonly string[] DefaultNames = { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" };

        private readonly GameSession session;
        private readonly GuiAssets assets;
        private readonly Dictionary<string, Texture2D> downloaded = new Dictionary<string, Texture2D>();
        private readonly HashSet<string> downloading = new HashSet<string>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<string, byte[]>> finished = new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<string, byte[]>>();
        private readonly System.Net.Http.HttpClient http = new System.Net.Http.HttpClient();

        public SkinManager(GameSession session, GuiAssets assets)
        {
            this.session = session;
            this.assets = assets;
        }

        private PlayerInfo Info(Guid uuid) => session.Entities.Players.TryGetValue(uuid, out var info) ? info : null;

        public bool IsSlim(Guid uuid)
        {
            var info = Info(uuid);
            if (info?.SkinUrl != null) return info.Slim;
            return DefaultIndex(uuid) < 9;
        }

        /// <summary>Java UUID.hashCode, floorMod 18: 9 slim then 9 wide skins (vanilla DefaultPlayerSkin).</summary>
        private static int DefaultIndex(Guid uuid)
        {
            Core.Network.UuidUtil.ToLongs(uuid, out var most, out var least);
            var hilo = most ^ least;
            var hash = (int)(hilo >> 32) ^ (int)hilo;
            return ((hash % 18) + 18) % 18;
        }

        public Texture Get(Guid uuid)
        {
            while (finished.TryDequeue(out var result)) Decode(result.Key, result.Value);
            var info = Info(uuid);
            if (info?.SkinUrl != null)
            {
                if (downloaded.TryGetValue(info.SkinUrl, out var skin) && skin != null) return skin;
                Download(info.SkinUrl);
            }
            var index = DefaultIndex(uuid);
            var path = "assets/minecraft/textures/entity/player/" + (index < 9 ? "slim/" : "wide/") + DefaultNames[index % 9] + ".png";
            return assets.Get(path)?.Texture ?? assets.Get("assets/minecraft/textures/entity/player/wide/steve.png")?.Texture ?? Texture2D.whiteTexture;
        }

        /// <summary>A skin by its textures.minecraft.net url (player heads), downloaded once; null until it is ready.</summary>
        public Texture GetUrl(string url)
        {
            while (finished.TryDequeue(out var result)) Decode(result.Key, result.Value);
            if (downloaded.TryGetValue(url, out var skin)) return skin;
            Download(url);
            return null;
        }

        private async void Download(string url)
        {
            if (!downloading.Add(url)) return;
            try
            {
                var data = await http.GetByteArrayAsync(url.Replace("http://", "https://"));
                finished.Enqueue(new KeyValuePair<string, byte[]>(url, data));
            }
            catch (Exception exception)
            {
                Core.Util.Log.Warn("Can not download skin " + url + ": " + exception.Message);
                downloaded[url] = null;
            }
        }

        /// <summary>Legacy 64x32 skins are converted to 64x64 (left limbs mirrored from the right ones), like vanilla.</summary>
        private void Decode(string url, byte[] data)
        {
            try
            {
                var image = PngDecoder.Decode(data);
                if (image.Height == 32)
                {
                    var converted = new RgbaImage(64, 64);
                    Buffer.BlockCopy(image.Pixels, 0, converted.Pixels, 0, image.Pixels.Length);
                    void Copy(int sx, int sy, int dx, int dy, int w, int h)
                    {
                        for (var y = 0; y < h; y++)
                        {
                            for (var x = 0; x < w; x++)
                            {
                                Buffer.BlockCopy(image.Pixels, ((sy + y) * 64 + sx + x) * 4, converted.Pixels, ((dy + y) * 64 + dx + w - 1 - x) * 4, 4);
                            }
                        }
                    }
                    // legs and arms: each face copied mirrored into the new left limb areas
                    int[][] faces =
                    {
                        new[] { 4, 16, 20, 48, 4, 4 }, new[] { 8, 16, 24, 48, 4, 4 }, new[] { 0, 20, 24, 52, 4, 12 }, new[] { 4, 20, 20, 52, 4, 12 },
                        new[] { 8, 20, 16, 52, 4, 12 }, new[] { 12, 20, 28, 52, 4, 12 }, new[] { 44, 16, 36, 48, 4, 4 }, new[] { 48, 16, 40, 48, 4, 4 },
                        new[] { 40, 20, 40, 52, 4, 12 }, new[] { 44, 20, 36, 52, 4, 12 }, new[] { 48, 20, 32, 52, 4, 12 }, new[] { 52, 20, 44, 52, 4, 12 },
                    };
                    foreach (var face in faces) Copy(face[0], face[1], face[2], face[3], face[4], face[5]);
                    image = converted;
                }
                downloaded[url] = assets.ToTexture(image, url);
            }
            catch (Exception exception)
            {
                Core.Util.Log.Warn("Invalid skin " + url + ": " + exception.Message);
                downloaded[url] = null;
            }
        }

        public void Dispose() => http.Dispose();
    }
}
