// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WhiteFox.Client
{
    /// <summary>
    /// The enhanced graphics mode: the classic look (Minecraft light levels, vanilla sky colors) plus a few light
    /// effects that also run on phones: camera motion blur, volumetric clouds drawn into the empty sky, and leaves and
    /// plants swaying in the wind (WhiteFoxWind.hlsl in the classic block shader).
    /// </summary>
    public sealed class EnhancedEffects : IDisposable
    {
        private static readonly int SunDirectionId = Shader.PropertyToID("_WF_SunDirection");
        private static readonly int SunColorId = Shader.PropertyToID("_WF_SunColor");
        private static readonly int SkyZenithId = Shader.PropertyToID("_WF_SkyZenith");
        private static readonly int SkyHorizonId = Shader.PropertyToID("_WF_SkyHorizon");
        private static readonly int CloudStepsId = Shader.PropertyToID("_WF_CloudSteps");
        private static readonly int CloudLightStepsId = Shader.PropertyToID("_WF_CloudLightSteps");
        private static readonly int CloudCoverageId = Shader.PropertyToID("_WF_CloudCoverage");
        private static readonly int CloudWindId = Shader.PropertyToID("_WF_CloudWind");
        private static readonly int CloudNoiseId = Shader.PropertyToID("_WF_CloudNoise");
        private static readonly int WindStrengthId = Shader.PropertyToID("_WF_WindStrength");

        private readonly GameObject root;
        private readonly VolumeProfile profile;
        private readonly Volume volume;
        private readonly MotionBlur motionBlur;
        private readonly Bloom bloom;
        private static readonly int GlowId = Shader.PropertyToID("_WF_EnhancedGlow");
        private int cloudSteps;
        private bool wind;
        private GameObject clouds;
        private Material cloudMaterial;
        private Mesh cloudMesh;
        private Texture3D cloudNoise;
        private Vector2 cloudWind;

        public bool Enabled { get; private set; }
        /// <summary>Volumetric clouds are drawn (the vanilla cloud boxes are hidden then).</summary>
        public bool VolumetricClouds => Enabled && cloudSteps > 0;

        public EnhancedEffects(Transform parent)
        {
            root = new GameObject("Enhanced effects");
            root.transform.SetParent(parent, false);
            // only motion blur: no tonemapping or color grading, the colors stay the classic ones
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            motionBlur = profile.Add<MotionBlur>(true);
            motionBlur.mode.Override(MotionBlurMode.CameraOnly);
            motionBlur.intensity.Override(0.25f);
            motionBlur.quality.Override(MotionBlurQuality.Low);
            motionBlur.clamp.Override(0.05f);
            // HDR glow: only what goes past white blooms (the emissive texels), the rest keeps the classic colors
            bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.0f);
            bloom.intensity.Override(0.9f);
            bloom.scatter.Override(0.6f);
            bloom.active = false;
            volume = root.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 90;
            volume.sharedProfile = profile;
            root.SetActive(false);
        }

        public void SetEnabled(bool enabled, Camera camera, Settings settings)
        {
            Enabled = enabled;
            root.SetActive(enabled);
            // phones: fewer steps through the cloud layer (the clouds are soft anyway)
            var mobile = Application.isMobilePlatform;
            cloudSteps = settings.EnhancedClouds == 0 ? 0 : settings.EnhancedClouds == 1 ? (mobile ? 14 : 24) : (mobile ? 24 : 48);
            wind = settings.WavingPlants;
            if (enabled)
            {
                if (cloudSteps > 0)
                {
                    EnsureClouds(camera);
                    if (cloudNoise == null) cloudNoise = CloudNoise.Create();
                    Shader.SetGlobalTexture(CloudNoiseId, cloudNoise);
                    Shader.SetGlobalFloat(CloudStepsId, cloudSteps);
                    Shader.SetGlobalFloat(CloudLightStepsId, settings.EnhancedClouds >= 2 ? (mobile ? 3 : 5) : (mobile ? 2 : 3));
                }
                var blur = settings.EnhancedMotionBlur;
                motionBlur.active = blur > 0;
                motionBlur.intensity.Override(blur >= 2 ? 0.6f : 0.25f);
                motionBlur.quality.Override(blur >= 2 ? MotionBlurQuality.High : MotionBlurQuality.Low);
                bloom.active = settings.EnhancedHdr;
                Shader.SetGlobalFloat(GlowId, settings.EnhancedHdr ? 1 : 0);
                camera.allowHDR = true;
                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = blur > 0 || settings.EnhancedHdr;
                data.requiresDepthTexture = blur > 0; // motion blur reprojects the depth
            }
            if (!enabled || !wind) Shader.SetGlobalFloat(WindStrengthId, 0);
            if (!enabled) Shader.SetGlobalFloat(GlowId, 0);
            if (clouds != null) clouds.SetActive(enabled && cloudSteps > 0);
        }

        /// <summary>The clouds are a full screen triangle at the far plane: they only cover pixels where the sky shows.</summary>
        private void EnsureClouds(Camera camera)
        {
            if (clouds != null || camera == null) return;
            cloudMesh = new Mesh { name = "Enhanced clouds" };
            cloudMesh.SetVertices(new List<Vector3> { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) });
            cloudMesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            cloudMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000); // placed in clip space by the shader
            cloudMaterial = new Material(Resources.Load<Shader>("WhiteFox/EnhancedClouds")) { name = "Enhanced clouds" };
            clouds = new GameObject("Enhanced clouds", typeof(MeshFilter), typeof(MeshRenderer));
            clouds.transform.SetParent(camera.transform, false);
            clouds.GetComponent<MeshFilter>().sharedMesh = cloudMesh;
            var renderer = clouds.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = cloudMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// Per frame: the sun (vanilla celestial angle), the classic sky and fog colors as the light around the clouds,
        /// the cloud drift and the wind. Clouds only where there is a sky (the overworld).
        /// </summary>
        public void Update(long timeOfDay, Color sky, Color fog, float rain, float thunder, bool overworld)
        {
            if (!Enabled) return;
            if (clouds != null && clouds.activeSelf != (overworld && cloudSteps > 0)) clouds.SetActive(overworld && cloudSteps > 0);

            var time = timeOfDay / 24000.0;
            var angle = time - 0.25;
            angle -= Math.Floor(angle);
            angle += (1.0 - (Math.Cos(angle * Math.PI) + 1.0) / 2.0 - angle) / 3.0;
            var radians = angle * Math.PI * 2.0;
            var sunDirection = new Vector3((float)-Math.Sin(radians), (float)Math.Cos(radians), -0.25f).normalized;
            var elevation = sunDirection.y;
            var sunset = Mathf.Clamp01(1.0f - Mathf.Abs(elevation) * 4.0f);
            var sunColor = Color.Lerp(new Color(1.0f, 0.95f, 0.88f), new Color(1.0f, 0.6f, 0.35f), sunset);
            var overcast = Mathf.Clamp01(rain);
            var sunStrength = Mathf.Clamp01(elevation * 5.0f + 0.3f) * (1 - overcast * 0.85f) * 1.1f;
            Shader.SetGlobalVector(SunDirectionId, sunDirection);
            Shader.SetGlobalVector(SunColorId, sunColor.linear * sunStrength);
            Shader.SetGlobalVector(SkyZenithId, sky.linear * 1.5f);
            Shader.SetGlobalVector(SkyHorizonId, fog.linear * 1.5f);

            // like the realistic sky: drift east, more clouds in rain
            cloudWind += new Vector2(-Time.deltaTime * 0.6f, 0);
            cloudWind.x %= 4096;
            Shader.SetGlobalVector(CloudWindId, new Vector4(cloudWind.x, 0, cloudWind.y, 0));
            Shader.SetGlobalFloat(CloudCoverageId, Mathf.Lerp(0.42f, 0.9f, overcast));
            Shader.SetGlobalFloat(WindStrengthId, wind ? 1.0f + 1.2f * overcast + 1.0f * Mathf.Clamp01(thunder) : 0);
        }

        public void Dispose()
        {
            if (cloudMaterial != null) UnityEngine.Object.Destroy(cloudMaterial);
            if (cloudMesh != null) UnityEngine.Object.Destroy(cloudMesh);
            if (cloudNoise != null) UnityEngine.Object.Destroy(cloudNoise);
            if (clouds != null) UnityEngine.Object.Destroy(clouds);
            UnityEngine.Object.Destroy(profile);
            UnityEngine.Object.Destroy(root);
        }
    }
}
