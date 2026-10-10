// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Numerics;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Models
{
    public sealed class ModelFace
    {
        public string Texture;
        /// <summary>u1, v1, u2, v2 in pixels (0..16), or null for the automatic uv.</summary>
        public float[] UV;
        public int Rotation;
        public int TintIndex = -1;
        public Direction? CullFace;
    }

    public sealed class ElementRotation
    {
        public Vector3 Origin = new Vector3(0.5f, 0.5f, 0.5f);
        public Axis Axis;
        public float Angle;
        public bool Rescale;
    }

    public sealed class ModelElement
    {
        /// <summary>In block units (0..1), already divided by 16.</summary>
        public Vector3 From;
        public Vector3 To;
        public bool Shade = true;
        public ElementRotation Rotation;
        public readonly Dictionary<Direction, ModelFace> Faces = new Dictionary<Direction, ModelFace>();
    }

    /// <summary>models/block/*.json with its parent chain already merged (Minosoft's BlockModel).</summary>
    public sealed class BlockModel
    {
        public ResourceLocation Name;
        public BlockModel Parent;
        public readonly Dictionary<string, string> Textures = new Dictionary<string, string>();
        public List<ModelElement> Elements;
        public bool? AmbientOcclusion;

        public IReadOnlyList<ModelElement> ResolvedElements
        {
            get
            {
                for (var model = this; model != null; model = model.Parent)
                {
                    if (model.Elements != null) return model.Elements;
                }
                return Array.Empty<ModelElement>();
            }
        }

        public bool ResolvedAmbientOcclusion
        {
            get
            {
                for (var model = this; model != null; model = model.Parent)
                {
                    if (model.AmbientOcclusion.HasValue) return model.AmbientOcclusion.Value;
                }
                return true;
            }
        }

        /// <summary>Resolves "#side" references through the texture map of the model and its parents.</summary>
        public ResourceLocation? ResolveTexture(string reference)
        {
            for (var depth = 0; depth < 16; depth++)
            {
                if (string.IsNullOrEmpty(reference)) return null;
                if (reference[0] != '#') return ResourceLocation.Of(reference);
                var key = reference.Substring(1);
                string next = null;
                for (var model = this; model != null && next == null; model = model.Parent)
                {
                    model.Textures.TryGetValue(key, out next);
                }
                reference = next;
            }
            return null;
        }
    }

    /// <summary>Loads and caches block models (port of Minosoft's BlockLoader).</summary>
    public sealed class ModelLoader
    {
        private readonly AssetManager assets;
        private readonly Dictionary<ResourceLocation, BlockModel> cache = new Dictionary<ResourceLocation, BlockModel>();

        public ModelLoader(AssetManager assets)
        {
            this.assets = assets;
        }

        public BlockModel Load(ResourceLocation name)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            cache[name] = null; // guards against parent cycles

            if (!assets.TryGetText(name.ToAssetPath("models/", ".json"), out var text))
            {
                Log.Warn("Missing block model " + name);
                return null;
            }

            var json = JObject.Parse(text);
            var model = new BlockModel { Name = name };
            if (json["parent"] != null)
            {
                var parent = ResourceLocation.Of((string)json["parent"]);
                if (parent.Path.StartsWith("builtin/")) return Store(name, model);
                model.Parent = Load(parent);
            }
            if (json["textures"] is JObject textures)
            {
                foreach (var pair in textures) model.Textures[pair.Key] = (string)pair.Value;
            }
            if (json["ambientocclusion"] != null) model.AmbientOcclusion = (bool)json["ambientocclusion"];
            if (json["elements"] is JArray elements)
            {
                model.Elements = new List<ModelElement>();
                foreach (var element in elements) model.Elements.Add(ParseElement((JObject)element));
            }
            return Store(name, model);
        }

        private BlockModel Store(ResourceLocation name, BlockModel model)
        {
            cache[name] = model;
            return model;
        }

        private static Vector3 ReadVector(JToken token) => new Vector3((float)token[0], (float)token[1], (float)token[2]);

        private static ModelElement ParseElement(JObject json)
        {
            var element = new ModelElement
            {
                From = ReadVector(json["from"]) / 16.0f,
                To = ReadVector(json["to"]) / 16.0f,
                Shade = (bool?)json["shade"] ?? true,
            };
            if (json["rotation"] is JObject rotation)
            {
                var angle = (float?)rotation["angle"] ?? 0.0f;
                if (angle != 0.0f)
                {
                    element.Rotation = new ElementRotation
                    {
                        Origin = rotation["origin"] != null ? ReadVector(rotation["origin"]) / 16.0f : new Vector3(0.5f),
                        Axis = (string)rotation["axis"] == "x" ? Axis.X : (string)rotation["axis"] == "y" ? Axis.Y : Axis.Z,
                        Angle = angle,
                        Rescale = (bool?)rotation["rescale"] ?? false,
                    };
                }
            }
            if (json["faces"] is JObject faces)
            {
                foreach (var pair in faces)
                {
                    var face = (JObject)pair.Value;
                    var modelFace = new ModelFace
                    {
                        Texture = (string)face["texture"],
                        Rotation = (((int?)face["rotation"] ?? 0) % 360 + 360) % 360,
                        TintIndex = (int?)face["tintindex"] ?? -1,
                    };
                    if (face["uv"] is JArray uv) modelFace.UV = new[] { (float)uv[0], (float)uv[1], (float)uv[2], (float)uv[3] };
                    if (face["cullface"] != null) modelFace.CullFace = Directions.Parse((string)face["cullface"]);
                    element.Faces[Directions.Parse(pair.Key)] = modelFace;
                }
            }
            return element;
        }
    }
}
