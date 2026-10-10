// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Worlds
{
    /// <summary>Blocks drawn by a block entity renderer instead of (or on top of) their block model.</summary>
    public enum BlockEntityKind
    {
        None,
        Chest,
        Sign,
        WallSign,
        HangingSign,
        WallHangingSign,
        Bed,
        Banner,
        WallBanner,
        Skull,
        WallSkull,
        ShulkerBox,
    }

    public static class BlockEntityKinds
    {
        public static BlockEntityKind Of(Block block)
        {
            if (block == null) return BlockEntityKind.None;
            var name = block.Name.Path;
            if (name == "chest" || name == "trapped_chest" || name == "ender_chest") return BlockEntityKind.Chest;
            if (name.EndsWith("_wall_hanging_sign")) return BlockEntityKind.WallHangingSign;
            if (name.EndsWith("_hanging_sign")) return BlockEntityKind.HangingSign;
            if (name.EndsWith("_wall_sign")) return BlockEntityKind.WallSign;
            if (name.EndsWith("_sign")) return BlockEntityKind.Sign;
            if (name.EndsWith("_bed")) return BlockEntityKind.Bed;
            if (name.EndsWith("_wall_banner")) return BlockEntityKind.WallBanner;
            if (name.EndsWith("_banner")) return BlockEntityKind.Banner;
            if (name.EndsWith("_wall_skull") || name.EndsWith("_wall_head")) return BlockEntityKind.WallSkull;
            if (name.EndsWith("_skull") || name.EndsWith("_head") && name != "piston_head") return BlockEntityKind.Skull;
            if (name == "shulker_box" || name.EndsWith("_shulker_box")) return BlockEntityKind.ShulkerBox;
            return BlockEntityKind.None;
        }
    }

    /// <summary>Data of a block entity (Minosoft's BlockEntity): the NBT sent by the server and the lid animation.</summary>
    public sealed class BlockEntity
    {
        public readonly BlockPos Position;
        /// <summary>Id in the minecraft:block_entity_type registry, -1 when unknown.</summary>
        public int Type = -1;
        public object Nbt;
        /// <summary>Increased every time the NBT changes (the renderer rebuilds sign text, banner patterns...).</summary>
        public int Version;

        // chest lid / shulker box opening (vanilla ChestLidController, ShulkerBoxBlockEntity)
        public int Viewers;
        public float Openness;
        public float PreviousOpenness;

        public BlockEntity(BlockPos position) => Position = position;

        public float LerpOpenness(float partial) => PreviousOpenness + (Openness - PreviousOpenness) * partial;
    }

    /// <summary>The block entities of the loaded chunks (Minosoft's chunk block entity storage, one map per world).</summary>
    public sealed class BlockEntityStore
    {
        private readonly Dictionary<BlockPos, BlockEntity> entries = new Dictionary<BlockPos, BlockEntity>();
        private readonly List<BlockEntity> animating = new List<BlockEntity>();

        public event Action<BlockEntity> Changed;

        public int Count => entries.Count;

        public BlockEntity Get(BlockPos position) => entries.TryGetValue(position, out var entity) ? entity : null;

        private BlockEntity GetOrCreate(BlockPos position)
        {
            if (!entries.TryGetValue(position, out var entity)) entries[position] = entity = new BlockEntity(position);
            return entity;
        }

        public void Set(BlockPos position, int type, object nbt)
        {
            var entity = GetOrCreate(position);
            if (type >= 0) entity.Type = type;
            entity.Nbt = nbt;
            entity.Version++;
            Changed?.Invoke(entity);
        }

        /// <summary>Block Action packet: action 1 of chests, ender chests and shulker boxes is the number of viewers.</summary>
        public void OnAction(BlockPos position, int action, int parameter)
        {
            if (action != 1) return;
            var entity = GetOrCreate(position);
            entity.Viewers = parameter;
            if (!animating.Contains(entity)) animating.Add(entity);
        }

        /// <summary>Game tick: lids move 0.1 per tick toward open (any viewer) or closed.</summary>
        public void Tick()
        {
            for (var i = animating.Count - 1; i >= 0; i--)
            {
                var entity = animating[i];
                entity.PreviousOpenness = entity.Openness;
                entity.Openness = Math.Max(0, Math.Min(1, entity.Openness + (entity.Viewers > 0 ? 0.1f : -0.1f)));
                if (entity.Openness == entity.PreviousOpenness && (entity.Openness == 0 || entity.Openness == 1)) animating.RemoveAt(i);
            }
        }

        public void Remove(BlockPos position)
        {
            if (!entries.TryGetValue(position, out var entity)) return;
            entries.Remove(position);
            animating.Remove(entity);
        }

        public void RemoveChunk(ChunkPos chunk)
        {
            var removed = new List<BlockPos>();
            foreach (var position in entries.Keys)
            {
                if (position.ChunkPos.Equals(chunk)) removed.Add(position);
            }
            foreach (var position in removed) Remove(position);
        }

        public void Clear()
        {
            entries.Clear();
            animating.Clear();
        }
    }
}
