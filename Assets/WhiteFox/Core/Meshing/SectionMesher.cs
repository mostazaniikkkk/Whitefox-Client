// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WhiteFox.Core.Data;
using WhiteFox.Core.Models;
using WhiteFox.Core.Textures;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Meshing
{
    /// <summary>
    /// Interleaved vertex as uploaded to the GPU (48 bytes), in the attribute order Unity requires:
    /// position (float3), color (RGBA8: tint, alpha = direction shade), texcoord0 (u, v, texture layer, ambient occlusion),
    /// texcoord1 (block light, sky light), texcoord2 (SNorm8x4: normal, w = emission), texcoord3 (SNorm8x4 tangent, w = sign).
    /// Positions are in render space: minecraft coordinates with Z negated (Unity is left handed). The image is not
    /// mirrored by that, so faces keep their on-screen winding and the index order is reversed instead.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkVertex
    {
        public float X, Y, Z;
        public uint Color;
        public float U, V, Layer, AO;
        public float BlockLight, SkyLight;
        public uint Normal;
        public uint Tangent;
    }

    public sealed class SectionMeshData
    {
        public SectionPos Position;
        public int Version;
        public readonly List<ChunkVertex> Vertices = new List<ChunkVertex>();
        public readonly List<int> OpaqueIndices = new List<int>();
        public readonly List<int> TranslucentIndices = new List<int>();
        /// <summary>Water surfaces get their own submesh so the realistic renderer can use a water shader.</summary>
        public readonly List<int> WaterIndices = new List<int>();
        /// <summary>Clear glass and glass panes: cutout in the classic mode, a reflecting pane in the realistic one.</summary>
        public readonly List<int> GlassIndices = new List<int>();

        /// <summary>Which faces see each other through the section (cave culling), see <see cref="OcclusionGraph"/>.</summary>
        public ulong Occlusion = OcclusionGraph.All;

        /// <summary>Blocks drawn by a block entity renderer (chests, signs, beds...), see <see cref="Worlds.BlockEntityKind"/>.</summary>
        public readonly List<BlockPos> Special = new List<BlockPos>();

        public bool IsEmpty => Vertices.Count == 0;
    }

    /// <summary>
    /// Biome colors from the vanilla color maps (Minosoft's GrassTintCalculator/FoliageTintCalculator): plains by default
    /// (local world, unknown biomes) and per biome of the server registry.
    /// </summary>
    public sealed class BiomeColors
    {
        public int Grass = 0x91BD59;
        public int Foliage = 0x77AB2F;
        public int Water = 0x3F76E4;

        private RgbaImage grassMap;
        private RgbaImage foliageMap;

        public void Load(Assets.AssetManager assets, float temperature = 0.8f, float downfall = 0.4f)
        {
            grassMap = LoadMap(assets, "assets/minecraft/textures/colormap/grass.png");
            foliageMap = LoadMap(assets, "assets/minecraft/textures/colormap/foliage.png");
            Grass = Sample(grassMap, temperature, downfall, Grass);
            Foliage = Sample(foliageMap, temperature, downfall, Foliage);
            Worlds.BiomeRegistry.Plains.Grass = Grass;
            Worlds.BiomeRegistry.Plains.Foliage = Foliage;
        }

        private static RgbaImage LoadMap(Assets.AssetManager assets, string path)
        {
            if (!assets.TryGet(path, out var data)) return null;
            var map = PngDecoder.Decode(data);
            return map.Width == 256 && map.Height == 256 ? map : null;
        }

        private static int Sample(RgbaImage map, float temperature, float downfall, int fallback)
        {
            if (map == null) return fallback;
            temperature = Math.Max(0, Math.Min(1, temperature));
            downfall = Math.Max(0, Math.Min(1, downfall)) * temperature;
            var x = (int)((1.0f - temperature) * 255.0f);
            var y = (int)((1.0f - downfall) * 255.0f);
            return map.GetRgb(x, y);
        }

        /// <summary>Final grass and foliage colors of every biome: override or color map, then the grass modifier.</summary>
        public void Apply(Worlds.BiomeRegistry registry)
        {
            foreach (var biome in registry.ById.Values)
            {
                var grass = biome.GrassColorOverride >= 0 ? biome.GrassColorOverride : Sample(grassMap, biome.Temperature, biome.Downfall, Grass);
                if (biome.GrassModifier == "dark_forest") grass = ((grass & 0xFEFEFE) + 0x28340A) >> 1;
                else if (biome.GrassModifier == "swamp") grass = 0x4C763C; // vanilla picks 0x4C763C or 0x6A7039 by noise
                biome.Grass = grass;
                biome.Foliage = biome.FoliageColorOverride >= 0 ? biome.FoliageColorOverride : Sample(foliageMap, biome.Temperature, biome.Downfall, Foliage);
            }
        }
    }

    /// <summary>Textures of the fluid models (Minosoft's FluidModel). Must be registered before the textures load.</summary>
    public sealed class FluidTextures
    {
        public TextureEntry WaterStill;
        public TextureEntry WaterFlow;
        public TextureEntry LavaStill;
        public TextureEntry LavaFlow;

        public static FluidTextures Register(TextureManager textures) => new FluidTextures
        {
            WaterStill = textures.Get("minecraft:block/water_still"),
            WaterFlow = textures.Get("minecraft:block/water_flow"),
            LavaStill = textures.Get("minecraft:block/lava_still"),
            LavaFlow = textures.Get("minecraft:block/lava_flow"),
        };
    }

    /// <summary>
    /// Meshes one 16x16x16 section: solid blocks (Minosoft's SolidSectionMesher: neighbour face culling, per vertex
    /// ambient occlusion, per face light, biome tint) and fluids (FluidSectionMesher: corner heights, flow direction).
    /// Thread safe: it works on a padded 18x18x18 snapshot of blocks and light, taken by <see cref="Snapshot"/>.
    /// </summary>
    public sealed class SectionMesher
    {
        private const int P = 18;
        private static readonly float[] AOBrightness = { 1.0f, 0.85f, 0.75f, 0.60f };
        private static readonly int[] NoAO = { 0, 0, 0, 0 };

        private static readonly HashSet<string> AlwaysWater = new HashSet<string> { "water", "kelp", "kelp_plant", "seagrass", "tall_seagrass", "bubble_column" };

        private readonly BiomeColors colors;
        private readonly FluidTextures fluids;

        public SectionMesher(BiomeColors colors, FluidTextures fluids)
        {
            this.colors = colors;
            this.fluids = fluids;
        }

        public sealed class SectionSnapshot
        {
            public SectionPos Position;
            public int Version;
            public readonly BlockState[] Blocks = new BlockState[P * P * P];
            public readonly byte[] Light = new byte[P * P * P];
            public bool Empty;
            /// <summary>Biomes of the quarts (4x4x4 cells) of the section with a one quart border in x and z (6 x 4 x 6), or null.</summary>
            public Worlds.Biome[] Biomes;
        }

        private static int PIndex(int x, int y, int z) => ((y + 1) * P + (z + 1)) * P + (x + 1);

        /// <summary>Copies the section and a one block border around it. Must run on the game thread.</summary>
        public static SectionSnapshot Snapshot(Worlds.World world, SectionPos position)
        {
            var snapshot = new SectionSnapshot { Position = position };
            var chunk = world.GetChunk(position.X, position.Z);
            var section = chunk?.GetSection(position.Y);
            snapshot.Version = section?.Version ?? 0;
            if (section == null || section.IsEmpty)
            {
                snapshot.Empty = true;
                return snapshot;
            }

            var baseX = position.X << 4;
            var baseY = position.Y << 4;
            var baseZ = position.Z << 4;
            if (world.HasBiomes)
            {
                snapshot.Biomes = new Worlds.Biome[6 * 4 * 6];
                for (var qz = -1; qz <= 4; qz++)
                {
                    for (var qy = 0; qy < 4; qy++)
                    {
                        for (var qx = -1; qx <= 4; qx++)
                        {
                            snapshot.Biomes[((qz + 1) * 4 + qy) * 6 + qx + 1] = world.GetBiome(baseX + qx * 4, baseY + qy * 4, baseZ + qz * 4);
                        }
                    }
                }
            }
            for (var cz = -1; cz <= 1; cz++)
            {
                for (var cx = -1; cx <= 1; cx++)
                {
                    var neighbour = world.GetChunk(position.X + cx, position.Z + cz);
                    var x0 = cx < 0 ? -1 : cx == 0 ? 0 : 16;
                    var x1 = cx < 0 ? -1 : cx == 0 ? 15 : 16;
                    var z0 = cz < 0 ? -1 : cz == 0 ? 0 : 16;
                    var z1 = cz < 0 ? -1 : cz == 0 ? 15 : 16;
                    for (var y = -1; y <= 16; y++)
                    {
                        var wy = baseY + y;
                        for (var z = z0; z <= z1; z++)
                        {
                            for (var x = x0; x <= x1; x++)
                            {
                                var index = PIndex(x, y, z);
                                if (neighbour == null)
                                {
                                    snapshot.Light[index] = 0xF0;
                                    continue;
                                }
                                var lx = (baseX + x) & 0x0F;
                                var lz = (baseZ + z) & 0x0F;
                                if (wy < world.MinY || wy > world.MaxY)
                                {
                                    snapshot.Light[index] = wy > world.MaxY ? (byte)0xF0 : (byte)0;
                                    continue;
                                }
                                snapshot.Blocks[index] = neighbour.Get(lx, wy, lz);
                                snapshot.Light[index] = (byte)neighbour.GetLight(lx, wy, lz);
                            }
                        }
                    }
                }
            }
            return snapshot;
        }

        public SectionMeshData Mesh(SectionSnapshot snapshot)
        {
            var data = new SectionMeshData { Position = snapshot.Position, Version = snapshot.Version };
            if (snapshot.Empty) return data;
            PrepareTints(snapshot);
            data.Occlusion = OcclusionGraph.Compute((x, y, z) => Occludes(snapshot, x, y, z));

            var worldX = snapshot.Position.X << 4;
            var worldY = snapshot.Position.Y << 4;
            var worldZ = snapshot.Position.Z << 4;
            var aoLevels = new int[4];
            var smoothLight = new float[8];
            var positions = new Vector3[4];
            var uvs = new Vector2[4];
            var windWeights = new float[4];

            for (var y = 0; y < 16; y++)
            {
                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var state = snapshot.Blocks[PIndex(x, y, z)];
                        if (state == null) continue;
                        if (state.Block.Special != Worlds.BlockEntityKind.None) data.Special.Add(new BlockPos(worldX + x, worldY + y, worldZ + z));

                        var fluid = GetFluid(state);
                        if (fluid != Fluid.None) MeshFluid(data, snapshot, x, y, z, fluid, positions, uvs);

                        if (!(state.Model is BlockRenderModel render)) continue;
                        var model = render.Pick(worldX + x, worldY + y, worldZ + z);
                        var tint = GetTint(state.Block, x, y, z);
                        var emission = state.Luminance / 15.0f;
                        var wind = WindOf(state);
                        var glass = state.Block.Name.Path == "glass" || state.Block.Name.Path == "glass_pane";

                        foreach (var quad in model.Quads)
                        {
                            if (quad.CullFace.HasValue && IsCulled(snapshot, state, x, y, z, quad.CullFace.Value)) continue;

                            var face = quad.Face;
                            var lightIndex = quad.OnBoundary || quad.CullFace.HasValue
                                ? PIndex(x + face.X(), y + face.Y(), z + face.Z())
                                : PIndex(x, y, z);
                            var light = snapshot.Light[lightIndex];
                            if (light == 0 && !quad.OnBoundary) light = MaxNeighbourLight(snapshot, x, y, z);

                            var useAO = model.AmbientOcclusion && quad.OnBoundary;
                            for (var i = 0; i < 4; i++) aoLevels[i] = useAO ? AmbientOcclusion(snapshot, x, y, z, face, quad.Positions[i]) : 0;
                            // smooth lighting: per vertex average of the cells around the corner (vanilla "smooth lighting")
                            if (quad.OnBoundary)
                            {
                                for (var i = 0; i < 4; i++) SmoothLight(snapshot, x, y, z, face, quad.Positions[i], smoothLight, i);
                            }

                            for (var i = 0; i < 4; i++)
                            {
                                positions[i] = quad.Positions[i] + new Vector3(x, y, z);
                                uvs[i] = quad.UVs[i];
                            }
                            var indices = quad.Texture.Transparency == TextureTransparency.Translucent ? data.TranslucentIndices : glass ? data.GlassIndices : data.OpaqueIndices;
                            if (wind != Wind.None)
                            {
                                for (var i = 0; i < 4; i++)
                                {
                                    var height = Math.Max(0.0f, Math.Min(1.0f, quad.Positions[i].Y));
                                    windWeights[i] = wind == Wind.Leaves ? 0.5f : wind == Wind.UpperHalf ? 1 + height : height;
                                }
                            }
                            EmitQuad(data, indices, positions, uvs, quad.Texture.Layer, aoLevels, quad.TintIndex >= 0 ? tint : 0xFFFFFF,
                                quad.Shade ? Shade(face) : 1.0f, light, emission, face, false, quad.OnBoundary ? smoothLight : null,
                                wind != Wind.None ? windWeights : null);
                        }
                    }
                }
            }
            return data;
        }

        // ---------------------------------------------------------------- quads

        /// <summary>
        /// Adds a quad given in block local minecraft coordinates, ordered top-left, bottom-left, bottom-right, top-right
        /// as seen from outside, with minecraft uvs (v down). Computes the normal and tangent for normal mapping.
        /// </summary>
        private static void EmitQuad(SectionMeshData data, List<int> indices, Vector3[] positions, Vector2[] uvs, int layer, int[] ao,
            int tint, float shade, byte light, float emission, Direction face, bool doubleSided, float[] vertexLight = null,
            float[] wind = null)
        {
            // render space (z negated) and Unity uvs (v up)
            var p0 = ToRender(positions[0]);
            var p1 = ToRender(positions[1]);
            var p2 = ToRender(positions[2]);
            var p3 = ToRender(positions[3]);
            var t0 = new Vector2(uvs[0].X, 1 - uvs[0].Y);
            var t1 = new Vector2(uvs[1].X, 1 - uvs[1].Y);
            var t2 = new Vector2(uvs[2].X, 1 - uvs[2].Y);

            Frame(positions, p0, p1, p2, t0, t1, t2, emission, face, out var normal, out var packedNormal, out var packedTangent);
            var color = (uint)(tint & 0xFF0000) >> 16 | (uint)(tint & 0x00FF00) | (uint)(tint & 0x0000FF) << 16 | (uint)(Math.Max(0, Math.Min(255, (int)(shade * 255))) << 24);
            var blockLight = (light & 0x0F) / 15.0f;
            var skyLight = (light >> 4) / 15.0f;

            var first = data.Vertices.Count;
            for (var i = 0; i < 4; i++)
            {
                var p = ToRender(positions[i]);
                data.Vertices.Add(new ChunkVertex
                {
                    X = p.X,
                    Y = p.Y,
                    Z = p.Z,
                    Color = color,
                    U = uvs[i].X,
                    V = 1.0f - uvs[i].Y,
                    Layer = layer,
                    AO = AOBrightness[ao[i]],
                    BlockLight = vertexLight != null ? vertexLight[i] : blockLight,
                    SkyLight = vertexLight != null ? vertexLight[4 + i] : skyLight,
                    Normal = packedNormal,
                    Tangent = wind != null ? WithWind(packedTangent, wind[i]) : packedTangent,
                });
            }

            // the quad is counter clockwise on screen (OpenGL front face); Unity expects clockwise front faces
            if (ao[0] + ao[2] > ao[1] + ao[3])
            {
                indices.Add(first + 1);
                indices.Add(first + 3);
                indices.Add(first + 2);
                indices.Add(first + 1);
                indices.Add(first);
                indices.Add(first + 3);
            }
            else
            {
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 1);
                indices.Add(first);
                indices.Add(first + 3);
                indices.Add(first + 2);
            }

            if (!doubleSided) return;
            // back side (visible from inside the fluid) with the normal flipped
            var backNormal = PackSNorm(-normal.X, -normal.Y, -normal.Z, emission);
            var back = data.Vertices.Count;
            for (var i = 0; i < 4; i++)
            {
                var vertex = data.Vertices[first + i];
                vertex.Normal = backNormal;
                data.Vertices.Add(vertex);
            }
            indices.Add(back);
            indices.Add(back + 1);
            indices.Add(back + 2);
            indices.Add(back);
            indices.Add(back + 2);
            indices.Add(back + 3);
        }

        private static Vector3 ToRender(Vector3 p) => new Vector3(p.X, p.Y, -p.Z);

        // ---------------------------------------------------------------- wind

        private enum Wind : byte
        {
            None,
            Leaves,     // the whole block sways a little
            Plant,      // rooted at the bottom of the block
            UpperHalf,  // upper half of a tall plant: continues the sway of the lower half
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Block, Wind> WindKinds =
            new System.Collections.Concurrent.ConcurrentDictionary<Block, Wind>();

        private static readonly HashSet<string> WindPlants = new HashSet<string>
        {
            "grass", "short_grass", "fern", "dead_bush", "dandelion", "poppy", "blue_orchid", "allium", "azure_bluet",
            "red_tulip", "orange_tulip", "white_tulip", "pink_tulip", "oxeye_daisy", "cornflower", "lily_of_the_valley",
            "wither_rose", "torchflower", "wheat", "carrots", "potatoes", "beetroots", "sweet_berry_bush", "seagrass",
            "pink_petals", "crimson_roots", "warped_roots", "nether_sprouts",
        };

        private static readonly HashSet<string> WindTallPlants = new HashSet<string>
        {
            "tall_grass", "large_fern", "sunflower", "lilac", "rose_bush", "peony", "tall_seagrass", "pitcher_plant",
        };

        /// <summary>How the wind moves a block in the realistic mode (the shader reads it from the vertices).</summary>
        private static Wind WindOf(BlockState state)
        {
            var kind = WindKinds.GetOrAdd(state.Block, block =>
            {
                var path = block.Name.Path;
                if (path.EndsWith("_leaves") || path == "vine") return Wind.Leaves;
                if (WindTallPlants.Contains(path)) return Wind.UpperHalf;
                if (WindPlants.Contains(path) || path.EndsWith("_sapling")) return Wind.Plant;
                return Wind.None;
            });
            // tall plants: the lower half is rooted, the upper half continues it
            if (kind == Wind.UpperHalf && state["half"] != "upper") return Wind.Plant;
            return kind;
        }

        private static uint WithWind(uint packedTangent, float weight)
        {
            var sign = (sbyte)(byte)(packedTangent >> 24) < 0 ? -1.0f : 1.0f;
            var w = sign * (0.05f + Math.Max(0.0f, Math.Min(2.0f, weight)) * 0.4f);
            var packed = (uint)(byte)(sbyte)Math.Round(w * 127.0f);
            return (packedTangent & 0x00FFFFFF) | (packed << 24);
        }

        /// <summary>Normal and tangent of a quad (minecraft space positions, render space p0..p2, Unity uvs), packed for the
        /// chunk vertex format; also used by the block item meshes.</summary>
        public static void Frame(Vector3[] positions, Vector3 p0, Vector3 p1, Vector3 p2, Vector2 t0, Vector2 t1, Vector2 t2,
            float emission, Direction face, out Vector3 normal, out uint packedNormal, out uint packedTangent)
        {
            // the face is counter clockwise in minecraft space, so this points outwards there; z flips with the space
            var normalMc = Vector3.Cross(positions[1] - positions[0], positions[2] - positions[1]);
            if (normalMc.LengthSquared() < 1e-10f) normalMc = Vector3.Cross(positions[2] - positions[0], positions[3] - positions[0]);
            if (normalMc.LengthSquared() < 1e-10f) normalMc = new Vector3(face.X(), face.Y(), face.Z());
            normalMc = Vector3.Normalize(normalMc);
            normal = new Vector3(normalMc.X, normalMc.Y, -normalMc.Z);

            var e1 = p1 - p0;
            var e2 = p2 - p0;
            var d1 = t1 - t0;
            var d2 = t2 - t0;
            var det = d1.X * d2.Y - d2.X * d1.Y;
            Vector3 tangent, bitangent;
            if (Math.Abs(det) < 1e-12f)
            {
                tangent = Math.Abs(normal.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal)) : Vector3.UnitX;
                bitangent = Vector3.Cross(normal, tangent);
            }
            else
            {
                var r = 1.0f / det;
                tangent = (e1 * d2.Y - e2 * d1.Y) * r;
                bitangent = (e2 * d1.X - e1 * d2.X) * r;
            }
            tangent -= normal * Vector3.Dot(normal, tangent);
            tangent = tangent.LengthSquared() < 1e-12f ? Vector3.UnitX : Vector3.Normalize(tangent);
            var sign = Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0 ? -1.0f : 1.0f;

            packedNormal = PackSNorm(normal.X, normal.Y, normal.Z, emission);
            packedTangent = PackSNorm(tangent.X, tangent.Y, tangent.Z, sign);
        }

        private static uint PackSNorm(float x, float y, float z, float w)
        {
            uint Pack(float v) => (uint)(byte)(sbyte)Math.Round(Math.Max(-1.0f, Math.Min(1.0f, v)) * 127.0f);
            return Pack(x) | (Pack(y) << 8) | (Pack(z) << 16) | (Pack(w) << 24);
        }

        // ---------------------------------------------------------------- fluids

        private enum Fluid
        {
            None,
            Water,
            Lava,
        }

        private static Fluid GetFluid(BlockState state)
        {
            if (state == null) return Fluid.None;
            var path = state.Block.Name.Path;
            if (path == "lava") return Fluid.Lava;
            if (AlwaysWater.Contains(path) || state["waterlogged"] == "true") return Fluid.Water;
            return Fluid.None;
        }

        /// <summary>Vanilla fluid height: amount / 9, sources and falling fluid are 8/9.</summary>
        private static float FluidHeight(BlockState state, Fluid fluid)
        {
            if (GetFluid(state) != fluid) return 0.0f;
            var level = state.Block.IsFluid ? int.Parse(state["level"] ?? "0") : 0;
            if (level == 0 || level >= 8) return 8.0f / 9.0f;
            return (8 - level) / 9.0f;
        }

        /// <summary>Minosoft's FluidCornerHeightUtil.getFluidHeight.</summary>
        private static float NeighbourHeight(SectionSnapshot snapshot, int x, int y, int z, Fluid fluid)
        {
            if (GetFluid(snapshot.Blocks[PIndex(x, y + 1, z)]) == fluid) return 1.0f;
            var state = snapshot.Blocks[PIndex(x, y, z)];
            if (state == null) return 0.0f;
            var height = FluidHeight(state, fluid);
            if (height != 0.0f) return height;
            return state.FullOpaque || state.HasCollision && IsFullCube(state) ? -1.0f : 0.0f;
        }

        private static bool IsFullCube(BlockState state)
        {
            if (state.CollisionShape.Length != 1) return false;
            var box = state.CollisionShape[0];
            return box.Min.X <= 0 && box.Min.Y <= 0 && box.Min.Z <= 0 && box.Max.X >= 1 && box.Max.Y >= 1 && box.Max.Z >= 1;
        }

        /// <summary>Minosoft's averageHeight: a corner is 1 if any neighbour is covered, heights &gt;= 0.8 weigh 10 times more.</summary>
        private static float Corner(float a, float b, float c, float d)
        {
            if (a >= 1.0f || b >= 1.0f || c >= 1.0f || d >= 1.0f) return 1.0f;
            float total = 0;
            var count = 0;
            foreach (var value in new[] { a, b, c, d })
            {
                if (value < 0.0f) continue;
                var weight = value >= 0.8f ? 10 : 1;
                total += value * weight;
                count += weight;
            }
            return count == 0 ? 0.0f : total / count;
        }

        private bool IsFluidSideCulled(SectionSnapshot snapshot, int x, int y, int z, Direction direction, Fluid fluid)
        {
            var neighbour = snapshot.Blocks[PIndex(x + direction.X(), y + direction.Y(), z + direction.Z())];
            if (neighbour == null) return false;
            if (GetFluid(neighbour) == fluid) return true;
            return neighbour.OpaqueSide[(int)direction.Opposite()];
        }

        /// <summary>Port of Minosoft's FluidSectionMesher for one block.</summary>
        private void MeshFluid(SectionMeshData data, SectionSnapshot snapshot, int x, int y, int z, Fluid fluid, Vector3[] positions, Vector2[] uvs)
        {
            var water = fluid == Fluid.Water;
            var still = water ? fluids?.WaterStill : fluids?.LavaStill;
            var flow = water ? fluids?.WaterFlow : fluids?.LavaFlow;
            if (still == null || flow == null) return;

            var up = GetFluid(snapshot.Blocks[PIndex(x, y + 1, z)]) != fluid;
            var down = !IsFluidSideCulled(snapshot, x, y, z, Direction.Down, fluid);
            var north = !IsFluidSideCulled(snapshot, x, y, z, Direction.North, fluid);
            var south = !IsFluidSideCulled(snapshot, x, y, z, Direction.South, fluid);
            var west = !IsFluidSideCulled(snapshot, x, y, z, Direction.West, fluid);
            var east = !IsFluidSideCulled(snapshot, x, y, z, Direction.East, fluid);
            if (!up && !down && !north && !south && !west && !east) return;

            // heights of the 3x3 neighbourhood, then the 4 corners (0: -x-z, 1: +x-z, 2: +x+z, 3: -x+z)
            var h = new float[9];
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++) h[(dz + 1) * 3 + dx + 1] = NeighbourHeight(snapshot, x + dx, y, z + dz, fluid);
            }
            var c0 = Corner(h[0], h[1], h[3], h[4]);
            var c1 = Corner(h[1], h[2], h[4], h[5]);
            var c2 = Corner(h[4], h[5], h[7], h[8]);
            var c3 = Corner(h[3], h[4], h[6], h[7]);

            var light = snapshot.Light[PIndex(x, y, z)];
            var above = snapshot.Light[PIndex(x, y + 1, z)];
            light = (byte)(Math.Max(light & 0xF0, above & 0xF0) | Math.Max(light & 0x0F, above & 0x0F));
            var tint = water ? GetTint(TintType.Water, x, y, z) : 0xFFFFFF;
            var emission = water ? 0.0f : 1.0f;
            var indices = water ? data.WaterIndices : data.OpaqueIndices;
            var origin = new Vector3(x, y, z);

            if (up)
            {
                // flow direction from the height difference to the horizontal neighbours (simplified Fluid.updateVelocity)
                var self = h[4];
                float vx = 0, vz = 0;
                if (h[3] >= 0) vx -= self - h[3];
                if (h[5] >= 0) vx += self - h[5];
                if (h[1] >= 0) vz -= self - h[1];
                if (h[7] >= 0) vz += self - h[7];
                var flowing = Math.Abs(vx) > 0.001f || Math.Abs(vz) > 0.001f;

                // TL, BL, BR, TR seen from above = corners 0, 3, 2, 1
                positions[0] = origin + new Vector3(0, c0, 0);
                positions[1] = origin + new Vector3(0, c3, 1);
                positions[2] = origin + new Vector3(1, c2, 1);
                positions[3] = origin + new Vector3(1, c1, 0);
                TextureEntry texture;
                if (!flowing)
                {
                    texture = still;
                    uvs[0] = new Vector2(0, 0);
                    uvs[1] = new Vector2(0, 1);
                    uvs[2] = new Vector2(1, 1);
                    uvs[3] = new Vector2(1, 0);
                }
                else
                {
                    texture = flow;
                    var angle = (float)Math.Atan2(vx, vz);
                    var s = (float)Math.Sin(angle) * 0.25f;
                    var c = (float)Math.Cos(angle) * 0.25f;
                    uvs[0] = new Vector2(0.5f - c + s, 0.5f - c - s); // corner 0
                    uvs[3] = new Vector2(0.5f + c + s, 0.5f - c + s); // corner 1
                    uvs[2] = new Vector2(0.5f + c - s, 0.5f + c + s); // corner 2
                    uvs[1] = new Vector2(0.5f - c - s, 0.5f + c - s); // corner 3
                }
                EmitQuad(data, indices, positions, uvs, texture.Layer, NoAO, tint, 1.0f, light, emission, Direction.Up, water);
            }

            if (down)
            {
                positions[0] = origin + new Vector3(0, 0, 1);
                positions[1] = origin + new Vector3(0, 0, 0);
                positions[2] = origin + new Vector3(1, 0, 0);
                positions[3] = origin + new Vector3(1, 0, 1);
                uvs[0] = new Vector2(0, 0);
                uvs[1] = new Vector2(0, 1);
                uvs[2] = new Vector2(1, 1);
                uvs[3] = new Vector2(1, 0);
                EmitQuad(data, indices, positions, uvs, still.Layer, NoAO, tint, 0.5f, light, emission, Direction.Down, false);
            }

            if (north) Side(data, indices, origin, new Vector3(1, c1, 0), new Vector3(0, c0, 0), flow, tint, 0.8f, light, emission, Direction.North, water, positions, uvs);
            if (south) Side(data, indices, origin, new Vector3(0, c3, 1), new Vector3(1, c2, 1), flow, tint, 0.8f, light, emission, Direction.South, water, positions, uvs);
            if (west) Side(data, indices, origin, new Vector3(0, c0, 0), new Vector3(0, c3, 1), flow, tint, 0.6f, light, emission, Direction.West, water, positions, uvs);
            if (east) Side(data, indices, origin, new Vector3(1, c2, 1), new Vector3(1, c1, 0), flow, tint, 0.6f, light, emission, Direction.East, water, positions, uvs);
        }

        /// <summary>Side face from the top-left to the top-right corner (seen from outside), down to y = 0.</summary>
        private static void Side(SectionMeshData data, List<int> indices, Vector3 origin, Vector3 topLeft, Vector3 topRight, TextureEntry texture,
            int tint, float shade, byte light, float emission, Direction face, bool doubleSided, Vector3[] positions, Vector2[] uvs)
        {
            positions[0] = origin + topLeft;
            positions[1] = origin + new Vector3(topLeft.X, 0, topLeft.Z);
            positions[2] = origin + new Vector3(topRight.X, 0, topRight.Z);
            positions[3] = origin + topRight;
            uvs[0] = new Vector2(0.0f, (1.0f - topLeft.Y) * 0.5f);
            uvs[1] = new Vector2(0.0f, 0.5f);
            uvs[2] = new Vector2(0.5f, 0.5f);
            uvs[3] = new Vector2(0.5f, (1.0f - topRight.Y) * 0.5f);
            EmitQuad(data, indices, positions, uvs, texture.Layer, NoAO, tint, shade, light, emission, face, doubleSided);
        }

        // ---------------------------------------------------------------- solid blocks

        /// <summary>Minosoft's FaceCulling: hidden by an opaque neighbour side, or by the same block (glass next to glass).</summary>
        private static bool IsCulled(SectionSnapshot snapshot, BlockState state, int x, int y, int z, Direction direction)
        {
            var neighbour = snapshot.Blocks[PIndex(x + direction.X(), y + direction.Y(), z + direction.Z())];
            if (neighbour == null) return false;
            var opposite = (int)direction.Opposite();
            if (neighbour.OpaqueSide[opposite]) return true;
            return neighbour.Block == state.Block && neighbour.FullSide[opposite] && state.FullSide[(int)direction];
        }

        private static byte MaxNeighbourLight(SectionSnapshot snapshot, int x, int y, int z)
        {
            int block = 0, sky = 0;
            foreach (var direction in Directions.All)
            {
                var light = snapshot.Light[PIndex(x + direction.X(), y + direction.Y(), z + direction.Z())];
                block = Math.Max(block, light & 0x0F);
                sky = Math.Max(sky, light >> 4);
            }
            return (byte)((sky << 4) | block);
        }

        /// <summary>Classic vertex AO (Minosoft's AmbientOcclusionUtil): two sides and the corner in the layer in front of the face.</summary>
        private static int AmbientOcclusion(SectionSnapshot snapshot, int x, int y, int z, Direction face, Vector3 vertex)
        {
            int fx = x + face.X(), fy = y + face.Y(), fz = z + face.Z();
            int ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
            switch (face.GetAxis())
            {
                case Axis.X:
                    ay = vertex.Y > 0.5f ? 1 : -1;
                    bz = vertex.Z > 0.5f ? 1 : -1;
                    break;
                case Axis.Y:
                    ax = vertex.X > 0.5f ? 1 : -1;
                    bz = vertex.Z > 0.5f ? 1 : -1;
                    break;
                default:
                    ax = vertex.X > 0.5f ? 1 : -1;
                    by = vertex.Y > 0.5f ? 1 : -1;
                    break;
            }
            var side1 = Occludes(snapshot, fx + ax, fy + ay, fz + az);
            var side2 = Occludes(snapshot, fx + bx, fy + by, fz + bz);
            if (side1 && side2) return 3;
            var corner = Occludes(snapshot, fx + ax + bx, fy + ay + by, fz + az + bz);
            return (side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0);
        }

        /// <summary>
        /// Averages block and sky light of the cell in front of the face and the (non opaque) cells next to the vertex,
        /// so light changes gradually across faces instead of in block steps. Writes block light to result[index] and
        /// sky light to result[4 + index] (0..1).
        /// </summary>
        private static void SmoothLight(SectionSnapshot snapshot, int x, int y, int z, Direction face, Vector3 vertex, float[] result, int index)
        {
            int fx = x + face.X(), fy = y + face.Y(), fz = z + face.Z();
            int ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
            switch (face.GetAxis())
            {
                case Axis.X:
                    ay = vertex.Y > 0.5f ? 1 : -1;
                    bz = vertex.Z > 0.5f ? 1 : -1;
                    break;
                case Axis.Y:
                    ax = vertex.X > 0.5f ? 1 : -1;
                    bz = vertex.Z > 0.5f ? 1 : -1;
                    break;
                default:
                    ax = vertex.X > 0.5f ? 1 : -1;
                    by = vertex.Y > 0.5f ? 1 : -1;
                    break;
            }
            float block = 0, sky = 0;
            var count = 0;
            void Sample(int sx, int sy, int sz)
            {
                if (sx < -1 || sy < -1 || sz < -1 || sx > 16 || sy > 16 || sz > 16) return;
                var cell = PIndex(sx, sy, sz);
                var state = snapshot.Blocks[cell];
                if (state != null && state.FullOpaque) return;
                var light = snapshot.Light[cell];
                block += light & 0x0F;
                sky += light >> 4;
                count++;
            }
            Sample(fx, fy, fz);
            var side1 = Occludes(snapshot, fx + ax, fy + ay, fz + az);
            var side2 = Occludes(snapshot, fx + bx, fy + by, fz + bz);
            Sample(fx + ax, fy + ay, fz + az);
            Sample(fx + bx, fy + by, fz + bz);
            if (!(side1 && side2)) Sample(fx + ax + bx, fy + ay + by, fz + az + bz);
            if (count == 0)
            {
                var light = snapshot.Light[PIndex(fx, fy, fz)];
                result[index] = (light & 0x0F) / 15.0f;
                result[4 + index] = (light >> 4) / 15.0f;
                return;
            }
            result[index] = block / count / 15.0f;
            result[4 + index] = sky / count / 15.0f;
        }

        private static bool Occludes(SectionSnapshot snapshot, int x, int y, int z)
        {
            if (x < -1 || y < -1 || z < -1 || x > 16 || y > 16 || z > 16) return false;
            var state = snapshot.Blocks[PIndex(x, y, z)];
            return state != null && state.FullOpaque;
        }

        /// <summary>Minosoft's Shades: down 0.5, up 1.0, north/south 0.8, west/east 0.6.</summary>
        private static float Shade(Direction direction)
        {
            switch (direction)
            {
                case Direction.Down: return 0.5f;
                case Direction.Up: return 1.0f;
                case Direction.North:
                case Direction.South:
                    return 0.8f;
                default: return 0.6f;
            }
        }

        // tint colors of the 6 x 4 x 6 quarts of the section being meshed (grass, foliage, water), or null without biomes
        [ThreadStatic] private static int[] quartGrass;
        [ThreadStatic] private static int[] quartFoliage;
        [ThreadStatic] private static int[] quartWater;
        [ThreadStatic] private static bool hasQuarts;

        private void PrepareTints(SectionSnapshot snapshot)
        {
            hasQuarts = snapshot.Biomes != null;
            if (!hasQuarts) return;
            if (quartGrass == null)
            {
                quartGrass = new int[144];
                quartFoliage = new int[144];
                quartWater = new int[144];
            }
            for (var i = 0; i < 144; i++)
            {
                var biome = snapshot.Biomes[i] ?? Worlds.BiomeRegistry.Plains;
                quartGrass[i] = biome.Grass;
                quartFoliage[i] = biome.Foliage;
                quartWater[i] = biome.WaterColor;
            }
        }

        private int GetTint(Block block, int x, int y, int z)
        {
            if (block.Tint == TintType.Constant) return block.ConstantTint;
            return GetTint(block.Tint, x, y, z);
        }

        /// <summary>
        /// Biome tint at a block of the section: bilinear between the quart centers around it, which blends biome borders
        /// like vanilla's biome blend (5x5 average) does.
        /// </summary>
        private int GetTint(TintType type, int x, int y, int z)
        {
            if (type == TintType.None || type == TintType.Constant) return 0xFFFFFF;
            if (!hasQuarts) return type == TintType.Grass ? colors.Grass : type == TintType.Foliage ? colors.Foliage : colors.Water;
            var source = type == TintType.Grass ? quartGrass : type == TintType.Foliage ? quartFoliage : quartWater;
            var fx = (x - 1.5f) / 4f;
            var fz = (z - 1.5f) / 4f;
            var qx = (int)Math.Floor(fx);
            var qz = (int)Math.Floor(fz);
            var tx = fx - qx;
            var tz = fz - qz;
            var qy = Math.Max(0, Math.Min(3, y >> 2));
            int Index(int ix, int iz) => ((Math.Max(-1, Math.Min(4, iz)) + 1) * 4 + qy) * 6 + Math.Max(-1, Math.Min(4, ix)) + 1;
            var c00 = source[Index(qx, qz)];
            var c10 = source[Index(qx + 1, qz)];
            var c01 = source[Index(qx, qz + 1)];
            var c11 = source[Index(qx + 1, qz + 1)];
            if (c00 == c10 && c00 == c01 && c00 == c11) return c00;
            int Channel(int shift)
            {
                float a = (c00 >> shift) & 0xFF, b = (c10 >> shift) & 0xFF, c = (c01 >> shift) & 0xFF, d = (c11 >> shift) & 0xFF;
                var top = a + (b - a) * tx;
                var bottom = c + (d - c) * tx;
                return (int)(top + (bottom - top) * tz + 0.5f) & 0xFF;
            }
            return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        }
    }
}
