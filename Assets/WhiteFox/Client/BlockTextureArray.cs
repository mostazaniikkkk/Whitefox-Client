// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Textures;

namespace WhiteFox.Client
{
    /// <summary>
    /// GPU side of <see cref="TextureManager"/> (Minosoft's StaticTextureArray + SpriteAnimator): one Texture2DArray with
    /// every block texture, plus a second array with all animation frames that are copied into place on the GPU.
    /// </summary>
    public sealed class BlockTextureArray : IDisposable
    {
        public readonly Texture2DArray Texture;
        /// <summary>LabPBR normal maps (linear).</summary>
        public readonly Texture2DArray Normals;
        /// <summary>LabPBR specular maps (linear).</summary>
        public readonly Texture2DArray Specular;
        private readonly Texture2DArray frames;
        private readonly TextureManager manager;
        private readonly int[] currentFrames;
        private readonly int mipCount;
        private readonly bool gpuCopy;

        public BlockTextureArray(TextureManager manager)
        {
            this.manager = manager;
            var size = manager.Size;
            mipCount = Math.Min(5, (int)Math.Log(size, 2) + 1);
            gpuCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;

            Texture = Create(size, manager.Layers.Count, "WhiteFox block textures");
            for (var i = 0; i < manager.Layers.Count; i++) Texture.SetPixelData(FlipRows(manager.Layers[i], size), 0, i);
            Texture.Apply(true, false);

            Normals = Create(size, manager.NormalLayers.Count, "WhiteFox normal maps", true);
            for (var i = 0; i < manager.NormalLayers.Count; i++) Normals.SetPixelData(FlipRows(manager.NormalLayers[i], size), 0, i);
            Normals.Apply(true, true);
            Specular = Create(size, manager.SpecularLayers.Count, "WhiteFox specular maps", true);
            for (var i = 0; i < manager.SpecularLayers.Count; i++) Specular.SetPixelData(FlipRows(manager.SpecularLayers[i], size), 0, i);
            Specular.Apply(true, true);

            if (manager.Frames.Count > 0)
            {
                frames = Create(size, manager.Frames.Count, "WhiteFox animation frames");
                for (var i = 0; i < manager.Frames.Count; i++) frames.SetPixelData(FlipRows(manager.Frames[i], size), 0, i);
                frames.Apply(true, !gpuCopy);
            }

            currentFrames = new int[manager.Animations.Count];
        }

        private Texture2DArray Create(int size, int depth, string name, bool linear = false)
        {
            return new Texture2DArray(size, size, Math.Max(1, depth), TextureFormat.RGBA32, mipCount, linear)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };
        }

        /// <summary>The core stores rows top first (png order), Unity expects the bottom row first.</summary>
        private static byte[] FlipRows(byte[] pixels, int size)
        {
            var result = new byte[pixels.Length];
            var stride = size * 4;
            for (var y = 0; y < size; y++) Buffer.BlockCopy(pixels, y * stride, result, (size - 1 - y) * stride, stride);
            return result;
        }

        /// <summary>Advances animated textures to the given game tick.</summary>
        public void Animate(long tick)
        {
            if (frames == null) return;
            var changed = false;
            for (var i = 0; i < manager.Animations.Count; i++)
            {
                var animation = manager.Animations[i];
                var frame = animation.FrameAt(tick);
                if (frame == currentFrames[i]) continue;
                currentFrames[i] = frame;
                var source = animation.FrameLayers[frame];
                if (gpuCopy)
                {
                    for (var mip = 0; mip < mipCount; mip++) Graphics.CopyTexture(frames, source, mip, Texture, animation.Layer, mip);
                }
                else
                {
                    Texture.SetPixelData(FlipRows(manager.Frames[source], manager.Size), 0, animation.Layer);
                    changed = true;
                }
            }
            if (changed) Texture.Apply(true, false);
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(Texture);
            UnityEngine.Object.Destroy(Normals);
            UnityEngine.Object.Destroy(Specular);
            if (frames != null) UnityEngine.Object.Destroy(frames);
        }
    }
}
