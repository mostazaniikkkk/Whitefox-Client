// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Data
{
    public sealed class BlockProperty
    {
        public readonly string Name;
        public readonly string[] Values;

        public BlockProperty(string name, string[] values)
        {
            Name = name;
            Values = values;
        }

        public int IndexOf(string value) => Array.IndexOf(Values, value);
    }

    public enum TintType
    {
        None,
        Grass,
        Foliage,
        Water,
        Constant,
    }

    public sealed class Block
    {
        public readonly int Id;
        public readonly ResourceLocation Name;
        public readonly BlockProperty[] Properties;
        public readonly int MinStateId;
        public readonly int DefaultStateId;
        public BlockState[] States;

        public float Hardness;
        public bool Transparent;
        public int EmitLight;
        public int FilterLight;
        public bool IsAir;
        public bool IsFluid;
        /// <summary>Drops only with the right tool (vanilla requiresCorrectToolForDrops); digs 3.3x slower by hand.</summary>
        public bool RequiresTool;
        /// <summary>minecraft-data material ("mineable/pickaxe", "plant;mineable/axe"...) for the tool speeds.</summary>
        public string Material;
        /// <summary>Items that can harvest the block when <see cref="RequiresTool"/> is set.</summary>
        public HashSet<int> HarvestTools;

        // movement properties that minecraft-data does not contain; hardcoded like Minosoft's FrictionBlock/JumpBlock/VelocityBlock
        public float Friction = 0.6f;
        public float SpeedFactor = 1.0f;
        public float JumpFactor = 1.0f;

        public TintType Tint = TintType.None;
        public int ConstantTint = 0xFFFFFF;

        public Block(int id, ResourceLocation name, BlockProperty[] properties, int minStateId, int defaultStateId)
        {
            Id = id;
            Name = name;
            Properties = properties;
            MinStateId = minStateId;
            DefaultStateId = defaultStateId;
        }

        public BlockState DefaultState => States[DefaultStateId - MinStateId];

        public int PropertyIndex(string name)
        {
            for (var i = 0; i < Properties.Length; i++)
            {
                if (Properties[i].Name == name) return i;
            }
            return -1;
        }

        private Worlds.BlockEntityKind? special;

        /// <summary>Drawn by a block entity renderer (chests, signs, beds, banners, heads, shulker boxes).</summary>
        public Worlds.BlockEntityKind Special => special ??= Worlds.BlockEntityKinds.Of(this);

        public override string ToString() => Name.ToString();
    }

    /// <summary>Immutable block state. Air is represented by null in the world (like Minosoft).</summary>
    public sealed class BlockState
    {
        public readonly int Id;
        public readonly Block Block;
        /// <summary>Value index per property of <see cref="Block.Properties"/>.</summary>
        public readonly int[] ValueIndices;

        public AABB[] CollisionShape = Array.Empty<AABB>();
        /// <summary>Selection box; filled from the model when no collision exists (flowers, grass).</summary>
        public AABB[] OutlineShape = Array.Empty<AABB>();

        public int Luminance;
        public int Opacity;

        /// <summary>True when every side is fully covered by opaque faces. Set by the model baker; used for culling and AO.</summary>
        public bool FullOpaque;

        /// <summary>Per side: the side is fully covered by a face (opaque or not). Used for same-block culling (glass).</summary>
        public readonly bool[] FullSide = new bool[6];

        /// <summary>Per side: the side is fully covered by an opaque face.</summary>
        public readonly bool[] OpaqueSide = new bool[6];

        /// <summary>Baked render model (set by the model baker).</summary>
        public object Model;

        public BlockState(int id, Block block, int[] valueIndices)
        {
            Id = id;
            Block = block;
            ValueIndices = valueIndices;
        }

        public bool HasCollision => CollisionShape.Length > 0;

        public string this[string property]
        {
            get
            {
                var index = Block.PropertyIndex(property);
                return index < 0 ? null : Block.Properties[index].Values[ValueIndices[index]];
            }
        }

        public bool Matches(string property, string value) => this[property] == value;

        public BlockState With(string property, string value)
        {
            var index = Block.PropertyIndex(property);
            if (index < 0) return this;
            var valueIndex = Block.Properties[index].IndexOf(value);
            if (valueIndex < 0) return this;
            var indices = (int[])ValueIndices.Clone();
            indices[index] = valueIndex;
            return Block.States[BlockRegistry.ComputeStateIndex(Block.Properties, indices)];
        }

        public IEnumerable<KeyValuePair<string, string>> PropertyValues()
        {
            for (var i = 0; i < Block.Properties.Length; i++)
            {
                yield return new KeyValuePair<string, string>(Block.Properties[i].Name, Block.Properties[i].Values[ValueIndices[i]]);
            }
        }

        public override string ToString()
        {
            if (Block.Properties.Length == 0) return Block.Name.ToString();
            var parts = new List<string>();
            foreach (var pair in PropertyValues()) parts.Add(pair.Key + "=" + pair.Value);
            return Block.Name + "[" + string.Join(",", parts) + "]";
        }
    }
}
