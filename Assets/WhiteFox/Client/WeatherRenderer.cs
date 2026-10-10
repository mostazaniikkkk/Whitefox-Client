// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;
using Random = UnityEngine.Random;

namespace WhiteFox.Client
{
    /// <summary>
    /// Rain, snow and clouds (vanilla LevelRenderer.renderSnowAndRain / tickRain / renderClouds): falling sheets in the
    /// columns around the camera where the biome has precipitation (snow when it is cold enough), splashes and rain
    /// sounds, and the cloud layer at y 192 built from environment/clouds.png, drifting east.
    /// </summary>
    public sealed class WeatherRenderer : IDisposable
    {
        private const int Radius = 10;
        private const float CloudHeight = 192;
        private const float CloudCell = 12;

        private readonly Material rainMaterial;
        private readonly Material snowMaterial;
        private readonly Material cloudMaterial;
        private readonly Mesh rainMesh = new Mesh { name = "Rain" };
        private readonly Mesh snowMesh = new Mesh { name = "Snow" };
        private readonly Mesh cloudMesh = new Mesh { name = "Clouds", indexFormat = IndexFormat.UInt32 };
        private readonly bool[] cloudMap;
        private readonly System.Random random = new System.Random();
        private int cloudAnchorX = int.MinValue, cloudAnchorZ = int.MinValue, cloudRadius;
        private int rainSoundCooldown;

        private readonly List<Vector3> rainVertices = new List<Vector3>(), snowVertices = new List<Vector3>();
        private readonly List<Vector2> rainUv = new List<Vector2>(), snowUv = new List<Vector2>();
        private readonly List<Vector2> rainLight = new List<Vector2>(), snowLight = new List<Vector2>();
        private readonly List<Color> rainColors = new List<Color>(), snowColors = new List<Color>();
        private readonly List<int> rainIndices = new List<int>(), snowIndices = new List<int>();

        public bool CloudsEnabled = true;

        public WeatherRenderer(GuiAssets assets)
        {
            var shader = Resources.Load<Shader>("WhiteFox/Weather");
            rainMaterial = new Material(shader) { name = "Rain" };
            snowMaterial = new Material(shader) { name = "Snow" };
            var rain = assets.Get("assets/minecraft/textures/environment/rain.png");
            var snow = assets.Get("assets/minecraft/textures/environment/snow.png");
            if (rain != null)
            {
                rain.Texture.wrapMode = TextureWrapMode.Repeat;
                rainMaterial.SetTexture("_MainTex", rain.Texture);
            }
            if (snow != null)
            {
                snow.Texture.wrapMode = TextureWrapMode.Repeat;
                snowMaterial.SetTexture("_MainTex", snow.Texture);
            }
            cloudMaterial = new Material(Resources.Load<Shader>("WhiteFox/Cloud")) { name = "Clouds" };
            rainMesh.MarkDynamic();
            snowMesh.MarkDynamic();

            var clouds = assets.Get("assets/minecraft/textures/environment/clouds.png");
            if (clouds != null && clouds.Texture.isReadable)
            {
                var pixels = clouds.Texture.GetPixels32();
                cloudMap = new bool[clouds.Width * clouds.Height];
                for (var y = 0; y < clouds.Height; y++)
                {
                    for (var x = 0; x < clouds.Width; x++) cloudMap[y * clouds.Width + x] = pixels[(clouds.Height - 1 - y) * clouds.Width + x].a > 128;
                }
                cloudSize = clouds.Width;
            }
        }

        private readonly int cloudSize = 256;

        private bool Cloud(int x, int z)
        {
            if (cloudMap == null) return false;
            x = ((x % cloudSize) + cloudSize) % cloudSize;
            z = ((z % cloudSize) + cloudSize) % cloudSize;
            return cloudMap[z * cloudSize + x];
        }

        // ------------------------------------------------------------------ ticking: splashes and sounds

        public void Tick(GameSession session, ParticleEngine particles)
        {
            var world = session.World;
            var rain = world.RainLevel;
            if (rain <= 0 || session.Dimension.Effects != "minecraft:overworld") return;
            var player = session.Player;
            var splashes = (int)(100 * rain * rain);
            Vec3d? soundAt = null;
            var cameraY = (int)Math.Floor(player.EyePosition.Y);
            for (var i = 0; i < splashes; i++)
            {
                var x = (int)Math.Floor(player.Position.X) + random.Next(21) - 10;
                var z = (int)Math.Floor(player.Position.Z) + random.Next(21) - 10;
                var top = world.GetChunk(x >> 4, z >> 4)?.GetHeight(x & 15, z & 15) ?? world.MinY;
                if (top > cameraY + 10 || top < cameraY - 10) continue;
                var biome = world.GetBiome(x, top, z);
                if (!biome.HasPrecipitation || biome.Snows(top)) continue;
                var ground = world.GetBlock(x, top - 1, z);
                if (ground == null) continue;
                var position = new Vector3(x + (float)random.NextDouble(), top + 0.1f, z + (float)random.NextDouble());
                if (ground.Block.Name.Path.Contains("lava") || ground.Block.Name.Path.Contains("magma")) continue;
                particles?.Splash(position);
                if (soundAt == null || random.Next(3) == 0) soundAt = new Vec3d(position.x, position.y, position.z);
            }
            if (soundAt.HasValue && rainSoundCooldown-- <= 0)
            {
                rainSoundCooldown = random.Next(3);
                var above = soundAt.Value.Y > player.EyePosition.Y + 1 && world.GetChunk((int)Math.Floor(player.Position.X) >> 4, (int)Math.Floor(player.Position.Z) >> 4)?.GetHeight((int)Math.Floor(player.Position.X) & 15, (int)Math.Floor(player.Position.Z) & 15) > player.EyePosition.Y;
                if (above) session.PlaySound("weather.rain.above", soundAt, 0.1f, 0.5f, SoundCategory.Weather);
                else session.PlaySound("weather.rain", soundAt, 0.2f, 1.0f, SoundCategory.Weather);
            }
        }

        // ------------------------------------------------------------------ rendering

        public void Render(GameSession session, Camera camera, float partial, int viewDistance)
        {
            if (camera == null) return;
            var overworld = session.Dimension.Effects == "minecraft:overworld";
            if (overworld && session.World.RainLevel > 0) RenderPrecipitation(session, camera, partial);
            if (overworld && CloudsEnabled) RenderClouds(session, camera, partial, viewDistance);
        }

        private void RenderPrecipitation(GameSession session, Camera camera, float partial)
        {
            var world = session.World;
            var rain = world.RainLevel;
            var cameraRender = camera.transform.position;
            var cx = cameraRender.x + RenderOrigin.X;
            var cy = cameraRender.y;
            var cz = -cameraRender.z + RenderOrigin.Z;
            var bx = (int)Math.Floor(cx);
            var by = (int)Math.Floor(cy);
            var bz = (int)Math.Floor(cz);
            var ticks = session.Ticks + partial;

            rainVertices.Clear(); rainUv.Clear(); rainLight.Clear(); rainColors.Clear(); rainIndices.Clear();
            snowVertices.Clear(); snowUv.Clear(); snowLight.Clear(); snowColors.Clear(); snowIndices.Clear();
            for (var z = bz - Radius; z <= bz + Radius; z++)
            {
                for (var x = bx - Radius; x <= bx + Radius; x++)
                {
                    float dx = x + 0.5f - (float)cx, dz = z + 0.5f - (float)cz;
                    var distance = dx * dx + dz * dz;
                    if (distance > Radius * Radius) continue;
                    var height = world.GetChunk(x >> 4, z >> 4)?.GetHeight(x & 15, z & 15) ?? world.MinY;
                    var bottom = Math.Max(by - Radius, height);
                    var top = Math.Max(by + Radius, height);
                    if (bottom >= top) continue;
                    var biome = world.GetBiome(x, by, z);
                    if (!biome.HasPrecipitation) continue;
                    var snow = biome.Snows(Math.Max(height, by));
                    var length = Mathf.Sqrt(distance);
                    var nx = length > 0.001f ? dx / length : 1;
                    var nz = length > 0.001f ? dz / length : 0;
                    // a sheet across the view direction through the column center (vanilla: -dz/dx * 0.5)
                    var a = new Vector3(x + 0.5f - nz * 0.5f, 0, z + 0.5f + nx * 0.5f);
                    var b = new Vector3(x + 0.5f + nz * 0.5f, 0, z + 0.5f - nx * 0.5f);
                    var hash = (x * x * 3121 + x * 45238971 ^ z * z * 418711 + z * 13761) & 0xFF;
                    var alpha = ((1 - distance / (Radius * Radius)) * 0.5f + 0.5f) * rain;
                    var light = world.GetLight(x, Math.Max(height, by), z);
                    var lightUv = new Vector2((light & 0x0F) / 15f, (light >> 4) / 15f);
                    float vOffset, uOffset = 0;
                    if (snow)
                    {
                        vOffset = -((ticks % 512) + hash) / 512f;
                        uOffset = (float)(random.NextDouble() * 0) + (float)Math.Sin(ticks * 0.01 + hash) * 0.01f;
                    }
                    else
                    {
                        vOffset = -(((ticks + hash) % 32) / 32f) * (3 + (hash & 3));
                    }
                    var vertices = snow ? snowVertices : rainVertices;
                    var uvs = snow ? snowUv : rainUv;
                    var lights = snow ? snowLight : rainLight;
                    var colors = snow ? snowColors : rainColors;
                    var indices = snow ? snowIndices : rainIndices;
                    var first = vertices.Count;
                    vertices.Add(RenderOrigin.ToRender(a.x, bottom, a.z));
                    vertices.Add(RenderOrigin.ToRender(b.x, bottom, b.z));
                    vertices.Add(RenderOrigin.ToRender(b.x, top, b.z));
                    vertices.Add(RenderOrigin.ToRender(a.x, top, a.z));
                    var v0 = bottom * 0.25f + vOffset;
                    var v1 = top * 0.25f + vOffset;
                    uvs.Add(new Vector2(0 + uOffset, v0));
                    uvs.Add(new Vector2(1 + uOffset, v0));
                    uvs.Add(new Vector2(1 + uOffset, v1));
                    uvs.Add(new Vector2(0 + uOffset, v1));
                    var color = new Color(1, 1, 1, alpha);
                    for (var i = 0; i < 4; i++)
                    {
                        lights.Add(lightUv);
                        colors.Add(color);
                    }
                    indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }
            }
            Draw(rainMesh, rainVertices, rainUv, rainLight, rainColors, rainIndices, rainMaterial, camera);
            Draw(snowMesh, snowVertices, snowUv, snowLight, snowColors, snowIndices, snowMaterial, camera);
        }

        private static void Draw(Mesh mesh, List<Vector3> vertices, List<Vector2> uv, List<Vector2> light, List<Color> colors, List<int> indices, Material material, Camera camera)
        {
            if (indices.Count == 0) return;
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetUVs(1, light);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0, false);
            mesh.bounds = new Bounds(camera.transform.position, Vector3.one * 64);
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera, 0, null, ShadowCastingMode.Off, false);
        }

        /// <summary>Fancy clouds: 12x12x4 block boxes from the cloud texture, drifting 0.03 blocks per tick east.</summary>
        private void RenderClouds(GameSession session, Camera camera, float partial, int viewDistance)
        {
            if (cloudMap == null) return;
            var ticks = (session.World.TimeOfDay + session.Ticks * 0) + partial;
            var drift = ((session.Ticks + partial) * 0.03f) % (cloudSize * CloudCell);
            var cameraRender = camera.transform.position;
            var cx = cameraRender.x + RenderOrigin.X + drift;
            var cz = -cameraRender.z + RenderOrigin.Z;
            var anchorX = (int)Math.Floor(cx / CloudCell);
            var anchorZ = (int)Math.Floor(cz / CloudCell);
            var radius = Mathf.Clamp(viewDistance * 16 / (int)CloudCell + 2, 8, 48);
            if (anchorX != cloudAnchorX || anchorZ != cloudAnchorZ || radius != cloudRadius) BuildClouds(anchorX, anchorZ, radius);

            // color: white by day, dark at night, grayer in rain (vanilla getCloudColor)
            var day = Mathf.Clamp01(Shader.GetGlobalFloat("_WF_DayLight"));
            var rain = session.World.RainLevel;
            var brightness = day * (1 - rain * 0.95f * 0.5f);
            cloudMaterial.SetColor("_CloudColor", new Color(brightness * 0.9f + 0.1f, brightness * 0.9f + 0.1f, brightness * 0.85f + 0.15f, 0.8f));

            // the mesh is built around the anchor cell; the drift moves it continuously
            var originX = anchorX * CloudCell - drift;
            var originZ = anchorZ * CloudCell;
            var position = RenderOrigin.ToRender(originX, CloudHeight, originZ);
            Graphics.DrawMesh(cloudMesh, Matrix4x4.Translate(position), cloudMaterial, 0, camera, 0, null, ShadowCastingMode.Off, false);
        }

        private void BuildClouds(int anchorX, int anchorZ, int radius)
        {
            cloudAnchorX = anchorX;
            cloudAnchorZ = anchorZ;
            cloudRadius = radius;
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();
            const float h = 4;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade)
            {
                var first = vertices.Count;
                // render space: z negated (and the winding with it)
                vertices.Add(new Vector3(a.x, a.y, -a.z));
                vertices.Add(new Vector3(b.x, b.y, -b.z));
                vertices.Add(new Vector3(c.x, c.y, -c.z));
                vertices.Add(new Vector3(d.x, d.y, -d.z));
                var color = new Color(shade, shade, shade, 1);
                colors.Add(color);
                colors.Add(color);
                colors.Add(color);
                colors.Add(color);
                indices.AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
            }
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    var tx = anchorX + dx;
                    var tz = anchorZ + dz;
                    if (!Cloud(tx, tz)) continue;
                    float x0 = dx * CloudCell, x1 = x0 + CloudCell, z0 = dz * CloudCell, z1 = z0 + CloudCell;
                    // minecraft space, counter clockwise seen from outside
                    Quad(new Vector3(x0, h, z0), new Vector3(x0, h, z1), new Vector3(x1, h, z1), new Vector3(x1, h, z0), 1.0f);
                    Quad(new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), new Vector3(x1, 0, z1), new Vector3(x0, 0, z1), 0.7f);
                    if (!Cloud(tx - 1, tz)) Quad(new Vector3(x0, 0, z0), new Vector3(x0, 0, z1), new Vector3(x0, h, z1), new Vector3(x0, h, z0), 0.9f);
                    if (!Cloud(tx + 1, tz)) Quad(new Vector3(x1, 0, z1), new Vector3(x1, 0, z0), new Vector3(x1, h, z0), new Vector3(x1, h, z1), 0.9f);
                    if (!Cloud(tx, tz - 1)) Quad(new Vector3(x1, 0, z0), new Vector3(x0, 0, z0), new Vector3(x0, h, z0), new Vector3(x1, h, z0), 0.8f);
                    if (!Cloud(tx, tz + 1)) Quad(new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), new Vector3(x1, h, z1), new Vector3(x0, h, z1), 0.8f);
                }
            }
            cloudMesh.Clear();
            cloudMesh.SetVertices(vertices);
            cloudMesh.SetColors(colors);
            cloudMesh.SetTriangles(indices, 0);
            cloudMesh.RecalculateBounds();
        }

        public void Dispose()
        {
            UnityEngine.Object.Destroy(rainMesh);
            UnityEngine.Object.Destroy(snowMesh);
            UnityEngine.Object.Destroy(cloudMesh);
            UnityEngine.Object.Destroy(rainMaterial);
            UnityEngine.Object.Destroy(snowMaterial);
            UnityEngine.Object.Destroy(cloudMaterial);
        }
    }
}
