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
    /// Display entities (vanilla DisplayRenderer and its block, item and text renderers), used a lot by server plugins
    /// for holograms and decorations: the entity position, the billboard or entity rotation, then the interpolated
    /// transformation (translation, left rotation, scale, right rotation) and what the display shows.
    /// </summary>
    public sealed partial class EntityRenderer
    {
        /// <summary>The font for text displays (set by the client).</summary>
        public GuiFont Font;

        private Shader textShader;

        private static readonly ItemDisplay[] ItemContexts =
        {
            ItemDisplay.None, ItemDisplay.ThirdPersonLeftHand, ItemDisplay.ThirdPersonRightHand, ItemDisplay.FirstPersonLeftHand,
            ItemDisplay.FirstPersonRightHand, ItemDisplay.Head, ItemDisplay.Gui, ItemDisplay.Ground, ItemDisplay.Fixed,
        };

        private void UpdateDisplay(SimpleView view, Entity entity, Vec3d position, float partial)
        {
            var display = entity.Display;
            if (display == null)
            {
                view.Root.SetActive(false);
                return;
            }

            // vanilla DisplayRenderer.calculateOrientation; the camera angles as vanilla's Camera has them
            var yaw = MathUtil.LerpYaw(entity.PreviousYaw, entity.Yaw, partial);
            var pitch = Mathf.Lerp(entity.PreviousPitch, entity.Pitch, partial);
            float cameraYaw = 0, cameraPitch = 0;
            var camera = Camera.main;
            if (camera != null)
            {
                var forward = camera.transform.forward;
                forward.z = -forward.z; // render space to minecraft space
                cameraYaw = Mathf.Atan2(-forward.x, forward.z) * Mathf.Rad2Deg;
                cameraPitch = Mathf.Asin(Mathf.Clamp(-forward.y, -1, 1)) * Mathf.Rad2Deg;
            }
            float orientationYaw = yaw, orientationPitch = pitch;
            switch (display.Billboard)
            {
                case 1: // vertical: turns around y towards the camera
                    orientationYaw = cameraYaw - 180;
                    break;
                case 2: // horizontal: tilts towards the camera
                    orientationPitch = -cameraPitch;
                    break;
                case 3: // center: faces the camera
                    orientationYaw = cameraYaw - 180;
                    orientationPitch = -cameraPitch;
                    break;
            }
            var transform = display.Shown(session.Ticks, partial);
            var matrix = Matrix4x4.Translate(RenderOrigin.Relative(position.X, position.Y, position.Z))
                         * RotY(-orientationYaw * Mathf.Deg2Rad) * RotX(orientationPitch * Mathf.Deg2Rad)
                         * Matrix4x4.Translate(ToVector(transform.Translation)) * Rotation(transform.Left)
                         * Matrix4x4.Scale(ToVector(transform.Scale)) * Rotation(transform.Right);

            // brightness override: packed block << 4 | sky << 20
            if (display.Brightness >= 0)
            {
                properties.SetVector(LightId, new Vector4(((display.Brightness >> 4) & 0x0F) / 15f, ((display.Brightness >> 20) & 0x0F) / 15f, 0, 0));
            }

            switch (view.Kind)
            {
                case "block_display":
                    UpdateBlockDisplay(view, display);
                    Apply(view.Root.transform, matrix);
                    break;
                case "item_display":
                {
                    var name = ItemModels.Key(display.Item);
                    if (name != view.ItemName)
                    {
                        view.ItemName = name;
                        SetItem(view.Root.transform, ref view.ItemTransform, view.Renderers, name);
                        view.ItemModel = items.Get(name?.TrimEnd('*'));
                    }
                    // ItemDisplayRenderer: turned half around, then the item's display transform of the context
                    Apply(view.Root.transform, matrix * RotY(Mathf.PI));
                    if (view.ItemTransform != null && view.ItemModel != null)
                    {
                        var context = ItemContexts[Mathf.Clamp(display.ItemContext, 0, ItemContexts.Length - 1)];
                        Apply(view.ItemTransform, ItemModels.DisplayMatrix(view.ItemModel, context), true);
                    }
                    break;
                }
                case "text_display":
                    if (view.Variant != display.TextVersion || view.Renderers.Count == 0) BuildText(view, display);
                    Apply(view.Root.transform, matrix);
                    break;
            }
        }

        private static Vector3 ToVector(Vec3d value) => new Vector3((float)value.X, (float)value.Y, (float)value.Z);

        /// <summary>A quaternion of the entity data (x, y, z, w) as a minecraft space rotation.</summary>
        private static Matrix4x4 Rotation(float[] quaternion)
        {
            if (quaternion == null || quaternion.Length < 4) return Matrix4x4.identity;
            var q = new Quaternion(quaternion[0], quaternion[1], quaternion[2], quaternion[3]);
            var length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (length < 1e-6f) return Matrix4x4.identity;
            q = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
            return Matrix4x4.Rotate(q);
        }

        /// <summary>BlockDisplayRenderer: the block model from the corner at the entity position (not centered).</summary>
        private void UpdateBlockDisplay(SimpleView view, DisplayData display)
        {
            var state = display.Block;
            var key = state?.Id ?? -1;
            if (view.Variant == key && view.Pieces.Count > 0) return;
            view.Variant = key;
            foreach (var piece in view.Pieces)
            {
                foreach (var old in piece.GetComponentsInChildren<Renderer>(true)) view.Renderers.Remove(old);
                Object.Destroy(piece.gameObject);
            }
            view.Pieces.Clear();
            foreach (var oldMesh in view.OwnedMeshes) Object.Destroy(oldMesh);
            view.OwnedMeshes.Clear();
            var block = new GameObject("Block", typeof(MeshFilter), typeof(MeshRenderer));
            block.transform.SetParent(view.Root.transform, false);
            view.Pieces.Add(block.transform);
            if (!(state?.Model is BlockRenderModel render) || render.Models[0].Quads.Length == 0) return;
            var built = ItemIcons.BuildMesh(state.Block, render.Models[0]);
            view.OwnedMeshes.Add(built);
            block.GetComponent<MeshFilter>().sharedMesh = built;
            var renderer = block.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = blockMaterial;
            view.Renderers.Add(renderer);
        }

        /// <summary>
        /// TextDisplayRenderer: the lines wrapped to the line width, aligned, on the background color, drawn into a
        /// texture; 1/40 block per pixel, the bottom of the text at the entity position, readable from +z.
        /// </summary>
        private void BuildText(SimpleView view, DisplayData display)
        {
            view.Variant = display.TextVersion;
            foreach (var piece in view.Pieces)
            {
                foreach (var old in piece.GetComponentsInChildren<Renderer>(true)) view.Renderers.Remove(old);
                Object.Destroy(piece.gameObject);
            }
            view.Pieces.Clear();
            foreach (var oldMesh in view.OwnedMeshes) Object.Destroy(oldMesh);
            view.OwnedMeshes.Clear();
            foreach (var owned in view.OwnedMaterials)
            {
                if (owned.mainTexture != null) Object.Destroy(owned.mainTexture);
                Object.Destroy(owned);
            }
            view.OwnedMaterials.Clear();
            if (Font == null) return;

            var lines = Font.Wrap(display.Text ?? "", Mathf.Max(1, display.LineWidth));
            var textWidth = 0;
            foreach (var line in lines) textWidth = Mathf.Max(textWidth, Mathf.CeilToInt(Font.Width(line)));
            var lineHeight = (int)GuiFont.LineHeight;
            int width = textWidth + 2, height = lines.Length * lineHeight + 1;
            if (width > 2048 || height > 2048) return;

            // background (flag 0x04: the default background of chat and name tags), then shadow and text
            var pixels = new Color32[width * height];
            var background = (display.TextFlags & 0x04) != 0 ? 0x40000000 : display.Background;
            var backgroundColor = new Color32((byte)((background >> 16) & 0xFF), (byte)((background >> 8) & 0xFF), (byte)(background & 0xFF), (byte)((background >> 24) & 0xFF));
            if (backgroundColor.a > 0)
            {
                for (var i = 0; i < pixels.Length; i++) pixels[i] = backgroundColor;
            }
            var opacity = (byte)(display.TextOpacity & 0xFF);
            if (display.TextOpacity == -1) opacity = 255;
            var shadow = (display.TextFlags & 0x01) != 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var lineWidth = Font.Width(lines[i]);
                var x = (display.TextFlags & 0x08) != 0 ? 1 : (display.TextFlags & 0x10) != 0 ? width - 1 - lineWidth : (width - lineWidth) / 2;
                var y = 1 + i * lineHeight;
                if (shadow) Font.Rasterize(lines[i], pixels, width, height, x + 1, y + 1, new Color32(0x3F, 0x3F, 0x3F, opacity), true);
                Font.Rasterize(lines[i], pixels, width, height, x, y, new Color32(0xFF, 0xFF, 0xFF, opacity));
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Text display" };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            if (textShader == null) textShader = Resources.Load<Shader>("WhiteFox/TextDisplay");
            var material = new Material(textShader) { name = "Text display" };
            material.SetTexture("_MainTex", texture);
            // see through (flag 0x02): drawn over blocks, like vanilla's see-through text
            material.SetFloat("_ZTest", (display.TextFlags & 0x02) != 0 ? (float)CompareFunction.Always : (float)CompareFunction.LessEqual);
            view.OwnedMaterials.Add(material);

            // the quad in minecraft display space: x centered, y from 0 up, facing +z
            const float scale = 0.025f;
            float halfWidth = width / 2f * scale, top = height * scale;
            var mesh = new Mesh { name = "Text display" };
            // render space (z negated); the parent matrix is conjugated by Apply
            mesh.SetVertices(new List<Vector3> { new Vector3(-halfWidth, 0, 0), new Vector3(halfWidth, 0, 0), new Vector3(halfWidth, top, 0), new Vector3(-halfWidth, top, 0) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            view.OwnedMeshes.Add(mesh);
            var gameObject = new GameObject("Text", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(view.Root.transform, false);
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            view.Renderers.Add(renderer);
            view.Pieces.Add(gameObject.transform);
        }
    }
}
