// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Game;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>Black wireframe around the targeted block (Minosoft's BlockOutlineRenderer).</summary>
    public sealed class BlockOutline : System.IDisposable
    {
        private readonly GameObject gameObject;
        private readonly Mesh mesh;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<int> indices = new List<int>();
        private BlockPos? current;
        private object currentState;

        public BlockOutline(Transform parent)
        {
            gameObject = new GameObject("Block outline", typeof(MeshFilter), typeof(MeshRenderer));
            gameObject.transform.SetParent(parent, false);
            mesh = new Mesh { name = "Block outline" };
            mesh.MarkDynamic();
            gameObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("Hidden/Internal-Colored")) { color = new Color(0, 0, 0, 0.45f) };
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.renderQueue = (int)RenderQueue.Transparent;
            var renderer = gameObject.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(gameObject);
        }

        public void Update(BlockHit? hit)
        {
            if (!hit.HasValue)
            {
                gameObject.SetActive(false);
                current = null;
                return;
            }
            gameObject.SetActive(true);
            var value = hit.Value;
            gameObject.transform.localPosition = RenderOrigin.ToRender(value.Position.X, value.Position.Y, value.Position.Z);
            if (current == value.Position && currentState == value.State) return;
            current = value.Position;
            currentState = value.State;

            vertices.Clear();
            indices.Clear();
            foreach (var box in value.State.OutlineShape) AddBox(box.Grow(0.002));
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
        }

        private void AddBox(AABB box)
        {
            var first = vertices.Count;
            for (var i = 0; i < 8; i++)
            {
                var x = (i & 1) == 0 ? box.Min.X : box.Max.X;
                var y = (i & 2) == 0 ? box.Min.Y : box.Max.Y;
                var z = (i & 4) == 0 ? box.Min.Z : box.Max.Z;
                vertices.Add(new Vector3((float)x, (float)y, (float)-z));
            }
            int[] edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
            foreach (var edge in edges) indices.Add(first + edge);
        }
    }
}
