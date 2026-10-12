// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Entities;
using WhiteFox.Core.Local;
using WhiteFox.Core.Network;
using WhiteFox.Core.Physics;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Game
{
    /// <summary>Per frame input collected by the engine layer and consumed by the next tick.</summary>
    public sealed class InputState
    {
        public MovementInput Movement;
        public bool JumpPressed;
        public bool ForwardPressed;
        public bool DropPressed;
        public bool DropStack;
        public bool SwapPressed;
        public bool AttackHeld;
        public bool AttackPressed;
        public bool UseHeld;
        public bool UsePressed;
        public bool PickPressed;

        public void ClearEdges()
        {
            JumpPressed = false;
            ForwardPressed = false;
            DropPressed = false;
            SwapPressed = false;
            AttackPressed = false;
            UsePressed = false;
            PickPressed = false;
        }
    }

    public sealed class MapData
    {
        public readonly byte[] Colors = new byte[128 * 128];
        /// <summary>Markers of the map (players, frames, banners, structures); replaced by every packet that has them.</summary>
        public List<MapDecoration> Decorations = new List<MapDecoration>();
        public int Version;
    }

    public sealed class ChatLine
    {
        /// <summary>Legacy formatted text (§ codes).</summary>
        public string Text;
        /// <summary>Clickable / hoverable ranges of the plain text.</summary>
        public List<TextLink> Links;
        /// <summary>Environment.TickCount when received (for fading).</summary>
        public int Received;
    }

    /// <summary>
    /// The client session (Minosoft's PlaySession + SessionTicker): world, local player and interaction, ticked at
    /// 20 TPS on the game thread. Normally connected to a server through <see cref="Server"/>; without a server it
    /// runs the local flat test world (Minosoft's LocalConnection), which only exists for development.
    /// </summary>
    public sealed class GameSession
    {
        public const int TicksPerSecond = 20;
        public const double TickSeconds = 1.0 / TicksPerSecond;

        public readonly ClientResources Resources;
        public readonly World World;
        public readonly LocalPlayer Player;
        public readonly PlayerPhysics Physics;
        public readonly InputState Input = new InputState();

        /// <summary>The server connection, or null for the local test world.</summary>
        public readonly ServerSession Server;
        /// <summary>Only for the local test world.</summary>
        public readonly LocalChunkManager ChunkManager;

        /// <summary>Item name ("stone") and count per hotbar slot, for the HUD icons (from <see cref="Inventory"/>).</summary>
        public readonly string[] HotbarItems = new string[9];
        public readonly int[] HotbarCounts = new int[9];
        public readonly Inventory Inventory;
        public readonly EntityManager Entities;
        /// <summary>The entity under the crosshair (closer than the targeted block), or null.</summary>
        public Entity TargetEntity { get; private set; }
        public int SelectedSlot;

        public readonly List<ChatLine> Chat = new List<ChatLine>();
        public string ActionBar;
        public int ActionBarReceived;

        public float Health = 20;
        public int Food = 20;
        public float Saturation = 5;
        /// <summary>Experience bar progress (0..1), level and total points.</summary>
        public float ExperienceBar;
        public int ExperienceLevel;
        public int ExperienceTotal;
        /// <summary>Air supply (300 = full, shown as bubbles under water).</summary>
        public int Air = 300;
        /// <summary>Set when the player died; the death screen offers to respawn.</summary>
        public string DeathMessage;
        public bool Dead => DeathMessage != null;
        public string DimensionName = "minecraft:overworld";
        /// <summary>Current dimension type (height, sky light, effects).</summary>
        public DimensionType Dimension = new DimensionType();

        private void ApplyDimension(DimensionType dimension)
        {
            Dimension = dimension;
            if (Server == null) return;
            World.Biomes = Server.Biomes;
            Resources.Colors.Apply(World.Biomes);
        }
        public string DisconnectReason { get; private set; }
        /// <summary>True once the server placed the player (first position packet), always true locally.</summary>
        public bool Spawned { get; private set; }

        /// <summary>Server teleports received (a growing count while walking means the server rejected movement).</summary>
        public int TeleportCount { get; private set; }
        public long Ticks { get; private set; }

        /// <summary>Milliseconds per call spent applying packets (chunks are the expensive part: light).</summary>
        public double NetworkBudget = double.MaxValue;
        public BlockHit? Target { get; private set; }

        private readonly MovementSender movement;
        private int breakCooldown;

        // ------------------------------------------------------------------ particles

        public event Action<ParticleRequest> Particle;

        public void SpawnParticles(ParticleRequest request) => Particle?.Invoke(request);

        /// <summary>Particle packet: resolves the registry id.</summary>
        public void OnParticle(int id, ParticleRequest request)
        {
            if (Resources.ParticleTypes == null || id < 0 || id >= Resources.ParticleTypes.Length) return;
            request.Name = Resources.ParticleTypes[id];
            Particle?.Invoke(request);
        }

        // ------------------------------------------------------------------ signs, books, maps

        /// <summary>The server asks to edit a sign (position, front side).</summary>
        public event Action<BlockPos, bool> SignEditorOpened;
        /// <summary>A book to read (written book in a hand) or to edit (book and quill): stack, hotbar slot or 40.</summary>
        public event Action<ItemStack, int, bool> BookOpened;

        /// <summary>Map colors per map id (128 x 128 color ids), with a version for the renderer.</summary>
        public readonly Dictionary<int, MapData> Maps = new Dictionary<int, MapData>();

        public void OnOpenSignEditor(BlockPos position, bool front) => SignEditorOpened?.Invoke(position, front);

        public void OnOpenBook(int hand)
        {
            var slot = hand == 1 ? 40 : SelectedSlot;
            var stack = hand == 1 ? Inventory.Player.Slots[45] : Inventory.Player.Slots[36 + SelectedSlot];
            if (stack != null) BookOpened?.Invoke(stack, slot, false);
        }

        public void OnMapDecorations(int id, List<MapDecoration> decorations)
        {
            if (!Maps.TryGetValue(id, out var map)) Maps[id] = map = new MapData();
            map.Decorations = decorations;
            map.Version++;
        }

        public void OnMapData(int id, int x, int z, int columns, int rows, byte[] colors)
        {
            if (!Maps.TryGetValue(id, out var map)) Maps[id] = map = new MapData();
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var target = (z + row) * 128 + x + column;
                    var source = row * columns + column;
                    if (target < map.Colors.Length && source < colors.Length) map.Colors[target] = colors[source];
                }
            }
            map.Version++;
        }

        // ------------------------------------------------------------------ server resource packs

        /// <summary>The server offers a resource pack; the client answers with <see cref="ServerSession.SendResourcePackStatus"/>.</summary>
        public event Action<ResourcePackOffer> ResourcePackOffered;
        /// <summary>The server removes one pack (id) or all of them (null).</summary>
        public event Action<Guid?> ResourcePackRemoved;

        public void OnResourcePackOffer(ResourcePackOffer offer)
        {
            if (ResourcePackOffered != null) ResourcePackOffered(offer);
            else Server?.SendResourcePackStatus(offer.Id, 1);
        }

        public void OnResourcePackRemove(Guid? id) => ResourcePackRemoved?.Invoke(id);

        // ------------------------------------------------------------------ sounds

        /// <summary>Sounds the client plays (server packets and the local predictions vanilla makes itself).</summary>
        public event System.Action<SoundRequest> Sound;

        private float stepDistance;
        private float nextStep = 1;
        private int digTicks;
        private BlockPos? interacted;
        private long interactedTick;

        public void PlaySound(string name, Vec3d? position, float volume = 1, float pitch = 1, SoundCategory category = SoundCategory.Master)
        {
            if (name == null) return;
            Sound?.Invoke(new SoundRequest { Name = name, Position = position, Volume = volume, Pitch = pitch, Category = category });
        }

        public void PlayBlockSound(BlockState state, BlockPos position, BlockSoundKind kind)
        {
            if (state == null) return;
            Sound?.Invoke(new SoundRequest { Block = state, BlockKind = kind, Position = new Vec3d(position.X + 0.5, position.Y + 0.5, position.Z + 0.5), Volume = 1, Pitch = 1, Category = SoundCategory.Block });
        }

        /// <summary>Sound Effect / Entity Sound Effect packets: registry id (or name), at a position or an entity.</summary>
        public void OnSoundPacket(int id, string name, int category, Vec3d? position, int entityId, float volume, float pitch)
        {
            if (name == null && Resources.SoundEvents != null && id >= 0 && id < Resources.SoundEvents.Length) name = Resources.SoundEvents[id];
            if (entityId >= 0)
            {
                var entity = Entities.Get(entityId);
                if (entity == null) return;
                position = entity.Position;
            }
            PlaySound(name, position, volume, pitch, (SoundCategory)Math.Max(0, Math.Min(9, category)));
        }

        /// <summary>World Event 2001: a block was broken by somebody else (vanilla plays its break sound).</summary>
        private static readonly string[] HeadInstruments = { "zombie", "skeleton", "creeper", "dragon", "wither_skeleton", "piglin", "custom_head" };

        /// <summary>
        /// Block Action: chest lids and friends, and note blocks (vanilla NoteBlock.triggerEvent): the server sends the
        /// sound, the client adds the colored note above tunable instruments.
        /// </summary>
        public void OnBlockAction(BlockPos position, int action, int parameter)
        {
            var state = World.GetBlock(position);
            if (state?.Block.Name.Path == "note_block")
            {
                if (Array.IndexOf(HeadInstruments, state["instrument"]) >= 0) return;
                int.TryParse(state["note"], out var note);
                Particle?.Invoke(new ParticleRequest { Name = "note", Position = new Vec3d(position.X + 0.5, position.Y + 1.2, position.Z + 0.5), Offset = new Vec3d(note / 24.0, 0, 0), Speed = 1, Count = 0 });
                return;
            }
            World.BlockEntities.OnAction(position, action, parameter);
        }

        /// <summary>
        /// Pickup Item (vanilla ClientPacketListener.handleTakeItemEntity): the pop of an item, or the chime of an
        /// experience orb, at the entity being picked up; then it disappears.
        /// </summary>
        public void OnPickup(int collected)
        {
            if (Entities.Entities.TryGetValue(collected, out var entity))
            {
                var random = SoundRandom;
                if (entity.Type?.Name.Path == "experience_orb") PlaySound("entity.experience_orb.pickup", entity.Position, 0.1f, (float)((random.NextDouble() - random.NextDouble()) * 0.35 + 0.9), SoundCategory.Player);
                else PlaySound("entity.item.pickup", entity.Position, 0.2f, (float)(((random.NextDouble() - random.NextDouble()) * 0.7 + 1) * 2), SoundCategory.Player);
            }
            Entities.Remove(collected);
        }

        /// <summary>A jukebox starts (sound event of the disc) or stops (null) playing at a position.</summary>
        public event Action<BlockPos, string> Record;

        /// <summary>
        /// The ender dragon is hit through its 8 parts (vanilla EnderDragonPart, ids right after the dragon's): head 0,
        /// neck 1, body 2, three tail parts, two wings. Only the head takes the full damage. The part is picked from where
        /// the look ray meets the dragon, with the part offsets of EnderDragon.aiStep.
        /// </summary>
        private int DragonPartHit(Entity target)
        {
            if (target.Type?.Name.Path != "ender_dragon") return target.Id;
            var eye = Player.EyePosition;
            var front = MathUtil.Front(Player.Yaw, Player.Pitch);
            Entities.Raycast(eye, front, 8, out var distance);
            var hit = eye + front * distance;
            var radians = target.Yaw * MathUtil.DegToRad;
            // the dragon's own "forward" (its yaw points backwards): sin, -cos; to the side: cos, sin
            var forwardX = Math.Sin(radians);
            var forwardZ = -Math.Cos(radians);
            var dx = hit.X - target.Position.X;
            var dz = hit.Z - target.Position.Z;
            var along = dx * forwardX + dz * forwardZ;
            var side = dx * forwardZ * -1 + dz * forwardX;
            int part;
            if (along > 4.5) part = 0; // head
            else if (along > 2.5) part = 1; // neck
            else if (Math.Abs(side) > 3) part = side > 0 ? 6 : 7; // wings
            else if (along > -2) part = 2; // body
            else part = along > -4 ? 3 : along > -6 ? 4 : 5; // tail
            return target.Id + 1 + part;
        }

        public void OnWorldEvent(int type, BlockPos position, int data)
        {
            if (type == 2002 || type == 2007)
            {
                // splash (2002) and lingering-like instant (2007) potion breaking: the sound and a cloud in its color
                var center = new Vec3d(position.X + 0.5, position.Y, position.Z + 0.5);
                PlaySound("entity.splash_potion.break", center, 1, 0.9f + (float)SoundRandom.NextDouble() * 0.1f, SoundCategory.Neutral);
                var color = new Vec3d(((data >> 16) & 0xFF) / 255.0, ((data >> 8) & 0xFF) / 255.0, (data & 0xFF) / 255.0);
                for (var i = 0; i < 40; i++)
                {
                    var angle = SoundRandom.NextDouble() * Math.PI * 2;
                    var radius = SoundRandom.NextDouble() * 1.5;
                    Particle?.Invoke(new ParticleRequest
                    {
                        Name = "entity_effect",
                        Position = center + new Vec3d(Math.Cos(angle) * radius, SoundRandom.NextDouble() * 0.6, Math.Sin(angle) * radius),
                        Offset = color,
                        Speed = 1,
                        Count = 0,
                        Scale = 1,
                    });
                }
                return;
            }
            if (type == 1010)
            {
                // play record: data is the disc item id (music_disc_cat -> music_disc.cat)
                var item = Resources.Items.Get(data)?.Name.Path;
                if (item == null || !item.StartsWith("music_disc_")) return;
                Record?.Invoke(position, "music_disc." + item.Substring("music_disc_".Length));
                if (TextComponent.Language.TryGetValue("item.minecraft." + item + ".desc", out var title) && TextComponent.Language.TryGetValue("record.nowPlaying", out var format))
                {
                    OnChat("§d" + format.Replace("%s", title), true);
                }
                return;
            }
            if (type == 1011)
            {
                Record?.Invoke(position, null);
                return;
            }
            if (type != 2001) return;
            var state = Resources.Blocks.GetState(data);
            PlayBlockSound(state, position, BlockSoundKind.Break);
            BreakParticles(position, state);
        }

        /// <summary>The block break particles (vanilla ParticleEngine.destroy).</summary>
        public void BreakParticles(BlockPos position, BlockState state)
        {
            if (state == null) return;
            Particle?.Invoke(new ParticleRequest { Name = "#break", Position = new Vec3d(position.X, position.Y, position.Z), Block = state });
        }

        /// <summary>Damage Event: hurt animation and the hurt sound, played by every client itself.</summary>
        public void OnDamageEvent(int entityId)
        {
            if (Server != null && entityId == Server.EntityId)
            {
                PlaySound("entity.player.hurt", Player.Position, 1, 1, SoundCategory.Player);
                OwnHurtTime = 10;
                return;
            }
            var entity = Entities.Get(entityId);
            if (entity == null) return;
            entity.HurtTime = 10;
            var type = entity.Type?.Name.Path;
            if (type != null) PlaySound("entity." + type + ".hurt", entity.Position, 1, MobPitch(entity), entity.IsPlayer ? SoundCategory.Player : SoundCategory.Hostile);
        }

        /// <summary>Entity Event 3: death animation and sound.</summary>
        public void OnEntityDeath(int entityId)
        {
            var entity = Entities.Get(entityId);
            if (entity == null) return;
            entity.Health = 0;
            var type = entity.Type?.Name.Path;
            if (type != null && entity.IsLiving) PlaySound("entity." + type + ".death", entity.Position, 1, MobPitch(entity), entity.IsPlayer ? SoundCategory.Player : SoundCategory.Hostile);
        }

        private static readonly Random SoundRandom = new Random();

        private static float MobPitch(Entity entity) => (float)(SoundRandom.NextDouble() - SoundRandom.NextDouble()) * 0.2f + (entity.Baby ? 1.5f : 1.0f);

        /// <summary>Footsteps of the own player (vanilla Entity.move: one step per block of moveDist).</summary>
        private long lastUseTick = -100;
        private string lastHeld;

        /// <summary>
        /// Buckets (vanilla BucketItem: Player.playSound, which the server sends to everybody except the user): when a
        /// right click turns the held bucket into a filled one or back, the fill / empty sound of its content.
        /// </summary>
        private void TickBucketSounds()
        {
            var held = Inventory.Player.Slots[36 + SelectedSlot]?.Item?.Name.Path;
            var before = lastHeld;
            lastHeld = held;
            if (held == before || Ticks - lastUseTick > 10 || before == null || held == null) return;
            string sound = null;
            if (before == "bucket" && held == "milk_bucket") sound = "entity.cow.milk";
            else if (before == "bucket" && held.EndsWith("_bucket")) sound = "item.bucket.fill" + BucketSuffix(held);
            else if (held == "bucket" && before.EndsWith("_bucket")) sound = "item.bucket.empty" + BucketSuffix(before);
            if (sound != null) PlaySound(sound, Player.EyePosition, 1, 1, SoundCategory.Player);
        }

        private static string BucketSuffix(string bucket)
        {
            switch (bucket)
            {
                case "lava_bucket": return "_lava";
                case "powder_snow_bucket": return "_powder_snow";
                case "axolotl_bucket": return "_axolotl";
                case "tadpole_bucket": return "_tadpole";
                case "water_bucket": return "";
                default: return bucket.EndsWith("_bucket") && bucket != "milk_bucket" ? "_fish" : "";
            }
        }

        private bool wasInWater;
        private float swimDistance;
        private float nextSwim;

        /// <summary>
        /// Water sounds of the player (vanilla Entity.doWaterSplashEffect / waterSwimSound): a splash when falling in,
        /// louder when fast, and swimming strokes while moving in water.
        /// </summary>
        private void TickWaterSounds()
        {
            var v = Player.Velocity;
            var speed = (float)Math.Sqrt(v.X * v.X * 0.2 + v.Y * v.Y + v.Z * v.Z * 0.2);
            var inWater = Player.InWater;
            if (inWater && !wasInWater && !Player.Flying && Spawned)
            {
                var volume = Math.Min(1, speed * 0.2f);
                PlaySound(volume < 0.25f ? "entity.player.splash" : "entity.player.splash.high_speed", Player.Position, Math.Max(volume, 0.08f), 1 + (float)(SoundRandom.NextDouble() - SoundRandom.NextDouble()) * 0.4f, SoundCategory.Player);
            }
            wasInWater = inWater;
            if (!inWater || Player.Flying) return;
            var d = Player.Position - Player.PreviousPosition;
            swimDistance += (float)Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z) * 0.6f;
            if (swimDistance <= nextSwim) return;
            nextSwim = swimDistance + 1;
            PlaySound("entity.player.swim", Player.Position, Math.Min(1, speed * 0.35f), 1 + (float)(SoundRandom.NextDouble() - SoundRandom.NextDouble()) * 0.4f, SoundCategory.Player);
        }

        private void TickSteps()
        {
            TickWaterSounds();
            var dx = Player.Position.X - Player.PreviousPosition.X;
            var dz = Player.Position.Z - Player.PreviousPosition.Z;
            if (Player.Flying || Player.Riding || Player.InWater) return;
            // on ladders, vines and scaffolding the climbing counts too (vanilla Entity.move: moveDist with dy on
            // climbable blocks), with the step sound of the block climbed
            var climbing = Player.Climbing && !Player.OnGround;
            if (!Player.OnGround && !climbing) return;
            var dy = climbing ? Player.Position.Y - Player.PreviousPosition.Y : 0;
            stepDistance += (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) * 0.6f;
            if (stepDistance <= nextStep) return;
            var below = climbing ? BlockPos.Floor(Player.Position) : BlockPos.Floor(Player.Position.X, Player.Position.Y - 0.2, Player.Position.Z);
            var state = World.GetBlock(below);
            if (state == null) return;
            nextStep = stepDistance + 1;
            PlayBlockSound(state, below, BlockSoundKind.Step);
        }

        /// <summary>Block updates after our own interaction: the place / open / close sound vanilla predicts locally.</summary>
        private void OnBlockChangedForSounds(BlockPos position, BlockState previous, BlockState state)
        {
            if (!interacted.HasValue || Ticks - interactedTick > 20 || state == null) return;
            var origin = interacted.Value;
            if (Math.Abs(position.X - origin.X) > 1 || Math.Abs(position.Y - origin.Y) > 1 || Math.Abs(position.Z - origin.Z) > 1) return;
            if (previous == null || Placement.IsReplaceable(previous))
            {
                if (previous?.Block == state.Block) return;
                PlayBlockSound(state, position, BlockSoundKind.Place);
                interacted = null;
                return;
            }
            if (previous.Block != state.Block || state["open"] == null || previous["open"] == state["open"]) return;
            var name = state.Block.Name.Path;
            var open = state["open"] == "true";
            var iron = name.StartsWith("iron_");
            var wood = name.StartsWith("bamboo_") ? "bamboo_wood" : name.StartsWith("cherry_") ? "cherry_wood" : name.StartsWith("crimson_") || name.StartsWith("warped_") ? "nether_wood" : null;
            string sound;
            if (name.EndsWith("_trapdoor")) sound = "block." + (iron ? "iron" : wood ?? "wooden") + "_trapdoor";
            else if (name.EndsWith("_door")) sound = "block." + (iron ? "iron" : wood ?? "wooden") + "_door";
            else if (name.EndsWith("_fence_gate")) sound = "block." + (wood != null ? wood + "_fence_gate" : "fence_gate");
            else return;
            PlaySound(sound + (open ? ".open" : ".close"), new Vec3d(position.X + 0.5, position.Y + 0.5, position.Z + 0.5), 1, 1, SoundCategory.Block);
            interacted = null;
        }
        private int useCooldown;
        private int jumpTriggerTime;
        private int sprintTriggerTime;
        private bool doubleTapSprint;
        private int sentSlot = -1;

        // survival digging (Minosoft's SurvivalDigger)
        private BlockPos? digging;
        private Direction diggingFace;
        private float diggingProgress;

        public bool IsRemote => Server != null;
        private bool usingItem;
        private int usingTicks;
        private int dropCooldown;
        public float DiggingProgress => digging.HasValue ? diggingProgress : 0;

        public GameSession(ClientResources resources, int viewDistance, ServerSession server = null)
        {
            Resources = resources;
            Server = server;
            World = new World();
            Player = new LocalPlayer();
            Physics = new PlayerPhysics(World, Player);
            Inventory = new Inventory(this);
            Entities = new EntityManager(World);
            World.BlockChanged += OnBlockChangedForSounds;

            if (server != null)
            {
                movement = new MovementSender(server, Player);
                Player.Gamemode = Gamemode.Survival;
                Player.AllowFlying = false;
                return;
            }

            var flat = FlatGenerator.Default(resources.Blocks, World.MinY);
            ChunkManager = new LocalChunkManager(World, new ShowcaseDecorator(flat, resources.Blocks, flat.SurfaceY), viewDistance);
            Player.Teleport(new Vec3d(0.5, flat.SurfaceY, 0.5));
            Spawned = true;

            string[] hotbar = { "stone", "oak_planks", "glass", "oak_log", "torch", "oak_stairs", "stone_brick_slab", "glowstone", "oak_leaves" };
            for (var i = 0; i < hotbar.Length; i++)
            {
                var item = resources.Items.Get(ResourceLocation.Minecraft(hotbar[i]));
                if (item != null) Inventory.Player.Slots[36 + i] = new ItemStack { Item = item, Count = item.StackSize };
            }
            UpdateHotbar();
        }

        /// <summary>Generates the chunks around the spawn so the first frame already has ground (local world only).</summary>
        public void Prepare()
        {
            if (ChunkManager == null) return;
            var previous = ChunkManager.ChunksPerTick;
            ChunkManager.ChunksPerTick = int.MaxValue;
            var old = ChunkManager.ViewDistance;
            ChunkManager.ViewDistance = Math.Min(old, 2);
            ChunkManager.Update(BlockPos.Floor(Player.Position).ChunkPos);
            ChunkManager.ViewDistance = old;
            ChunkManager.ChunksPerTick = previous;
        }

        /// <summary>Applies received packets; called every frame so chunks appear without waiting for the next tick.</summary>
        public void ProcessNetwork(double budgetMilliseconds = double.MaxValue) => Server?.Process(this, budgetMilliseconds);

        public void Tick()
        {
            ProcessNetwork(NetworkBudget);
            if (DisconnectReason != null) return;
            Ticks++;
            World.TimeOfDay = (World.TimeOfDay + 1) % 24000;

            ChunkManager?.Update(BlockPos.Floor(Player.Position).ChunkPos);
            if (!Spawned || Dead)
            {
                Input.ClearEdges();
                return;
            }

            // double tap jump toggles flying (vanilla: 7 ticks)
            if (jumpTriggerTime > 0) jumpTriggerTime--;
            if (Input.JumpPressed && Player.AllowFlying)
            {
                if (jumpTriggerTime == 0) jumpTriggerTime = 7;
                else
                {
                    Player.ToggleFly = true;
                    jumpTriggerTime = 0;
                }
            }

            // double tap forward sprints (vanilla, 7 ticks; Minosoft only has the sprint key)
            if (sprintTriggerTime > 0) sprintTriggerTime--;
            if (Input.ForwardPressed)
            {
                if (sprintTriggerTime > 0) doubleTapSprint = true;
                else sprintTriggerTime = 7;
            }
            if (Input.Movement.Forwards <= 0) doubleTapSprint = false;

            // elytra: jump in the air starts gliding (vanilla LocalPlayer.aiStep / tryToStartFallFlying)
            var chest = Inventory.Player.Slots[6];
            Player.WearingElytra = chest?.Item?.Name.Path == "elytra" && Nbt.GetInt(chest.Nbt, "Damage") < 431;
            if (Input.JumpPressed && !Player.OnGround && !Player.FallFlying && !Player.Flying && !Player.Riding && Player.WearingElytra && !Player.InWater && !Player.InLava && Player.EffectLevel(Effects.Levitation) == 0 && Player.Velocity.Y < 0.2)
            {
                Player.FallFlying = true;
                fallFlyingStart = Ticks;
                Server?.SendPlayerCommand(8);
            }

            var movementInput = Input.Movement;
            movementInput.Sprint |= doubleTapSprint;
            Player.Input = movementInput;
            Player.Food = Food;
            Entities.OwnId = Server?.EntityId ?? -1;
            Entities.OwnEyePosition = Player.EyePosition;
            Entities.Tick();
            World.BlockEntities.Tick();
            if (Player.SwingTime >= 0 && ++Player.SwingTime >= 6) Player.SwingTime = -1;
            if (Player.Riding) TickRiding();
            else Physics.Tick();
            if (!IsRemote) TickLocalAir();
            TickSteps();
            TickBucketSounds();
            TickGliding();
            TickEntityItemUse();
            TickArrows();
            if (OwnHurtTime > 0) OwnHurtTime--;
            TickPotionParticles();
            if (!Player.Riding) movement?.Tick();

            if (IsRemote && SelectedSlot != sentSlot)
            {
                sentSlot = SelectedSlot;
                Server.SendHeldItem(SelectedSlot);
            }

            UpdateTarget();
            TickInteraction();
            Input.ClearEdges();
        }

        /// <summary>Rotation is applied immediately on mouse movement (Minosoft's PersonView.handleMouse).</summary>
        public void Rotate(float deltaYaw, float deltaPitch)
        {
            Player.Yaw = MathUtil.WrapDegrees(Player.Yaw + deltaYaw);
            Player.Pitch = Math.Max(-90.0f, Math.Min(90.0f, Player.Pitch + deltaPitch));
        }

        public void UpdateTarget()
        {
            var eye = Player.EyePosition;
            var front = MathUtil.Front(Player.Yaw, Player.Pitch);
            Target = Raycast.Blocks(World, eye, front, Player.ReachDistance);
            // entities are reachable up to 3 blocks (creative 5), and only if nothing solid is in between
            var entityReach = Player.Gamemode == Gamemode.Creative ? 5.0 : 3.0;
            var blockDistance = Target.HasValue ? Target.Value.Distance : double.MaxValue;
            var entity = Entities.Raycast(eye, front, Math.Min(entityReach, blockDistance), out _);
            TargetEntity = entity != null && entity.Id != Player.Vehicle ? entity : null;
            if (TargetEntity != null) Target = null;
        }

        public void SendChat(string text)
        {
            if (Server != null) Server.SendChat(text);
            else OnChat("<" + "local" + "> " + text, false);
        }

        // ------------------------------------------------------------------ interaction

        private void TickInteraction()
        {
            if (breakCooldown > 0) breakCooldown--;
            if (useCooldown > 0) useCooldown--;

            if (Input.PickPressed && Target.HasValue && !IsRemote)
            {
                var item = Resources.Items.Get(Target.Value.State.Block.Name);
                var existing = item == null ? -1 : Array.IndexOf(HotbarItems, item.Name.Path);
                if (existing >= 0) SelectedSlot = existing;
                else if (item != null)
                {
                    Inventory.Player.Slots[36 + SelectedSlot] = new ItemStack { Item = item, Count = item.StackSize };
                    UpdateHotbar();
                }
            }

            // entities: left click attacks, right click interacts (Interact packet)
            if (TargetEntity != null)
            {
                if (Input.AttackPressed)
                {
                    Server?.SendInteract(DragonPartHit(TargetEntity), 1, Player.Pose == Pose.Sneaking);
                    Server?.SendSwingArm();
                    Player.Swing();
                }
                if (Input.UsePressed && useCooldown == 0)
                {
                    var hit = TargetEntity.Position;
                    var eye = Player.EyePosition;
                    Server?.SendInteract(TargetEntity.Id, 2, Player.Pose == Pose.Sneaking, (float)(eye.X - hit.X) * 0.5f, (float)(eye.Y - hit.Y) * 0.5f, (float)(eye.Z - hit.Z) * 0.5f);
                    Server?.SendInteract(TargetEntity.Id, 0, Player.Pose == Pose.Sneaking);
                    Server?.SendSwingArm();
                    Player.Swing();
                    useCooldown = 4;
                }
                return;
            }

            if (Player.Gamemode == Gamemode.Creative || !IsRemote) TickCreativeBreaking();
            else TickSurvivalDigging();

            TickUse();
            TickDropAndSwap();
        }

        /// <summary>
        /// Right click (Minosoft's UseHandler + ShortUseHandler + LongUseHandler): blocks are placed every 4 ticks
        /// while held; usable items (food, bows, shields...) are used while held and released afterwards.
        /// </summary>
        private void TickUse()
        {
            var heldStack = Inventory.Player.Slots[36 + SelectedSlot];
            var held = heldStack?.Item;
            var isBlockItem = held != null && Resources.Blocks.Get(held.Name) != null;
            var offHand = Inventory.Player.Slots[45]?.Item;
            var offIsBlock = offHand != null && Resources.Blocks.Get(offHand.Name) != null;

            if (usingItem)
            {
                usingTicks++;
                var usedStack = Player.UsingOffHand ? Inventory.Player.Slots[45] : heldStack;
                if (usedStack != null) ItemUseEffects(usedStack, usingTicks, Player.EyePosition, Player.Yaw, Player.Pitch, Player.Position);
                // vanilla LivingEntity.completeUsingItem: food and drinks are done after their use duration (the
                // server eats them then); still holding the button eats the next one once the right click delay passed
                var duration = UseDuration(usedStack);
                if (duration > 0 && usingTicks >= duration)
                {
                    if (Resources.ItemProperties.IsFood(usedStack.Item)) SpawnItemParticles(usedStack, Player.EyePosition, Player.Yaw, Player.Pitch, 16);
                    usingItem = false;
                    Player.UsingItem = false;
                    useCooldown = 4;
                    return;
                }
                if (Input.UseHeld && usedStack != null) return;
                Riptide(heldStack, usingTicks);
                usingItem = false;
                Player.UsingItem = false;
                if (IsRemote) Server.SendPlayerAction(5, new BlockPos(0, 0, 0), Direction.Down);
                return;
            }

            if (Input.UsePressed) useCooldown = 0;
            if (!Input.UseHeld || useCooldown != 0) return;
            useCooldown = 4;
            lastUseTick = Ticks;

            var target = Target;
            if (target.HasValue)
            {
                var hit = target.Value;
                Player.Swing();
                if (IsRemote)
                {
                    float cursorX = (float)(hit.Point.X - hit.Position.X), cursorY = (float)(hit.Point.Y - hit.Position.Y), cursorZ = (float)(hit.Point.Z - hit.Position.Z);
                    Server.SendUseItemOn(hit.Position, hit.Face, cursorX, cursorY, cursorZ);
                    // vanilla MultiPlayerGameMode: when the main hand does nothing there (no block in it, a block that
                    // is not opened or switched), the off hand gets its turn: a torch or block in the off hand is placed
                    if (!isBlockItem && offIsBlock && !Interactive(hit.State)) Server.SendUseItemOn(hit.Position, hit.Face, cursorX, cursorY, cursorZ, 1);
                    interacted = Placement.IsReplaceable(hit.State) ? hit.Position : hit.Position.Offset(hit.Face);
                    if (hit.State["open"] != null) interacted = hit.Position;
                    interactedTick = Ticks;
                    Server.SendSwingArm();
                }
                else
                {
                    PlaceLocal(hit);
                }
                if (isBlockItem || offIsBlock && !Interactive(hit.State)) return;
            }
            if (!IsRemote) return;
            // nothing to use in the main hand: the off hand (a shield, food, a bow) is used instead
            var useOffHand = (held == null || isBlockItem || !Resources.ItemProperties.IsUsable(held, Food)) && offHand != null && !offIsBlock
                             && Resources.ItemProperties.IsUsable(offHand, Food);
            if (useOffHand)
            {
                Server.SendUseItem(1);
                usingItem = true;
                usingTicks = 0;
                Player.UsingItem = true;
                Player.UsingOffHand = true;
                return;
            }
            if (held == null || isBlockItem) return;
            Server.SendUseItem();
            // the book and quill editor is opened by the client itself (vanilla Player.openItemGui)
            if (held.Name.Path == "writable_book" && Input.UsePressed) BookOpened?.Invoke(heldStack, SelectedSlot, true);
            if (Resources.ItemProperties.IsUsable(held, Food))
            {
                usingItem = true;
                usingTicks = 0;
                Player.UsingItem = true;
                Player.UsingOffHand = false;
            }
        }

        /// <summary>Vanilla getUseDuration of the items that finish by themselves (0: used until released).</summary>
        private int UseDuration(ItemStack stack)
        {
            var path = stack?.Item?.Name.Path;
            if (path == null) return 0;
            if (path == "honey_bottle") return 40;
            if (path == "potion" || path == "milk_bucket") return 32;
            if (Resources.ItemProperties.IsFood(stack.Item)) return path == "dried_kelp" ? 16 : 32;
            return 0;
        }

        /// <summary>Blocks a right click opens or switches (the off hand does not place a block against them).</summary>
        private static bool Interactive(BlockState state)
        {
            if (state == null) return false;
            if (state.Block.Special != Worlds.BlockEntityKind.None) return true;
            if (state["open"] != null || state["powered"] != null && !state.Block.Name.Path.Contains("rail")) return true;
            var path = state.Block.Name.Path;
            return path.EndsWith("crafting_table") || path.EndsWith("anvil") || path.EndsWith("_bed") || path == "loom" || path == "stonecutter"
                   || path == "grindstone" || path == "cartography_table" || path == "smithing_table" || path.EndsWith("button") || path == "lever"
                   || path == "repeater" || path == "comparator" || path == "note_block" || path == "cake" || path == "respawn_anchor";
        }

        /// <summary>Q drops one item, Ctrl+Q the stack; F swaps main and off hand (Player Action 4/3/6).</summary>
        private void TickDropAndSwap()
        {
            if (dropCooldown > 0) dropCooldown--;
            if (Input.DropPressed && dropCooldown == 0)
            {
                dropCooldown = 2;
                var slot = 36 + SelectedSlot;
                var stack = Inventory.Player.Slots[slot];
                if (stack != null)
                {
                    var all = Input.DropStack || stack.Count == 1;
                    if (IsRemote) Server.SendPlayerAction(all ? 3 : 4, new BlockPos(0, 0, 0), Direction.Down);
                    Inventory.Player.Slots[slot] = all ? null : new ItemStack { Item = stack.Item, Count = stack.Count - 1, Nbt = stack.Nbt };
                    UpdateHotbar();
                }
            }
            if (Input.SwapPressed)
            {
                var slot = 36 + SelectedSlot;
                var main = Inventory.Player.Slots[slot];
                Inventory.Player.Slots[slot] = Inventory.Player.Slots[45];
                Inventory.Player.Slots[45] = main;
                if (IsRemote) Server.SendPlayerAction(6, new BlockPos(0, 0, 0), Direction.Down);
                UpdateHotbar();
            }
        }

        /// <summary>Air in the local test world (the server sends it otherwise): -1 per tick under water, refills +4.</summary>
        private void TickLocalAir()
        {
            if (Player.EyeInWater && Player.EffectLevel(Effects.WaterBreathing) == 0 && Player.Gamemode != Gamemode.Creative) Air = Math.Max(-20, Air - 1);
            else Air = Math.Min(300, Air + 4);
        }

        /// <summary>Instant breaking with a 5 tick cooldown while held (Minosoft's CreativeBreaker).</summary>
        private void TickCreativeBreaking()
        {
            if (Input.AttackPressed) breakCooldown = 0;
            if (!Input.AttackHeld || breakCooldown != 0 || !Target.HasValue) return;
            var hit = Target.Value;
            if (IsRemote)
            {
                Server.SendPlayerAction(0, hit.Position, hit.Face);
                Server.SendSwingArm();
            }
            Player.Swing();
            PlayBlockSound(hit.State, hit.Position, BlockSoundKind.Break);
            BreakParticles(hit.Position, hit.State);
            World.SetBlock(hit.Position, null); // predicted; the server corrects it with a block update if needed
            breakCooldown = 5;
            UpdateTarget();
        }

        /// <summary>
        /// Destroy progress per tick (Minosoft's SurvivalDigger / vanilla getDestroyProgress): tool speed, efficiency,
        /// haste and mining fatigue, slower under water and in the air, divided by hardness and 30 (100 without the
        /// right tool).
        /// </summary>
        public float DestroyProgress(BlockState state)
        {
            var hardness = state.Block.Hardness;
            if (hardness < 0) return 0;
            if (hardness == 0) return 1;
            var stack = Inventory.Player.Slots[36 + SelectedSlot];
            var speed = Resources.ItemProperties.ToolSpeed(state.Block, stack?.Item);
            if (speed > 1)
            {
                var efficiency = ItemProperties.EnchantmentLevel(stack, "minecraft:efficiency");
                if (efficiency > 0) speed += efficiency * efficiency + 1;
            }
            var haste = Player.EffectLevel(Effects.Haste);
            if (haste > 0) speed *= 1 + 0.2f * haste;
            var fatigue = Player.EffectLevel(Effects.MiningFatigue);
            if (fatigue > 0) speed *= fatigue == 1 ? 0.3f : fatigue == 2 ? 0.09f : fatigue == 3 ? 0.0027f : 8.1E-4f;
            if (Player.EyeInWater && ItemProperties.EnchantmentLevel(Inventory.Player.Slots[5], "minecraft:aqua_affinity") == 0) speed /= 5;
            if (!Player.OnGround) speed /= 5;
            var canHarvest = !state.Block.RequiresTool || stack != null && state.Block.HarvestTools != null && state.Block.HarvestTools.Contains(stack.Item.Id);
            return speed / hardness / (canHarvest ? 30 : 100);
        }

        /// <summary>Survival digging (Minosoft's SurvivalDigger): progress until 1, then Player Action "finish".</summary>
        private void TickSurvivalDigging()
        {
            var target = Target;
            if (!Input.AttackHeld || !target.HasValue || breakCooldown > 0)
            {
                if (digging.HasValue)
                {
                    Server.SendPlayerAction(1, digging.Value, diggingFace);
                    digging = null;
                }
                return;
            }
            var hit = target.Value;
            if (!digging.HasValue || digging.Value != hit.Position)
            {
                if (digging.HasValue) Server.SendPlayerAction(1, digging.Value, diggingFace);
                digging = hit.Position;
                diggingFace = hit.Face;
                diggingProgress = 0;
                digTicks = 0;
                Server.SendPlayerAction(0, hit.Position, hit.Face);
            }
            if (digTicks++ % 4 == 0) PlayBlockSound(hit.State, hit.Position, BlockSoundKind.Hit);
            // crack particles on the hit face (vanilla ParticleEngine.crack)
            Particle?.Invoke(new ParticleRequest { Name = "#crack", Position = new Vec3d(hit.Position.X, hit.Position.Y, hit.Position.Z), Block = hit.State, Count = (int)hit.Face });
            Server.SendSwingArm();
            Player.Swing();

            var progress = DestroyProgress(hit.State);
            if (progress <= 0) return; // unbreakable
            diggingProgress += progress;
            if (diggingProgress < 1.0f) return;
            Server.SendPlayerAction(2, hit.Position, hit.Face);
            PlayBlockSound(hit.State, hit.Position, BlockSoundKind.Break);
            BreakParticles(hit.Position, hit.State);
            World.SetBlock(hit.Position, null);
            digging = null;
            breakCooldown = 5;
            UpdateTarget();
        }

        private void PlaceLocal(BlockHit hit)
        {
            var held = HotbarItems[SelectedSlot];
            var block = held == null ? null : Resources.Blocks.Get(held);
            if (block == null) return;
            var position = Placement.IsReplaceable(hit.State) ? hit.Position : hit.Position.Offset(hit.Face);
            if (!Placement.IsReplaceable(World.GetBlock(position))) return;
            if (position.Y < World.MinY || position.Y > World.MaxY) return;

            var state = Placement.GetPlacementState(block, hit, Player.Yaw, Player.Pitch);
            var player = Player.AABB;
            foreach (var box in state.CollisionShape)
            {
                if (box.Offset(position).Intersects(player)) return;
            }
            World.SetBlock(position, state);
            PlayBlockSound(state, position, BlockSoundKind.Place);
            UpdateTarget();
        }

        // ------------------------------------------------------------------ packet handlers (game thread)

        public void OnJoin(int entityId, DimensionType dimension, string dimensionName, int gamemode, int viewDistance)
        {
            ServerUi.Clear();
            Entities.Clear();
            Player.Vehicle = -1;
            DimensionName = dimensionName;
            World.Reset(dimension.MinY, dimension.Height);
            ApplyDimension(dimension);
            OnGamemode(gamemode);
            Spawned = false;
            Log.Info($"Joined {Server?.Host} as entity {entityId} in {dimensionName} (y {dimension.MinY}..{dimension.MinY + dimension.Height - 1})");
        }

        public void OnRespawn(DimensionType dimension, string dimensionName, int gamemode)
        {
            DeathMessage = null;
            Air = 300;
            Player.Effects.Clear();
            Entities.Clear();
            Player.Vehicle = -1;
            DimensionName = dimensionName;
            World.Reset(dimension.MinY, dimension.Height);
            ApplyDimension(dimension);
            OnGamemode(gamemode);
            Spawned = false;
            Health = 20;
        }

        // ------------------------------------------------------------------ entities

        /// <summary>Entity data index of the texture variant of a type (1.20.4), -1 without one.</summary>
        /// <summary>The panda texture: brown and weak are recessive and only show when both genes are the same.</summary>
        private static int PandaVariant(Entity panda)
        {
            var gene = panda.MainGene;
            if ((gene == 4 || gene == 5) && panda.HiddenGene != gene) return 0;
            return gene >= 0 && gene < 7 ? gene : 0;
        }

        /// <summary>Entity Event 4 on evoker fangs: the bite starts.</summary>
        public void OnEntityAttack(int id)
        {
            if (Entities.Entities.TryGetValue(id, out var entity)) entity.AttackStart = entity.Age;
        }

        /// <summary>Link Entities: a lead from a mob to its holder (-1: no lead).</summary>
        public void OnLeash(int attached, int holder)
        {
            if (Entities.Entities.TryGetValue(attached, out var entity)) entity.LeashHolder = holder == 0 ? -1 : holder;
        }

        private static int VariantIndex(string path)
        {
            switch (path)
            {
                case "cat":
                case "parrot":
                    return 19;
                case "horse":
                    return 18;
                case "tropical_fish":
                case "axolotl":
                case "frog":
                case "rabbit":
                case "fox":
                    return 17;
                case "llama":
                case "trader_llama":
                    return 21;
                default:
                    return -1;
            }
        }

        /// <summary>Projectiles drawn as their item (vanilla ThrownItemRenderer), item in entity data 8.</summary>
        public static readonly HashSet<string> ThrownItems = new HashSet<string> { "snowball", "egg", "ender_pearl", "potion", "experience_bottle", "eye_of_ender", "fireball", "small_fireball", "firework_rocket" };

        /// <summary>Update Attributes: the ones the client uses (the jump strength of horses).</summary>
        public void OnAttributes(int id, Dictionary<string, double> attributes)
        {
            if (Server != null && id == Server.EntityId)
            {
                // the own movement speed (Speed / Slowness effects included, sprinting is added by the physics)
                if (attributes.TryGetValue("minecraft:generic.movement_speed", out var speed) || attributes.TryGetValue("generic.movement_speed", out speed))
                {
                    Player.MovementSpeed = (float)speed;
                    Player.MovementSpeedFromServer = true;
                }
                return;
            }
            var entity = Entities.Get(id);
            if (entity == null) return;
            foreach (var pair in attributes)
            {
                if (pair.Key == "minecraft:horse.jump_strength" || pair.Key == "horse.jump_strength") entity.JumpStrength = pair.Value;
            }
        }

        /// <summary>Set Cooldown: the item can not be used for a while (ender pearls, chorus fruit, shields).</summary>
        public void OnCooldown(int itemId, int ticks)
        {
            if (ticks <= 0) cooldowns.Remove(itemId);
            else cooldowns[itemId] = (Ticks, ticks);
        }

        private readonly Dictionary<int, (long start, int duration)> cooldowns = new Dictionary<int, (long start, int duration)>();

        /// <summary>How much of the cooldown of an item is left (1 just started, 0 none), like vanilla ItemCooldowns.</summary>
        public float CooldownLeft(Item item, float partial)
        {
            if (item == null || !cooldowns.TryGetValue(item.Id, out var cooldown)) return 0;
            var left = (cooldown.start + cooldown.duration - (Ticks + partial)) / cooldown.duration;
            if (left <= 0)
            {
                cooldowns.Remove(item.Id);
                return 0;
            }
            return (float)Math.Min(1, left);
        }

        /// <summary>The own player was hurt: ticks left of the hurt effect (camera tilt, red flash) and where from.</summary>
        public int OwnHurtTime;
        public float OwnHurtDirection;

        /// <summary>Hurt Animation: the camera of the own player tilts away from the hit (the yaw it came from).</summary>
        public void OnHurtAnimation(int entityId, float yaw)
        {
            if (Server != null && entityId == Server.EntityId)
            {
                OwnHurtTime = 10;
                OwnHurtDirection = yaw;
                return;
            }
            var entity = Entities.Get(entityId);
            if (entity != null) entity.HurtTime = 10;
        }

        /// <summary>
        /// Explosion (vanilla Explosion.finalizeExplosion on the client): the destroyed blocks become air, the own player
        /// is pushed, the large or small explosion particles and the sound.
        /// </summary>
        public void OnExplosion(Vec3d center, float power, List<BlockPos> destroyed, Vec3d push, int interaction, int smallId, ParticleRequest small,
            int largeId, ParticleRequest large, int soundId, string soundName)
        {
            if (interaction == 1 || interaction == 2)
            {
                var air = Resources.Blocks.Get("air")?.DefaultState;
                foreach (var position in destroyed) World.SetBlock(position, air);
            }
            Player.Velocity = Player.Velocity + push;
            var big = power >= 2 && interaction != 0;
            OnParticle(big ? largeId : smallId, big ? large : small);
            var pitch = (1 + (float)(SoundRandom.NextDouble() - SoundRandom.NextDouble()) * 0.2f) * 0.7f;
            OnSoundPacket(soundId, soundName, (int)SoundCategory.Block, center, -1, 4, pitch);
        }

        /// <summary>The own effect particles are left out in first person (they would fill the view).</summary>
        public bool OwnParticlesVisible = true;

        /// <summary>Golden hearts (the own player's absorption, entity data 15).</summary>
        public float Absorption;
        private int OwnPotionColor;
        private bool OwnPotionAmbient;

        /// <summary>
        /// Effect particles around living entities (vanilla LivingEntity.tickEffects): swirls in the mixed color of their
        /// effects, every other tick on average, rarely and faint for beacon effects.
        /// </summary>
        private void TickPotionParticles()
        {
            foreach (var entity in Entities.Entities.Values)
            {
                if (entity.PotionColor > 0 && !entity.Invisible) PotionParticle(entity.PotionColor, entity.PotionAmbient, entity.Position, entity.Width, entity.Height);
            }
            if (OwnPotionColor > 0 && OwnParticlesVisible) PotionParticle(OwnPotionColor, OwnPotionAmbient, Player.Position, 0.6f, 1.8f);
        }

        private void PotionParticle(int color, bool ambient, Vec3d position, float width, float height)
        {
            var spawn = ambient ? SoundRandom.Next(15) == 0 && SoundRandom.Next(5) == 0 : SoundRandom.Next(2) == 0;
            if (!spawn) return;
            Particle?.Invoke(new ParticleRequest
            {
                Name = ambient ? "ambient_entity_effect" : "entity_effect",
                Position = position + new Vec3d((SoundRandom.NextDouble() - 0.5) * width, SoundRandom.NextDouble() * height, (SoundRandom.NextDouble() - 0.5) * width),
                Offset = new Vec3d(((color >> 16) & 0xFF) / 255.0, ((color >> 8) & 0xFF) / 255.0, (color & 0xFF) / 255.0),
                Speed = 1,
                Count = 0,
                Scale = 1,
            });
        }

        public void OnEntityData(int id, Dictionary<int, object> values)
        {
            if (Server != null && id == Server.EntityId)
            {
                if (values.TryGetValue(15, out var absorption) && absorption is float absorptionValue) Absorption = absorptionValue;
                if (values.TryGetValue(10, out var ownColor) && ownColor is int ownColorValue) OwnPotionColor = ownColorValue;
                if (values.TryGetValue(11, out var ownAmbient) && ownAmbient is bool ownAmbientValue) OwnPotionAmbient = ownAmbientValue;
                if (values.TryGetValue(1, out var air) && air is int airValue) Air = airValue;
                // the server ends gliding (landing, elytra broke): flag 0x80
                if (values.TryGetValue(0, out var ownFlags) && ownFlags is byte flagsValue && (flagsValue & 0x80) == 0 && Player.FallFlying && Ticks - fallFlyingStart > 5) Player.FallFlying = false;
                return;
            }
            var entity = Entities.Get(id);
            if (entity == null) return;
            var path = entity.Type?.Name.Path;
            if (path != null && path.EndsWith("_display"))
            {
                // block, item and text displays: their own indices from 8 on
                if (entity.Display == null) entity.Display = new DisplayData();
                foreach (var pair in values)
                {
                    if (pair.Key >= 8) entity.Display.Apply(path, pair.Key, pair.Value, Ticks);
                    else if (pair.Key == 0 && pair.Value is byte displayFlags) entity.Flags = displayFlags;
                }
                entity.Display.EndUpdate(Ticks);
                return;
            }
            foreach (var pair in values)
            {
                switch (pair.Key)
                {
                    case 0 when pair.Value is byte flags:
                        entity.Flags = flags;
                        break;
                    case 2:
                        entity.CustomName = pair.Value as string;
                        break;
                    case 3 when pair.Value is bool visible:
                        entity.CustomNameVisible = visible;
                        break;
                    case 6 when pair.Value is int pose:
                        entity.PoseId = pose;
                        break;
                    case 8 when pair.Value is ItemStack stack && path != null && (path == "item" || path.EndsWith("item_frame") || ThrownItems.Contains(path)):
                        entity.Item = stack;
                        break;
                    case 9 when path != null && path.EndsWith("item_frame") && pair.Value is int frameRotation:
                        entity.FrameRotation = frameRotation;
                        break;
                    case int variantIndex when pair.Value is int variant && VariantIndex(path) == variantIndex:
                        entity.Variant = variant;
                        break;
                    case 17 when path == "mooshroom" && pair.Value is string mooshroom:
                        entity.Variant = mooshroom == "brown" ? 1 : 0;
                        break;
                    case int dataIndex when pair.Value is int[] villager && (path == "villager" && dataIndex == 18 || path == "zombie_villager" && dataIndex == 20):
                        entity.VillagerData = villager;
                        break;
                    case 10 when entity.IsLiving && pair.Value is int potionColor:
                        entity.PotionColor = potionColor;
                        break;
                    case 11 when entity.IsLiving && pair.Value is bool potionAmbient:
                        entity.PotionAmbient = potionAmbient;
                        break;
                    // creeper: 16 swelling direction (-1 idle, 1 fusing), 17 charged, 18 ignited (flint and steel)
                    case 16 when path == "creeper" && pair.Value is int swellDirection:
                        entity.SwellDirection = swellDirection;
                        break;
                    case 17 when path == "creeper" && pair.Value is bool charged:
                        entity.Charged = charged;
                        break;
                    case 18 when path == "creeper" && pair.Value is bool ignited:
                        entity.Ignited = ignited;
                        break;
                    case 8 when (path == "arrow" || path == "spectral_arrow" || path == "trident") && pair.Value is byte arrowFlags:
                        entity.ArrowFlags = arrowFlags;
                        break;
                    case 10 when path == "arrow" && pair.Value is int arrowColor:
                        entity.ArrowColor = arrowColor;
                        break;
                    case 16 when path == "ender_dragon" && pair.Value is int phase:
                        entity.DragonPhase = phase;
                        break;
                    case int witherTarget when path == "wither" && witherTarget >= 16 && witherTarget <= 18 && pair.Value is int targetId:
                        entity.WitherTargets[witherTarget - 16] = targetId;
                        break;
                    case 16 when (path == "guardian" || path == "elder_guardian") && pair.Value is bool moving:
                        entity.Moving = moving;
                        break;
                    case 8 when pair.Value is byte living && entity.IsLiving:
                        entity.UsingHand = (living & 0x01) == 0 ? 0 : (living & 0x02) != 0 ? 2 : 1;
                        if (entity.UsingHand == 0) entity.UseTicks = 0;
                        break;
                    case 8 when path == "painting" && pair.Value is int painting:
                        entity.Variant = painting;
                        break;
                    case 9 when path == "firework_rocket" && pair.Value is int attached:
                        entity.AttachedTo = attached;
                        break;
                    // Strider: 17 boost time, 18 cold (suffocating outside lava), 19 saddle
                    case 19 when path == "strider" && pair.Value is bool striderSaddle:
                        entity.Saddled = striderSaddle;
                        break;
                    case 18 when path == "strider" && pair.Value is bool cold:
                        entity.Variant = cold ? 1 : 0;
                        break;
                    case 16 when path == "phantom" && pair.Value is int phantomSize:
                        entity.PhantomSize = phantomSize;
                        break;
                    case 17 when path == "shulker" && pair.Value is byte peek:
                        entity.Peek = peek;
                        break;
                    case 18 when path == "shulker" && pair.Value is byte shulkerColor:
                        entity.Variant = shulkerColor;
                        break;
                    case 20 when path == "panda" && pair.Value is byte mainGene:
                        entity.MainGene = mainGene;
                        entity.Variant = PandaVariant(entity);
                        break;
                    case 21 when path == "panda" && pair.Value is byte hiddenGene:
                        entity.HiddenGene = hiddenGene;
                        entity.Variant = PandaVariant(entity);
                        break;
                    case 8 when path == "tnt" && pair.Value is int fuse:
                        entity.Fuse = fuse;
                        break;
                    case 9 when path == "tnt" && pair.Value is int tntBlock:
                        entity.FallingBlock = Resources.Blocks.GetState(tntBlock);
                        break;
                    case 8 when path == "wither_skull" && pair.Value is bool dangerous:
                        entity.Dangerous = dangerous;
                        break;
                    case 9 when path == "end_crystal" && pair.Value is bool bottom:
                        entity.ShowBottom = bottom;
                        break;
                    case 8 when path == "fishing_bobber" && pair.Value is int hooked:
                        entity.Hooked = hooked - 1;
                        break;
                    case 15 when path == "armor_stand" && pair.Value is byte standFlags:
                        entity.StandFlags = standFlags;
                        break;
                    case int poseIndex when path == "armor_stand" && poseIndex >= 16 && poseIndex <= 21 && pair.Value is Vec3d rotation:
                        entity.Pose[poseIndex - 16] = rotation;
                        break;
                    case 17 when pair.Value is byte horseFlags && path != null && (path == "horse" || path == "donkey" || path == "mule" || path.EndsWith("_horse") || path == "camel"):
                        entity.Saddled = (horseFlags & 0x04) != 0;
                        break;
                    case 9 when entity.IsLiving && pair.Value is float health:
                        entity.Health = health;
                        break;
                    case 16 when pair.Value is bool baby:
                        entity.Baby = baby;
                        break;
                    case 16 when (path == "slime" || path == "magma_cube") && pair.Value is int size:
                        entity.SlimeSize = Math.Max(1, size);
                        break;
                    case 17 when path == "sheep" && pair.Value is byte wool:
                        entity.SheepWool = wool;
                        break;
                    case 17 when path == "pig" && pair.Value is bool saddle:
                        entity.Saddled = saddle;
                        break;
                    case 11 when path != null && path.EndsWith("boat") && pair.Value is int variant:
                        entity.Variant = variant;
                        break;
                }
            }
        }

        /// <summary>Relative (delta) or absolute (teleport) position update.</summary>
        public void OnEntityMove(int id, Vec3d position, float? yaw, float? pitch, bool onGround, bool relative)
        {
            var entity = Entities.Get(id);
            if (entity == null) return;
            entity.OnGround = onGround;
            var target = relative ? entity.Position + position : position;
            if (id == Player.Vehicle && ClientControls(entity) && relative) return; // we steer it
            entity.MoveTo(target, yaw, pitch);
        }

        public void OnEntityVelocity(int id, Vec3d velocity)
        {
            if (Server != null && id == Server.EntityId)
            {
                Player.Velocity = velocity; // knockback, explosions
                return;
            }
            var entity = Entities.Get(id);
            if (entity != null) entity.Velocity = velocity;
        }

        public void OnPassengers(int vehicle, int[] passengers)
        {
            var entity = Entities.Get(vehicle);
            if (entity != null)
            {
                foreach (var old in entity.Passengers)
                {
                    var passenger = Entities.Get(old);
                    if (passenger != null && passenger.Vehicle == vehicle) passenger.Vehicle = -1;
                }
                entity.Passengers.Clear();
                entity.Passengers.AddRange(passengers);
                foreach (var id in passengers)
                {
                    var passenger = Entities.Get(id);
                    if (passenger != null) passenger.Vehicle = vehicle;
                }
            }
            var own = Server?.EntityId ?? -1;
            if (Array.IndexOf(passengers, own) >= 0) Player.Vehicle = vehicle;
            else if (Player.Vehicle == vehicle) Player.Vehicle = -1;
        }

        private int boatTurn;
        private float boatRotationSpeed;

        /// <summary>
        /// Riding: the player sits on the vehicle and sends Player Input (Minosoft's MovementPacketSender.sendVehicle).
        /// Boats are steered by the client like in vanilla (simplified Boat.controlBoat), other vehicles by the server.
        /// </summary>
        private void TickRiding()
        {
            var vehicle = Entities.Get(Player.Vehicle);
            Player.PreviousPosition = Player.Position;
            Player.PreviousEyeHeight = Player.EyeHeight;
            if (vehicle == null)
            {
                Player.Vehicle = -1;
                return;
            }
            var input = Player.Input;
            var unmount = input.Sneak;
            if (vehicle.Type?.Name.Path.EndsWith("boat") == true) SteerBoat(vehicle, input);
            else if (ClientControls(vehicle)) SteerHorse(vehicle, input);

            var offset = Entity.RidingOffset(vehicle);
            Player.Position = vehicle.Position + new Vec3d(0, offset, 0);
            Player.Velocity = Vec3d.Zero;
            Player.FallDistance = 0;
            Player.OnGround = false;
            Player.Pose = Pose.Standing;
            Player.EyeHeight = LocalPlayer.StandingEyeHeight;

            Server?.SendRotation(Player.Yaw, Player.Pitch, false);
            Server?.SendPlayerInput(input.Sideways * 0.98f, input.Forwards * 0.98f, input.Jump, unmount);
            if (unmount && !IsRemote) Player.Vehicle = -1;
        }

        private static bool IsHorse(Entity entity)
        {
            var path = entity.Type?.Name.Path;
            return path == "horse" || path == "donkey" || path == "mule" || path == "skeleton_horse" || path == "zombie_horse" || path == "camel";
        }

        /// <summary>Vehicles moved by their rider's client (vanilla isControlledByLocalInstance): boats and saddled horses.</summary>
        private bool ClientControls(Entity vehicle)
        {
            var path = vehicle.Type?.Name.Path;
            if (path == null) return false;
            if (path.EndsWith("boat")) return true;
            if (IsHorse(vehicle)) return vehicle.Saddled;
            // pigs and striders follow the rider's carrot / warped fungus on a stick (vanilla ItemSteerable)
            if (path == "pig" || path == "strider") return vehicle.Saddled && Holding(path == "pig" ? "carrot_on_a_stick" : "warped_fungus_on_a_stick");
            return false;
        }

        private bool Holding(string item) => Inventory.Player.Slots[36 + SelectedSlot]?.Item?.Name.Path == item || Inventory.Player.Slots[45]?.Item?.Name.Path == item;

        /// <summary>
        /// Saddled horses (simplified AbstractHorse.travel): they turn with the rider, walk with WASD (slower backwards and
        /// sideways), jump with space, step up one block and fall with gravity; the result is sent as Move Vehicle.
        /// </summary>
        private void SteerHorse(Entity horse, MovementInput input)
        {
            var yaw = Player.Yaw;
            var path = horse.Type?.Name.Path;
            var steered = path == "pig" || path == "strider";
            // pigs and striders always walk forward where the rider looks, horses use WASD
            var forward = steered ? 1 : input.Forwards;
            var strafe = steered ? 0 : input.Sideways * 0.5f;
            if (forward <= 0) forward *= 0.25f;
            var speed = path == "pig" ? 0.16 : path == "strider" ? 0.2 : 0.3;
            var radians = yaw * MathUtil.DegToRad;
            var motion = new Vec3d((-Math.Sin(radians) * forward + Math.Cos(radians) * strafe) * speed, 0, (Math.Cos(radians) * forward + Math.Sin(radians) * strafe) * speed);
            var vertical = (horse.Velocity.Y - 0.08) * 0.98;
            // horses charge their jump while space is held and jump on release (vanilla jumpRidingScale)
            if (!steered)
            {
                if (input.Jump && horse.OnGround)
                {
                    jumpRidingTicks++;
                    HorseJump = jumpRidingTicks < 10 ? jumpRidingTicks * 0.1f : 0.8f + 2.0f / (jumpRidingTicks - 9) * 0.1f;
                }
                else if (jumpRidingTicks > 0)
                {
                    var power = HorseJump;
                    jumpRidingTicks = 0;
                    HorseJump = 0;
                    if (horse.OnGround && power > 0)
                    {
                        // AbstractHorse.executeRidersJump: the jump strength attribute times the charge
                        vertical = horse.JumpStrength * Math.Min(1, power);
                        Server?.SendPlayerCommand(5, (int)(Math.Min(1, power) * 100));
                    }
                }
            }
            var v = new Vec3d(motion.X, vertical, motion.Z);

            var shapes = new List<AABB>();
            var aabb = horse.AABB;
            PlayerPhysics.CollectCollisions(World, aabb.Extend(v).Grow(1.1), shapes);
            var moved = PlayerPhysics.Collide(v, aabb, shapes);
            if (horse.OnGround && (moved.X != v.X || moved.Z != v.Z))
            {
                // step height 1 (vanilla horses)
                var up = PlayerPhysics.Collide(new Vec3d(0, 1, 0), aabb, shapes);
                var raised = aabb.Offset(up);
                var horizontal = PlayerPhysics.Collide(new Vec3d(v.X, 0, v.Z), raised, shapes);
                var down = PlayerPhysics.Collide(new Vec3d(0, -up.Y, 0), raised.Offset(horizontal), shapes);
                var stepped = new Vec3d(horizontal.X, up.Y + down.Y, horizontal.Z);
                if (stepped.X * stepped.X + stepped.Z * stepped.Z > moved.X * moved.X + moved.Z * moved.Z) moved = stepped;
            }
            horse.OnGround = v.Y < 0 && moved.Y > v.Y;
            horse.PreviousPosition = horse.Position;
            horse.Teleport(horse.Position + moved, yaw, horse.Pitch);
            horse.PreviousPosition = horse.Position - moved;
            horse.Velocity = new Vec3d(moved.X, horse.OnGround ? 0 : moved.Y, moved.Z);
            horse.BodyYaw = yaw;
            horse.HeadYaw = yaw;
            Server?.SendMoveVehicle(horse.Position.X, horse.Position.Y, horse.Position.Z, yaw, horse.Pitch);
        }

        private long fallFlyingStart;

        /// <summary>Firework rockets attached to the gliding player push it along the look (FireworkRocketEntity.tick).</summary>
        private void TickGliding()
        {
            if (Player.SpinAttack > 0) Player.SpinAttack--;
            if (!Player.FallFlying || Server == null) return;
            foreach (var entity in Entities.Entities.Values)
            {
                if (entity.AttachedTo != Server.EntityId || entity.Type?.Name.Path != "firework_rocket") continue;
                var look = MathUtil.Front(Player.Yaw, Player.Pitch);
                var v = Player.Velocity;
                Player.Velocity = new Vec3d(v.X + look.X * 0.1 + (look.X * 1.5 - v.X) * 0.5, v.Y + look.Y * 0.1 + (look.Y * 1.5 - v.Y) * 0.5, v.Z + look.Z * 0.1 + (look.Z * 1.5 - v.Z) * 0.5);
                break;
            }
        }

        /// <summary>
        /// Trident with riptide released after 10 ticks in water or rain: the player is launched along the look
        /// (vanilla TridentItem.releaseUsing, done by the client for itself).
        /// </summary>
        private void Riptide(ItemStack trident, int useTicks)
        {
            if (trident?.Item?.Name.Path != "trident" || useTicks < 10) return;
            var level = ItemProperties.EnchantmentLevel(trident, "minecraft:riptide");
            if (level <= 0) return;
            var position = BlockPos.Floor(Player.Position);
            var height = World.GetChunk(position.ChunkPos)?.GetRainHeight(position.X & 15, position.Z & 15) ?? World.MinY;
            var rain = World.RainLevel > 0.2f && height <= position.Y + 1;
            if (!Player.InWater && !rain) return;
            var yaw = Player.Yaw * MathUtil.DegToRad;
            var pitch = Player.Pitch * MathUtil.DegToRad;
            var x = -Math.Sin(yaw) * Math.Cos(pitch);
            var y = -Math.Sin(pitch);
            var z = Math.Cos(yaw) * Math.Cos(pitch);
            var length = Math.Sqrt(x * x + y * y + z * z);
            var strength = 3.0 * ((1.0 + level) / 4.0);
            Player.Velocity += new Vec3d(x * strength / length, y * strength / length, z * strength / length);
            if (Player.OnGround) Physics.Move(new Vec3d(0, 1.1999999, 0));
            Player.SpinAttack = 20;
            PlaySound(level >= 3 ? "item.trident.riptide_3" : level == 2 ? "item.trident.riptide_2" : "item.trident.riptide_1", Player.Position, 1, 1, SoundCategory.Player);
        }

        /// <summary>
        /// Vanilla LivingEntity.triggerItemUseEffects while eating or drinking: from the 7th tick (fast food at once)
        /// every 4 ticks the eating sound and 5 crumbs of the food, or the drinking sound.
        /// </summary>
        private void ItemUseEffects(ItemStack stack, int ticks, Vec3d eye, float yaw, float pitch, Vec3d position)
        {
            var path = stack.Item.Name.Path;
            var drink = path.EndsWith("potion") || path == "milk_bucket" || path == "honey_bottle";
            var food = Resources.ItemProperties.IsFood(stack.Item);
            if (!drink && !food) return;
            var fast = path == "dried_kelp";
            if (!(fast || ticks >= 7) || ticks % 4 != 0) return;
            if (drink)
            {
                PlaySound(path == "honey_bottle" ? "item.honey_bottle.drink" : "entity.generic.drink", position, 0.5f, (float)(SoundRandom.NextDouble() * 0.1 + 0.9), SoundCategory.Player);
                return;
            }
            SpawnItemParticles(stack, eye, yaw, pitch, 5);
            PlaySound("entity.generic.eat", position, 0.5f + 0.5f * SoundRandom.Next(2), (float)((SoundRandom.NextDouble() - SoundRandom.NextDouble()) * 0.2 + 1), SoundCategory.Player);
        }

        /// <summary>Vanilla LivingEntity.spawnItemParticles: bits of the item in front of the face, flying forward and up.</summary>
        public void SpawnItemParticles(ItemStack stack, Vec3d eye, float yaw, float pitch, int count)
        {
            if (stack?.Item == null) return;
            var xRot = -pitch * MathUtil.DegToRad;
            var yRot = -yaw * MathUtil.DegToRad;
            for (var i = 0; i < count; i++)
            {
                var velocity = Rotate(new Vec3d((SoundRandom.NextDouble() - 0.5) * 0.1, SoundRandom.NextDouble() * 0.1 + 0.1, 0), xRot, yRot);
                var offset = Rotate(new Vec3d((SoundRandom.NextDouble() - 0.5) * 0.3, -SoundRandom.NextDouble() * 0.6 - 0.3, 0.6), xRot, yRot);
                Particle?.Invoke(new ParticleRequest
                {
                    Name = "item",
                    Item = stack,
                    Position = eye + offset,
                    Offset = new Vec3d(velocity.X, velocity.Y + 0.05, velocity.Z),
                    Speed = 1,
                    Count = 0,
                });
            }
        }

        /// <summary>Vec3.xRot then Vec3.yRot.</summary>
        private static Vec3d Rotate(Vec3d v, double xRot, double yRot)
        {
            var y = v.Y * Math.Cos(xRot) + v.Z * Math.Sin(xRot);
            var z = v.Z * Math.Cos(xRot) - v.Y * Math.Sin(xRot);
            var x = v.X * Math.Cos(yRot) + z * Math.Sin(yRot);
            z = z * Math.Cos(yRot) - v.X * Math.Sin(yRot);
            return new Vec3d(x, y, z);
        }

        /// <summary>Entity Event 47..52: an item of an entity broke (vanilla LivingEntity.breakItem): sound and pieces.</summary>
        /// <summary>Entity Event 29 / 30: a shield stopped a hit, or an axe disabled it (vanilla LivingEntity.handleEntityEvent).</summary>
        public void OnShield(int entityId, bool disabled)
        {
            Vec3d position;
            if (Server != null && entityId == Server.EntityId) position = Player.Position;
            else if (Entities.Get(entityId) is Entity entity) position = entity.Position;
            else return;
            var pitch = 0.8f + (float)SoundRandom.NextDouble() * 0.4f;
            PlaySound(disabled ? "item.shield.break" : "item.shield.block", position, disabled ? 0.8f : 1, pitch, SoundCategory.Player);
        }

        public void OnItemBreak(int entityId, int slot)
        {
            ItemStack stack;
            Vec3d eye, position;
            float yaw, pitch;
            if (Server != null && entityId == Server.EntityId)
            {
                // main hand, off hand, then head, chest, legs, feet of the player inventory
                var slots = new[] { 36 + SelectedSlot, 45, 5, 6, 7, 8 };
                stack = Inventory.Player.Slots[slots[slot]];
                eye = Player.EyePosition;
                position = Player.Position;
                yaw = Player.Yaw;
                pitch = Player.Pitch;
            }
            else
            {
                if (!Entities.Entities.TryGetValue(entityId, out var entity)) return;
                // equipment slots: main hand, off hand, feet, legs, chest, head
                var equipment = new[] { 0, 1, 5, 4, 3, 2 };
                stack = entity.Equipment[equipment[slot]];
                position = entity.Position;
                eye = new Vec3d(position.X, position.Y + entity.Height * 0.85, position.Z);
                yaw = entity.HeadYaw;
                pitch = entity.Pitch;
            }
            if (stack == null) return;
            PlaySound("entity.item.break", position, 0.8f, (float)(0.8 + SoundRandom.NextDouble() * 0.4), SoundCategory.Player);
            SpawnItemParticles(stack, eye, yaw, pitch, 5);
        }

        /// <summary>
        /// Flying arrows (vanilla AbstractArrow.tick on the client): critical ones leave a trail of crit particles, tipped
        /// ones swirl in their potion color.
        /// </summary>
        private void TickArrows()
        {
            foreach (var entity in Entities.Entities.Values)
            {
                var path = entity.Type?.Name.Path;
                if (path != "arrow" && path != "spectral_arrow" && path != "trident") continue;
                var motion = entity.Position - entity.PreviousPosition;
                var moving = motion.X * motion.X + motion.Y * motion.Y + motion.Z * motion.Z > 0.0001;
                if ((entity.ArrowFlags & 0x01) != 0 && moving)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        Particle?.Invoke(new ParticleRequest
                        {
                            Name = "crit",
                            Position = entity.PreviousPosition + motion * (i / 4.0),
                            Offset = new Vec3d(-motion.X, -motion.Y + 0.2, -motion.Z),
                            Speed = 1,
                            Count = 0,
                            Scale = 1,
                        });
                    }
                }
                if (entity.ArrowColor != -1 && (moving ? Ticks % 2 == 0 : Ticks % 5 == 0))
                {
                    var color = entity.ArrowColor;
                    Particle?.Invoke(new ParticleRequest
                    {
                        Name = "entity_effect",
                        Position = entity.Position + new Vec3d((SoundRandom.NextDouble() - 0.5) * entity.Width, SoundRandom.NextDouble() * entity.Height, (SoundRandom.NextDouble() - 0.5) * entity.Width),
                        Offset = new Vec3d(((color >> 16) & 0xFF) / 255.0, ((color >> 8) & 0xFF) / 255.0, (color & 0xFF) / 255.0),
                        Speed = 1,
                        Count = 0,
                        Scale = 1,
                    });
                }
            }
        }

        /// <summary>Other entities eating or drinking (their hand in use from the entity data).</summary>
        private void TickEntityItemUse()
        {
            foreach (var entity in Entities.Entities.Values)
            {
                if (entity.UsingHand == 0) continue;
                entity.UseTicks++;
                var stack = entity.Equipment[entity.UsingHand == 2 ? 1 : 0];
                if (stack?.Item == null) continue;
                var eye = new Vec3d(entity.Position.X, entity.Position.Y + entity.Height * 0.85, entity.Position.Z);
                ItemUseEffects(stack, entity.UseTicks, eye, entity.HeadYaw, entity.Pitch, entity.Position);
            }
        }

        /// <summary>Charge of the horse jump (vanilla jumpRidingScale, 0..1), shown instead of the experience bar.</summary>
        public float HorseJump { get; private set; }

        /// <summary>Riding something with a charged jump (the HUD shows the jump bar instead of the experience).</summary>
        public bool RidingJumpable
        {
            get
            {
                var vehicle = Player.Riding ? Entities.Get(Player.Vehicle) : null;
                return vehicle != null && IsHorse(vehicle) && vehicle.Saddled;
            }
        }
        private int jumpRidingTicks;
        private bool riding;

        private void SteerBoat(Entity boat, MovementInput input)
        {
            // rotation: A/D turn, forward/backward accelerate, water friction 0.9 (vanilla Boat.controlBoat/floatBoat)
            if (input.Sideways > 0) boatRotationSpeed -= 1;
            if (input.Sideways < 0) boatRotationSpeed += 1;
            var yaw = boat.Yaw + boatRotationSpeed;
            boatRotationSpeed *= 0.9f;
            var acceleration = 0.0;
            if (input.Forwards > 0) acceleration += 0.04;
            if (input.Forwards < 0) acceleration -= 0.005;
            if (input.Sideways != 0 && input.Forwards == 0) acceleration += 0.005;
            var radians = yaw * MathUtil.DegToRad;
            var v = boat.Velocity + new Vec3d(-Math.Sin(radians) * acceleration, 0, Math.Cos(radians) * acceleration);
            var onWater = Fluids.Get(World.GetBlock(BlockPos.Floor(boat.Position.X, boat.Position.Y - 0.1, boat.Position.Z))) == FluidType.Water
                || Fluids.Get(World.GetBlock(BlockPos.Floor(boat.Position))) == FluidType.Water;
            var friction = onWater ? 0.9 : 0.45;
            v = new Vec3d(v.X * friction, onWater ? 0 : v.Y - 0.04, v.Z * friction);

            var shapes = new List<AABB>();
            var aabb = boat.AABB;
            PlayerPhysics.CollectCollisions(World, aabb.Extend(v), shapes);
            var moved = PlayerPhysics.Collide(v, aabb, shapes);
            boat.PreviousPosition = boat.Position;
            boat.Teleport(boat.Position + moved, yaw, boat.Pitch);
            boat.PreviousPosition = boat.Position - moved;
            boat.Velocity = new Vec3d(moved.X == v.X ? v.X : 0, moved.Y, moved.Z == v.Z ? v.Z : 0);
            Player.Yaw += boatRotationSpeed;

            Server?.SendPaddleBoat(input.Sideways < 0 || input.Forwards > 0, input.Sideways > 0 || input.Forwards > 0);
            Server?.SendMoveVehicle(boat.Position.X, boat.Position.Y, boat.Position.Z, yaw, boat.Pitch);
        }

        public void OnReconfigure()
        {
            Entities.Clear();
            World.Clear();
            Spawned = false;
        }

        public void OnChunkData(ChunkPacketData data)
        {
            if (data.Sections.Length != World.SectionCount) return; // from a previous dimension
            World.AddChunk(ChunkDecoder.ToChunk(data, World));
            foreach (var entity in data.BlockEntities) World.BlockEntities.Set(entity.Position, entity.Type, entity.Nbt);
        }

        /// <summary>Synchronize Player Position: flags 0x01..0x10 mark x, y, z, yaw, pitch as relative.</summary>
        public void OnServerTeleport(double x, double y, double z, float yaw, float pitch, int flags, int teleportId)
        {
            var position = Player.Position;
            var velocity = Player.Velocity;
            if ((flags & 0x01) != 0) x += position.X;
            else velocity.X = 0;
            if ((flags & 0x02) != 0) y += position.Y;
            else velocity.Y = 0;
            if ((flags & 0x04) != 0) z += position.Z;
            else velocity.Z = 0;
            if ((flags & 0x08) != 0) yaw += Player.Yaw;
            if ((flags & 0x10) != 0) pitch += Player.Pitch;

            Player.Teleport(new Vec3d(x, y, z));
            Player.Velocity = velocity;
            Player.Yaw = MathUtil.WrapDegrees(yaw);
            Player.Pitch = Math.Max(-90.0f, Math.Min(90.0f, pitch));

            TeleportCount++;
            Server.SendConfirmTeleport(teleportId);
            Server.SendPositionRotation(x, y, z, Player.Yaw, Player.Pitch, false);
            movement.Reset();
            Spawned = true;
        }

        public void OnGamemode(int gamemode)
        {
            Player.Gamemode = gamemode >= 0 && gamemode <= 3 ? (Gamemode)gamemode : Gamemode.Survival;
            Player.AllowFlying = Player.Gamemode == Gamemode.Creative || Player.Gamemode == Gamemode.Spectator;
            if (!Player.AllowFlying) Player.Flying = false;
        }

        public void OnAbilities(bool flying, bool allowFlying, float flyingSpeed)
        {
            Player.Flying = flying;
            Player.AllowFlying = allowFlying;
            Player.FlyingSpeed = flyingSpeed;
        }

        public void OnHealth(float health, int food, float saturation)
        {
            Health = health;
            Food = food;
            Saturation = saturation;
            if (health <= 0 && DeathMessage == null) DeathMessage = "";
        }

        public void OnDeath(string message) => DeathMessage = message ?? "";

        /// <summary>Respawn button of the death screen (Client Command 0).</summary>
        public void Respawn()
        {
            DeathMessage = null;
            Server?.SendRespawn();
            if (!IsRemote) Health = 20;
        }

        public void OnExperience(float bar, int level, int total)
        {
            ExperienceBar = bar;
            ExperienceLevel = level;
            ExperienceTotal = total;
        }

        public void OnEffect(int effect, int amplifier, int duration)
        {
            Player.Effects[effect] = new StatusEffect { Id = effect, Amplifier = amplifier, Duration = duration };
        }

        /// <summary>Armor points of the worn armor (slots 5..8).</summary>
        public int ArmorPoints
        {
            get
            {
                var points = 0;
                for (var slot = 5; slot <= 8; slot++) points += ItemProperties.ArmorPoints(Inventory.Player.Slots[slot]?.Item);
                return points;
            }
        }

        public void OnChat(string text, bool overlay) => OnChat(TextComponent.Of(text), overlay);

        /// <summary>System and player chat (formatted, with click/hover events), or the action bar when overlay.</summary>
        public void OnChat(TextComponent component, bool overlay)
        {
            var text = component.ToLegacy(out var links);
            if (overlay)
            {
                ActionBar = text;
                ActionBarReceived = Environment.TickCount;
                return;
            }
            Log.Info("[Chat] " + LegacyFormatting.Strip(text));
            Chat.Add(new ChatLine { Text = text, Links = links, Received = Environment.TickCount });
            if (Chat.Count > 100) Chat.RemoveAt(0);
        }

        /// <summary>Recipes of the server and the unlocked ones (recipe book, stonecutter).</summary>
        public readonly RecipeBook Recipes = new RecipeBook();

        /// <summary>Titles, boss bars, scoreboard, teams, tab list header/footer and advancement toasts.</summary>
        public readonly ServerHud ServerUi = new ServerHud();

        /// <summary>Refreshes the HUD hotbar from the player inventory (slots 36..44).</summary>
        public void UpdateHotbar()
        {
            for (var i = 0; i < 9; i++)
            {
                var stack = Inventory.Player.Slots[36 + i];
                HotbarItems[i] = stack?.Item.Name.Path;
                HotbarCounts[i] = stack?.Count ?? 0;
            }
        }

        public void OnDisconnected(string reason)
        {
            DisconnectReason = reason;
        }
    }
}
