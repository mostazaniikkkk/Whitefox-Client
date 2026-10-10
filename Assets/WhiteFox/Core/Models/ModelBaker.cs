// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Data;
using WhiteFox.Core.Textures;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Models
{
    /// <summary>
    /// Turns blockstate + model json into <see cref="BlockRenderModel"/>s for every block state
    /// (Minosoft: ModelLoader.load + bake, SingleBlockStateApply.bake).
    /// Usage: <see cref="Bake"/>, then load the textures, then <see cref="FinalizeStates"/>.
    /// </summary>
    public sealed class ModelBaker
    {
        private readonly AssetManager assets;
        private readonly TextureManager textures;
        private readonly ModelLoader loader;
        private readonly Dictionary<string, BakedModel> bakedCache = new Dictionary<string, BakedModel>();

        public int BakedStates { get; private set; }

        public ModelBaker(AssetManager assets, TextureManager textures)
        {
            this.assets = assets;
            this.textures = textures;
            loader = new ModelLoader(assets);
        }

        public void Bake(BlockRegistry registry)
        {
            foreach (var block in registry.Blocks)
            {
                if (block.IsAir) continue;
                if (!assets.TryGetText(block.Name.ToAssetPath("blockstates/", ".json"), out var text)) continue;
                BlockStateDefinition definition;
                try
                {
                    definition = BlockStateDefinition.Parse(JObject.Parse(text));
                }
                catch (Exception exception)
                {
                    Log.Warn("Invalid blockstate " + block.Name + ": " + exception.Message);
                    continue;
                }

                foreach (var state in block.States)
                {
                    var selected = definition.Select(state);
                    if (selected.Count == 0) continue;
                    state.Model = definition.IsMultipart ? BakeMultipart(selected) : BakeWeighted(selected[0]);
                    BakedStates++;
                }
            }
        }

        /// <summary>Needs loaded textures: computes culling side info and the outline shape of every state.</summary>
        public void FinalizeStates(BlockRegistry registry)
        {
            foreach (var state in registry.AllStates())
            {
                for (var i = 0; i < 6; i++)
                {
                    state.FullSide[i] = false;
                    state.OpaqueSide[i] = false;
                }
                if (!(state.Model is BlockRenderModel render))
                {
                    state.FullOpaque = false;
                    continue;
                }
                var model = render.Models[0];
                var minX = float.MaxValue;
                var minY = float.MaxValue;
                var minZ = float.MaxValue;
                var maxX = float.MinValue;
                var maxY = float.MinValue;
                var maxZ = float.MinValue;
                foreach (var quad in model.Quads)
                {
                    foreach (var p in quad.Positions)
                    {
                        minX = Math.Min(minX, p.X);
                        minY = Math.Min(minY, p.Y);
                        minZ = Math.Min(minZ, p.Z);
                        maxX = Math.Max(maxX, p.X);
                        maxY = Math.Max(maxY, p.Y);
                        maxZ = Math.Max(maxZ, p.Z);
                    }
                    if (!quad.CoversFullSide) continue;
                    state.FullSide[(int)quad.Face] = true;
                    if (quad.Texture.Transparency == TextureTransparency.Opaque) state.OpaqueSide[(int)quad.Face] = true;
                }
                var full = true;
                for (var i = 0; i < 6; i++) full &= state.OpaqueSide[i];
                state.FullOpaque = full && !state.Block.Transparent;

                if (state.OutlineShape.Length == 0 && model.Quads.Length > 0)
                {
                    state.OutlineShape = new[] { new AABB(Clamp01(minX), Clamp01(minY), Clamp01(minZ), Clamp01(maxX), Clamp01(maxY), Clamp01(maxZ)) };
                }
            }
        }

        private static double Clamp01(float value) => Math.Max(0.0, Math.Min(1.0, value));

        private BlockRenderModel BakeWeighted(List<ModelApply> applies)
        {
            var models = new BakedModel[applies.Count];
            var weights = new int[applies.Count];
            var total = 0;
            for (var i = 0; i < applies.Count; i++)
            {
                models[i] = BakeApply(applies[i]);
                weights[i] = Math.Max(1, applies[i].Weight);
                total += weights[i];
            }
            return new BlockRenderModel { Models = models, Weights = weights, TotalWeight = total };
        }

        /// <summary>Multipart: every matching part contributes its quads (weighted parts use their first entry).</summary>
        private BlockRenderModel BakeMultipart(List<List<ModelApply>> parts)
        {
            var quads = new List<BakedQuad>();
            var ao = true;
            foreach (var part in parts)
            {
                if (part.Count == 0) continue;
                var baked = BakeApply(part[0]);
                quads.AddRange(baked.Quads);
                ao &= baked.AmbientOcclusion;
            }
            var model = new BakedModel { Quads = quads.ToArray(), AmbientOcclusion = ao };
            return new BlockRenderModel { Models = new[] { model }, Weights = new[] { 1 }, TotalWeight = 1 };
        }

        /// <summary>A block model by name without rotation (item models of block items: block/oak_fence_inventory...).</summary>
        public BakedModel BakeModel(ResourceLocation name) => BakeApply(new ModelApply { Model = name });

        private BakedModel BakeApply(ModelApply apply)
        {
            var key = apply.Model + "|" + apply.X + "|" + apply.Y + "|" + apply.UVLock;
            if (bakedCache.TryGetValue(key, out var cached)) return cached;

            var model = loader.Load(apply.Model);
            if (model == null) return bakedCache[key] = BakedModel.Empty;

            var quads = new List<BakedQuad>();
            foreach (var element in model.ResolvedElements)
            {
                foreach (var pair in element.Faces)
                {
                    quads.Add(BakeFace(model, element, pair.Key, pair.Value, apply));
                }
            }
            var baked = new BakedModel { Quads = quads.ToArray(), AmbientOcclusion = model.ResolvedAmbientOcclusion };
            bakedCache[key] = baked;
            return baked;
        }

        private BakedQuad BakeFace(BlockModel model, ModelElement element, Direction direction, ModelFace face, ModelApply apply)
        {
            var quad = new BakedQuad
            {
                TintIndex = face.TintIndex,
                Shade = element.Shade,
            };
            var texture = model.ResolveTexture(face.Texture);
            quad.Texture = textures.Get(texture.HasValue ? texture.Value.WithPrefix("") : TextureManager.Missing);

            // 1. positions of the unrotated face
            CornerPositions(direction, element.From, element.To, quad.Positions);

            // 2. uvs (in pixels), shifted by the face rotation
            var uv = face.UV ?? AutoUV(direction, element.From, element.To);
            var corners = new[]
            {
                new Vector2(uv[0], uv[1]),
                new Vector2(uv[0], uv[3]),
                new Vector2(uv[2], uv[3]),
                new Vector2(uv[2], uv[1]),
            };
            var shift = face.Rotation / 90;
            for (var i = 0; i < 4; i++) quad.UVs[i] = corners[(i + shift) % 4] / 16.0f;

            // 3. element rotation
            if (element.Rotation != null) ApplyElementRotation(element.Rotation, quad.Positions);

            // 4. blockstate rotation (x first, then y; both clockwise in 90 degree steps)
            var normal = new Vector3(direction.X(), direction.Y(), direction.Z());
            var cull = face.CullFace;
            if (apply.X != 0 || apply.Y != 0)
            {
                for (var i = 0; i < 4; i++) quad.Positions[i] = RotateBlock(quad.Positions[i] - new Vector3(0.5f), apply.X, apply.Y) + new Vector3(0.5f);
                normal = RotateBlock(normal, apply.X, apply.Y);
                if (cull.HasValue)
                {
                    var c = RotateBlock(new Vector3(cull.Value.X(), cull.Value.Y(), cull.Value.Z()), apply.X, apply.Y);
                    cull = Directions.FromVector(c.X, c.Y, c.Z);
                }
            }
            quad.Face = Directions.FromVector(normal.X, normal.Y, normal.Z);
            quad.CullFace = cull;

            // 5. uv lock: project the rotated face onto the texture again so it stays aligned with the world
            if (apply.UVLock && (apply.X != 0 || apply.Y != 0))
            {
                for (var i = 0; i < 4; i++) quad.UVs[i] = ProjectUV(quad.Face, quad.Positions[i]);
            }

            quad.OnBoundary = IsOnBoundary(quad);
            return quad;
        }

        private static bool IsOnBoundary(BakedQuad quad)
        {
            const float e = 0.0001f;
            var target = quad.Face.IsPositive() ? 1.0f : 0.0f;
            foreach (var p in quad.Positions)
            {
                float value;
                switch (quad.Face.GetAxis())
                {
                    case Axis.X:
                        value = p.X;
                        break;
                    case Axis.Y:
                        value = p.Y;
                        break;
                    default:
                        value = p.Z;
                        break;
                }
                if (Math.Abs(value - target) > e) return false;
            }
            return true;
        }

        /// <summary>Corners in the order top-left, bottom-left, bottom-right, top-right matching <see cref="AutoUV"/>.</summary>
        private static void CornerPositions(Direction direction, Vector3 from, Vector3 to, Vector3[] result)
        {
            switch (direction)
            {
                case Direction.Up:
                    result[0] = new Vector3(from.X, to.Y, from.Z);
                    result[1] = new Vector3(from.X, to.Y, to.Z);
                    result[2] = new Vector3(to.X, to.Y, to.Z);
                    result[3] = new Vector3(to.X, to.Y, from.Z);
                    break;
                case Direction.Down:
                    result[0] = new Vector3(from.X, from.Y, to.Z);
                    result[1] = new Vector3(from.X, from.Y, from.Z);
                    result[2] = new Vector3(to.X, from.Y, from.Z);
                    result[3] = new Vector3(to.X, from.Y, to.Z);
                    break;
                case Direction.North:
                    result[0] = new Vector3(to.X, to.Y, from.Z);
                    result[1] = new Vector3(to.X, from.Y, from.Z);
                    result[2] = new Vector3(from.X, from.Y, from.Z);
                    result[3] = new Vector3(from.X, to.Y, from.Z);
                    break;
                case Direction.South:
                    result[0] = new Vector3(from.X, to.Y, to.Z);
                    result[1] = new Vector3(from.X, from.Y, to.Z);
                    result[2] = new Vector3(to.X, from.Y, to.Z);
                    result[3] = new Vector3(to.X, to.Y, to.Z);
                    break;
                case Direction.West:
                    result[0] = new Vector3(from.X, to.Y, from.Z);
                    result[1] = new Vector3(from.X, from.Y, from.Z);
                    result[2] = new Vector3(from.X, from.Y, to.Z);
                    result[3] = new Vector3(from.X, to.Y, to.Z);
                    break;
                default:
                    result[0] = new Vector3(to.X, to.Y, to.Z);
                    result[1] = new Vector3(to.X, from.Y, to.Z);
                    result[2] = new Vector3(to.X, from.Y, from.Z);
                    result[3] = new Vector3(to.X, to.Y, from.Z);
                    break;
            }
        }

        /// <summary>Vanilla's default uv when a face has none: the projection of the element onto the face.</summary>
        private static float[] AutoUV(Direction direction, Vector3 from, Vector3 to)
        {
            from *= 16.0f;
            to *= 16.0f;
            switch (direction)
            {
                case Direction.Down: return new[] { from.X, 16 - to.Z, to.X, 16 - from.Z };
                case Direction.Up: return new[] { from.X, from.Z, to.X, to.Z };
                case Direction.North: return new[] { 16 - to.X, 16 - to.Y, 16 - from.X, 16 - from.Y };
                case Direction.South: return new[] { from.X, 16 - to.Y, to.X, 16 - from.Y };
                case Direction.West: return new[] { from.Z, 16 - to.Y, to.Z, 16 - from.Y };
                default: return new[] { 16 - to.Z, 16 - to.Y, 16 - from.Z, 16 - from.Y };
            }
        }

        /// <summary>Same projection as <see cref="AutoUV"/> for a single point (0..1 result).</summary>
        private static Vector2 ProjectUV(Direction direction, Vector3 p)
        {
            switch (direction)
            {
                case Direction.Down: return new Vector2(p.X, 1 - p.Z);
                case Direction.Up: return new Vector2(p.X, p.Z);
                case Direction.North: return new Vector2(1 - p.X, 1 - p.Y);
                case Direction.South: return new Vector2(p.X, 1 - p.Y);
                case Direction.West: return new Vector2(p.Z, 1 - p.Y);
                default: return new Vector2(1 - p.Z, 1 - p.Y);
            }
        }

        private static void ApplyElementRotation(ElementRotation rotation, Vector3[] positions)
        {
            var angle = rotation.Angle * (float)MathUtil.DegToRad;
            var scale = Vector3.One;
            if (rotation.Rescale)
            {
                var factor = 1.0f / (float)Math.Cos(Math.Abs(rotation.Angle) == 22.5f ? Math.PI / 8 : Math.PI / 4);
                scale = rotation.Axis == Axis.X ? new Vector3(1, factor, factor) : rotation.Axis == Axis.Y ? new Vector3(factor, 1, factor) : new Vector3(factor, factor, 1);
            }
            for (var i = 0; i < positions.Length; i++)
            {
                var p = Rotate(positions[i] - rotation.Origin, rotation.Axis, angle);
                positions[i] = p * scale + rotation.Origin;
            }
        }

        /// <summary>Right handed rotation (counter clockwise looking from the positive axis).</summary>
        private static Vector3 Rotate(Vector3 v, Axis axis, float angle)
        {
            var c = (float)Math.Cos(angle);
            var s = (float)Math.Sin(angle);
            switch (axis)
            {
                case Axis.X: return new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
                case Axis.Y: return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
                default: return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
            }
        }

        /// <summary>Blockstate rotation: vanilla rotates by -x around X and then by -y around Y (multiples of 90 degrees, exact).</summary>
        private static Vector3 RotateBlock(Vector3 v, int x, int y)
        {
            for (var i = 0; i < x / 90; i++) v = new Vector3(v.X, v.Z, -v.Y); // -90 around X
            for (var i = 0; i < y / 90; i++) v = new Vector3(-v.Z, v.Y, v.X); // -90 around Y
            return v;
        }
    }
}
