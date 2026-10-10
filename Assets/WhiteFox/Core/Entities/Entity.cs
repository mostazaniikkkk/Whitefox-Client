// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Physics;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Entities
{
    /// <summary>
    /// A server side entity as seen by the client (Minosoft's Entity + EntityRenderInfo): position and rotations
    /// with vanilla interpolation, entity data, equipment and the animation state used by the renderer.
    /// </summary>
    public sealed class Entity
    {
        public int Id;
        public Guid Uuid;
        public EntityType Type;
        /// <summary>Spawn data (falling block state, item frame direction, projectile owner...).</summary>
        public int Data;

        public Vec3d Position;
        public Vec3d PreviousPosition;
        public Vec3d Velocity;
        public bool OnGround;
        public float Yaw;
        public float Pitch;
        public float HeadYaw;
        public float BodyYaw;
        public float PreviousYaw;
        public float PreviousPitch;
        public float PreviousHeadYaw;
        public float PreviousBodyYaw;

        // vanilla lerpTo: living entities move to server positions over 3 ticks
        private Vec3d lerpTarget;
        private float lerpYaw;
        private float lerpPitch;
        private int lerpSteps;

        // entity data
        public byte Flags;
        public int PoseId;
        public string CustomName;
        public bool CustomNameVisible;
        public float Health = 1;
        public bool Baby;
        public byte SheepWool;
        public int SlimeSize = 1;
        public ItemStack Item;
        public BlockState FallingBlock;
        public readonly ItemStack[] Equipment = new ItemStack[6];
        public int ExperienceCount = 1;
        /// <summary>Texture variant of the entity data (boat wood, painting, cat, horse...), -1 = never sent (the type's default).</summary>
        public int Variant = -1;
        public bool Saddled;
        /// <summary>Firework rockets: the entity they boost (elytra), -1 when not attached.</summary>
        public int AttachedTo = -1;
        /// <summary>Living entities: the hand in use (0 none, 1 main hand, 2 off hand; entity data 8) and for how long.</summary>
        public int UsingHand;
        /// <summary>Guardians: swimming with the spikes pulled in (entity data 16); the spike animation 0 in .. 1 out.</summary>
        public bool Moving;
        public float Spikes = 1;
        public int UseTicks;
        /// <summary>Villager data (type, profession, level) of villagers and zombie villagers.</summary>
        public int[] VillagerData;
        /// <summary>Item frame rotation (0..7).</summary>
        public int FrameRotation;
        /// <summary>Armor stand flags (small, arms, no base plate) and pose (head, body, left/right arm, left/right leg; degrees).</summary>
        public byte StandFlags;
        public readonly Vec3d?[] Pose = new Vec3d?[6];

        // animation
        public float LimbSwing;
        public float LimbSwingAmount;
        public float PreviousLimbSwingAmount;
        public int HurtTime;
        public int DeathTime;
        public int SwingTime = -1;
        public int Age;

        public int Vehicle = -1;
        public readonly List<int> Passengers = new List<int>();

        public bool OnFire => (Flags & 0x01) != 0;
        public bool Sneaking => (Flags & 0x02) != 0 || PoseId == 5;
        public bool Sprinting => (Flags & 0x08) != 0;
        public bool Invisible => (Flags & 0x20) != 0;
        public bool Swimming => PoseId == 3;

        /// <summary>Height of the seat over the vehicle position (feet of the passenger).</summary>
        public static double RidingOffset(Entity vehicle)
        {
            var path = vehicle.Type?.Name.Path ?? "";
            return path.EndsWith("boat") ? -0.1 : path.Contains("minecart") ? 0.0625 : vehicle.Height * 0.75 - 0.35;
        }

        public bool IsPlayer => Type?.Name.Path == "player";
        public bool IsLiving => Type != null && Type.IsLiving;

        public float Width => (Type?.Width ?? 0.5f) * (Baby ? 0.5f : 1) * (Type?.Name.Path == "slime" || Type?.Name.Path == "magma_cube" ? SlimeSize : 1);
        public float Height => (Type?.Height ?? 0.5f) * (Baby ? 0.5f : 1) * (Type?.Name.Path == "slime" || Type?.Name.Path == "magma_cube" ? SlimeSize : 1) * (IsPlayer && Sneaking ? 1.5f / 1.8f : 1);

        public AABB AABB => new AABB(Position.X - Width / 2, Position.Y, Position.Z - Width / 2, Position.X + Width / 2, Position.Y + Height, Position.Z + Width / 2);

        public Vec3d Interpolated(double partial) => Vec3d.Lerp(PreviousPosition, Position, partial);

        /// <summary>A new position from the server (spawn, teleport, relative move).</summary>
        public void MoveTo(Vec3d position, float? yaw, float? pitch)
        {
            if (IsLiving)
            {
                lerpTarget = position;
                lerpYaw = yaw ?? (lerpSteps > 0 ? lerpYaw : Yaw);
                lerpPitch = pitch ?? (lerpSteps > 0 ? lerpPitch : Pitch);
                lerpSteps = 3;
                return;
            }
            Position = position;
            if (yaw.HasValue) Yaw = yaw.Value;
            if (pitch.HasValue) Pitch = pitch.Value;
        }

        public void Teleport(Vec3d position, float yaw, float pitch)
        {
            Position = PreviousPosition = lerpTarget = position;
            Yaw = PreviousYaw = lerpYaw = yaw;
            Pitch = PreviousPitch = lerpPitch = pitch;
            lerpSteps = 0;
        }

        public void Tick(World world)
        {
            PreviousPosition = Position;
            PreviousYaw = Yaw;
            PreviousPitch = Pitch;
            PreviousHeadYaw = HeadYaw;
            PreviousBodyYaw = BodyYaw;
            PreviousLimbSwingAmount = LimbSwingAmount;
            Age++;

            if (lerpSteps > 0)
            {
                Position = Position + (lerpTarget - Position) * (1.0 / lerpSteps);
                Yaw += MathUtil.WrapDegrees(lerpYaw - Yaw) / lerpSteps;
                Pitch += (lerpPitch - Pitch) / lerpSteps;
                lerpSteps--;
            }
            else if (!IsLiving)
            {
                TickPhysics(world);
            }

            if (IsLiving)
            {
                // walk animation (vanilla WalkAnimationState.update)
                var dx = Position.X - PreviousPosition.X;
                var dz = Position.Z - PreviousPosition.Z;
                var distance = (float)Math.Sqrt(dx * dx + dz * dz) * 4;
                if (distance > 1) distance = 1;
                LimbSwingAmount += (distance - LimbSwingAmount) * 0.4f;
                LimbSwing += LimbSwingAmount;

                // body rotation lags behind the head (vanilla LivingEntity.tickHeadTurn, simplified)
                var target = Yaw;
                if (dx * dx + dz * dz > 0.0025) target = (float)(Math.Atan2(dz, dx) * 180 / Math.PI) - 90;
                BodyYaw += MathUtil.WrapDegrees(target - BodyYaw) * 0.3f;
                var head = IsPlayer ? Yaw : HeadYaw;
                var difference = MathUtil.WrapDegrees(head - BodyYaw);
                if (difference < -75) BodyYaw = head + 75;
                if (difference > 75) BodyYaw = head - 75;
            }

            if (HurtTime > 0) HurtTime--;
            if (Health <= 0 && IsLiving) DeathTime++;
            if (SwingTime >= 0 && ++SwingTime >= 6) SwingTime = -1;
        }

        /// <summary>Client side movement of items, experience orbs, falling blocks and projectiles between server updates.</summary>
        private void TickPhysics(World world)
        {
            var path = Type?.Name.Path;
            double gravity, drag;
            switch (path)
            {
                case "item":
                case "falling_block":
                case "tnt":
                    gravity = 0.04;
                    drag = 0.98;
                    break;
                case "experience_orb":
                    gravity = 0.03;
                    drag = 0.98;
                    break;
                case "arrow":
                case "spectral_arrow":
                case "trident":
                    gravity = 0.05;
                    drag = 0.99;
                    break;
                case "snowball":
                case "egg":
                case "ender_pearl":
                case "potion":
                case "experience_bottle":
                    gravity = 0.03;
                    drag = 0.99;
                    break;
                default:
                    return; // boats, minecarts, item frames...: only server updates
            }
            if (OnGround && Velocity.LengthSquared < 1.0E-6) return;
            var v = Velocity;
            v.Y -= gravity;
            var shapes = new List<AABB>();
            var aabb = AABB;
            PlayerPhysics.CollectCollisions(world, aabb.Extend(v), shapes);
            var moved = PlayerPhysics.Collide(v, aabb, shapes);
            Position += moved;
            var collidedY = Math.Abs(moved.Y - v.Y) > 1.0E-7;
            OnGround = collidedY && v.Y < 0;
            if (Math.Abs(moved.X - v.X) > 1.0E-7) v.X = 0;
            if (Math.Abs(moved.Z - v.Z) > 1.0E-7) v.Z = 0;
            if (collidedY) v.Y = 0;
            var friction = OnGround ? 0.6 * 0.98 : drag;
            Velocity = new Vec3d(v.X * friction, v.Y * drag, v.Z * friction);
        }
    }

    public sealed class PlayerInfo
    {
        public Guid Uuid;
        public string Name;
        /// <summary>textures.minecraft.net skin url (online mode), or null for the default skin.</summary>
        public string SkinUrl;
        public bool Slim;
        public int Gamemode;
        public int Latency;
        public bool Listed = true;
        public string DisplayName;
    }

    /// <summary>All entities of the world (Minosoft's WorldEntities) and the tab list (PlayerInfo).</summary>
    public sealed class EntityManager
    {
        private readonly World world;
        public readonly Dictionary<int, Entity> Entities = new Dictionary<int, Entity>();
        public readonly Dictionary<Guid, PlayerInfo> Players = new Dictionary<Guid, PlayerInfo>();

        public event Action<Entity> Removed;

        public EntityManager(World world)
        {
            this.world = world;
        }

        public Entity Get(int id) => Entities.TryGetValue(id, out var entity) ? entity : null;

        public void Add(Entity entity)
        {
            if (Entities.ContainsKey(entity.Id)) Remove(entity.Id);
            Entities[entity.Id] = entity;
        }

        public void Remove(int id)
        {
            if (!Entities.TryGetValue(id, out var entity)) return;
            Entities.Remove(id);
            Removed?.Invoke(entity);
        }

        public void Clear()
        {
            foreach (var id in new List<int>(Entities.Keys)) Remove(id);
        }

        public void Tick()
        {
            foreach (var entity in Entities.Values) entity.Tick(world);
            // passengers are positioned by the client (vanilla Entity.positionRider)
            foreach (var vehicle in Entities.Values)
            {
                foreach (var id in vehicle.Passengers)
                {
                    if (!Entities.TryGetValue(id, out var passenger)) continue;
                    passenger.Vehicle = vehicle.Id;
                    passenger.PreviousPosition = passenger.Position;
                    passenger.Position = vehicle.Position + new Vec3d(0, Entity.RidingOffset(vehicle), 0);
                }
            }
        }

        /// <summary>Closest entity hit by the ray (vanilla ProjectileUtil.getEntityHitResult), its distance in <paramref name="distance"/>.</summary>
        public Entity Raycast(Vec3d origin, Vec3d direction, double maxDistance, out double distance)
        {
            Entity best = null;
            distance = maxDistance;
            foreach (var entity in Entities.Values)
            {
                if (entity.Type == null || entity.Type.Name.Path == "item" || entity.Type.Name.Path == "experience_orb" || entity.Type.Name.Path.EndsWith("arrow")) continue;
                var hit = entity.AABB.Grow(0.1).Raycast(origin, direction, out _);
                if (!hit.HasValue || hit.Value > distance) continue;
                distance = hit.Value;
                best = entity;
            }
            return best;
        }
    }
}
