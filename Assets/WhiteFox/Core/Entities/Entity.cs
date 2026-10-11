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
        /// <summary>Phantoms: size (entity data 16), scale 1 + 0.15 * size.</summary>
        public int PhantomSize;
        /// <summary>Shulkers: how far the lid is open, 0 .. 100 (entity data 17).</summary>
        public int Peek;
        /// <summary>The lid position the client shows (moves towards <see cref="Peek"/> smoothly).</summary>
        public float PeekClient;
        /// <summary>Panda genes (entity data 20 / 21): the texture shows the main gene, recessive ones only when doubled.</summary>
        public int MainGene;
        public int HiddenGene;
        /// <summary>Primed TNT: ticks left (entity data 8) and its block (9); evoker fangs: the tick the bite started.</summary>
        public int Fuse = 80;
        public int AttackStart = -1;
        /// <summary>Wither skulls: the blue, dangerous kind (8); end crystals: the bedrock base is drawn (9).</summary>
        public bool Dangerous;
        public bool ShowBottom = true;
        /// <summary>Fishing bobbers: the hooked entity (entity data 8), or -1.</summary>
        public int Hooked = -1;
        /// <summary>Leashed mobs: the entity holding the lead (Link Entities), or -1.</summary>
        public int LeashHolder = -1;
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

        // ---- ender dragon: the yaw and height of the last 64 ticks (vanilla EnderDragon.positions); the neck and tail
        // segments follow the flight path with them, and the phase (entity data 16)
        public int DragonPhase;
        /// <summary>Creepers (vanilla Creeper.tick, on the client too): the swelling before the explosion, 0..30 ticks.</summary>
        public int SwellDirection = -1;
        public bool Charged, Ignited;
        public int Swell, PreviousSwell;
        /// <summary>Arrows and tridents: entity data 8 (0x01 critical), the potion color of tipped arrows (-1 none).</summary>
        public byte ArrowFlags;
        public int ArrowColor = -1;
        /// <summary>Living entities: the color of their effects' particles (0 none) and if they are faint (beacons).</summary>
        public int PotionColor;
        public bool PotionAmbient;
        /// <summary>Horses: the jump strength attribute (vanilla 0.4 .. 1.0), the average until the server sends it.</summary>
        public double JumpStrength = 0.7;
        private double[] latency;
        private int latencyPointer = -1;

        /// <summary>Vanilla EnderDragon.getLatencyPos: yaw and y of <paramref name="ticks"/> ticks ago.</summary>
        public void LatencyPos(int ticks, float partial, out double yaw, out double y)
        {
            if (latency == null)
            {
                yaw = Yaw;
                y = Position.Y;
                return;
            }
            var i = (latencyPointer - ticks) & 63;
            var j = (latencyPointer - ticks - 1) & 63;
            yaw = latency[i * 2] + MathUtil.WrapDegrees((float)(latency[j * 2] - latency[i * 2])) * partial;
            y = latency[i * 2 + 1] + (latency[j * 2 + 1] - latency[i * 2 + 1]) * partial;
        }

        private void RecordLatency()
        {
            if (latency == null) latency = new double[128];
            if (latencyPointer < 0)
            {
                for (var i = 0; i < 64; i++)
                {
                    latency[i * 2] = Yaw;
                    latency[i * 2 + 1] = Position.Y;
                }
            }
            latencyPointer = (latencyPointer + 1) & 63;
            latency[latencyPointer * 2] = Yaw;
            latency[latencyPointer * 2 + 1] = Position.Y;
        }

        // ---- wither: the targets of the three heads (entity data 16..18) and where the side heads look
        public readonly int[] WitherTargets = new int[3];
        public readonly float[] HeadYaws = new float[2], HeadPitches = new float[2];
        public readonly float[] PreviousHeadYaws = new float[2], PreviousHeadPitches = new float[2];

        /// <summary>
        /// Vanilla WitherBoss.aiStep (client part): each side head turns towards its target (at most 10 degrees of yaw
        /// and 40 of pitch a tick), or back to the body without one.
        /// </summary>
        public void TickWitherHeads(Func<int, Vec3d?> eyeOf)
        {
            for (var i = 0; i < 2; i++)
            {
                PreviousHeadYaws[i] = HeadYaws[i];
                PreviousHeadPitches[i] = HeadPitches[i];
                var target = WitherTargets[i + 1] > 0 ? eyeOf(WitherTargets[i + 1]) : null;
                if (target.HasValue)
                {
                    // the side heads sit 1.3 blocks beside the body, 2.2 above the feet
                    var angle = (BodyYaw + 180 * i) * Math.PI / 180;
                    var dx = target.Value.X - (Position.X + Math.Cos(angle) * 1.3);
                    var dy = target.Value.Y - (Position.Y + 2.2);
                    var dz = target.Value.Z - (Position.Z + Math.Sin(angle) * 1.3);
                    var horizontal = Math.Sqrt(dx * dx + dz * dz);
                    var yaw = (float)(Math.Atan2(dz, dx) * 180 / Math.PI) - 90;
                    var pitch = (float)-(Math.Atan2(dy, horizontal) * 180 / Math.PI);
                    HeadPitches[i] = RotLerp(HeadPitches[i], pitch, 40);
                    HeadYaws[i] = RotLerp(HeadYaws[i], yaw, 10);
                }
                else
                {
                    HeadYaws[i] = RotLerp(HeadYaws[i], BodyYaw, 10);
                }
            }
        }

        private static float RotLerp(float from, float to, float max)
        {
            var difference = MathUtil.WrapDegrees(to - from);
            if (difference > max) difference = max;
            if (difference < -max) difference = -max;
            return from + difference;
        }
        /// <summary>Block, item and text displays: their transformation and contents.</summary>
        public DisplayData Display;

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

            if (Type?.Name.Path == "ender_dragon") RecordLatency();
            if (Type?.Name.Path == "creeper")
            {
                PreviousSwell = Swell;
                Swell = Math.Max(0, Math.Min(30, Swell + (Ignited ? 1 : SwellDirection)));
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

        /// <summary>The own player (not one of <see cref="Entities"/>): its id and eye position, for the wither's heads.</summary>
        public int OwnId = -1;
        public Vec3d OwnEyePosition;

        public void Tick()
        {
            foreach (var entity in Entities.Values) entity.Tick(world);
            foreach (var entity in Entities.Values)
            {
                if (entity.Type?.Name.Path != "wither") continue;
                entity.TickWitherHeads(id =>
                {
                    if (id == OwnId) return OwnEyePosition;
                    if (!Entities.TryGetValue(id, out var target)) return null;
                    return target.Position + new Vec3d(0, target.Height * 0.85, 0);
                });
            }
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
