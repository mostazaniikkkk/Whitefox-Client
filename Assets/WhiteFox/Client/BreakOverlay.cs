// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Textures;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>Breaking cracks (block/destroy_stage_0..9) over the block being dug (Minosoft's BlockBreakRenderer).</summary>
    public sealed class BreakOverlay : IDisposable
    {
        private readonly GameObject gameObject;
        private readonly Mesh mesh;
        private readonly TextureEntry[] stages;
        private int currentStage = -1;

        public BreakOverlay(Transform parent, Material material, TextureEntry[] stages)
        {
            this.stages = stages;
            gameObject = new GameObject("Break overlay", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(parent, false);
            mesh = new Mesh { name = "Break overlay" };
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            gameObject.SetActive(false);
        }

        public void Update(BlockPos? position, float progress)
        {
            if (!position.HasValue || progress <= 0 || stages[0] == null)
            {
                gameObject.SetActive(false);
                return;
            }
            var stage = Mathf.Clamp((int)(progress * 10), 0, 9);
            if (stage != currentStage) Build(stage);
            gameObject.SetActive(true);
            var p = position.Value;
            gameObject.transform.localPosition = RenderOrigin.ToRender(p.X, p.Y, p.Z);
        }

        /// <summary>A cube slightly larger than the block, every face with the crack texture, fully lit.</summary>
        private void Build(int stage)
        {
            currentStage = stage;
            var layer = stages[stage].Layer;
            const float e = 0.002f;
            var vertices = new ChunkVertex[24];
            var indices = new int[36];
            var corners = new[]
            {
                // render space (z negated): each face as top-left, bottom-left, bottom-right, top-right seen from outside
                new[] { new Vector3(0, 1, 0), new Vector3(0, 1, -1), new Vector3(1, 1, -1), new Vector3(1, 1, 0) }, // up
                new[] { new Vector3(0, 0, -1), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, -1) }, // down
                new[] { new Vector3(1, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 1, 0) }, // north
                new[] { new Vector3(0, 1, -1), new Vector3(0, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 1, -1) }, // south
                new[] { new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, -1) }, // west
                new[] { new Vector3(1, 1, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 0), new Vector3(1, 1, 0) }, // east
            };
            float[] us = { 0, 0, 1, 1 };
            float[] vs = { 1, 0, 0, 1 };
            for (var face = 0; face < 6; face++)
            {
                for (var i = 0; i < 4; i++)
                {
                    var position = corners[face][i] + (corners[face][i] - new Vector3(0.5f, 0.5f, -0.5f)) * (2 * e);
                    vertices[face * 4 + i] = new ChunkVertex
                    {
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        Color = 0xFFFFFFFF,
                        U = us[i],
                        V = vs[i],
                        Layer = layer,
                        AO = 1,
                        BlockLight = 1,
                        SkyLight = 1,
                    };
                }
                var first = face * 4;
                // the corners are counter clockwise on screen, Unity front faces are clockwise
                int[] order = { 0, 2, 1, 0, 3, 2 };
                for (var i = 0; i < 6; i++) indices[face * 6 + i] = first + order[i];
            }
            mesh.Clear();
            mesh.SetVertexBufferParams(vertices.Length, WorldRenderer.Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, vertices.Length);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length));
            mesh.bounds = new Bounds(new Vector3(0.5f, 0.5f, -0.5f), Vector3.one * 1.1f);
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(gameObject);
        }
    }
}
