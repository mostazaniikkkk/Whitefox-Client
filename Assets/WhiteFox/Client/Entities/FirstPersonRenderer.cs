// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Game;
using WhiteFox.Core.Physics;

namespace WhiteFox.Client.Entities
{
    /// <summary>
    /// First person view (vanilla ItemInHandRenderer + GameRenderer.bobView): the arm or the held item with the swing
    /// and equip animations, and the view bobbing while walking. The rig is a child of the camera, scaled down toward
    /// the eye so it does not go through walls; that does not change how it looks on screen.
    /// </summary>
    public sealed class FirstPersonRenderer : IDisposable
    {
        private const float RigScale = 0.15f;
        private static readonly int LightId = Shader.PropertyToID("_Light");

        private readonly GameSession session;
        private readonly ItemModels items;
        private readonly SkinManager skins;
        private readonly Transform rig;
        private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        private readonly Material armMaterial;
        private Transform arm;
        private Mesh[] armMeshes;
        private bool armSlim;
        private MeshRenderer armRenderer;
        private MeshRenderer sleeveRenderer;
        private Transform item;
        private List<Renderer> itemRenderers = new List<Renderer>();
        private string itemName;
        private bool itemGlint;
        private string displayedItem;

        // two handed map (vanilla renderTwoHandedMap): both arms and the map on its background
        private Transform mapRig;
        private Transform mapHands;
        private MeshRenderer[] mapHandRenderers;
        private bool mapHandsSlim;
        private Transform mapSheet;
        private MeshRenderer mapBackground;
        private MeshRenderer mapImage;
        private Material mapBackgroundMaterial;
        private Material mapImageMaterial;
        private readonly List<Mesh> mapMeshes = new List<Mesh>();
        private readonly MaterialPropertyBlock mapProperties = new MaterialPropertyBlock();

        // equip animation (mainHandHeight) and view bobbing (walkDist, bob)
        private float height = 1;
        private float previousHeight = 1;
        private float walkDistance;
        private float previousWalkDistance;
        private float bob;
        private float previousBob;

        public bool ViewBobbing = true;
        public MapTextures Maps;

        public FirstPersonRenderer(GameSession session, Camera camera, ItemModels items, SkinManager skins)
        {
            this.session = session;
            this.items = items;
            this.skins = skins;
            rig = new GameObject("First person").transform;
            rig.SetParent(camera.transform, false);
            rig.localScale = Vector3.one * RigScale;
            armMaterial = new Material(Resources.Load<Shader>("WhiteFox/Entity")) { name = "First person arm" };
        }

        /// <summary>Game tick: equip progress and bobbing (vanilla ItemInHandRenderer.tick, Player.aiStep).</summary>
        public void Tick()
        {
            var player = session.Player;
            var selected = ItemModels.Key(session.Inventory.Player.Slots[36 + session.SelectedSlot])?.TrimEnd('*') ?? session.HotbarItems[session.SelectedSlot];
            previousHeight = height;
            height += Mathf.Clamp((selected == displayedItem ? 1 : 0) - height, -0.4f, 0.4f);
            if (height < 0.1f) displayedItem = selected;

            var dx = player.Position.X - player.PreviousPosition.X;
            var dz = player.Position.Z - player.PreviousPosition.Z;
            var distance = (float)Math.Sqrt(dx * dx + dz * dz);
            previousWalkDistance = walkDistance;
            walkDistance += distance * 0.6f;
            previousBob = bob;
            var target = player.OnGround && !session.Dead && !player.Swimming && !player.Riding ? Mathf.Min(0.1f, distance) : 0;
            bob += (target - bob) * 0.4f;
        }

        /// <summary>Bobbing matrix of the view in minecraft view space (vanilla bobView).</summary>
        public Matrix4x4 BobMatrix(float partial)
        {
            if (!ViewBobbing) return Matrix4x4.identity;
            var walk = -Mathf.Lerp(previousWalkDistance, walkDistance, partial);
            var amount = Mathf.Lerp(previousBob, bob, partial);
            var angle = walk * Mathf.PI;
            return Matrix4x4.Translate(new Vector3(Mathf.Sin(angle) * amount * 0.5f, -Mathf.Abs(Mathf.Cos(angle) * amount), 0))
                   * EntityRenderer.RotZ(Mathf.Sin(angle) * amount * 3 * Mathf.Deg2Rad)
                   * EntityRenderer.RotX(Mathf.Abs(Mathf.Cos(angle - 0.2f) * amount) * 5 * Mathf.Deg2Rad);
        }

        public void Update(float partial, bool visible)
        {
            var player = session.Player;
            visible &= player.Gamemode != Gamemode.Spectator && !session.Dead;
            if (rig.gameObject.activeSelf != visible) rig.gameObject.SetActive(visible);
            if (!visible) return;

            var eye = player.EyePosition;
            var light = session.World.GetLight((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
            properties.Clear();
            properties.SetVector(LightId, new Vector4((light & 0x0F) / 15f, (light >> 4) / 15f, 0, 0));

            var swing = player.SwingTime >= 0 ? (player.SwingTime + partial) / 6f : 0;
            var equip = 1 - Mathf.Lerp(previousHeight, height, partial);
            var bobbing = BobMatrix(partial);
            var model = items.Get(displayedItem);
            var empty = displayedItem == null || model == null;

            // the arm, only when the hand is empty
            if (empty)
            {
                EnsureArm();
                arm.gameObject.SetActive(true);
                Apply(arm, bobbing * ArmMatrix(swing, equip));
                armRenderer.SetPropertyBlock(properties);
                sleeveRenderer.SetPropertyBlock(properties);
            }
            else if (arm != null)
            {
                arm.gameObject.SetActive(false);
            }

            var selectedStack = session.Inventory.Player.Slots[36 + session.SelectedSlot];
            var glint = Core.Data.ItemProperties.HasFoil(selectedStack);
            if (displayedItem != itemName || glint != itemGlint)
            {
                itemName = displayedItem;
                itemGlint = glint;
                if (item != null) UnityEngine.Object.Destroy(item.gameObject);
                item = null;
                if (!empty)
                {
                    var container = new GameObject("Held " + itemName).transform;
                    container.SetParent(rig, false);
                    itemRenderers = ItemModels.Instantiate(model, container, itemName);
                    if (glint) items.AddGlint(itemRenderers);
                    foreach (var renderer in itemRenderers)
                    {
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                    }
                    item = container;
                }
            }
            // a map in the main hand with nothing in the other hand is held with both hands
            var twoHanded = !empty && displayedItem == "filled_map" && session.Inventory.Player.Slots[45] == null;
            if (item != null && item.gameObject.activeSelf == twoHanded) item.gameObject.SetActive(!twoHanded);
            if (twoHanded)
            {
                RenderTwoHandedMap(bobbing, swing, equip, player.Pitch);
            }
            else if (mapRig != null && mapRig.gameObject.activeSelf)
            {
                mapRig.gameObject.SetActive(false);
            }
            if (item != null && !empty && !twoHanded)
            {
                Apply(item, bobbing * ItemMatrix(swing, equip) * ItemModels.DisplayMatrix(model, ItemDisplay.FirstPersonRightHand));
                var map = displayedItem == "filled_map" ? Maps?.Get(session, MapTextures.MapId(session.Inventory.Player.Slots[36 + session.SelectedSlot])) : null;
                if (map != null) properties.SetTexture("_MainTex", map);
                foreach (var renderer in itemRenderers) renderer.SetPropertyBlock(properties);
            }
        }

        /// <summary>Vanilla ItemInHandRenderer.renderTwoHandedMap + renderMapHand + renderMap.</summary>
        private void RenderTwoHandedMap(Matrix4x4 bobbing, float swing, float equip, float pitch)
        {
            EnsureMapRig();
            if (!mapRig.gameObject.activeSelf) mapRig.gameObject.SetActive(true);
            var root = Mathf.Sqrt(swing);
            var j = -0.2f * Mathf.Sin(swing * Mathf.PI);
            var k = -0.4f * Mathf.Sin(root * Mathf.PI);
            // calculateMapTilt: looking down brings the map up in front of the eyes
            var tilt = Mathf.Clamp01(1 - pitch / 45 + 0.1f);
            tilt = -Mathf.Cos(tilt * Mathf.PI) * 0.5f + 0.5f;
            var basis = bobbing
                        * Matrix4x4.Translate(new Vector3(0, -j / 2, k))
                        * Matrix4x4.Translate(new Vector3(0, 0.04f + equip * -1.2f + tilt * -0.5f, -0.72f))
                        * Deg(EntityRenderer.RotX, tilt * -85);

            var hands = basis * Deg(EntityRenderer.RotY, 90);
            for (var side = 0; side < 2; side++)
            {
                var f = side == 0 ? 1 : -1;
                var hand = hands
                           * Deg(EntityRenderer.RotY, 92)
                           * Deg(EntityRenderer.RotX, 45)
                           * Deg(EntityRenderer.RotZ, f * -41)
                           * Matrix4x4.Translate(new Vector3(f * 0.3f, -1.1f, 0.45f))
                           * Matrix4x4.Translate(new Vector3(-f * 5 / 16f, 2 / 16f, 0)); // arm pivot
                Apply(mapHandRenderers[side * 2].transform, hand);
                mapHandRenderers[side * 2].SetPropertyBlock(properties);
                mapHandRenderers[side * 2 + 1].SetPropertyBlock(properties);
            }

            var sheet = basis
                        * Deg(EntityRenderer.RotX, Mathf.Sin(root * Mathf.PI) * 20)
                        * Matrix4x4.Scale(Vector3.one * 2)
                        * Deg(EntityRenderer.RotY, 180)
                        * Deg(EntityRenderer.RotZ, 180)
                        * Matrix4x4.Scale(Vector3.one * 0.38f)
                        * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0))
                        * Matrix4x4.Scale(Vector3.one / 128);
            Apply(mapSheet, sheet);
            mapBackground.SetPropertyBlock(properties);
            var texture = Maps?.Get(session, MapTextures.MapId(session.Inventory.Player.Slots[36 + session.SelectedSlot]));
            mapImage.enabled = texture != null;
            mapProperties.Clear();
            mapProperties.SetVector(LightId, properties.GetVector(LightId));
            if (texture != null) mapProperties.SetTexture("_MainTex", texture);
            mapImage.SetPropertyBlock(mapProperties);
        }

        private void EnsureMapRig()
        {
            var uuid = session.Server?.Uuid ?? Guid.Empty;
            var slim = skins.IsSlim(uuid);
            if (mapRig == null)
            {
                mapRig = new GameObject("Two handed map").transform;
                mapRig.SetParent(rig, false);
                mapSheet = new GameObject("Map").transform;
                mapSheet.SetParent(mapRig, false);
                var shader = armMaterial.shader;
                mapBackgroundMaterial = new Material(shader) { name = "Map background" };
                mapImageMaterial = new Material(shader) { name = "Map image" };
                var background = items.MapBackground;
                if (background != null) mapBackgroundMaterial.SetTexture("_MainTex", background);
                // vanilla quads: the background (-7..135) at z 0 and the map (0..128) just in front of it (z -0.01)
                mapBackground = Quad("Background", -7, 135, 0, mapBackgroundMaterial);
                mapImage = Quad("Image", 0, 128, 0.01f * 128, mapImageMaterial);
            }
            if (mapHands != null && slim == mapHandsSlim) return;
            if (mapHands != null) UnityEngine.Object.Destroy(mapHands.gameObject);
            mapHandsSlim = slim;
            mapHands = new GameObject("Hands").transform;
            mapHands.SetParent(mapRig, false);
            var model = EntityModels.Create("player", slim);
            var layer = model.Layers[0];
            mapHandRenderers = new MeshRenderer[4];
            var names = new[] { "right_arm", "left_arm" };
            for (var side = 0; side < 2; side++)
            {
                var part = layer.Parts.Find(p => p.Name == names[side]);
                var sleeve = part.Children.Count > 0 ? part.Children[0] : null;
                var armMesh = EntityRenderer.PartMesh(part, layer);
                mapMeshes.Add(armMesh);
                var armObject = new GameObject(names[side], typeof(MeshFilter), typeof(MeshRenderer));
                armObject.transform.SetParent(mapHands, false);
                armObject.GetComponent<MeshFilter>().sharedMesh = armMesh;
                mapHandRenderers[side * 2] = armObject.GetComponent<MeshRenderer>();
                mapHandRenderers[side * 2].sharedMaterial = armMaterial;
                mapHandRenderers[side * 2].shadowCastingMode = ShadowCastingMode.Off;
                var sleeveObject = new GameObject("Sleeve", typeof(MeshFilter), typeof(MeshRenderer));
                sleeveObject.transform.SetParent(armObject.transform, false);
                if (sleeve != null)
                {
                    EntityRenderer.Apply(sleeveObject.transform, Matrix4x4.Translate(sleeve.Pivot / 16f), true);
                    var sleeveMesh = EntityRenderer.PartMesh(sleeve, layer);
                    mapMeshes.Add(sleeveMesh);
                    sleeveObject.GetComponent<MeshFilter>().sharedMesh = sleeveMesh;
                }
                mapHandRenderers[side * 2 + 1] = sleeveObject.GetComponent<MeshRenderer>();
                mapHandRenderers[side * 2 + 1].sharedMaterial = armMaterial;
                mapHandRenderers[side * 2 + 1].shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        /// <summary>A square from min to max (x right, y down like the vanilla map space), z already in render space.</summary>
        private MeshRenderer Quad(string name, float min, float max, float z, Material material)
        {
            var mesh = new Mesh { name = "Map " + name };
            mesh.SetVertices(new List<Vector3> { new Vector3(min, max, z), new Vector3(max, max, z), new Vector3(max, min, z), new Vector3(min, min, z) });
            mesh.SetNormals(new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward });
            // vanilla uv (0, 1) at (min, max): the bottom of the image, which is v = 0 in Unity
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            mapMeshes.Add(mesh);
            var gameObject = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(mapSheet, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }

        /// <summary>Vanilla renderPlayerArm (right arm).</summary>
        private static Matrix4x4 ArmMatrix(float swing, float equip)
        {
            var root = Mathf.Sqrt(swing);
            var x = -0.3f * Mathf.Sin(root * Mathf.PI);
            var y = 0.4f * Mathf.Sin(root * Mathf.PI * 2);
            var z = -0.4f * Mathf.Sin(swing * Mathf.PI);
            var k = Mathf.Sin(swing * swing * Mathf.PI);
            var l = Mathf.Sin(root * Mathf.PI);
            return Matrix4x4.Translate(new Vector3(x + 0.64000005f, y - 0.6f + equip * -0.6f, z - 0.71999997f))
                   * Deg(EntityRenderer.RotY, 45)
                   * Deg(EntityRenderer.RotY, l * 70)
                   * Deg(EntityRenderer.RotZ, k * -20)
                   * Matrix4x4.Translate(new Vector3(-1, 3.6f, 3.5f))
                   * Deg(EntityRenderer.RotZ, 120)
                   * Deg(EntityRenderer.RotX, 200)
                   * Deg(EntityRenderer.RotY, -135)
                   * Matrix4x4.Translate(new Vector3(5.6f, 0, 0))
                   * Matrix4x4.Translate(new Vector3(-5 / 16f, 2 / 16f, 0)); // right arm pivot
        }

        /// <summary>Vanilla renderArmWithItem (default use animation): swing offset, applyItemArmTransform, applyItemArmAttackTransform.</summary>
        private static Matrix4x4 ItemMatrix(float swing, float equip)
        {
            var root = Mathf.Sqrt(swing);
            var swingOffset = new Vector3(-0.4f * Mathf.Sin(root * Mathf.PI), 0.2f * Mathf.Sin(root * Mathf.PI * 2), -0.2f * Mathf.Sin(swing * Mathf.PI));
            var f = Mathf.Sin(swing * swing * Mathf.PI);
            var g = Mathf.Sin(root * Mathf.PI);
            return Matrix4x4.Translate(swingOffset)
                   * Matrix4x4.Translate(new Vector3(0.56f, -0.52f + equip * -0.6f, -0.72f))
                   * Deg(EntityRenderer.RotY, 45 + f * -20)
                   * Deg(EntityRenderer.RotZ, g * -20)
                   * Deg(EntityRenderer.RotX, g * -80)
                   * Deg(EntityRenderer.RotY, -45);
        }

        private static Matrix4x4 Deg(Func<float, Matrix4x4> rotation, float degrees) => rotation(degrees * Mathf.Deg2Rad);

        private static void Apply(Transform transform, Matrix4x4 matrix) => EntityRenderer.Apply(transform, matrix, true);

        private void EnsureArm()
        {
            var uuid = session.Server?.Uuid ?? Guid.Empty;
            var slim = skins.IsSlim(uuid);
            armMaterial.SetTexture("_MainTex", skins.Get(uuid));
            if (arm != null && slim == armSlim) return;
            if (arm != null)
            {
                UnityEngine.Object.Destroy(arm.gameObject);
                foreach (var mesh in armMeshes) UnityEngine.Object.Destroy(mesh);
            }
            armSlim = slim;
            var model = EntityModels.Create("player", slim);
            var layer = model.Layers[0];
            var part = layer.Parts.Find(p => p.Name == "right_arm");
            var sleeve = part.Children.Count > 0 ? part.Children[0] : null;
            armMeshes = new[] { EntityRenderer.PartMesh(part, layer), sleeve != null ? EntityRenderer.PartMesh(sleeve, layer) : new Mesh() };

            arm = new GameObject("Arm", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            arm.SetParent(rig, false);
            arm.GetComponent<MeshFilter>().sharedMesh = armMeshes[0];
            armRenderer = arm.GetComponent<MeshRenderer>();
            armRenderer.sharedMaterial = armMaterial;
            armRenderer.shadowCastingMode = ShadowCastingMode.Off;
            var sleeveObject = new GameObject("Sleeve", typeof(MeshFilter), typeof(MeshRenderer));
            sleeveObject.transform.SetParent(arm, false);
            if (sleeve != null) EntityRenderer.Apply(sleeveObject.transform, Matrix4x4.Translate(sleeve.Pivot / 16f), true);
            sleeveObject.GetComponent<MeshFilter>().sharedMesh = armMeshes[1];
            sleeveRenderer = sleeveObject.GetComponent<MeshRenderer>();
            sleeveRenderer.sharedMaterial = armMaterial;
            sleeveRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        public void Dispose()
        {
            if (armMeshes != null) foreach (var mesh in armMeshes) UnityEngine.Object.Destroy(mesh);
            foreach (var mesh in mapMeshes) UnityEngine.Object.Destroy(mesh);
            if (mapBackgroundMaterial != null) UnityEngine.Object.Destroy(mapBackgroundMaterial);
            if (mapImageMaterial != null) UnityEngine.Object.Destroy(mapImageMaterial);
            UnityEngine.Object.Destroy(armMaterial);
            if (rig != null) UnityEngine.Object.Destroy(rig.gameObject); // the camera may already be destroyed on quit
        }
    }
}
