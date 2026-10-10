// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Physics
{
    public enum Gamemode
    {
        Survival,
        Creative,
        Adventure,
        Spectator,
    }

    public enum Pose
    {
        Standing,
        Sneaking,
        Swimming,
        /// <summary>Gliding with an elytra (0.6 high like swimming).</summary>
        FallFlying,
    }

    /// <summary>Status effect ids of the minecraft:mob_effect registry (1.20.4) that matter on the client.</summary>
    public static class Effects
    {
        public const int Speed = 0;
        public const int Slowness = 1;
        public const int Haste = 2;
        public const int MiningFatigue = 3;
        public const int JumpBoost = 7;
        public const int WaterBreathing = 12;
        public const int Blindness = 14;
        public const int NightVision = 15;
        public const int Levitation = 24;
        public const int SlowFalling = 27;
        public const int ConduitPower = 28;
        public const int DolphinsGrace = 29;
        public const int Darkness = 32;

        public static readonly string[] Names =
        {
            "speed", "slowness", "haste", "mining_fatigue", "strength", "instant_health", "instant_damage", "jump_boost", "nausea", "regeneration",
            "resistance", "fire_resistance", "water_breathing", "invisibility", "blindness", "night_vision", "hunger", "weakness", "poison", "wither",
            "health_boost", "absorption", "saturation", "glowing", "levitation", "luck", "unluck", "slow_falling", "conduit_power", "dolphins_grace",
            "bad_omen", "hero_of_the_village", "darkness",
        };
    }

    public sealed class StatusEffect
    {
        public int Id;
        public int Amplifier;
        /// <summary>Remaining ticks, -1 = infinite.</summary>
        public int Duration;
    }

    /// <summary>Input of one tick (Minosoft's PlayerMovementInput). Sideways is positive to the left, like vanilla.</summary>
    public struct MovementInput
    {
        public float Forwards;
        public float Sideways;
        public float Upwards;
        public bool Jump;
        public bool Sneak;
        public bool Sprint;
    }

    /// <summary>The client controlled player (Minosoft's LocalPlayerEntity + the state of its physics).</summary>
    public sealed class LocalPlayer
    {
        public const float Width = 0.6f;
        public const float StandingHeight = 1.8f;
        public const float SneakingHeight = 1.5f;
        public const float SwimmingHeight = 0.6f;
        public const float StandingEyeHeight = 1.62f;
        public const float SneakingEyeHeight = 1.27f;
        public const float SwimmingEyeHeight = 0.4f;

        public Vec3d Position;
        public Vec3d PreviousPosition;
        public Vec3d Velocity;

        /// <summary>Yaw in degrees, 0 = south (+Z), 90 = west.</summary>
        public float Yaw;
        /// <summary>Pitch in degrees, positive = looking down, clamped to +-90.</summary>
        public float Pitch;

        public float EyeHeight = StandingEyeHeight;
        public float PreviousEyeHeight = StandingEyeHeight;

        public bool OnGround;
        public bool HorizontalCollision;
        public bool VerticalCollision;
        public float FallDistance;

        public bool Sprinting;
        public bool Flying;
        public bool AllowFlying = true;
        public float MovementSpeed = 0.1f;
        public float FlyingSpeed = 0.05f;
        public Pose Pose = Pose.Standing;
        public Gamemode Gamemode = Gamemode.Creative;

        // fluids (Minosoft's SubmersionState)
        public double WaterHeight;
        public double LavaHeight;
        public bool EyeInWater;
        public bool EyeInLava;
        public bool Swimming;
        public bool Climbing;

        /// <summary>Using an item (eating, drawing a bow...): movement is slowed down to 20%.</summary>
        public bool UsingItem;
        public int Food = 20;
        public readonly Dictionary<int, StatusEffect> Effects = new Dictionary<int, StatusEffect>();

        public MovementInput Input;

        /// <summary>Entity id of the vehicle the player sits in, or -1.</summary>
        public int Vehicle = -1;
        /// <summary>Arm swing animation: ticks since the swing started (0..5), -1 when not swinging.</summary>
        public int SwingTime = -1;
        public bool Riding => Vehicle >= 0;

        public void Swing()
        {
            if (SwingTime < 0 || SwingTime >= 3) SwingTime = 0;
        }

        /// <summary>Elytra: an elytra is worn (and not broken) / the player is gliding.</summary>
        public bool WearingElytra;
        public bool FallFlying;
        /// <summary>Riptide spin attack ticks left (trident).</summary>
        public int SpinAttack;

        /// <summary>Set for one tick when jump was double tapped.</summary>
        public bool ToggleFly;

        public bool InWater => WaterHeight > 0;
        public bool InLava => LavaHeight > 0;

        public int EffectLevel(int effect) => Effects.TryGetValue(effect, out var value) ? value.Amplifier + 1 : 0;

        public static float HeightOf(Pose pose) => pose == Pose.Sneaking ? SneakingHeight : pose == Pose.Swimming || pose == Pose.FallFlying ? SwimmingHeight : StandingHeight;
        public static float EyeHeightOf(Pose pose) => pose == Pose.Sneaking ? SneakingEyeHeight : pose == Pose.Swimming || pose == Pose.FallFlying ? SwimmingEyeHeight : StandingEyeHeight;

        public float Height => HeightOf(Pose);

        public AABB GetAABB(Pose pose)
        {
            var height = HeightOf(pose);
            const double half = Width / 2.0;
            return new AABB(Position.X - half, Position.Y, Position.Z - half, Position.X + half, Position.Y + height, Position.Z + half);
        }

        public AABB AABB => GetAABB(Pose);

        public Vec3d EyePosition => new Vec3d(Position.X, Position.Y + EyeHeight, Position.Z);

        public float ReachDistance => Gamemode == Gamemode.Creative ? 5.0f : 4.5f;

        public void Teleport(Vec3d position)
        {
            Position = position;
            PreviousPosition = position;
            Velocity = Vec3d.Zero;
            FallDistance = 0;
        }
    }
}
