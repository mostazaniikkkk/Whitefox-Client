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
    /// Everything around the realistic chunk shaders: a shadow casting sun/moon light that follows the time of day,
    /// the procedural sky, the global shader values shared by the sky and the block/water shaders, and post
    /// processing (ACES tonemapping, bloom, color grading).
    /// </summary>
    public sealed class RealisticEnvironment : IDisposable
    {
        private static readonly int SunDirection = Shader.PropertyToID("_WF_SunDirection");
        private static readonly int SunColor = Shader.PropertyToID("_WF_SunColor");
        private static readonly int Overcast = Shader.PropertyToID("_WF_Overcast");
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
        private readonly MotionBlur motionBlur;
        private float shadowDistance = -1;
        private int shadowCascades = -1;
        private bool sunShadows = true;

        public float SunIntensity = 3.2f;
        public float Ambient = 1.0f;
        public float BlockLightIntensity = 2.2f;
        /// <summary>Rain (0..1) and thunder (0..1): an overcast sky, a hidden sun, soft light and closer grey fog.</summary>
        public float Rain;
        public float Thunder;
        /// <summary>How much sky the camera sees (0 in caves): volumetric light only outdoors.</summary>
        public float Outdoors = 1;
        // eye adaptation: how much the eye is adapted to daylight (follows Outdoors over a second or two)
        private float adaptation = 1;
        /// <summary>Ray march steps of the volumetric sun light, 0 off.</summary>
        public int VolumetricSteps;
        /// <summary>Volumetric clouds are drawn in the sky (the vanilla cloud boxes are hidden then).</summary>
        public bool VolumetricClouds { get; private set; }
        private Texture3D cloudNoise;
        private Vector2 cloudWind;
        private bool wavingPlants = true;
        private float wetness;
        private static readonly int WetnessId = Shader.PropertyToID("_WF_Wetness");
        private static readonly int CloudStepsId = Shader.PropertyToID("_WF_CloudSteps");
        private static readonly int CloudLightStepsId = Shader.PropertyToID("_WF_CloudLightSteps");
        private static readonly int CloudCoverageId = Shader.PropertyToID("_WF_CloudCoverage");
        private static readonly int CloudWindId = Shader.PropertyToID("_WF_CloudWind");
        private static readonly int WindStrengthId = Shader.PropertyToID("_WF_WindStrength");
        private static readonly int CloudNoiseId = Shader.PropertyToID("_WF_CloudNoise");

        private GameObject volumetric;
        private bool godRays;
        private float mist;
        private static readonly int MistDensityId = Shader.PropertyToID("_WF_MistDensity");
        private static readonly int MistSunlightId = Shader.PropertyToID("_WF_MistSunlight");
        private static readonly int MistColorId = Shader.PropertyToID("_WF_MistColor");
        private Material volumetricMaterial;
        private Mesh volumetricMesh;
        private static readonly int VolumetricStepsId = Shader.PropertyToID("_WF_VolumetricSteps");
        private static readonly int VolumetricStrengthId = Shader.PropertyToID("_WF_VolumetricStrength");

        /// <summary>The full screen triangle that adds the volumetric light, a child of the camera.</summary>
        private void EnsureVolumetric(Camera camera)
        {
            if (volumetric != null || camera == null) return;
            volumetricMesh = new Mesh { name = "Volumetric light" };
            volumetricMesh.SetVertices(new List<Vector3> { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) });
            volumetricMesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            // never culled: the triangle is placed in clip space by the shader
            volumetricMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000);
            volumetricMaterial = new Material(Resources.Load<Shader>("WhiteFox/Volumetric")) { name = "Volumetric light" };
            volumetric = new GameObject("Volumetric light", typeof(MeshFilter), typeof(MeshRenderer));
            volumetric.transform.SetParent(camera.transform, false);
            volumetric.GetComponent<MeshFilter>().sharedMesh = volumetricMesh;
            var renderer = volumetric.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = volumetricMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
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
            // camera motion blur (URP MotionBlur, camera mode): off unless chosen in the quality options
            motionBlur = profile.Add<MotionBlur>(true);
            motionBlur.mode.Override(MotionBlurMode.CameraOnly);
            motionBlur.active = false;
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
            godRays = settings.VolumetricLight > 0;
            wavingPlants = settings.WavingPlants;
            mist = settings.Mist == 0 ? 0 : settings.Mist == 1 ? 0.006f : 0.016f;
            // the mist alone needs fewer steps than sharp light shafts
            VolumetricSteps = settings.VolumetricLight == 2 ? 20 : settings.VolumetricLight == 1 ? 10 : mist > 0 ? 8 : 0;
            VolumetricClouds = settings.VolumetricClouds > 0;
            if (VolumetricClouds && cloudNoise == null)
            {
                cloudNoise = CloudNoise.Create();
                Shader.SetGlobalTexture(CloudNoiseId, cloudNoise);
            }
            Shader.SetGlobalFloat(CloudStepsId, settings.VolumetricClouds == 0 ? 0 : settings.VolumetricClouds == 1 ? 24 : 48);
            Shader.SetGlobalFloat(CloudLightStepsId, settings.VolumetricClouds >= 2 ? 5 : 3);
            if (VolumetricSteps > 0) EnsureVolumetric(camera);
            else if (volumetric != null) volumetric.SetActive(false);
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
            motionBlur.active = settings.MotionBlur > 0;
            motionBlur.intensity.Override(settings.MotionBlur >= 2 ? 0.6f : 0.25f);
            motionBlur.quality.Override(settings.MotionBlur >= 2 ? MotionBlurQuality.High : MotionBlurQuality.Low);
            motionBlur.clamp.Override(0.05f);
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
        /// <summary>WF_Atmosphere of WhiteFoxCommon.hlsl, for the fog color (the sky at the horizon where the camera looks).</summary>
        private static Vector3 Atmosphere(Vector3 direction, Vector3 sunDirection)
        {
            var rayleigh = new Vector3(0.17f, 0.41f, 1.0f) * 0.6f;
            var up = Mathf.Max(direction.y, 0);
            var viewAir = 1 / (up + 0.25f);
            var sunAir = 1 / (Mathf.Max(sunDirection.y, 0) + 0.12f);
            var cosine = Vector3.Dot(direction, sunDirection);
            var g = 0.8f;
            var haze = (1 - g * g) / (4 * Mathf.PI * Mathf.Pow(Mathf.Max(1 + g * g - 2 * g * cosine, 1e-4f), 1.5f)) * (1 - Mathf.Exp(-0.12f * viewAir));
            var result = Vector3.zero;
            var hazeColor = new Vector3(1.0f, 0.95f, 0.85f);
            var multipleColor = new Vector3(0.3f, 0.5f, 1.0f);
            var low = Mathf.Clamp01(sunDirection.y * 3 + 0.35f);
            for (var i = 0; i < 3; i++)
            {
                var sunlight = Mathf.Exp(-rayleigh[i] * Mathf.Max(sunAir - 0.9f, 0));
                var air = 1 - Mathf.Exp(-rayleigh[i] * viewAir);
                var scatter = air * (0.75f * (1 + cosine * cosine));
                result[i] = sunlight * (scatter * 1.8f + haze * hazeColor[i] * 0.9f) + air * multipleColor[i] * 0.8f * low;
            }
            return result;
        }

        /// <summary>The direction the camera looks (for the fog color).</summary>
        public Vector3 ViewDirection = Vector3.forward;

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
            // clouds cover the sky in rain: the sun light becomes weak and diffuse, the sky a grey overcast
            var overcast = Mathf.Clamp01(Rain) * (0.82f + 0.12f * Mathf.Clamp01(Thunder));
            float Gray(Color c) => c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            var zenithGray = Gray(zenith) * (0.62f - 0.2f * Thunder);
            var horizonGray = Gray(horizon) * (0.7f - 0.2f * Thunder);
            zenith = Color.Lerp(zenith, new Color(zenithGray, zenithGray, zenithGray * 1.04f), overcast);
            horizon = Color.Lerp(horizon, new Color(horizonGray, horizonGray, horizonGray * 1.04f), overcast);
            sunColor = Color.Lerp(sunColor, new Color(0.85f, 0.88f, 0.92f), overcast);
            Shader.SetGlobalFloat(Overcast, overcast);
            var intensity = (moon ? 0.25f * night : SunIntensity * Mathf.Clamp01(elevation * 5.0f + 0.15f)) * (1 - overcast * 0.85f);
            sun.transform.rotation = Quaternion.LookRotation(-lightDirection);
            sun.color = moon ? new Color(0.55f, 0.65f, 1.0f) : sunColor;
            sun.intensity = intensity;
            sun.shadows = intensity > 0.01f && sunShadows ? LightShadows.Soft : LightShadows.None;
            // the clouds drift east like vanilla's (about 0.6 blocks per second), more of them in rain
            cloudWind += new Vector2(-Time.deltaTime * 0.6f, 0);
            cloudWind.x %= 4096;
            Shader.SetGlobalVector(CloudWindId, new Vector4(cloudWind.x, 0, cloudWind.y, 0));
            // surfaces get wet within half a minute of rain and dry in about two minutes
            wetness = Mathf.MoveTowards(wetness, Mathf.Clamp01(Rain), Time.deltaTime * (Rain > wetness ? 1 / 30f : 1 / 120f));
            Shader.SetGlobalFloat(WetnessId, wetness);
            // leaves and plants sway harder in rain and storms
            Shader.SetGlobalFloat(WindStrengthId, wavingPlants ? 1.0f + 1.2f * Mathf.Clamp01(Rain) + 1.0f * Mathf.Clamp01(Thunder) : 0);
            Shader.SetGlobalFloat(CloudCoverageId, Mathf.Lerp(0.5f, 0.92f, Mathf.Clamp01(Rain)));

            // god rays need the sun shadow map; they fade in caves, in rain and at night
            if (volumetric != null)
            {
                // mist: thicker at dawn (morning fog), in rain, none in caves (it would light them up)
                var hour = timeOfDay % 24000;
                var morning = hour > 22500 ? (hour - 22500) / 1500f : hour < 3000 ? 1 - hour / 3000f : 0;
                var mistDensity = mist * (1 + 2.5f * morning + 2.5f * Mathf.Clamp01(Rain)) * Mathf.Clamp01(Outdoors);
                var active = VolumetricSteps > 0 && sunShadows && (godRays && intensity > 0.01f || mistDensity > 0);
                if (volumetric.activeSelf != active) volumetric.SetActive(active);
                Shader.SetGlobalFloat(VolumetricStepsId, VolumetricSteps);
                var sunReach = Mathf.Clamp01(Outdoors) * (1 - overcast * 0.8f) * (moon ? 0.35f : 1);
                Shader.SetGlobalFloat(VolumetricStrengthId, godRays ? 0.6f * sunReach : 0);
                Shader.SetGlobalFloat(MistDensityId, mistDensity);
                Shader.SetGlobalFloat(MistSunlightId, 0.5f * sunReach);
                // the mist is lit by the sky around it
                Shader.SetGlobalVector(MistColorId, Color.Lerp(horizon, zenith, 0.3f).linear * 0.9f);
            }

            Shader.SetGlobalVector(SunDirection, sunDirection);
            Shader.SetGlobalVector(SunColor, sunColor.linear * (SunIntensity * Mathf.Clamp01(elevation * 5.0f + 0.3f) * (1 - overcast * 0.85f)));
            Shader.SetGlobalVector(SkyZenith, zenith.linear * 1.3f);
            Shader.SetGlobalVector(SkyHorizon, horizon.linear * 1.3f);
            Shader.SetGlobalVector(BlockLightColor, new Color(1.0f, 0.72f, 0.42f).linear * BlockLightIntensity);
            Shader.SetGlobalFloat(NightFactor, night);
            // block lights are dimmed by day only while the eye is adapted to daylight; indoors and in caves the eye
            // adapts to the torches, like it does at night
            adaptation = Mathf.MoveTowards(adaptation, Mathf.Clamp01(Outdoors), Time.deltaTime * 0.6f);
            Exposure = Mathf.Lerp(1.0f, Mathf.Lerp(1.0f, 0.2f, Mathf.Clamp01(elevation * 3.0f)), adaptation);
            Shader.SetGlobalFloat(AmbientStrength, Ambient * Mathf.Lerp(0.25f, 1.0f, day));

            // the fog takes the color of the sky at the horizon in front of the camera, so distant terrain melts into it
            var flat = new Vector3(ViewDirection.x, 0, ViewDirection.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            var atmosphere = Atmosphere(flat.normalized, sunDirection);
            var scatteringDay = Mathf.Clamp01(sunDirection.y * 5 + 0.25f) * (1 - overcast);
            var linearHorizon = horizon.linear * 1.3f;
            var fog = Vector3.Lerp(new Vector3(linearHorizon.r, linearHorizon.g, linearHorizon.b), atmosphere, scatteringDay);
            RenderSettings.fogColor = new Color(fog.x, fog.y, fog.z).gamma;
            // rain brings the fog closer (vanilla thickens it a little in rain, the realistic sky a lot)
            RenderSettings.fogStartDistance = fogStart * (1 - overcast * 0.6f);
            RenderSettings.fogEndDistance = fogEnd * (1 - overcast * 0.35f);
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
            if (cloudNoise != null) UnityEngine.Object.Destroy(cloudNoise);
        }
    }
}
