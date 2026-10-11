// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WhiteFox.Client
{
    /// <summary>
    /// Renders the volumetric clouds at half resolution into a texture before the camera draws (CloudPass.shader), for
    /// the realistic sky and the enhanced clouds. The ray march is the expensive part of the sky; at half resolution it
    /// costs about a quarter, and soft clouds look the same.
    /// </summary>
    public sealed class CloudPass : IDisposable
    {
        private static readonly int TextureId = Shader.PropertyToID("_WF_CloudTexture");
        private static readonly int LowResId = Shader.PropertyToID("_WF_CloudLowRes");
        private static readonly int ViewProjectionId = Shader.PropertyToID("_WF_CloudViewProjection");
        private static readonly int InverseViewProjectionId = Shader.PropertyToID("_WF_CloudInverseViewProjection");
        private static readonly int CameraId = Shader.PropertyToID("_WF_CloudCamera");
        private static readonly int TargetSizeId = Shader.PropertyToID("_WF_CloudTargetSize");

        private Material material;
        private RenderTexture target;

        /// <summary>Renders the clouds for this frame's camera, or turns the pass off (the sky marches itself then).</summary>
        public void Render(Camera camera, bool active)
        {
            if (!active || camera == null)
            {
                Shader.SetGlobalFloat(LowResId, 0);
                return;
            }
            if (material == null) material = new Material(Resources.Load<Shader>("WhiteFox/CloudPass")) { name = "Cloud pass" };

            // half of the internal resolution (the render scale of the pipeline), a quarter on phones
            var scale = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset ? asset.renderScale : 1;
            var divisor = Application.isMobilePlatform ? 4 : 2;
            var width = Mathf.Max(16, Mathf.CeilToInt(camera.pixelWidth * scale / divisor));
            var height = Mathf.Max(16, Mathf.CeilToInt(camera.pixelHeight * scale / divisor));
            // phones march the clouds every other frame: the sky reprojects the last texture with the matrix it was
            // made with, so turning the camera keeps the clouds in place
            if (Application.isMobilePlatform && target != null && target.width == width && target.height == height && Time.frameCount % 2 == 1)
            {
                Shader.SetGlobalFloat(LowResId, 1);
                return;
            }
            if (target == null || target.width != width || target.height != height)
            {
                if (target != null) target.Release();
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "Volumetric clouds",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                target.Create();
            }

            // OpenGL convention matrices: the sky shaders project a view direction back into the texture with them
            var viewProjection = camera.projectionMatrix * camera.worldToCameraMatrix;
            Shader.SetGlobalMatrix(ViewProjectionId, viewProjection);
            Shader.SetGlobalMatrix(InverseViewProjectionId, viewProjection.inverse);
            Shader.SetGlobalVector(CameraId, camera.transform.position);
            Shader.SetGlobalVector(TargetSizeId, new Vector4(width, height, 0, 0));
            Graphics.Blit(null, target, material, 0);
            Shader.SetGlobalTexture(TextureId, target);
            Shader.SetGlobalFloat(LowResId, 1);
        }

        public void Dispose()
        {
            Shader.SetGlobalFloat(LowResId, 0);
            if (target != null) target.Release();
            if (material != null) UnityEngine.Object.Destroy(material);
        }
    }
}
