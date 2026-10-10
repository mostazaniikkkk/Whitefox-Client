// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Game
{
    /// <summary>Sound categories (vanilla SoundSource, same ids as the protocol).</summary>
    public enum SoundCategory
    {
        Master,
        Music,
        Record,
        Weather,
        Block,
        Hostile,
        Neutral,
        Player,
        Ambient,
        Voice,
    }

    /// <summary>What a block sound is for: the sound event is block.{sound type}.{kind}.</summary>
    public enum BlockSoundKind
    {
        Break,
        Place,
        Hit,
        Step,
        Fall,
    }

    /// <summary>Particles to spawn (Particle packet, or client effects like block breaking).</summary>
    public struct ParticleRequest
    {
        public string Name;
        public Vec3d Position;
        /// <summary>Spread (count &gt; 0) or direction (count 0) of the packet.</summary>
        public Vec3d Offset;
        public float Speed;
        public int Count;
        public BlockState Block;
        public ItemStack Item;
        /// <summary>dust: color and scale.</summary>
        public float R, G, B, Scale;
    }

    /// <summary>A sound to play (Minosoft's AudioPlayer requests): a named sound event or the sound of a block.</summary>
    public struct SoundRequest
    {
        /// <summary>Sound event like "entity.cow.ambient", or null for a block sound.</summary>
        public string Name;
        public BlockState Block;
        public BlockSoundKind BlockKind;
        /// <summary>Minecraft position, or null for sounds without position (music, interface).</summary>
        public Vec3d? Position;
        /// <summary>Volume and pitch requested by the server; block sounds multiply them with their sound type.</summary>
        public float Volume;
        public float Pitch;
        public SoundCategory Category;
    }
}
