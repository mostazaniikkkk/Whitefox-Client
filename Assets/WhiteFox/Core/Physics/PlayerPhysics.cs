// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Physics
{
    /// <summary>
    /// Vanilla compatible movement of the local player, ported from Minosoft's LocalPlayerPhysics, LivingEntityPhysics,
    /// InputPhysics, JumpPhysics, ClimbingPhysics, WaterFluid/LavaFluid.travel, SubmersionState,
    /// CollisionMovementPhysics and SneakAdjuster. Runs once per tick (20 TPS).
    /// Not implemented yet: elytra, riding, riptide, entity collisions.
    /// </summary>
    public sealed class PlayerPhysics
    {
        public const double Gravity = 0.08;
        public const double SlowFallingGravity = 0.01;
        public const double AirResistance = 0.98;
        public const float FrictionWeight = 0.21600002f;
        public const float FrictionMultiplier = 0.91f;
        public const float StepHeight = 0.6f;
        public const float JumpVelocity = 0.42f;
        public const float SprintJumpBoost = 0.2f;
        public const double SneakCheck = 0.05;
        public const float SprintSpeedMultiplier = 1.3f;
        public const double SwimUpwards = 0.04;
        public const double FluidJumpThreshold = 0.4;

        private readonly World world;
        private readonly LocalPlayer player;
        private readonly List<AABB> collisions = new List<AABB>();

        private float forwards;
        private float sideways;
        private bool jumping;
        private int jumpingCooldown;
        private Vec3d? stuckMultiplier;
        private Vec3d fluidPush;

        public PlayerPhysics(World world, LocalPlayer player)
        {
            this.world = world;
            this.player = player;
        }

        public void Tick()
        {
            player.PreviousPosition = player.Position;
            player.PreviousEyeHeight = player.EyeHeight;
            if (world.GetChunk(BlockPos.Floor(player.Position).ChunkPos) == null) return; // like Minosoft: no ticking in unloaded chunks

            UpdateSubmersion();
            UpdateSwimming();
            UpdateInput();
            UpdateSprinting();
            UpdateFlying();
            Sink();
            UpdateFlyY();

            // LivingEntityPhysics.tickMovement
            var v = player.Velocity;
            if (Math.Abs(v.X) < 0.003) v.X = 0;
            if (Math.Abs(v.Y) < 0.003) v.Y = 0;
            if (Math.Abs(v.Z) < 0.003) v.Z = 0;
            player.Velocity = v;
            TryJump();
            forwards *= 0.98f;
            sideways *= 0.98f;

            if (player.FallFlying && (player.OnGround || player.InWater || player.InLava || !player.WearingElytra || player.Flying || Fluids.IsClimbable(world.GetBlock(BlockPos.Floor(player.Position))))) player.FallFlying = false;
            if (player.Flying) TravelFlying();
            else if (player.FallFlying) TravelFallFlying();
            else if (player.Swimming) TravelSwimming();
            else Travel();

            CheckInsideBlocks();
            if (player.OnGround && player.Flying && player.Gamemode != Gamemode.Spectator) player.Flying = false;
            UpdatePose();
            TickEffects();
        }

        // ---- fluids (SubmersionState) ----

        /// <summary>Fluid heights over the player box and the push of flowing fluids (vanilla updateFluidHeightAndDoFluidPushing).</summary>
        private void UpdateSubmersion()
        {
            var aabb = player.AABB.Shrink(0.001);
            double water = 0, lava = 0;
            var push = Vec3d.Zero;
            var pushes = 0;
            var minX = (int)Math.Floor(aabb.Min.X);
            var maxX = (int)Math.Ceiling(aabb.Max.X) - 1;
            var minY = (int)Math.Floor(aabb.Min.Y);
            var maxY = (int)Math.Ceiling(aabb.Max.Y) - 1;
            var minZ = (int)Math.Floor(aabb.Min.Z);
            var maxZ = (int)Math.Ceiling(aabb.Max.Z) - 1;
            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    for (var z = minZ; z <= maxZ; z++)
                    {
                        var state = world.GetBlock(x, y, z);
                        var fluid = Fluids.Get(state);
                        if (fluid == FluidType.None) continue;
                        var top = y + FluidSurface(x, y, z, fluid, state);
                        if (top < aabb.Min.Y) continue;
                        var height = top - aabb.Min.Y;
                        if (fluid == FluidType.Water) water = Math.Max(water, height);
                        else lava = Math.Max(lava, height);
                        var flow = FlowDirection(x, y, z, fluid, state);
                        if (height < FluidJumpThreshold) flow *= height;
                        push += flow;
                        pushes++;
                    }
                }
            }
            player.WaterHeight = water;
            player.LavaHeight = lava;
            fluidPush = pushes > 0 ? push * (1.0 / pushes) : Vec3d.Zero;

            var eye = player.EyePosition;
            var eyeBlock = BlockPos.Floor(eye.X, eye.Y - 0.11111111, eye.Z);
            var eyeState = world.GetBlock(eyeBlock);
            var eyeFluid = Fluids.Get(eyeState);
            var inFluid = eyeFluid != FluidType.None && eye.Y - 0.11111111 < eyeBlock.Y + FluidSurface(eyeBlock.X, eyeBlock.Y, eyeBlock.Z, eyeFluid, eyeState);
            player.EyeInWater = inFluid && eyeFluid == FluidType.Water;
            player.EyeInLava = inFluid && eyeFluid == FluidType.Lava;

            if (water > 0) player.FallDistance = 0;
            if (lava > 0) player.FallDistance *= 0.5f;

            // flowing fluids push the player (not while flying)
            if (!player.Flying && fluidPush.LengthSquared > 1.0E-8)
            {
                var length = fluidPush.Length;
                var speed = water > 0 ? 0.014 : 0.0023333333333333335;
                var v = player.Velocity;
                var added = fluidPush * (speed / length);
                if (Math.Abs(v.X) < 0.003 && Math.Abs(v.Z) < 0.003 && added.HorizontalLengthSquared < 0.0045 * 0.0045)
                {
                    added = new Vec3d(added.X, added.Y, added.Z);
                    var horizontal = Math.Sqrt(added.HorizontalLengthSquared);
                    if (horizontal > 0) added = new Vec3d(added.X / horizontal * 0.0045, added.Y, added.Z / horizontal * 0.0045);
                }
                player.Velocity = v + added;
            }
        }

        /// <summary>Surface height in the block; 1 if the same fluid is above.</summary>
        private float FluidSurface(int x, int y, int z, FluidType fluid, BlockState state)
        {
            if (Fluids.Get(world.GetBlock(x, y + 1, z)) == fluid) return 1.0f;
            return Fluids.Height(state);
        }

        /// <summary>Direction the fluid flows to (vanilla FlowingFluid.getFlow, simplified).</summary>
        private Vec3d FlowDirection(int x, int y, int z, FluidType fluid, BlockState state)
        {
            var self = Fluids.Height(state);
            double fx = 0, fz = 0;
            foreach (var direction in Directions.All)
            {
                if (direction.GetAxis() == Axis.Y) continue;
                var nx = x + direction.X();
                var nz = z + direction.Z();
                var neighbour = world.GetBlock(nx, y, nz);
                float height;
                if (Fluids.Get(neighbour) == fluid) height = Fluids.Height(neighbour);
                else if (neighbour == null || !neighbour.HasCollision) height = Fluids.Get(world.GetBlock(nx, y - 1, nz)) == fluid ? self - 8.0f / 9.0f : 0;
                else continue;
                var difference = self - height;
                fx += direction.X() * difference;
                fz += direction.Z() * difference;
            }
            var length = Math.Sqrt(fx * fx + fz * fz);
            return length < 1.0E-5 ? Vec3d.Zero : new Vec3d(fx / length, 0, fz / length);
        }

        /// <summary>Swimming = sprinting in water with the head under water (vanilla updateSwimming).</summary>
        private void UpdateSwimming()
        {
            if (player.Flying || player.Gamemode == Gamemode.Spectator)
            {
                player.Swimming = false;
                return;
            }
            if (player.Swimming) player.Swimming = player.Sprinting && player.InWater;
            else player.Swimming = player.Sprinting && player.EyeInWater && Fluids.Get(world.GetBlock(BlockPos.Floor(player.Position))) == FluidType.Water;
        }

        private void Sink()
        {
            if (!player.InWater || !player.Input.Sneak || player.Flying) return;
            var v = player.Velocity;
            v.Y -= SwimUpwards;
            player.Velocity = v;
        }

        // ---- input, sprinting, flying ----

        private void UpdateInput()
        {
            forwards = player.Input.Forwards;
            sideways = player.Input.Sideways;
            jumping = player.Input.Jump;

            var slowdown = !player.Flying && !player.Swimming && !WouldPoseCollide(Pose.Sneaking) && (player.Input.Sneak || WouldPoseCollide(Pose.Standing));
            if (slowdown)
            {
                forwards *= 0.3f;
                sideways *= 0.3f;
            }
            if (player.UsingItem)
            {
                forwards *= 0.2f;
                sideways *= 0.2f;
            }
        }

        private void UpdateSprinting()
        {
            player.Sprinting = ShouldSprint();
        }

        /// <summary>Minosoft's LocalPlayerPhysics.shouldSprint (hunger, water, collisions, blindness).</summary>
        private bool ShouldSprint()
        {
            if (player.Food <= 6 && !player.AllowFlying) return false;
            var walking = forwards > 0.0005f;
            if (player.Swimming)
            {
                if (!player.InWater) return false;
                if (!player.OnGround && !player.Input.Sneak && !walking) return false;
            }
            else
            {
                if (!walking) return false;
                if (player.InWater && !player.EyeInWater) return false;
                if (player.HorizontalCollision) return false;
            }
            if (player.Sprinting) return true;
            if (!player.Input.Sprint || player.UsingItem) return false;
            if (player.EffectLevel(Effects.Blindness) > 0) return false;
            return player.InWater ? forwards > 0.0005f : forwards > 0.8f;
        }

        private void UpdateFlying()
        {
            var toggle = player.ToggleFly;
            player.ToggleFly = false;
            if (player.Gamemode == Gamemode.Spectator)
            {
                player.Flying = true;
                return;
            }
            if (!player.AllowFlying || !toggle || player.Swimming) return;
            player.Flying = !player.Flying;
        }

        private void UpdateFlyY()
        {
            if (!player.Flying || player.Input.Upwards == 0.0f) return;
            var v = player.Velocity;
            v.Y += player.Input.Upwards * player.FlyingSpeed * 3.0f;
            player.Velocity = v;
        }

        /// <summary>Jumping, or swimming up in a fluid (LivingEntityPhysics.tryJump).</summary>
        private void TryJump()
        {
            if (jumpingCooldown > 0) jumpingCooldown--;
            if (!jumping || player.Flying)
            {
                jumpingCooldown = 0;
                return;
            }
            var height = player.LavaHeight > 0 ? player.LavaHeight : player.WaterHeight;
            var inFluid = player.InWater || player.InLava;
            if (inFluid && (height > FluidJumpThreshold || !player.OnGround))
            {
                var swim = player.Velocity;
                swim.Y += SwimUpwards;
                player.Velocity = swim;
                return;
            }
            if (!player.OnGround || jumpingCooldown != 0) return;

            var v = player.Velocity;
            v.Y = JumpVelocity * GetJumpFactor() + 0.1 * player.EffectLevel(Effects.JumpBoost);
            if (player.Sprinting)
            {
                var yaw = player.Yaw * MathUtil.DegToRad;
                v.X += -Math.Sin(yaw) * SprintJumpBoost;
                v.Z += Math.Cos(yaw) * SprintJumpBoost;
            }
            player.Velocity = v;
            jumpingCooldown = 10;
        }

        /// <summary>Movement speed attribute: base 0.1, speed/slowness effects, sprinting +30%.</summary>
        private float MovementSpeed
        {
            get
            {
                var speed = player.MovementSpeed * (1 + 0.2f * player.EffectLevel(Effects.Speed));
                speed *= Math.Max(0, 1 - 0.15f * player.EffectLevel(Effects.Slowness));
                return player.Sprinting ? speed * SprintSpeedMultiplier : speed;
            }
        }

        private float AirSpeed => player.Sprinting ? 0.025999999f : 0.02f;

        private double CurrentGravity
        {
            get
            {
                if (player.Velocity.Y <= 0 && player.EffectLevel(Effects.SlowFalling) > 0)
                {
                    player.FallDistance = 0;
                    return SlowFallingGravity;
                }
                return Gravity;
            }
        }

        // ---- travel ----

        /// <summary>
        /// Elytra gliding (vanilla LivingEntity.travel): gravity reduced by how level the look is, diving turns height into
        /// speed, pulling up turns speed into height, the motion follows the look direction; drag 0.99 / 0.98.
        /// </summary>
        private void TravelFallFlying()
        {
            var v = player.Velocity;
            if (v.Y > -0.5) player.FallDistance = 1;
            var look = MathUtil.Front(player.Yaw, player.Pitch);
            var pitch = player.Pitch * MathUtil.DegToRad;
            var horizontalLook = Math.Sqrt(look.X * look.X + look.Z * look.Z);
            var horizontalSpeed = Math.Sqrt(v.X * v.X + v.Z * v.Z);
            var lookLength = look.Length;
            var lift = Math.Cos(pitch);
            lift = lift * lift * Math.Min(1.0, lookLength / 0.4);
            var gravity = player.EffectLevel(Effects.SlowFalling) > 0 && v.Y <= 0 ? 0.01 : 0.08;
            v = new Vec3d(v.X, v.Y + gravity * (-1.0 + lift * 0.75), v.Z);
            if (v.Y < 0 && horizontalLook > 0)
            {
                var amount = v.Y * -0.1 * lift;
                v = new Vec3d(v.X + look.X * amount / horizontalLook, v.Y + amount, v.Z + look.Z * amount / horizontalLook);
            }
            if (pitch < 0 && horizontalLook > 0)
            {
                var amount = horizontalSpeed * -Math.Sin(pitch) * 0.04;
                v = new Vec3d(v.X - look.X * amount / horizontalLook, v.Y + amount * 3.2, v.Z - look.Z * amount / horizontalLook);
            }
            if (horizontalLook > 0)
            {
                v = new Vec3d(v.X + (look.X / horizontalLook * horizontalSpeed - v.X) * 0.1, v.Y, v.Z + (look.Z / horizontalLook * horizontalSpeed - v.Z) * 0.1);
            }
            player.Velocity = new Vec3d(v.X * 0.99, v.Y * 0.98, v.Z * 0.99);
            Move(player.Velocity);
        }

        private void Travel()
        {
            if (player.InLava && player.LavaHeight > 0) TravelLava();
            else if (player.InWater) TravelWater();
            else TravelNormal();
        }

        private void TravelNormal(float? airSpeedOverride = null)
        {
            float slowdown;
            if (player.OnGround)
            {
                var friction = GetVelocityState()?.Block.Friction ?? 0.6f;
                var speed = MovementSpeed * (FrictionWeight / (friction * friction * friction));
                ApplyMovementInput(speed);
                slowdown = friction * FrictionMultiplier;
            }
            else
            {
                ApplyMovementInput(airSpeedOverride ?? AirSpeed);
                slowdown = FrictionMultiplier;
            }

            LimitClimbingSpeed();
            Move(player.Velocity);
            ApplyClimbingSpeed();

            var v = player.Velocity;
            v.X *= slowdown;
            var levitation = player.EffectLevel(Effects.Levitation);
            if (levitation > 0)
            {
                v.Y += (0.05 * levitation - v.Y) * 0.2;
                player.FallDistance = 0;
            }
            else
            {
                v.Y -= CurrentGravity;
            }
            v.Y *= AirResistance;
            v.Z *= slowdown;
            player.Velocity = v;
        }

        /// <summary>Vanilla LivingEntity.travel, water branch (Minosoft's WaterFluid.travel).</summary>
        private void TravelWater()
        {
            var gravity = CurrentGravity;
            var falling = player.Velocity.Y <= 0;
            var startY = player.Position.Y;
            var friction = player.Sprinting ? 0.9f : 0.8f;
            if (player.EffectLevel(Effects.DolphinsGrace) > 0) friction = 0.96f;
            ApplyMovementInput(0.02f);
            Move(player.Velocity);

            var v = player.Velocity;
            if (player.HorizontalCollision && player.Climbing) v.Y = 0.2;
            v = new Vec3d(v.X * friction, v.Y * 0.8, v.Z * friction);
            v = FluidFalling(v, gravity, falling);
            if (player.HorizontalCollision && IsSpaceEmpty(player.AABB.Offset(v.X, v.Y + 0.6 - player.Position.Y + startY, v.Z))) v.Y = 0.3;
            player.Velocity = v;
        }

        /// <summary>Vanilla LivingEntity.travel, lava branch (Minosoft's LavaFluid.travel).</summary>
        private void TravelLava()
        {
            var gravity = CurrentGravity;
            var falling = player.Velocity.Y <= 0;
            var startY = player.Position.Y;
            ApplyMovementInput(0.02f);
            Move(player.Velocity);

            var v = player.Velocity;
            if (player.LavaHeight <= FluidJumpThreshold)
            {
                v = new Vec3d(v.X * 0.5, v.Y * 0.8, v.Z * 0.5);
                v = FluidFalling(v, gravity, falling);
            }
            else
            {
                v = v * 0.5;
            }
            v.Y -= gravity / 4.0;
            if (player.HorizontalCollision && IsSpaceEmpty(player.AABB.Offset(v.X, v.Y + 0.6 - player.Position.Y + startY, v.Z))) v.Y = 0.3;
            player.Velocity = v;
        }

        /// <summary>Vanilla getFluidFallingAdjustedMovement.</summary>
        private Vec3d FluidFalling(Vec3d v, double gravity, bool falling)
        {
            if (player.Sprinting) return v;
            var y = falling && Math.Abs(v.Y - 0.005) >= 0.003 && Math.Abs(v.Y - gravity / 16.0) < 0.003 ? -0.003 : v.Y - gravity / 16.0;
            return new Vec3d(v.X, y, v.Z);
        }

        /// <summary>Sprint swimming: the look direction steers up and down (Minosoft's travelSwimming).</summary>
        private void TravelSwimming()
        {
            var look = MathUtil.Front(player.Yaw, player.Pitch).Y;
            var above = world.GetBlock(BlockPos.Floor(player.Position.X, player.Position.Y + 1.0 - 0.1, player.Position.Z));
            if (look <= 0 || jumping || Fluids.Get(above) != FluidType.None)
            {
                var v = player.Velocity;
                v.Y += (look - v.Y) * (look < -0.2 ? 0.085 : 0.06);
                player.Velocity = v;
            }
            Travel();
        }

        private void TravelFlying()
        {
            var upwards = player.Velocity.Y * 0.6;
            var airSpeed = player.FlyingSpeed;
            if (player.Sprinting) airSpeed *= 2.0f;
            TravelNormal(airSpeed);
            var v = player.Velocity;
            v.Y = upwards;
            player.Velocity = v;
            player.FallDistance = 0;
        }

        // ---- climbing (ClimbingPhysics) ----

        private void LimitClimbingSpeed()
        {
            player.Climbing = !player.Flying && player.Gamemode != Gamemode.Spectator && Fluids.IsClimbable(world.GetBlock(BlockPos.Floor(player.Position)));
            if (!player.Climbing) return;
            player.FallDistance = 0;
            var v = player.Velocity;
            v.X = Math.Max(-0.15, Math.Min(0.15, v.X));
            v.Z = Math.Max(-0.15, Math.Min(0.15, v.Z));
            v.Y = Math.Max(v.Y, -0.15);
            var scaffolding = world.GetBlock(BlockPos.Floor(player.Position))?.Block.Name.Path == "scaffolding";
            if (v.Y < 0 && player.Input.Sneak && !scaffolding) v.Y = 0; // holding on
            player.Velocity = v;
        }

        private void ApplyClimbingSpeed()
        {
            if (!player.Climbing || !(player.HorizontalCollision || jumping)) return;
            var v = player.Velocity;
            v.Y = 0.2;
            player.Velocity = v;
        }

        // ---- blocks the player is inside of (cobweb, berry bush) ----

        private void CheckInsideBlocks()
        {
            var aabb = player.AABB.Shrink(1.0E-7);
            for (var x = (int)Math.Floor(aabb.Min.X); x <= (int)Math.Floor(aabb.Max.X); x++)
            {
                for (var y = (int)Math.Floor(aabb.Min.Y); y <= (int)Math.Floor(aabb.Max.Y); y++)
                {
                    for (var z = (int)Math.Floor(aabb.Min.Z); z <= (int)Math.Floor(aabb.Max.Z); z++)
                    {
                        switch (world.GetBlock(x, y, z)?.Block.Name.Path)
                        {
                            case "cobweb":
                                if (!player.Flying) stuckMultiplier = new Vec3d(0.25, 0.05, 0.25);
                                break;
                            case "sweet_berry_bush":
                                stuckMultiplier = new Vec3d(0.8, 0.75, 0.8);
                                break;
                            case "powder_snow":
                                stuckMultiplier = new Vec3d(0.9, 1.5, 0.9);
                                break;
                        }
                    }
                }
            }
        }

        private void TickEffects()
        {
            var expired = new List<int>();
            foreach (var effect in player.Effects.Values)
            {
                if (effect.Duration > 0 && --effect.Duration == 0) expired.Add(effect.Id);
            }
            foreach (var id in expired) player.Effects.Remove(id);
        }

        /// <summary>Vanilla movementInputToVelocity.</summary>
        private void ApplyMovementInput(float speed)
        {
            double x = sideways, y = 0, z = forwards;
            var length2 = x * x + y * y + z * z;
            if (length2 < 1.0E-7) return;
            if (length2 > 1.0)
            {
                var length = Math.Sqrt(length2);
                x /= length;
                z /= length;
            }
            x *= speed;
            z *= speed;
            var yaw = player.Yaw * MathUtil.DegToRad;
            var sin = Math.Sin(yaw);
            var cos = Math.Cos(yaw);
            var v = player.Velocity;
            v.X += x * cos - z * sin;
            v.Z += z * cos + x * sin;
            player.Velocity = v;
        }

        // ---- movement and collision (EntityPhysics.move) ----

        public void Move(Vec3d movement)
        {
            var adjusted = movement;
            if (stuckMultiplier.HasValue)
            {
                // cobweb and friends (Entity.makeStuckInBlock): this move is slowed, the velocity is lost
                var m = stuckMultiplier.Value;
                adjusted = new Vec3d(adjusted.X * m.X, adjusted.Y * m.Y, adjusted.Z * m.Z);
                stuckMultiplier = null;
                player.Velocity = Vec3d.Zero;
            }
            if (ShouldAdjustForSneaking(adjusted)) adjusted = AdjustForSneaking(adjusted);

            var collision = adjusted.LengthSquared >= 1.0E-7 ? Collide(adjusted) : adjusted;
            player.Position += collision;

            var collidedX = !Approximately(adjusted.X, collision.X);
            var collidedZ = !Approximately(adjusted.Z, collision.Z);
            player.HorizontalCollision = collidedX || collidedZ;
            player.VerticalCollision = adjusted.Y != collision.Y;
            player.OnGround = player.VerticalCollision && adjusted.Y < 0.0;

            var v = player.Velocity;
            if (collidedX) v.X = 0;
            if (collidedZ) v.Z = 0;
            if (player.VerticalCollision) v.Y = 0; // Block.updateEntityAfterFallOn (slime bounce not implemented)
            player.Velocity = v;

            if (player.OnGround) player.FallDistance = 0;
            else if (collision.Y < 0) player.FallDistance -= (float)collision.Y;

            var multiplier = GetSpeedFactor();
            if (multiplier != 1.0f && !player.Flying)
            {
                v = player.Velocity;
                v.X *= multiplier;
                v.Z *= multiplier;
                player.Velocity = v;
            }
        }

        private static bool Approximately(double a, double b) => Math.Abs(a - b) < 1.0E-5;

        private Vec3d Collide(Vec3d movement)
        {
            var aabb = player.AABB;
            CollectCollisions(aabb.Extend(movement), collisions);
            var collision = Collide(movement, aabb, collisions);

            var collidedX = movement.X != collision.X;
            var collidedY = movement.Y != collision.Y;
            var collidedZ = movement.Z != collision.Z;
            var grounded = player.OnGround || collidedY && movement.Y < 0.0;
            if (!grounded || !(collidedX || collidedZ)) return collision;

            // step up (CollisionMovementPhysics.collideStepping)
            CollectCollisions(aabb.Extend(new Vec3d(movement.X, StepHeight, movement.Z)), collisions);
            var total = Collide(new Vec3d(movement.X, StepHeight, movement.Z), aabb, collisions);
            var vertical = Collide(new Vec3d(0, StepHeight, 0), aabb.Extend(new Vec3d(movement.X, 0, movement.Z)), collisions);
            if (vertical.Y < StepHeight)
            {
                var horizontal = Collide(new Vec3d(movement.X, 0, movement.Z), aabb.Offset(vertical), collisions) + vertical;
                if (horizontal.HorizontalLengthSquared > total.HorizontalLengthSquared) total = horizontal;
            }
            if (total.HorizontalLengthSquared > collision.HorizontalLengthSquared)
            {
                return total + Collide(new Vec3d(0, -total.Y + movement.Y, 0), aabb.Offset(total), collisions);
            }
            return collision;
        }

        /// <summary>Y first, then the larger horizontal axis (vanilla compares absolute values).</summary>
        public static Vec3d Collide(Vec3d movement, AABB aabb, List<AABB> shapes)
        {
            var length2 = movement.LengthSquared;
            if (length2 < 1.0E-7) return Vec3d.Zero;
            var adjusted = movement;
            var offset = Vec3d.Zero;

            adjusted.Y = CheckMovement(Axis.Y, adjusted.Y, offset, aabb, shapes);
            offset.Y = adjusted.Y;

            var zPriority = Math.Abs(adjusted.Z) > Math.Abs(adjusted.X);
            if (zPriority)
            {
                adjusted.Z = CheckMovement(Axis.Z, adjusted.Z, offset, aabb, shapes);
                offset.Z = adjusted.Z;
            }
            adjusted.X = CheckMovement(Axis.X, adjusted.X, offset, aabb, shapes);
            offset.X = adjusted.X;
            if (!zPriority) adjusted.Z = CheckMovement(Axis.Z, adjusted.Z, offset, aabb, shapes);

            if (adjusted.LengthSquared > length2) return Vec3d.Zero;
            return adjusted;
        }

        private static double CheckMovement(Axis axis, double value, Vec3d offset, AABB aabb, List<AABB> shapes)
        {
            if (value == 0.0 || Math.Abs(value) < 1.0E-7) return 0.0;
            foreach (var shape in shapes)
            {
                value = shape.CalculateMaxDistance(aabb, offset, value, axis);
                if (value == 0.0) return 0.0;
            }
            return value;
        }

        /// <summary>All block collision boxes inside the area (blocks one below are included for fences/walls).</summary>
        public void CollectCollisions(AABB area, List<AABB> result) => CollectCollisions(world, area, result);

        public static void CollectCollisions(World world, AABB area, List<AABB> result)
        {
            result.Clear();
            var minX = (int)Math.Floor(area.Min.X - 1.0E-7);
            var minY = (int)Math.Floor(area.Min.Y - 1.0E-7) - 1;
            var minZ = (int)Math.Floor(area.Min.Z - 1.0E-7);
            var maxX = (int)Math.Floor(area.Max.X + 1.0E-7);
            var maxY = (int)Math.Floor(area.Max.Y + 1.0E-7);
            var maxZ = (int)Math.Floor(area.Max.Z + 1.0E-7);
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    var chunk = world.GetChunk(x >> 4, z >> 4);
                    if (chunk == null)
                    {
                        // unloaded chunks are solid, so the player can not fall out of the world while chunks load
                        for (var y = minY; y <= maxY; y++) result.Add(AABB.FullBlock.Offset(x, y, z));
                        continue;
                    }
                    for (var y = Math.Max(minY, world.MinY); y <= Math.Min(maxY, world.MaxY); y++)
                    {
                        var state = chunk.Get(x & 0x0F, y, z & 0x0F);
                        if (state == null || !state.HasCollision) continue;
                        foreach (var box in state.CollisionShape) result.Add(box.Offset(x, y, z));
                    }
                }
            }
        }

        public bool IsSpaceEmpty(AABB aabb)
        {
            var shapes = new List<AABB>();
            CollectCollisions(aabb, shapes);
            foreach (var shape in shapes)
            {
                if (shape.Intersects(aabb)) return false;
            }
            return true;
        }

        private bool WouldPoseCollide(Pose pose) => !IsSpaceEmpty(player.GetAABB(pose).Shrink(1.0E-7));

        /// <summary>Minosoft's calculatePose: swimming, sneaking or standing, falling back to a pose that fits.</summary>
        private void UpdatePose()
        {
            var pose = player.FallFlying ? Pose.FallFlying : player.Swimming ? Pose.Swimming : player.Input.Sneak && !player.Flying ? Pose.Sneaking : Pose.Standing;
            if (player.Gamemode != Gamemode.Spectator && WouldPoseCollide(pose))
            {
                pose = !WouldPoseCollide(Pose.Sneaking) ? Pose.Sneaking : Pose.Swimming;
            }
            player.Pose = pose;
            player.EyeHeight = LocalPlayer.EyeHeightOf(pose);
        }

        // ---- sneaking (SneakAdjuster) ----

        private bool ShouldAdjustForSneaking(Vec3d movement)
        {
            if (!player.Input.Sneak || movement.Y > 0.0 || player.Flying || !player.OnGround) return false;
            return player.FallDistance < StepHeight && !IsSpaceEmpty(player.AABB.Offset(0, player.FallDistance - StepHeight, 0));
        }

        private static double SneakStep(double value)
        {
            if (value < SneakCheck && value >= -SneakCheck) return 0.0;
            return value > 0.0 ? value - SneakCheck : value + SneakCheck;
        }

        private Vec3d AdjustForSneaking(Vec3d movement)
        {
            var aabb = player.AABB;
            var x = movement.X;
            var z = movement.Z;
            while (x != 0.0 && IsSpaceEmpty(aabb.Offset(x, -StepHeight, 0))) x = SneakStep(x);
            while (z != 0.0 && IsSpaceEmpty(aabb.Offset(0, -StepHeight, z))) z = SneakStep(z);
            while (x != 0.0 && z != 0.0 && IsSpaceEmpty(aabb.Offset(x, -StepHeight, z)))
            {
                x = SneakStep(x);
                z = SneakStep(z);
            }
            return new Vec3d(x, movement.Y, z);
        }

        // ---- block properties below the player ----

        /// <summary>The block that affects movement: 0.5 below the feet (vanilla getBlockPosBelowThatAffectsMyMovement).</summary>
        private BlockState GetVelocityState()
        {
            var p = player.Position;
            return world.GetBlock(BlockPos.Floor(p.X, p.Y - 0.5000001, p.Z));
        }

        private float GetSpeedFactor()
        {
            var feet = world.GetBlock(BlockPos.Floor(player.Position));
            var factor = feet?.Block.SpeedFactor ?? 1.0f;
            if (factor != 1.0f && feet?.Block.IsFluid != true) return factor;
            return GetVelocityState()?.Block.SpeedFactor ?? 1.0f;
        }

        private float GetJumpFactor()
        {
            var feet = world.GetBlock(BlockPos.Floor(player.Position));
            var factor = feet?.Block.JumpFactor ?? 1.0f;
            if (factor != 1.0f) return factor;
            return GetVelocityState()?.Block.JumpFactor ?? 1.0f;
        }
    }
}
