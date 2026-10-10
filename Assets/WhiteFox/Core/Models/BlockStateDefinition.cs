// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Models
{
    /// <summary>One model reference of a blockstate file (Minosoft's SingleBlockStateApply).</summary>
    public sealed class ModelApply
    {
        public ResourceLocation Model;
        public int X;
        public int Y;
        public bool UVLock;
        public int Weight = 1;

        public static ModelApply Parse(JObject json) => new ModelApply
        {
            Model = ResourceLocation.Of((string)json["model"]),
            X = (((int?)json["x"] ?? 0) % 360 + 360) % 360,
            Y = (((int?)json["y"] ?? 0) % 360 + 360) % 360,
            UVLock = (bool?)json["uvlock"] ?? false,
            Weight = (int?)json["weight"] ?? 1,
        };

        public static List<ModelApply> ParseList(JToken token)
        {
            var result = new List<ModelApply>();
            if (token is JArray array)
            {
                foreach (var entry in array) result.Add(Parse((JObject)entry));
            }
            else if (token is JObject single)
            {
                result.Add(Parse(single));
            }
            return result;
        }
    }

    /// <summary>"when" condition of a multipart case (Minosoft's PropertyCondition/AndCondition/OrCondition).</summary>
    public abstract class Condition
    {
        public abstract bool Matches(BlockState state);

        public static Condition Parse(JToken token)
        {
            if (!(token is JObject json)) return new AlwaysCondition();
            if (json["OR"] is JArray or) return new AnyCondition(ParseAll(or), true);
            if (json["AND"] is JArray and) return new AnyCondition(ParseAll(and), false);
            return new PropertyCondition(json);
        }

        private static List<Condition> ParseAll(JArray array)
        {
            var result = new List<Condition>();
            foreach (var entry in array) result.Add(Parse(entry));
            return result;
        }
    }

    public sealed class AlwaysCondition : Condition
    {
        public override bool Matches(BlockState state) => true;
    }

    public sealed class AnyCondition : Condition
    {
        private readonly List<Condition> conditions;
        private readonly bool or;

        public AnyCondition(List<Condition> conditions, bool or)
        {
            this.conditions = conditions;
            this.or = or;
        }

        public override bool Matches(BlockState state)
        {
            foreach (var condition in conditions)
            {
                if (condition.Matches(state) == or) return or;
            }
            return !or;
        }
    }

    public sealed class PropertyCondition : Condition
    {
        private readonly List<KeyValuePair<string, string[]>> properties = new List<KeyValuePair<string, string[]>>();

        public PropertyCondition(JObject json)
        {
            foreach (var pair in json)
            {
                properties.Add(new KeyValuePair<string, string[]>(pair.Key, pair.Value.ToString().ToLowerInvariant().Split('|')));
            }
        }

        public override bool Matches(BlockState state)
        {
            foreach (var pair in properties)
            {
                var value = state[pair.Key];
                if (value == null || System.Array.IndexOf(pair.Value, value) < 0) return false;
            }
            return true;
        }
    }

    /// <summary>blockstates/*.json: either "variants" or "multipart" (Minosoft's DirectBlockModel).</summary>
    public sealed class BlockStateDefinition
    {
        private readonly List<KeyValuePair<Dictionary<string, string>, List<ModelApply>>> variants = new List<KeyValuePair<Dictionary<string, string>, List<ModelApply>>>();
        private readonly List<KeyValuePair<Condition, List<ModelApply>>> multipart = new List<KeyValuePair<Condition, List<ModelApply>>>();

        public bool IsMultipart => multipart.Count > 0;

        public static BlockStateDefinition Parse(JObject json)
        {
            var definition = new BlockStateDefinition();
            if (json["variants"] is JObject variants)
            {
                foreach (var pair in variants)
                {
                    var properties = new Dictionary<string, string>();
                    if (pair.Key.Length > 0)
                    {
                        foreach (var part in pair.Key.Split(','))
                        {
                            var split = part.Split('=');
                            if (split.Length == 2) properties[split[0]] = split[1];
                        }
                    }
                    definition.variants.Add(new KeyValuePair<Dictionary<string, string>, List<ModelApply>>(properties, ModelApply.ParseList(pair.Value)));
                }
            }
            if (json["multipart"] is JArray parts)
            {
                foreach (var part in parts)
                {
                    definition.multipart.Add(new KeyValuePair<Condition, List<ModelApply>>(Condition.Parse(part["when"]), ModelApply.ParseList(part["apply"])));
                }
            }
            return definition;
        }

        /// <summary>Variants: the weighted list of the matching variant. Multipart: one list per matching part.</summary>
        public List<List<ModelApply>> Select(BlockState state)
        {
            var result = new List<List<ModelApply>>();
            foreach (var variant in variants)
            {
                var matches = true;
                foreach (var property in variant.Key)
                {
                    if (state[property.Key] != property.Value)
                    {
                        matches = false;
                        break;
                    }
                }
                if (!matches) continue;
                result.Add(variant.Value);
                return result;
            }
            foreach (var part in multipart)
            {
                if (part.Key.Matches(state)) result.Add(part.Value);
            }
            return result;
        }
    }
}
