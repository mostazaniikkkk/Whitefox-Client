// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Entities;
using WhiteFox.Core.Models;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Entities
{
    /// <summary>
    /// Non-living entities drawn by their own vanilla renderers: primed TNT, end crystals, thrown tridents, wither
    /// skulls, shulker bullets, llama spit, dragon fireballs, fishing bobbers with their line, leash knots and the leads
    /// of leashed mobs, evoker fangs.
    /// </summary>
    public sealed partial class EntityRenderer
    {
        public static readonly HashSet<string> ObjectKinds = new HashSet<string>
        {
            "tnt", "end_crystal", "trident", "wither_skull", "shulker_bullet", "llama_spit", "dragon_fireball", "fishing_bobber", "leash_knot", "evoker_fangs",
            "wind_charge",
        };

        private readonly Dictionary<Color, Material> lineMaterials = new Dictionary<Color, Material>();
        private readonly Dictionary<int, LineRenderer> leads = new Dictionary<int, LineRenderer>();

        /// <summary>A model part as a child object of a simple view (the matrices are set every frame).</summary>
        private Transform Piece(SimpleView view, ModelLayer layer, ModelPart part, Material material)
        {
            var gameObject = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            var mesh = PartMesh(part, layer);
            view.OwnedMeshes.Add(mesh);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            view.Renderers.Add(renderer);
            view.Pieces.Add(gameObject.transform);
            return gameObject.transform;
        }

        private static ModelLayer ObjectLayer(string texture, int width, int height) =>
            new ModelLayer { Texture = "assets/minecraft/textures/" + texture + ".png", TextureWidth = width, TextureHeight = height };

        /// <summary>A camera facing square (dragon fireball, fishing hook), 1 block, centered.</summary>
        private void Billboard(SimpleView view, string texture, bool fullbright)
        {
            var mesh = new Mesh { name = "Billboard" };
            mesh.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) });
            mesh.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            mesh.RecalculateBounds();
            view.OwnedMeshes.Add(mesh);
            var gameObject = new GameObject("Billboard", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            var material = MaterialFor("assets/minecraft/textures/" + texture + ".png");
            if (fullbright)
            {
                material = new Material(material) { name = texture + " fullbright" };
                material.SetFloat("_Fullbright", 1);
                view.OwnedMaterials.Add(material);
            }
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            view.Renderers.Add(renderer);
            view.Pieces.Add(gameObject.transform);
        }

        /// <summary>A line (the entity shader ignores vertex colors, so every color has its own tinted material).</summary>
        private LineRenderer Line(Transform parent, Color color, float width)
        {
            if (!lineMaterials.TryGetValue(color, out var material))
            {
                material = new Material(shader) { name = "Line " + color };
                material.SetTexture("_MainTex", Texture2D.whiteTexture);
                material.SetColor("_Tint", color);
                lineMaterials[color] = material;
            }
            var line = new GameObject("Line").AddComponent<LineRenderer>();
            line.transform.SetParent(parent, false);
            line.sharedMaterial = material;
            line.widthMultiplier = width;
            line.startColor = line.endColor = color;
            line.useWorldSpace = true;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.numCapVertices = 0;
            line.generateLightingData = true; // normals for the entity shader
            return line;
        }

        private SimpleView CreateObject(Entity entity, string path, GameObject rootObject)
        {
            var view = new SimpleView { Root = rootObject, Kind = path };
            switch (path)
            {
                case "end_crystal":
                {
                    var layer = ObjectLayer("entity/end_crystal/end_crystal", 64, 32);
                    var material = MaterialFor(layer.Texture);
                    Piece(view, layer, new ModelPart("base", 0, 0, 0).Box(0, 16, -6, 0, -6, 12, 4, 12), material);
                    Piece(view, layer, new ModelPart("glass", 0, 0, 0).Box(0, 0, -4, -4, -4, 8, 8, 8), material);
                    Piece(view, layer, new ModelPart("glass", 0, 0, 0).Box(0, 0, -4, -4, -4, 8, 8, 8), material);
                    Piece(view, layer, new ModelPart("cube", 0, 0, 0).Box(32, 0, -4, -4, -4, 8, 8, 8), material);
                    break;
                }
                case "trident":
                {
                    var layer = ObjectLayer("entity/trident", 32, 32);
                    var pole = new ModelPart("pole", 0, 0, 0).Box(0, 6, -0.5f, 2, -0.5f, 1, 25, 1).Box(4, 0, -1.5f, 0, -0.5f, 3, 2, 1)
                        .Box(4, 3, -2.5f, -3, -0.5f, 1, 4, 1).Box(0, 0, -0.5f, -4, -0.5f, 1, 4, 1).Box(4, 3, 1.5f, -3, -0.5f, 1, 4, 1, 0, true);
                    Piece(view, layer, pole, MaterialFor(layer.Texture));
                    break;
                }
                case "wither_skull":
                {
                    var layer = ObjectLayer(entity.Dangerous ? "entity/wither/wither_invulnerable" : "entity/wither/wither", 64, 64);
                    Piece(view, layer, new ModelPart("head", 0, 0, 0).Box(0, 35, -4, -8, -4, 8, 8, 8), MaterialFor(layer.Texture));
                    view.ItemName = entity.Dangerous ? "dangerous" : "";
                    break;
                }
                case "shulker_bullet":
                {
                    var layer = ObjectLayer("entity/shulker/spark", 64, 32);
                    Piece(view, layer, new ModelPart("main", 0, 0, 0).Box(0, 0, -4, -4, -1, 8, 8, 2).Box(0, 10, -1, -4, -4, 2, 8, 8).Box(20, 0, -4, -1, -4, 8, 2, 8), MaterialFor(layer.Texture));
                    break;
                }
                case "llama_spit":
                {
                    var layer = ObjectLayer("entity/llama/spit", 64, 32);
                    Piece(view, layer, new ModelPart("main", 0, 0, 0).Box(0, 0, -4, 0, 0, 2, 2, 2).Box(0, 0, 0, -4, 0, 2, 2, 2).Box(0, 0, 0, 0, -4, 2, 2, 2)
                        .Box(0, 0, 0, 0, 0, 2, 2, 2).Box(0, 0, 2, 0, 0, 2, 2, 2).Box(0, 0, 0, 2, 0, 2, 2, 2).Box(0, 0, 0, 0, 2, 2, 2, 2), MaterialFor(layer.Texture));
                    break;
                }
                case "leash_knot":
                {
                    var layer = ObjectLayer("entity/lead_knot", 32, 32);
                    Piece(view, layer, new ModelPart("knot", 0, 0, 0).Box(0, 0, -3, -8, -3, 6, 8, 6), MaterialFor(layer.Texture));
                    break;
                }
                case "evoker_fangs":
                {
                    var layer = ObjectLayer("entity/illager/evoker_fangs", 64, 32);
                    var material = MaterialFor(layer.Texture);
                    Piece(view, layer, new ModelPart("base", 0, 0, 0).Box(0, 0, 0, 0, 0, 10, 12, 10), material);
                    Piece(view, layer, new ModelPart("upper_jaw", 0, 0, 0).Box(40, 0, 0, 0, 0, 4, 14, 8), material);
                    Piece(view, layer, new ModelPart("lower_jaw", 0, 0, 0).Box(40, 0, 0, 0, 0, 4, 14, 8), material);
                    break;
                }
                case "dragon_fireball":
                    Billboard(view, "entity/enderdragon/dragon_fireball", true);
                    break;
                case "wind_charge":
                {
                    // WindChargeModel: the swirl (a flat and a tall box, turned 45 degrees) around the 4x4x4 charge
                    var layer = ObjectLayer("entity/projectiles/wind_charge", 64, 32);
                    var material = MaterialFor(layer.Texture);
                    Piece(view, layer, new ModelPart("wind", 0, 0, 0).Box(15, 20, -4, -1, -4, 8, 2, 8).Box(0, 9, -3, -2, -3, 6, 4, 6), material);
                    Piece(view, layer, new ModelPart("wind_charge", 0, 0, 0).Box(0, 0, -2, -2, -2, 4, 4, 4), material);
                    break;
                }
                case "fishing_bobber":
                    Billboard(view, "entity/fishing_hook", false);
                    view.Line = Line(view.Root.transform, Color.black, 0.025f);
                    break;
            }
            return view;
        }

        private static Matrix4x4 Deg(System.Func<float, Matrix4x4> rotation, float degrees) => rotation(degrees * Mathf.Deg2Rad);

        private static Matrix4x4 AxisAngle(Vector3 axis, float degrees)
        {
            // Minecraft space rotation; Apply conjugates it into render space
            return Matrix4x4.Rotate(Quaternion.AngleAxis(degrees, axis.normalized));
        }

        /// <summary>The per-frame matrices of the object renderers (vanilla *Renderer.render).</summary>
        private void UpdateObject(SimpleView view, Entity entity, Vec3d position, float partial)
        {
            var origin = Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z));
            var yaw = MathUtil.LerpYaw(entity.PreviousYaw, entity.Yaw, partial);
            var pitch = Mathf.Lerp(entity.PreviousPitch, entity.Pitch, partial);
            var age = entity.Age + partial;
            switch (view.Kind)
            {
                case "tnt":
                {
                    // TntRenderer: the block, swelling during the last 10 ticks
                    if (view.Renderers.Count == 0)
                    {
                        var state = entity.FallingBlock ?? session.Resources.Blocks.Get("tnt")?.DefaultState;
                        if (state?.Model is BlockRenderModel render && render.Models[0].Quads.Length > 0)
                        {
                            var block = new GameObject("Block", typeof(MeshFilter), typeof(MeshRenderer));
                            block.transform.SetParent(view.Root.transform, false);
                            block.transform.localPosition = new Vector3(-0.5f, -0.5f, 0.5f);
                            var mesh = ItemIcons.BuildMesh(state.Block, render.Models[0]);
                            view.OwnedMeshes.Add(mesh);
                            block.GetComponent<MeshFilter>().sharedMesh = mesh;
                            var renderer = block.GetComponent<MeshRenderer>();
                            renderer.sharedMaterial = blockMaterial;
                            view.Renderers.Add(renderer);
                        }
                    }
                    var left = entity.Fuse - partial + 1;
                    var scale = 1f;
                    if (left < 10)
                    {
                        var f = Mathf.Clamp01(1 - left / 10);
                        f *= f;
                        f *= f;
                        scale = 1 + f * 0.3f;
                    }
                    view.Root.transform.localPosition = RenderOrigin.ToRender(position.X, position.Y + 0.5, position.Z);
                    view.Root.transform.localScale = Vector3.one * scale;
                    break;
                }
                case "end_crystal":
                {
                    // EndCrystalRenderer: the base, then two glass shells and the core turning and floating above it
                    var spin = age * 3;
                    var bob = Mathf.Sin(age * 0.2f) / 2 + 0.5f;
                    bob = (bob * bob + bob) * 0.4f - 1.4f;
                    var axis = new Vector3(0.70710677f, 0, 0.70710677f);
                    var root = origin * Matrix4x4.Scale(Vector3.one * 2) * Matrix4x4.Translate(new Vector3(0, -0.5f, 0));
                    view.Pieces[0].gameObject.SetActive(entity.ShowBottom);
                    Apply(view.Pieces[0], root);
                    var glass = root * Deg(RotY, spin) * Matrix4x4.Translate(new Vector3(0, 1.5f + bob / 2, 0)) * AxisAngle(axis, 60);
                    Apply(view.Pieces[1], glass);
                    var inner = glass * Matrix4x4.Scale(Vector3.one * 0.875f) * AxisAngle(axis, 60) * Deg(RotY, spin);
                    Apply(view.Pieces[2], inner);
                    Apply(view.Pieces[3], inner * Matrix4x4.Scale(Vector3.one * 0.875f) * AxisAngle(axis, 60) * Deg(RotY, spin));
                    break;
                }
                case "trident":
                    // ThrownTridentRenderer: yaw - 90 around y, pitch + 90 around z
                    Apply(view.Pieces[0], origin * Deg(RotY, yaw - 90) * Deg(RotZ, pitch + 90));
                    break;
                case "wither_skull":
                    if (view.ItemName != (entity.Dangerous ? "dangerous" : ""))
                    {
                        var dangerous = entity.Dangerous;
                        view.ItemName = dangerous ? "dangerous" : "";
                        foreach (var renderer in view.Renderers) renderer.sharedMaterial = MaterialFor("assets/minecraft/textures/" + (dangerous ? "entity/wither/wither_invulnerable" : "entity/wither/wither") + ".png");
                    }
                    // WitherSkullRenderer: scale(-1, -1, 1), the head turned by yaw and pitch
                    Apply(view.Pieces[0], origin * Matrix4x4.Scale(new Vector3(-1, -1, 1)) * Deg(RotY, yaw) * Deg(RotX, pitch));
                    break;
                case "shulker_bullet":
                    Apply(view.Pieces[0], origin * Matrix4x4.Translate(new Vector3(0, 0.15f, 0)) * Deg(RotY, Mathf.Sin(age * 0.1f) * 180) * Deg(RotX, Mathf.Cos(age * 0.1f) * 180)
                                          * Deg(RotZ, Mathf.Sin(age * 0.15f) * 360) * Matrix4x4.Scale(new Vector3(-0.5f, -0.5f, 0.5f)));
                    break;
                case "llama_spit":
                    Apply(view.Pieces[0], origin * Matrix4x4.Translate(new Vector3(0, 0.15f, 0)) * Deg(RotY, yaw - 90) * Deg(RotZ, pitch));
                    break;
                case "leash_knot":
                    Apply(view.Pieces[0], origin * Matrix4x4.Scale(new Vector3(-1, -1, 1)));
                    break;
                case "evoker_fangs":
                {
                    // EvokerFangsRenderer: nothing until the bite starts, then the jaws snap up out of the ground and close
                    var progress = 0f;
                    if (entity.AttackStart >= 0) progress = Mathf.Clamp01((entity.Age - entity.AttackStart + partial) / 20f);
                    foreach (var piece in view.Pieces)
                    {
                        if (piece.gameObject.activeSelf != progress > 0) piece.gameObject.SetActive(progress > 0);
                    }
                    if (progress <= 0) break;
                    var size = progress > 0.9f ? 2 * (1 - progress) / 0.1f : 2;
                    var root = origin * Deg(RotY, 90 - entity.Yaw) * Matrix4x4.Scale(new Vector3(-size, -size, size)) * Matrix4x4.Translate(new Vector3(0, -0.626f, 0)) * Matrix4x4.Scale(Vector3.one * 0.5f);
                    var f = Mathf.Min(1, progress * 2);
                    f = 1 - f * f * f;
                    var rise = (progress + Mathf.Sin(progress * 2.7f)) * 0.6f * 12;
                    var y = (24 - rise) / 16;
                    Apply(view.Pieces[0], root * Matrix4x4.Translate(new Vector3(-5 / 16f, y, -5 / 16f)));
                    Apply(view.Pieces[1], root * Matrix4x4.Translate(new Vector3(1.5f / 16, y, -4 / 16f)) * RotZ(Mathf.PI - f * 0.35f * Mathf.PI));
                    Apply(view.Pieces[2], root * Matrix4x4.Translate(new Vector3(-1.5f / 16, y, 4 / 16f)) * RotZ(Mathf.PI + f * 0.35f * Mathf.PI) * RotY(Mathf.PI));
                    break;
                }
                case "wind_charge":
                {
                    // WindChargeModel.setupAnim: the swirl and the charge spin in opposite directions, 16 degrees a tick
                    var spin = age * 16;
                    Apply(view.Pieces[0], origin * Deg(RotY, -45 + spin));
                    Apply(view.Pieces[1], origin * Deg(RotY, -spin));
                    break;
                }
                case "dragon_fireball":
                case "fishing_bobber":
                {
                    var camera = Camera.main;
                    var piece = view.Pieces[0];
                    piece.localPosition = RenderOrigin.ToRender(position.X, position.Y + (view.Kind == "fishing_bobber" ? 0.25 : 0), position.Z);
                    if (camera != null) piece.rotation = camera.transform.rotation;
                    piece.localScale = Vector3.one * (view.Kind == "dragon_fireball" ? 2 : 0.5f);
                    if (view.Line != null) UpdateFishingLine(view, entity, position, partial);
                    break;
                }
            }
        }

        /// <summary>
        /// FishingHookRenderer: 16 line segments from the rod in the owner's hand to the bobber, sagging like vanilla
        /// (y follows (f² + f) / 2 of the height difference).
        /// </summary>
        private void UpdateFishingLine(SimpleView view, Entity bobber, Vec3d position, float partial)
        {
            Vec3d hand;
            if (session.Server != null && bobber.Data == session.Server.EntityId)
            {
                var player = session.Player;
                var yaw = player.Yaw * Mathf.Deg2Rad;
                var eye = player.EyePosition;
                // in front of the eyes, to the right and a little down: where the rod tip is in first person
                hand = new Vec3d(eye.X - Mathf.Sin(yaw) * 0.6 - Mathf.Cos(yaw) * 0.35, eye.Y - 0.3, eye.Z + Mathf.Cos(yaw) * 0.6 - Mathf.Sin(yaw) * 0.35);
            }
            else if (session.Entities.Entities.TryGetValue(bobber.Data, out var owner))
            {
                var p = owner.Interpolated(partial);
                var yaw = MathUtil.LerpYaw(owner.PreviousBodyYaw, owner.BodyYaw, partial) * Mathf.Deg2Rad;
                hand = new Vec3d(p.X - Mathf.Cos(yaw) * 0.35 - Mathf.Sin(yaw) * 0.8, p.Y + owner.Height * 0.85 - 0.45, p.Z - Mathf.Sin(yaw) * 0.35 + Mathf.Cos(yaw) * 0.8);
            }
            else
            {
                view.Line.positionCount = 0;
                return;
            }
            const int segments = 16;
            view.Line.positionCount = segments + 1;
            var start = new Vec3d(position.X, position.Y + 0.25, position.Z);
            for (var i = 0; i <= segments; i++)
            {
                var f = i / (float)segments;
                var point = new Vec3d(start.X + (hand.X - start.X) * f, start.Y + (hand.Y - start.Y) * (f * f + f) * 0.5, start.Z + (hand.Z - start.Z) * f);
                view.Line.SetPosition(i, RenderOrigin.ToRender(point.X, point.Y, point.Z));
            }
        }

        /// <summary>
        /// Leads (vanilla MobRenderer.renderLeash): 24 segments from the mob to its holder (a leash knot or a player's
        /// hand), hanging down in the middle, in the two browns of the lead.
        /// </summary>
        private void UpdateLeads(float partial)
        {
            var seen = new HashSet<int>();
            foreach (var entity in session.Entities.Entities.Values)
            {
                if (entity.LeashHolder < 0) continue;
                Vec3d holderPosition;
                if (session.Server != null && entity.LeashHolder == session.Server.EntityId)
                {
                    var player = session.Player;
                    var yaw = player.Yaw * Mathf.Deg2Rad;
                    holderPosition = new Vec3d(player.Position.X - Mathf.Cos(yaw) * 0.35, player.Position.Y + 1.0, player.Position.Z - Mathf.Sin(yaw) * 0.35);
                }
                else if (session.Entities.Entities.TryGetValue(entity.LeashHolder, out var holder))
                {
                    var p = holder.Interpolated(partial);
                    var knot = holder.Type?.Name.Path == "leash_knot";
                    holderPosition = new Vec3d(p.X, p.Y + (knot ? 0.3 : holder.Height * 0.55), p.Z);
                }
                else
                {
                    continue;
                }
                seen.Add(entity.Id);
                if (!leads.TryGetValue(entity.Id, out var line) || line == null)
                {
                    line = Line(root, new Color(0.5f, 0.4f, 0.3f), 0.05f);
                    leads[entity.Id] = line;
                }
                var mob = entity.Interpolated(partial);
                var start = new Vec3d(mob.X, mob.Y + entity.Height * 0.7, mob.Z);
                const int segments = 24;
                line.positionCount = segments + 1;
                var dy = holderPosition.Y - start.Y;
                for (var i = 0; i <= segments; i++)
                {
                    var f = i / (float)segments;
                    var y = dy > 0 ? dy * f * f : dy - dy * (1 - f) * (1 - f);
                    var point = new Vec3d(start.X + (holderPosition.X - start.X) * f, start.Y + y, start.Z + (holderPosition.Z - start.Z) * f);
                    line.SetPosition(i, RenderOrigin.ToRender(point.X, point.Y, point.Z));
                }
            }
            var gone = new List<int>();
            foreach (var pair in leads)
            {
                if (seen.Contains(pair.Key)) continue;
                if (pair.Value != null) Object.Destroy(pair.Value.gameObject);
                gone.Add(pair.Key);
            }
            foreach (var id in gone) leads.Remove(id);
        }
    }
}
