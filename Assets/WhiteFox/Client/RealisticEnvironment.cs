// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WhiteFox.Client
{
    /// <summary>
    /// Everything around the realistic chunk shaders: a shadow casting sun/moon light that follows the time of day,
    /// the procedural sky, the global shader values shared by the sky and the block/water shaders, and post
    /// processing (ACES tonemapping, bloom, color grading).
    /// </summary>
    public sealed class RealisticEnvironment : IDisposable
    {
        private static readonly int SunDirection = Shader.PropertyToID("_WF_SunDirection");
        private static readonly int SunColor = Shader.PropertyToID("_WF_SunColor");
        private static readonly int SkyZenith = Shader.PropertyToID("_WF_SkyZenith");
        private static readonly int SkyHorizon = Shader.PropertyToID("_WF_SkyHorizon");
        private static readonly int BlockLightColor = Shader.PropertyToID("_WF_BlockLightColor");
        private static readonly int NightFactor = Shader.PropertyToID("_WF_NightFactor");
        private static readonly int AmbientStrength = Shader.PropertyToID("_WF_AmbientStrength");

        private readonly GameObject root;
        private readonly Light sun;
        private readonly Material sky;
        private readonly Volume volume;
        private readonly VolumeProfile profile;
        private readonly Bloom bloom;
        private float shadowDistance = -1;
        private int shadowCascades = -1;
        private bool sunShadows = true;

        public float SunIntensity = 3.2f;
        public float Ambient = 1.0f;
        public float BlockLightIntensity = 2.2f;
        public bool Enabled { get; private set; }
        public Light Sun => sun;
        /// <summary>Relative brightness of artificial lights: low at noon (the eye adapts to the sun), full at night.</summary>
        public float Exposure { get; private set; } = 1.0f;

        public RealisticEnvironment(Transform parent)
        {
            root = new GameObject("Realistic environment");
            root.transform.SetParent(parent, false);

            var sunObject = new GameObject("Sun");
            sunObject.transform.SetParent(root.transform, false);
            sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1.0f;
            sun.renderMode = LightRenderMode.ForcePixel;

            sky = new Material(Resources.Load<Shader>("WhiteFox/Sky")) { name = "WhiteFox sky" };

            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);
            bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.6f);
            bloom.scatter.Override(0.65f);
            var colors = profile.Add<ColorAdjustments>(true);
            colors.postExposure.Override(0.35f);
            colors.contrast.Override(10.0f);
            colors.saturation.Override(12.0f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.18f);

            volume = root.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100;
            volume.sharedProfile = profile;
        }

        /// <summary>Applies the modern quality settings (sun shadows, bloom, post processing).</summary>
        public void ApplyQuality(Settings settings, Camera camera)
        {
            sunShadows = settings.SunShadows > 0;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset)
            {
                // remember the asset values: in the editor, changes to the asset would be saved to disk
                if (shadowDistance < 0)
                {
                    shadowDistance = asset.shadowDistance;
                    shadowCascades = asset.shadowCascadeCount;
                }
                asset.shadowDistance = settings.SunShadows <= 1 ? 48 : settings.SunShadows == 2 ? 96 : 128;
                asset.shadowCascadeCount = settings.SunShadows <= 1 ? 2 : 4;
            }
            bloom.active = settings.Bloom;
            volume.enabled = settings.PostProcessing;
            if (Enabled) camera.GetUniversalAdditionalCameraData().renderPostProcessing = settings.PostProcessing;
        }

        public void SetEnabled(bool enabled, Camera camera)
        {
            Enabled = enabled;
            root.SetActive(enabled);
            camera.clearFlags = enabled ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            RenderSettings.skybox = enabled ? sky : null;
            RenderSettings.sun = enabled ? sun : null;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = enabled;
            data.requiresDepthTexture = enabled; // water absorption
            data.requiresColorTexture = enabled; // water refraction
            data.renderShadows = enabled;
        }

        /// <summary>Sun position, colors and fog from the time of day (vanilla celestial angle, sun moves east to west).</summary>
        public void Update(long timeOfDay, float fogStart, float fogEnd)
        {
            var time = timeOfDay / 24000.0;
            var angle = time - 0.25;
            angle -= Math.Floor(angle);
            angle += (1.0 - (Math.Cos(angle * Math.PI) + 1.0) / 2.0 - angle) / 3.0;
            var radians = angle * Math.PI * 2.0;

            // minecraft: (-sin, cos, 0); a slight tilt to the south gives nicer shadows at noon. Render space negates z.
            var sunDirection = new Vector3((float)-Math.Sin(radians), (float)Math.Cos(radians), -0.25f).normalized;
            var elevation = sunDirection.y;
            var day = Mathf.Clamp01(elevation * 4.0f + 0.3f);
            var night = Mathf.Clamp01(-elevation * 4.0f);
            var sunset = Mathf.Clamp01(1.0f - Mathf.Abs(elevation) * 4.0f);

            var sunColor = Color.Lerp(new Color(1.0f, 0.95f, 0.88f), new Color(1.0f, 0.55f, 0.28f), sunset);
            var zenith = Color.Lerp(new Color(0.012f, 0.016f, 0.045f), new Color(0.32f, 0.55f, 1.0f), day);
            var horizon = Color.Lerp(new Color(0.03f, 0.04f, 0.08f), new Color(0.72f, 0.84f, 1.0f), day);
            horizon = Color.Lerp(horizon, new Color(1.0f, 0.58f, 0.34f), sunset * 0.7f);

            // the main light is the sun during the day and the moon during the night
            var moon = elevation < -0.05f;
            var lightDirection = moon ? -sunDirection : sunDirection;
            var intensity = moon ? 0.25f * night : SunIntensity * Mathf.Clamp01(elevation * 5.0f + 0.15f);
            sun.transform.rotation = Quaternion.LookRotation(-lightDirection);
            sun.color = moon ? new Color(0.55f, 0.65f, 1.0f) : sunColor;
            sun.intensity = intensity;
            sun.shadows = intensity > 0.01f && sunShadows ? LightShadows.Soft : LightShadows.None;

            Shader.SetGlobalVector(SunDirection, sunDirection);
            Shader.SetGlobalVector(SunColor, sunColor.linear * (SunIntensity * Mathf.Clamp01(elevation * 5.0f + 0.3f)));
            Shader.SetGlobalVector(SkyZenith, zenith.linear * 1.3f);
            Shader.SetGlobalVector(SkyHorizon, horizon.linear * 1.3f);
            Shader.SetGlobalVector(BlockLightColor, new Color(1.0f, 0.72f, 0.42f).linear * BlockLightIntensity);
            Shader.SetGlobalFloat(NightFactor, night);
            Exposure = Mathf.Lerp(1.0f, 0.2f, Mathf.Clamp01(elevation * 3.0f));
            Shader.SetGlobalFloat(AmbientStrength, Ambient * Mathf.Lerp(0.25f, 1.0f, day));

            RenderSettings.fogColor = horizon;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
        }

        public void Dispose()
        {
            if (shadowDistance >= 0 && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset)
            {
                asset.shadowDistance = shadowDistance;
                asset.shadowCascadeCount = shadowCascades;
            }
            UnityEngine.Object.Destroy(root);
            UnityEngine.Object.Destroy(sky);
            UnityEngine.Object.Destroy(profile);
        }
    }
}
