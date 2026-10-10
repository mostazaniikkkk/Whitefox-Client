// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhiteFox.Core.Data;
using WhiteFox.Core.Entities;
using WhiteFox.Core.Game;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Network
{
    /// <summary>A resource pack offered by the server (Add Resource Pack packet).</summary>
    public sealed class ResourcePackOffer
    {
        public Guid Id;
        public string Url;
        /// <summary>SHA-1 (hex) of the zip, or empty when the server did not set one.</summary>
        public string Hash;
        public bool Forced;
        public string Prompt;
    }

    /// <summary>A map marker (vanilla MapDecoration): icon type, position -128..127 (half map pixels), rotation in 16ths.</summary>
    public sealed class MapDecoration
    {
        public int Type;
        public int X;
        public int Z;
        public int Rotation;
        public string Name;
    }

    public sealed class DimensionType
    {
        public string Name;
        public int MinY = -64;
        public int Height = 384;
        public bool HasSkylight = true;
        /// <summary>minecraft:overworld, minecraft:the_nether or minecraft:the_end (sky, fog, clouds).</summary>
        public string Effects = "minecraft:overworld";
        public float AmbientLight;
    }

    /// <summary>
    /// Connection to a server (Minosoft's PlaySession + the packet handlers): login, configuration and play.
    /// Packets are decoded on the network thread; everything that touches the game state is queued and applied by
    /// <see cref="Process"/> on the game thread, in order (Minosoft runs non thread safe packets in order too).
    /// </summary>
    public sealed class ServerSession : IDisposable
    {
        private readonly BlockRegistry blocks;
        private readonly ItemRegistry items;
        private readonly EntityTypeRegistry entityTypes;
        private readonly NetworkConnection connection = new NetworkConnection();
        private readonly ConcurrentQueue<Action<GameSession>> pending = new ConcurrentQueue<Action<GameSession>>();
        private readonly Dictionary<string, DimensionType> dimensionTypes = new Dictionary<string, DimensionType>();
        private DimensionType dimension = new DimensionType { Name = "minecraft:overworld" };
        private int biomeBits = 6;

        /// <summary>Biomes of the registry data (configuration phase).</summary>
        public Worlds.BiomeRegistry Biomes = new Worlds.BiomeRegistry();
        private int sequence;

        public readonly string Host;
        public readonly int Port;
        public readonly string Username;
        public Guid Uuid { get; private set; }
        /// <summary>The account used to join; a Microsoft account also works on online mode servers.</summary>
        public readonly Accounts.Account Account;
        public int ViewDistance = 8;

        public ProtocolState State { get; private set; } = ProtocolState.Handshake;
        public string DisconnectReason { get; private set; }
        public bool IsDisconnected => DisconnectReason != null;
        public int EntityId { get; private set; }
        public long BytesReceived => connection.BytesReceived;
        public long BytesSent => connection.BytesSent;
        public int PendingPackets => pending.Count;

        /// <summary>Status text for the loading screen (any thread).</summary>
        public volatile string Status = "Connecting";

        public ServerSession(string host, int port, string username, BlockRegistry blocks, ItemRegistry items, EntityTypeRegistry entityTypes)
            : this(host, port, Accounts.Account.Offline(username), blocks, items, entityTypes)
        {
        }

        public ServerSession(string host, int port, Accounts.Account account, BlockRegistry blocks, ItemRegistry items, EntityTypeRegistry entityTypes)
        {
            this.entityTypes = entityTypes;
            Host = host;
            Port = port;
            Account = account;
            Username = account.Name;
            this.blocks = blocks;
            this.items = items;
            Uuid = account.Uuid;
            connection.PacketReceived += OnPacket;
            connection.Disconnected += reason =>
            {
                // a server that hangs up during login without a message usually speaks another version
                if (State != ProtocolState.Play && reason != null && reason.Contains("Server closed the connection"))
                {
                    reason += "\nThe server closed the connection while logging in. WhiteFox speaks Minecraft " + Protocol.VersionName +
                              "; servers on other versions need ViaVersion, and online mode servers a Microsoft account.";
                }
                DisconnectReason = reason;
                pending.Enqueue(game => game.OnDisconnected(reason));
            };
        }

        /// <summary>Opens the connection and starts the login (the encryption follows for online mode servers).</summary>
        public async Task ConnectAsync()
        {
            Status = $"Connecting to {Host}:{Port}";
            await connection.ConnectAsync(Host, Port).ConfigureAwait(false);
            connection.StartReading();
            connection.Send(Protocol.Handshake.Intention, new OutBuffer().WriteVarInt(Protocol.Version).WriteString(Host).WriteShort(Port).WriteVarInt(2));
            State = ProtocolState.Login;
            Status = "Logging in";
            connection.Send(Protocol.LoginC2S.Start, new OutBuffer().WriteString(Username).WriteUuid(Uuid));
        }

        /// <summary>
        /// Applies what the network thread received, in order, on the game thread. Stops after
        /// <paramref name="budgetMilliseconds"/> so a burst of chunks does not freeze a frame; the rest waits.
        /// </summary>
        public void Process(GameSession game, double budgetMilliseconds = double.MaxValue)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < budgetMilliseconds && pending.TryDequeue(out var action))
            {
                try
                {
                    action(game);
                }
                catch (Exception exception)
                {
                    Log.Error("Error while handling packet: " + exception);
                }
            }
        }

        public void Disconnect(string reason = "Disconnected") => connection.Close(reason);
        public void Dispose() => Disconnect();

        // ------------------------------------------------------------------ receiving

        private void OnPacket(int id, InBuffer buffer)
        {
            try
            {
                switch (State)
                {
                    case ProtocolState.Login:
                        HandleLogin(id, buffer);
                        break;
                    case ProtocolState.Configuration:
                        HandleConfiguration(id, buffer);
                        break;
                    case ProtocolState.Play:
                        HandlePlay(id, buffer);
                        break;
                }
            }
            catch (Exception exception)
            {
                Log.Error($"Can not handle packet 0x{id:X2} in {State}: {exception}");
                connection.Close($"Invalid packet 0x{id:X2} ({State}): {exception.Message}");
            }
        }

        private void HandleLogin(int id, InBuffer buffer)
        {
            switch (id)
            {
                case Protocol.LoginS2C.Disconnect:
                    connection.Close(TextComponent.FromJson(buffer.ReadString()).ToPlain());
                    break;
                case Protocol.LoginS2C.EncryptionRequest:
                    HandleEncryptionRequest(buffer);
                    break;
                case Protocol.LoginS2C.Compression:
                    connection.SetCompression(buffer.ReadVarInt());
                    break;
                case Protocol.LoginS2C.Success:
                    Uuid = buffer.ReadUuid();
                    buffer.ReadString(); // name
                    var properties = buffer.ReadVarInt();
                    for (var i = 0; i < properties; i++)
                    {
                        buffer.ReadString();
                        buffer.ReadString();
                        if (buffer.ReadBool()) buffer.ReadString();
                    }
                    connection.Send(Protocol.LoginC2S.Acknowledged, new OutBuffer());
                    State = ProtocolState.Configuration;
                    Status = "Configuring";
                    SendClientInformation(Protocol.ConfigC2S.ClientInformation);
                    connection.Send(Protocol.ConfigC2S.PluginMessage, new OutBuffer().WriteString("minecraft:brand").WriteString("WhiteFox"));
                    break;
                case Protocol.LoginS2C.PluginRequest:
                    var messageId = buffer.ReadVarInt();
                    connection.Send(Protocol.LoginC2S.PluginResponse, new OutBuffer().WriteVarInt(messageId).WriteBool(false));
                    break;
            }
        }

        /// <summary>
        /// Online mode (vanilla ClientHandshakePacketListenerImpl.handleHello): a random AES key, the session server join
        /// with the server hash, the key and the verify token encrypted with the server's RSA key, then AES/CFB8 both ways.
        /// Runs on the network thread, which is fine: the server sends nothing until it gets the response.
        /// </summary>
        private void HandleEncryptionRequest(InBuffer buffer)
        {
            var serverId = buffer.ReadString();
            var publicKey = buffer.ReadByteArray();
            var verifyToken = buffer.ReadByteArray();
            if (Account == null || !Account.IsOnline)
            {
                connection.Close("This server is in online mode: a Microsoft account with Minecraft is needed (title screen → Accounts).");
                return;
            }
            var secret = new byte[16];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(secret);
            Status = "Authenticating";
            try
            {
                Accounts.MicrosoftAuth.JoinServerAsync(Account, Crypto.ServerHash(serverId, secret, publicKey)).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                connection.Close(exception is Accounts.AuthException ? exception.Message : "Could not authenticate with Mojang: " + exception.Message);
                return;
            }
            using (var rsa = Crypto.PublicKey(publicKey))
            {
                var padding = System.Security.Cryptography.RSAEncryptionPadding.Pkcs1;
                connection.Send(Protocol.LoginC2S.EncryptionResponse, new OutBuffer().WriteByteArray(rsa.Encrypt(secret, padding)).WriteByteArray(rsa.Encrypt(verifyToken, padding)));
            }
            connection.EnableEncryption(secret);
            Status = "Logging in";
        }

        private void HandleConfiguration(int id, InBuffer buffer)
        {
            switch (id)
            {
                case Protocol.ConfigS2C.Disconnect:
                    connection.Close(buffer.ReadNbtText().ToPlain());
                    break;
                case Protocol.ConfigS2C.FinishConfiguration:
                    connection.Send(Protocol.ConfigC2S.FinishConfiguration, new OutBuffer());
                    State = ProtocolState.Play;
                    Status = "Joining world";
                    break;
                case Protocol.ConfigS2C.KeepAlive:
                    connection.Send(Protocol.ConfigC2S.KeepAlive, new OutBuffer().WriteLong(buffer.ReadLong()));
                    break;
                case Protocol.ConfigS2C.Ping:
                    connection.Send(Protocol.ConfigC2S.Pong, new OutBuffer().WriteInt(buffer.ReadInt()));
                    break;
                case Protocol.ConfigS2C.RegistryData:
                    ReadRegistries(buffer.ReadNbt());
                    break;
                case Protocol.ConfigS2C.AddResourcePack:
                    ReadAddResourcePack(buffer);
                    break;
                case Protocol.ConfigS2C.RemoveResourcePack:
                    ReadRemoveResourcePack(buffer);
                    break;
            }
        }

        /// <summary>Registry codec sent by the server: dimension types (world height) and the biome count (direct palette bits).</summary>
        private void ReadRegistries(object nbt)
        {
            if (Nbt.Get(Nbt.Get(nbt, "minecraft:dimension_type"), "value") is List<object> dimensions)
            {
                foreach (var entry in dimensions)
                {
                    var element = Nbt.Get(entry, "element");
                    var type = new DimensionType
                    {
                        Name = Nbt.GetString(entry, "name"),
                        MinY = Nbt.GetInt(element, "min_y", -64),
                        Height = Nbt.GetInt(element, "height", 384),
                        HasSkylight = Nbt.GetInt(element, "has_skylight", 1) != 0,
                        Effects = Nbt.GetString(element, "effects") ?? "minecraft:overworld",
                        AmbientLight = Nbt.Get(element, "ambient_light") is float ambient ? ambient : 0,
                    };
                    if (type.Name != null) dimensionTypes[type.Name] = type;
                }
            }
            if (Nbt.Get(Nbt.Get(nbt, "minecraft:worldgen/biome"), "value") is List<object> biomes)
            {
                biomeBits = (int)Math.Ceiling(Math.Log(Math.Max(2, biomes.Count), 2));
                Biomes = Worlds.BiomeRegistry.FromNbt(biomes);
            }
        }

        private DimensionType GetDimension(string name) => dimensionTypes.TryGetValue(name, out var type) ? type : new DimensionType { Name = name };

        private void HandlePlay(int id, InBuffer buffer)
        {
            switch (id)
            {
                case Protocol.PlayS2C.KeepAlive:
                    connection.Send(Protocol.PlayC2S.KeepAlive, new OutBuffer().WriteLong(buffer.ReadLong()));
                    break;
                case Protocol.PlayS2C.Ping:
                    connection.Send(Protocol.PlayC2S.Pong, new OutBuffer().WriteInt(buffer.ReadInt()));
                    break;
                case Protocol.PlayS2C.Disconnect:
                    connection.Close(buffer.ReadNbtText().ToPlain());
                    break;
                case Protocol.PlayS2C.Login:
                    ReadLogin(buffer);
                    break;
                case Protocol.PlayS2C.Respawn:
                    ReadRespawn(buffer);
                    break;
                case Protocol.PlayS2C.ChunkData:
                {
                    var data = ChunkDecoder.Read(buffer, blocks, dimension.Height >> 4, biomeBits);
                    pending.Enqueue(game => game.OnChunkData(data));
                    break;
                }
                case Protocol.PlayS2C.UnloadChunk:
                {
                    var z = buffer.ReadInt();
                    var x = buffer.ReadInt();
                    pending.Enqueue(game => game.World.RemoveChunk(new ChunkPos(x, z)));
                    break;
                }
                case Protocol.PlayS2C.ChunkBatchFinished:
                    connection.Send(Protocol.PlayC2S.ChunkBatchReceived, new OutBuffer().WriteFloat(50.0f));
                    break;
                case Protocol.PlayS2C.BlockUpdate:
                {
                    var position = buffer.ReadPosition();
                    var state = blocks.GetState(buffer.ReadVarInt());
                    pending.Enqueue(game => game.World.SetBlock(position, state));
                    break;
                }
                case Protocol.PlayS2C.SoundEffect:
                case Protocol.PlayS2C.EntitySoundEffect:
                {
                    // registry id + 1, or 0 followed by an inline sound event (name, optional fixed range)
                    var soundId = buffer.ReadVarInt() - 1;
                    string soundName = null;
                    if (soundId < 0)
                    {
                        soundName = buffer.ReadString();
                        if (soundName.StartsWith("minecraft:")) soundName = soundName.Substring(10);
                        if (buffer.ReadBool()) buffer.ReadFloat();
                    }
                    var category = buffer.ReadVarInt();
                    Vec3d? position = null;
                    var entityId = -1;
                    if (id == Protocol.PlayS2C.SoundEffect) position = new Vec3d(buffer.ReadInt() / 8.0, buffer.ReadInt() / 8.0, buffer.ReadInt() / 8.0);
                    else entityId = buffer.ReadVarInt();
                    var volume = buffer.ReadFloat();
                    var pitch = buffer.ReadFloat();
                    pending.Enqueue(game => game.OnSoundPacket(soundId, soundName, category, position, entityId, volume, pitch));
                    break;
                }
                case Protocol.PlayS2C.Particle:
                {
                    var particleId = buffer.ReadVarInt();
                    buffer.ReadBool(); // long distance
                    var request = new Game.ParticleRequest
                    {
                        Position = new Vec3d(buffer.ReadDouble(), buffer.ReadDouble(), buffer.ReadDouble()),
                        Offset = new Vec3d(buffer.ReadFloat(), buffer.ReadFloat(), buffer.ReadFloat()),
                        Speed = buffer.ReadFloat(),
                        Count = buffer.ReadInt(),
                        Scale = 1,
                    };
                    // particle options (1.20.4 particle types with data)
                    switch (particleId)
                    {
                        case 2: // block
                        case 3: // block_marker
                        case 27: // falling_dust
                            request.Block = blocks.GetState(buffer.ReadVarInt());
                            break;
                        case 14: // dust
                            request.R = buffer.ReadFloat();
                            request.G = buffer.ReadFloat();
                            request.B = buffer.ReadFloat();
                            request.Scale = buffer.ReadFloat();
                            break;
                        case 15: // dust_color_transition: from, scale, to
                            request.R = buffer.ReadFloat();
                            request.G = buffer.ReadFloat();
                            request.B = buffer.ReadFloat();
                            request.Scale = buffer.ReadFloat();
                            buffer.ReadFloat();
                            buffer.ReadFloat();
                            buffer.ReadFloat();
                            break;
                        case 42: // item
                            request.Item = ReadSlot(buffer);
                            break;
                        case 43: // vibration: position source and ticks
                            if (buffer.ReadVarInt() == 0)
                            {
                                buffer.ReadPosition();
                            }
                            else
                            {
                                buffer.ReadVarInt();
                                buffer.ReadFloat();
                            }
                            buffer.ReadVarInt();
                            break;
                        case 33: // sculk_charge
                            buffer.ReadFloat();
                            break;
                        case 96: // shriek
                            buffer.ReadVarInt();
                            break;
                    }
                    pending.Enqueue(game => game.OnParticle(particleId, request));
                    break;
                }
                case Protocol.PlayS2C.WorldEvent:
                {
                    var type = buffer.ReadInt();
                    var position = buffer.ReadPosition();
                    var data = buffer.ReadInt();
                    pending.Enqueue(game => game.OnWorldEvent(type, position, data));
                    break;
                }
                case Protocol.PlayS2C.ChunkBiomes:
                {
                    // /fillbiome: new biome containers for whole chunks
                    var count = buffer.ReadVarInt();
                    var sectionCount = dimension.Height >> 4;
                    for (var i = 0; i < count; i++)
                    {
                        var packed = buffer.ReadLong();
                        var position = new ChunkPos((int)packed, (int)(packed >> 32));
                        var data = new InBuffer(buffer.ReadByteArray());
                        var biomes = new ushort[sectionCount * 64];
                        for (var section = 0; section < sectionCount && data.Remaining > 0; section++) ChunkDecoder.ReadBiomes(data, biomeBits, biomes, section * 64);
                        pending.Enqueue(game => game.World.SetBiomes(position, biomes));
                    }
                    break;
                }
                case Protocol.PlayS2C.UpdateRecipes:
                {
                    var recipes = ReadRecipes(buffer);
                    if (recipes != null) pending.Enqueue(game => game.Recipes.SetRecipes(recipes));
                    break;
                }
                case Protocol.PlayS2C.UpdateRecipeBook:
                {
                    var action = buffer.ReadVarInt();
                    var settings = new bool[8];
                    for (var i = 0; i < 8; i++) settings[i] = buffer.ReadBool();
                    var ids = new List<string>();
                    var count = buffer.ReadVarInt();
                    for (var i = 0; i < count; i++) ids.Add(buffer.ReadString());
                    if (action == 0)
                    {
                        // the second list only marks the new ones (highlighted)
                        var highlighted = buffer.ReadVarInt();
                        for (var i = 0; i < highlighted; i++) buffer.ReadString();
                    }
                    pending.Enqueue(game => game.Recipes.OnBook(action, ids, settings));
                    break;
                }
                case Protocol.PlayS2C.BlockEntityData:
                {
                    var position = buffer.ReadPosition();
                    var type = buffer.ReadVarInt();
                    var nbt = buffer.ReadNbt();
                    pending.Enqueue(game => game.World.BlockEntities.Set(position, type, nbt));
                    break;
                }
                case Protocol.PlayS2C.BlockAction:
                {
                    var position = buffer.ReadPosition();
                    var action = buffer.ReadByte();
                    var parameter = buffer.ReadByte();
                    pending.Enqueue(game => game.OnBlockAction(position, action, parameter));
                    break;
                }
                case Protocol.PlayS2C.UpdateSectionBlocks:
                {
                    var section = buffer.ReadLong();
                    var sx = (int)(section >> 42);
                    var sy = (int)(section << 44 >> 44);
                    var sz = (int)(section << 22 >> 42);
                    var count = buffer.ReadVarInt();
                    var changes = new KeyValuePair<BlockPos, BlockState>[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entry = buffer.ReadVarLong();
                        var local = (int)(entry & 0xFFF);
                        var position = new BlockPos((sx << 4) + ((local >> 8) & 0xF), (sy << 4) + (local & 0xF), (sz << 4) + ((local >> 4) & 0xF));
                        changes[i] = new KeyValuePair<BlockPos, BlockState>(position, blocks.GetState((int)(entry >> 12)));
                    }
                    pending.Enqueue(game => game.World.SetBlocks(changes));
                    break;
                }
                case Protocol.PlayS2C.SynchronizePosition:
                {
                    var x = buffer.ReadDouble();
                    var y = buffer.ReadDouble();
                    var z = buffer.ReadDouble();
                    var yaw = buffer.ReadFloat();
                    var pitch = buffer.ReadFloat();
                    var flags = buffer.ReadByte();
                    var teleportId = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnServerTeleport(x, y, z, yaw, pitch, flags, teleportId));
                    break;
                }
                case Protocol.PlayS2C.GameEvent:
                {
                    var type = buffer.ReadByte();
                    var value = buffer.ReadFloat();
                    if (type == 3) pending.Enqueue(game => game.OnGamemode((int)value));
                    // weather: 1 start raining, 2 stop raining, 7 rain level, 8 thunder level
                    else if (type == 1) pending.Enqueue(game => game.World.Raining = true);
                    else if (type == 2) pending.Enqueue(game =>
                    {
                        game.World.Raining = false;
                        game.World.RainLevel = 0;
                        game.World.ThunderLevel = 0;
                    });
                    else if (type == 7) pending.Enqueue(game => game.World.RainLevel = Math.Max(0, Math.Min(1, value)));
                    else if (type == 8) pending.Enqueue(game => game.World.ThunderLevel = Math.Max(0, Math.Min(1, value)));
                    break;
                }
                case Protocol.PlayS2C.PlayerAbilities:
                {
                    var flags = buffer.ReadByte();
                    var flyingSpeed = buffer.ReadFloat();
                    buffer.ReadFloat(); // fov modifier
                    pending.Enqueue(game => game.OnAbilities((flags & 0x02) != 0, (flags & 0x04) != 0, flyingSpeed));
                    break;
                }
                case Protocol.PlayS2C.SetHealth:
                {
                    var health = buffer.ReadFloat();
                    var food = buffer.ReadVarInt();
                    var saturation = buffer.ReadFloat();
                    pending.Enqueue(game => game.OnHealth(health, food, saturation));
                    break;
                }
                case Protocol.PlayS2C.UpdateTime:
                {
                    buffer.ReadLong(); // world age
                    var time = buffer.ReadLong();
                    pending.Enqueue(game => game.World.TimeOfDay = (Math.Abs(time) % 24000 + 24000) % 24000);
                    break;
                }
                case Protocol.PlayS2C.SystemChat:
                {
                    var message = buffer.ReadNbtText();
                    var overlay = buffer.ReadBool();
                    pending.Enqueue(game => game.OnChat(message, overlay));
                    break;
                }
                case Protocol.PlayS2C.SetActionBar:
                {
                    var message = buffer.ReadNbtText();
                    pending.Enqueue(game => game.OnChat(message, true));
                    break;
                }
                case Protocol.PlayS2C.SetTitle:
                {
                    var title = buffer.ReadNbtText().ToLegacy();
                    pending.Enqueue(game => game.ServerUi.SetTitle(title, game.Ticks));
                    break;
                }
                case Protocol.PlayS2C.SetSubtitle:
                {
                    var subtitle = buffer.ReadNbtText().ToLegacy();
                    pending.Enqueue(game => game.ServerUi.SetSubtitle(subtitle));
                    break;
                }
                case Protocol.PlayS2C.TitleTimes:
                {
                    int fadeIn = buffer.ReadInt(), stay = buffer.ReadInt(), fadeOut = buffer.ReadInt();
                    pending.Enqueue(game => game.ServerUi.SetTimes(fadeIn, stay, fadeOut));
                    break;
                }
                case Protocol.PlayS2C.ClearTitles:
                {
                    var reset = buffer.ReadBool();
                    pending.Enqueue(game => game.ServerUi.ClearTitles(reset));
                    break;
                }
                case Protocol.PlayS2C.TabListHeader:
                {
                    var header = buffer.ReadNbtText().ToLegacy();
                    var footer = buffer.ReadNbtText().ToLegacy();
                    pending.Enqueue(game =>
                    {
                        game.ServerUi.TabHeader = header;
                        game.ServerUi.TabFooter = footer;
                    });
                    break;
                }
                case Protocol.PlayS2C.BossBar:
                    ReadBossBar(buffer);
                    break;
                case Protocol.PlayS2C.UpdateObjectives:
                {
                    var name = buffer.ReadString();
                    var mode = buffer.ReadByte();
                    if (mode == 1)
                    {
                        pending.Enqueue(game => game.ServerUi.RemoveObjective(name));
                        break;
                    }
                    var objective = new Game.Objective { Name = name, DisplayName = buffer.ReadNbtText().ToLegacy(), Type = buffer.ReadVarInt() };
                    if (buffer.ReadBool()) ReadNumberFormat(buffer, out objective.NumberFormat, out objective.FixedFormat);
                    pending.Enqueue(game => game.ServerUi.Objectives[name] = objective);
                    break;
                }
                case Protocol.PlayS2C.DisplayObjective:
                {
                    var slot = buffer.ReadVarInt();
                    var name = buffer.ReadString();
                    pending.Enqueue(game =>
                    {
                        if (slot >= 0 && slot < game.ServerUi.Displays.Length) game.ServerUi.Displays[slot] = name.Length == 0 ? null : name;
                    });
                    break;
                }
                case Protocol.PlayS2C.UpdateScore:
                {
                    var score = new Game.Score { Holder = buffer.ReadString() };
                    var objective = buffer.ReadString();
                    score.Value = buffer.ReadVarInt();
                    if (buffer.ReadBool()) score.DisplayName = buffer.ReadNbtText().ToLegacy();
                    if (buffer.ReadBool()) ReadNumberFormat(buffer, out score.NumberFormat, out score.FixedFormat);
                    pending.Enqueue(game => game.ServerUi.SetScore(objective, score));
                    break;
                }
                case Protocol.PlayS2C.ResetScore:
                {
                    var holder = buffer.ReadString();
                    var objective = buffer.ReadBool() ? buffer.ReadString() : null;
                    pending.Enqueue(game => game.ServerUi.ResetScore(holder, objective));
                    break;
                }
                case Protocol.PlayS2C.UpdateTeams:
                    ReadTeams(buffer);
                    break;
                case Protocol.PlayS2C.UpdateAdvancements:
                    ReadAdvancements(buffer);
                    break;
                case Protocol.PlayS2C.PlayerChat:
                    ReadPlayerChat(buffer);
                    break;
                case Protocol.PlayS2C.DisguisedChat:
                {
                    var message = buffer.ReadNbtText();
                    buffer.ReadVarInt(); // chat type
                    var sender = buffer.ReadNbtText();
                    if (buffer.ReadBool()) buffer.ReadNbtText(); // target name
                    pending.Enqueue(game => game.OnChat(ChatMessage(sender, message), false));
                    break;
                }
                case Protocol.PlayS2C.StartConfiguration:
                    connection.Send(Protocol.PlayC2S.AcknowledgeConfiguration, new OutBuffer());
                    State = ProtocolState.Configuration;
                    Status = "Configuring";
                    pending.Enqueue(game => game.OnReconfigure());
                    break;
                case Protocol.PlayS2C.SetContainerContent:
                {
                    var window = buffer.ReadByte();
                    var stateId = buffer.ReadVarInt();
                    var count = buffer.ReadVarInt();
                    var slots = new ItemStack[count];
                    for (var i = 0; i < count; i++) slots[i] = ReadSlot(buffer);
                    var carried = ReadSlot(buffer);
                    pending.Enqueue(game => game.Inventory.OnContent(window, stateId, slots, carried));
                    break;
                }
                case Protocol.PlayS2C.SetContainerSlot:
                {
                    var window = buffer.ReadSByte();
                    var stateId = buffer.ReadVarInt();
                    var slot = buffer.ReadShort();
                    var stack = ReadSlot(buffer);
                    pending.Enqueue(game => game.Inventory.OnSlot(window, stateId, slot, stack));
                    break;
                }
                case Protocol.PlayS2C.OpenScreen:
                {
                    var window = buffer.ReadVarInt();
                    var type = buffer.ReadVarInt();
                    var title = buffer.ReadNbtText().ToPlain();
                    pending.Enqueue(game => game.Inventory.OnOpenScreen(window, type, title));
                    break;
                }
                case Protocol.PlayS2C.OpenSignEditor:
                {
                    var position = buffer.ReadPosition();
                    var front = buffer.ReadBool();
                    pending.Enqueue(game => game.OnOpenSignEditor(position, front));
                    break;
                }
                case Protocol.PlayS2C.OpenBook:
                {
                    var hand = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnOpenBook(hand));
                    break;
                }
                case Protocol.PlayS2C.MapData:
                {
                    var mapId = buffer.ReadVarInt();
                    buffer.ReadByte(); // scale
                    buffer.ReadBool(); // locked
                    List<MapDecoration> decorations = null;
                    if (buffer.ReadBool())
                    {
                        var icons = buffer.ReadVarInt();
                        decorations = new List<MapDecoration>();
                        for (var i = 0; i < icons; i++)
                        {
                            var decoration = new MapDecoration { Type = buffer.ReadVarInt(), X = (sbyte)buffer.ReadByte(), Z = (sbyte)buffer.ReadByte(), Rotation = buffer.ReadByte() & 15 };
                            if (buffer.ReadBool()) decoration.Name = buffer.ReadNbtText().ToLegacy();
                            decorations.Add(decoration);
                        }
                    }
                    if (decorations != null) pending.Enqueue(game => game.OnMapDecorations(mapId, decorations));
                    var columns = buffer.ReadByte();
                    if (columns == 0) break;
                    var rows = buffer.ReadByte();
                    var x = buffer.ReadByte();
                    var z = buffer.ReadByte();
                    var colors = buffer.ReadBytes(buffer.ReadVarInt());
                    pending.Enqueue(game => game.OnMapData(mapId, x, z, columns, rows, colors));
                    break;
                }
                case Protocol.PlayS2C.SetContainerProperty:
                {
                    var window = buffer.ReadByte();
                    var property = buffer.ReadShort();
                    var value = buffer.ReadShort();
                    pending.Enqueue(game => game.Inventory.OnProperty(window, property, value));
                    break;
                }
                case Protocol.PlayS2C.MerchantOffers:
                {
                    var window = buffer.ReadVarInt();
                    var count = buffer.ReadVarInt();
                    var offers = new List<Game.TradeOffer>();
                    for (var i = 0; i < count; i++)
                    {
                        var offer = new Game.TradeOffer { CostA = ReadSlot(buffer), Result = ReadSlot(buffer), CostB = ReadSlot(buffer), OutOfStock = buffer.ReadBool() };
                        offer.Uses = buffer.ReadInt();
                        offer.MaxUses = buffer.ReadInt();
                        buffer.ReadInt(); // xp
                        buffer.ReadInt(); // special price
                        buffer.ReadFloat(); // price multiplier
                        buffer.ReadInt(); // demand
                        offers.Add(offer);
                    }
                    pending.Enqueue(game => game.Inventory.OnOffers(window, offers));
                    break;
                }
                case Protocol.PlayS2C.CloseContainer:
                {
                    var window = buffer.ReadByte();
                    pending.Enqueue(game => game.Inventory.OnCloseScreen(window));
                    break;
                }
                case Protocol.PlayS2C.SetHeldItem:
                {
                    var slot = buffer.ReadByte();
                    pending.Enqueue(game => game.SelectedSlot = Math.Max(0, Math.Min(8, (int)slot)));
                    break;
                }
                case Protocol.PlayS2C.AddResourcePack:
                    ReadAddResourcePack(buffer);
                    break;
                case Protocol.PlayS2C.RemoveResourcePack:
                    ReadRemoveResourcePack(buffer);
                    break;
                case Protocol.PlayS2C.SetExperience:
                {
                    var bar = buffer.ReadFloat();
                    var level = buffer.ReadVarInt();
                    var total = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnExperience(bar, level, total));
                    break;
                }
                case Protocol.PlayS2C.EntityEffect:
                {
                    var entity = buffer.ReadVarInt();
                    var effect = buffer.ReadVarInt();
                    var amplifier = buffer.ReadByte();
                    var duration = buffer.ReadVarInt();
                    if (entity == EntityId) pending.Enqueue(game => game.OnEffect(effect, amplifier, duration));
                    break;
                }
                case Protocol.PlayS2C.RemoveEntityEffect:
                {
                    var entity = buffer.ReadVarInt();
                    var effect = buffer.ReadVarInt();
                    if (entity == EntityId) pending.Enqueue(game => game.Player.Effects.Remove(effect));
                    break;
                }
                case Protocol.PlayS2C.SetEntityMetadata:
                {
                    var entityId = buffer.ReadVarInt();
                    var values = ReadEntityData(buffer);
                    pending.Enqueue(game => game.OnEntityData(entityId, values));
                    break;
                }
                case Protocol.PlayS2C.SpawnEntity:
                {
                    var entity = new Entity
                    {
                        Id = buffer.ReadVarInt(),
                        Uuid = buffer.ReadUuid(),
                        Type = entityTypes.Get(buffer.ReadVarInt()),
                    };
                    var position = new Vec3d(buffer.ReadDouble(), buffer.ReadDouble(), buffer.ReadDouble());
                    var pitch = ReadAngle(buffer);
                    var yaw = ReadAngle(buffer);
                    entity.HeadYaw = entity.PreviousHeadYaw = ReadAngle(buffer);
                    entity.Data = buffer.ReadVarInt();
                    entity.Velocity = new Vec3d(buffer.ReadShort() / 8000.0, buffer.ReadShort() / 8000.0, buffer.ReadShort() / 8000.0);
                    entity.Teleport(position, yaw, pitch);
                    entity.BodyYaw = entity.PreviousBodyYaw = yaw;
                    if (entity.Type?.Name.Path == "falling_block") entity.FallingBlock = blocks.GetState(entity.Data);
                    if (entity.Type != null) pending.Enqueue(game => game.Entities.Add(entity));
                    break;
                }
                case Protocol.PlayS2C.SpawnExperienceOrb:
                {
                    var entity = new Entity { Id = buffer.ReadVarInt(), Type = entityTypes.Get("experience_orb") };
                    entity.Teleport(new Vec3d(buffer.ReadDouble(), buffer.ReadDouble(), buffer.ReadDouble()), 0, 0);
                    entity.ExperienceCount = buffer.ReadShort();
                    pending.Enqueue(game => game.Entities.Add(entity));
                    break;
                }
                case Protocol.PlayS2C.RemoveEntities:
                {
                    var count = buffer.ReadVarInt();
                    var ids = new int[count];
                    for (var i = 0; i < count; i++) ids[i] = buffer.ReadVarInt();
                    pending.Enqueue(game =>
                    {
                        foreach (var removed in ids) game.Entities.Remove(removed);
                    });
                    break;
                }
                case Protocol.PlayS2C.EntityPosition:
                case Protocol.PlayS2C.EntityPositionRotation:
                {
                    var entityId = buffer.ReadVarInt();
                    var delta = new Vec3d(buffer.ReadShort() / 4096.0, buffer.ReadShort() / 4096.0, buffer.ReadShort() / 4096.0);
                    float? yaw = null, pitch = null;
                    var rotation = id == Protocol.PlayS2C.EntityPositionRotation;
                    if (rotation)
                    {
                        yaw = ReadAngle(buffer);
                        pitch = ReadAngle(buffer);
                    }
                    var onGround = buffer.ReadBool();
                    pending.Enqueue(game => game.OnEntityMove(entityId, delta, yaw, pitch, onGround, true));
                    break;
                }
                case Protocol.PlayS2C.EntityRotation:
                {
                    var entityId = buffer.ReadVarInt();
                    var yaw = ReadAngle(buffer);
                    var pitch = ReadAngle(buffer);
                    var onGround = buffer.ReadBool();
                    pending.Enqueue(game => game.OnEntityMove(entityId, Vec3d.Zero, yaw, pitch, onGround, true));
                    break;
                }
                case Protocol.PlayS2C.TeleportEntity:
                {
                    var entityId = buffer.ReadVarInt();
                    var position = new Vec3d(buffer.ReadDouble(), buffer.ReadDouble(), buffer.ReadDouble());
                    var yaw = ReadAngle(buffer);
                    var pitch = ReadAngle(buffer);
                    var onGround = buffer.ReadBool();
                    pending.Enqueue(game => game.OnEntityMove(entityId, position, yaw, pitch, onGround, false));
                    break;
                }
                case Protocol.PlayS2C.HeadRotation:
                {
                    var entityId = buffer.ReadVarInt();
                    var yaw = ReadAngle(buffer);
                    pending.Enqueue(game =>
                    {
                        var entity = game.Entities.Get(entityId);
                        if (entity != null) entity.HeadYaw = yaw;
                    });
                    break;
                }
                case Protocol.PlayS2C.EntityVelocity:
                {
                    var entityId = buffer.ReadVarInt();
                    var velocity = new Vec3d(buffer.ReadShort() / 8000.0, buffer.ReadShort() / 8000.0, buffer.ReadShort() / 8000.0);
                    pending.Enqueue(game => game.OnEntityVelocity(entityId, velocity));
                    break;
                }
                case Protocol.PlayS2C.EntityAnimation:
                {
                    var entityId = buffer.ReadVarInt();
                    var animation = buffer.ReadByte();
                    pending.Enqueue(game =>
                    {
                        var entity = game.Entities.Get(entityId);
                        if (entity != null && (animation == 0 || animation == 3)) entity.SwingTime = 0;
                    });
                    break;
                }
                case Protocol.PlayS2C.DamageEvent:
                {
                    var entityId = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnDamageEvent(entityId));
                    break;
                }
                case Protocol.PlayS2C.EntityEvent:
                {
                    var entityId = buffer.ReadInt();
                    var status = buffer.ReadByte();
                    if (status == 3) pending.Enqueue(game => game.OnEntityDeath(entityId));
                    else if (status >= 47 && status <= 52) pending.Enqueue(game => game.OnItemBreak(entityId, status - 47));
                    break;
                }
                case Protocol.PlayS2C.SetEquipment:
                {
                    var entityId = buffer.ReadVarInt();
                    var equipment = new System.Collections.Generic.List<KeyValuePair<int, ItemStack>>();
                    while (true)
                    {
                        var slot = buffer.ReadByte();
                        equipment.Add(new KeyValuePair<int, ItemStack>(slot & 0x7F, ReadSlot(buffer)));
                        if ((slot & 0x80) == 0) break;
                    }
                    pending.Enqueue(game =>
                    {
                        var entity = game.Entities.Get(entityId);
                        if (entity == null) return;
                        foreach (var pair in equipment)
                        {
                            if (pair.Key < entity.Equipment.Length) entity.Equipment[pair.Key] = pair.Value;
                        }
                    });
                    break;
                }
                case Protocol.PlayS2C.SetPassengers:
                {
                    var vehicle = buffer.ReadVarInt();
                    var count = buffer.ReadVarInt();
                    var passengers = new int[count];
                    for (var i = 0; i < count; i++) passengers[i] = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnPassengers(vehicle, passengers));
                    break;
                }
                case Protocol.PlayS2C.PickupItem:
                {
                    var collected = buffer.ReadVarInt();
                    pending.Enqueue(game => game.OnPickup(collected));
                    break;
                }
                case Protocol.PlayS2C.PlayerInfoUpdate:
                    ReadPlayerInfo(buffer);
                    break;
                case Protocol.PlayS2C.PlayerInfoRemove:
                {
                    var count = buffer.ReadVarInt();
                    var uuids = new Guid[count];
                    for (var i = 0; i < count; i++) uuids[i] = buffer.ReadUuid();
                    pending.Enqueue(game =>
                    {
                        foreach (var uuid in uuids) game.Entities.Players.Remove(uuid);
                    });
                    break;
                }
                case Protocol.PlayS2C.CombatDeath:
                {
                    var player = buffer.ReadVarInt();
                    var message = buffer.ReadNbtText().ToPlain();
                    if (player == EntityId) pending.Enqueue(game => game.OnDeath(message));
                    break;
                }
            }
        }

        private void ReadLogin(InBuffer buffer)
        {
            EntityId = buffer.ReadInt();
            buffer.ReadBool(); // hardcore
            var dimensionCount = buffer.ReadVarInt();
            for (var i = 0; i < dimensionCount; i++) buffer.ReadString();
            buffer.ReadVarInt(); // max players
            var viewDistance = buffer.ReadVarInt();
            buffer.ReadVarInt(); // simulation distance
            buffer.ReadBool(); // reduced debug info
            buffer.ReadBool(); // respawn screen
            buffer.ReadBool(); // limited crafting
            var dimensionType = buffer.ReadString();
            var dimensionName = buffer.ReadString();
            buffer.ReadLong(); // hashed seed
            var gamemode = buffer.ReadByte();
            dimension = GetDimension(dimensionType);
            Status = "Loading terrain";
            var type = dimension;
            pending.Enqueue(game => game.OnJoin(EntityId, type, dimensionName, gamemode, viewDistance));
        }

        private void ReadRespawn(InBuffer buffer)
        {
            var dimensionType = buffer.ReadString();
            var dimensionName = buffer.ReadString();
            buffer.ReadLong(); // hashed seed
            var gamemode = buffer.ReadByte();
            dimension = GetDimension(dimensionType);
            var type = dimension;
            pending.Enqueue(game => game.OnRespawn(type, dimensionName, gamemode));
        }

        /// <summary>Player Chat (1.19.3+ signed chat); the signature is not verified, the sender name is shown.</summary>
        private void ReadPlayerChat(InBuffer buffer)
        {
            buffer.ReadUuid(); // sender
            buffer.ReadVarInt(); // index
            if (buffer.ReadBool()) buffer.Skip(256); // signature
            var message = buffer.ReadString(256);
            buffer.ReadLong(); // timestamp
            buffer.ReadLong(); // salt
            var previous = buffer.ReadVarInt();
            for (var i = 0; i < previous; i++)
            {
                if (buffer.ReadVarInt() == 0) buffer.Skip(256);
            }
            TextComponent unsigned = null;
            if (buffer.ReadBool()) unsigned = buffer.ReadNbtText();
            var filter = buffer.ReadVarInt();
            if (filter == 2) buffer.ReadBitSet();
            buffer.ReadVarInt(); // chat type
            var sender = buffer.ReadNbtText();
            var text = ChatMessage(sender, unsigned ?? TextComponent.Of(message));
            pending.Enqueue(game => game.OnChat(text, false));
        }

        /// <summary>chat.type.text: "&lt;sender&gt; message", keeping the styles and click events of both parts.</summary>
        private static TextComponent ChatMessage(TextComponent sender, TextComponent message)
        {
            var root = TextComponent.Of("<");
            root.Extra.Add(sender);
            root.Extra.Add(TextComponent.Of("> "));
            root.Extra.Add(message);
            return root;
        }

        private ItemStack[] ReadIngredient(InBuffer buffer)
        {
            var options = new ItemStack[buffer.ReadVarInt()];
            for (var i = 0; i < options.Length; i++) options[i] = ReadSlot(buffer);
            return options;
        }

        /// <summary>Update Recipes (1.20.4 serializers); stops at an unknown recipe type (the rest can not be read).</summary>
        private List<Game.Recipe> ReadRecipes(InBuffer buffer)
        {
            var recipes = new List<Game.Recipe>();
            var count = buffer.ReadVarInt();
            for (var i = 0; i < count; i++)
            {
                var type = buffer.ReadString();
                if (type.StartsWith("minecraft:")) type = type.Substring(10);
                var recipe = new Game.Recipe { Type = type, Id = buffer.ReadString() };
                switch (type)
                {
                    case "crafting_shaped":
                        recipe.Group = buffer.ReadString();
                        buffer.ReadVarInt(); // category
                        recipe.Width = buffer.ReadVarInt();
                        recipe.Height = buffer.ReadVarInt();
                        for (var slot = 0; slot < recipe.Width * recipe.Height; slot++) recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Result = ReadSlot(buffer);
                        buffer.ReadBool(); // show notification
                        break;
                    case "crafting_shapeless":
                    {
                        recipe.Group = buffer.ReadString();
                        buffer.ReadVarInt();
                        var ingredients = buffer.ReadVarInt();
                        for (var slot = 0; slot < ingredients; slot++) recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Result = ReadSlot(buffer);
                        break;
                    }
                    case "smelting":
                    case "blasting":
                    case "smoking":
                    case "campfire_cooking":
                        recipe.Group = buffer.ReadString();
                        buffer.ReadVarInt();
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Result = ReadSlot(buffer);
                        buffer.ReadFloat(); // experience
                        buffer.ReadVarInt(); // cooking time
                        break;
                    case "stonecutting":
                        recipe.Group = buffer.ReadString();
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Result = ReadSlot(buffer);
                        break;
                    case "smithing_transform":
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Result = ReadSlot(buffer);
                        break;
                    case "smithing_trim":
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        recipe.Ingredients.Add(ReadIngredient(buffer));
                        break;
                    default:
                        if (!type.StartsWith("crafting_special_") && type != "crafting_decorated_pot")
                        {
                            Log.Warn("Unknown recipe serializer " + type + ": the remaining recipes are ignored");
                            return recipes;
                        }
                        buffer.ReadVarInt(); // category
                        break;
                }
                recipes.Add(recipe);
            }
            return recipes;
        }

        public void SendPlaceRecipe(int windowId, string recipe, bool all) =>
            connection.Send(Protocol.PlayC2S.PlaceRecipe, new OutBuffer().WriteByte(windowId).WriteString(recipe).WriteBool(all));

        /// <summary>Set Beacon Effect: primary and secondary effect ids (-1 = none).</summary>
        public void SendBeaconEffect(int primary, int secondary)
        {
            var buffer = new OutBuffer().WriteBool(primary >= 0);
            if (primary >= 0) buffer.WriteVarInt(primary);
            buffer.WriteBool(secondary >= 0);
            if (secondary >= 0) buffer.WriteVarInt(secondary);
            connection.Send(Protocol.PlayC2S.SetBeaconEffect, buffer);
        }

        /// <summary>Change Container Slot State (crafter slots on / off).</summary>
        public void SendSlotState(int slot, int windowId, bool enabled) =>
            connection.Send(Protocol.PlayC2S.ChangeContainerSlotState, new OutBuffer().WriteVarInt(slot).WriteVarInt(windowId).WriteBool(enabled));

        /// <summary>Number format (1.20.3): 0 blank, 1 styled (style NBT), 2 fixed (text).</summary>
        private static void ReadNumberFormat(InBuffer buffer, out int format, out string fixedText)
        {
            format = buffer.ReadVarInt();
            fixedText = null;
            if (format == 1) buffer.ReadNbt();
            else if (format == 2) fixedText = buffer.ReadNbtText().ToLegacy();
        }

        private void ReadBossBar(InBuffer buffer)
        {
            var id = buffer.ReadUuid();
            var action = buffer.ReadVarInt();
            switch (action)
            {
                case 0:
                {
                    var title = buffer.ReadNbtText().ToLegacy();
                    var health = buffer.ReadFloat();
                    var color = buffer.ReadVarInt();
                    var division = buffer.ReadVarInt();
                    var flags = buffer.ReadByte();
                    pending.Enqueue(game =>
                    {
                        var bar = game.ServerUi.GetOrAddBossBar(id);
                        bar.Title = title;
                        bar.Health = health;
                        bar.Color = color;
                        bar.Division = division;
                        bar.Flags = flags;
                    });
                    break;
                }
                case 1:
                    pending.Enqueue(game => game.ServerUi.RemoveBossBar(id));
                    break;
                case 2:
                {
                    var health = buffer.ReadFloat();
                    pending.Enqueue(game => game.ServerUi.GetOrAddBossBar(id).Health = health);
                    break;
                }
                case 3:
                {
                    var title = buffer.ReadNbtText().ToLegacy();
                    pending.Enqueue(game => game.ServerUi.GetOrAddBossBar(id).Title = title);
                    break;
                }
                case 4:
                {
                    var color = buffer.ReadVarInt();
                    var division = buffer.ReadVarInt();
                    pending.Enqueue(game =>
                    {
                        var bar = game.ServerUi.GetOrAddBossBar(id);
                        bar.Color = color;
                        bar.Division = division;
                    });
                    break;
                }
                case 5:
                {
                    var flags = buffer.ReadByte();
                    pending.Enqueue(game => game.ServerUi.GetOrAddBossBar(id).Flags = flags);
                    break;
                }
            }
        }

        private void ReadTeams(InBuffer buffer)
        {
            var name = buffer.ReadString();
            var mode = buffer.ReadByte();
            if (mode == 1)
            {
                pending.Enqueue(game => game.ServerUi.RemoveTeam(name));
                return;
            }
            Game.Team info = null;
            if (mode == 0 || mode == 2)
            {
                info = new Game.Team { Name = name, DisplayName = buffer.ReadNbtText().ToLegacy() };
                buffer.ReadByte(); // friendly flags
                info.NameTagVisibility = buffer.ReadString();
                buffer.ReadString(); // collision rule
                info.Color = buffer.ReadVarInt();
                info.Prefix = buffer.ReadNbtText().ToLegacy();
                info.Suffix = buffer.ReadNbtText().ToLegacy();
            }
            var members = new List<string>();
            if (mode == 0 || mode == 3 || mode == 4)
            {
                var count = buffer.ReadVarInt();
                for (var i = 0; i < count; i++) members.Add(buffer.ReadString());
            }
            pending.Enqueue(game =>
            {
                var ui = game.ServerUi;
                if (!ui.Teams.TryGetValue(name, out var team))
                {
                    if (mode != 0) return;
                    ui.Teams[name] = team = new Game.Team { Name = name };
                }
                if (info != null)
                {
                    team.DisplayName = info.DisplayName;
                    team.NameTagVisibility = info.NameTagVisibility;
                    team.Color = info.Color;
                    team.Prefix = info.Prefix;
                    team.Suffix = info.Suffix;
                }
                if (mode == 0 || mode == 3) ui.SetTeamMembers(team, members, true);
                else if (mode == 4) ui.SetTeamMembers(team, members, false);
            });
        }

        private void ReadAdvancements(InBuffer buffer)
        {
            var reset = buffer.ReadBool();
            var added = new Dictionary<string, Game.Advancement>();
            var count = buffer.ReadVarInt();
            for (var i = 0; i < count; i++)
            {
                var id = buffer.ReadString();
                var advancement = new Game.Advancement();
                if (buffer.ReadBool()) buffer.ReadString(); // parent
                if (buffer.ReadBool())
                {
                    advancement.Title = buffer.ReadNbtText().ToLegacy();
                    advancement.Description = buffer.ReadNbtText().ToLegacy();
                    advancement.Icon = ReadSlot(buffer);
                    advancement.Frame = buffer.ReadVarInt();
                    var flags = buffer.ReadInt();
                    if ((flags & 0x01) != 0) buffer.ReadString(); // background
                    advancement.ShowToast = (flags & 0x02) != 0;
                    buffer.ReadFloat();
                    buffer.ReadFloat();
                }
                var groups = buffer.ReadVarInt();
                for (var g = 0; g < groups; g++)
                {
                    var group = new string[buffer.ReadVarInt()];
                    for (var c = 0; c < group.Length; c++) group[c] = buffer.ReadString();
                    advancement.Requirements.Add(group);
                }
                buffer.ReadBool(); // sends telemetry
                added[id] = advancement;
            }
            var removed = new List<string>();
            count = buffer.ReadVarInt();
            for (var i = 0; i < count; i++) removed.Add(buffer.ReadString());
            var progress = new Dictionary<string, List<KeyValuePair<string, bool>>>();
            count = buffer.ReadVarInt();
            for (var i = 0; i < count; i++)
            {
                var id = buffer.ReadString();
                var criteria = new List<KeyValuePair<string, bool>>();
                var criteriaCount = buffer.ReadVarInt();
                for (var c = 0; c < criteriaCount; c++)
                {
                    var criterion = buffer.ReadString();
                    var achieved = buffer.ReadBool();
                    if (achieved) buffer.ReadLong();
                    criteria.Add(new KeyValuePair<string, bool>(criterion, achieved));
                }
                progress[id] = criteria;
            }
            pending.Enqueue(game => game.ServerUi.OnAdvancements(reset, added, removed, progress));
        }

        private static float ReadAngle(InBuffer buffer) => buffer.ReadSByte() * 360.0f / 256.0f;

        /// <summary>
        /// Entity data (Minosoft's EntityData + EntityDataTypes for 1.20.4): index -> value. Particles (only used by
        /// area effect clouds) stop the reader, everything before is kept.
        /// </summary>
        private Dictionary<int, object> ReadEntityData(InBuffer buffer)
        {
            var values = new Dictionary<int, object>();
            while (buffer.Remaining > 0)
            {
                var index = buffer.ReadByte();
                if (index == 0xFF) break;
                var type = buffer.ReadVarInt();
                object value;
                switch (type)
                {
                    case 0: value = buffer.ReadByte(); break;
                    case 1: value = buffer.ReadVarInt(); break;
                    case 2: value = buffer.ReadVarLong(); break;
                    case 3: value = buffer.ReadFloat(); break;
                    case 4: value = buffer.ReadString(); break;
                    case 5: value = buffer.ReadNbtText().ToPlain(); break;
                    case 6: value = buffer.ReadBool() ? buffer.ReadNbtText().ToPlain() : null; break;
                    case 7: value = ReadSlot(buffer); break;
                    case 8: value = buffer.ReadBool(); break;
                    case 9: value = new Vec3d(buffer.ReadFloat(), buffer.ReadFloat(), buffer.ReadFloat()); break;
                    case 10: value = buffer.ReadPosition(); break;
                    case 11: value = buffer.ReadBool() ? (object)buffer.ReadPosition() : null; break;
                    case 12: value = buffer.ReadVarInt(); break;
                    case 13: value = buffer.ReadBool() ? (object)buffer.ReadUuid() : null; break;
                    case 14: value = blocks.GetState(buffer.ReadVarInt()); break;
                    case 15:
                    {
                        var state = buffer.ReadVarInt();
                        value = state == 0 ? null : blocks.GetState(state);
                        break;
                    }
                    case 16: value = buffer.ReadNbt(); break;
                    case 18: value = new[] { buffer.ReadVarInt(), buffer.ReadVarInt(), buffer.ReadVarInt() }; break;
                    case 19: value = buffer.ReadVarInt() - 1; break;
                    case 20: case 21: case 22: case 24: case 25: value = buffer.ReadVarInt(); break;
                    case 23:
                        if (buffer.ReadBool())
                        {
                            buffer.ReadString();
                            buffer.ReadPosition();
                        }
                        value = null;
                        break;
                    case 26: value = new Vec3d(buffer.ReadFloat(), buffer.ReadFloat(), buffer.ReadFloat()); break;
                    case 27:
                        value = new[] { buffer.ReadFloat(), buffer.ReadFloat(), buffer.ReadFloat(), buffer.ReadFloat() };
                        break;
                    default:
                        return values; // particle (17) or unknown: the rest can not be decoded
                }
                values[index] = value;
            }
            return values;
        }

        /// <summary>Player Info Update (tab list): names, skins (textures property), gamemode, latency, listing.</summary>
        private void ReadPlayerInfo(InBuffer buffer)
        {
            var actions = buffer.ReadByte();
            var count = buffer.ReadVarInt();
            var updates = new List<PlayerInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = new PlayerInfo { Uuid = buffer.ReadUuid(), Gamemode = -1, Latency = -1 };
                if ((actions & 0x01) != 0)
                {
                    info.Name = buffer.ReadString();
                    var properties = buffer.ReadVarInt();
                    for (var p = 0; p < properties; p++)
                    {
                        var name = buffer.ReadString();
                        var value = buffer.ReadString();
                        if (buffer.ReadBool()) buffer.ReadString();
                        if (name == "textures") ReadSkin(info, value);
                    }
                }
                if ((actions & 0x02) != 0 && buffer.ReadBool())
                {
                    buffer.ReadUuid();
                    buffer.ReadLong();
                    buffer.ReadByteArray();
                    buffer.ReadByteArray();
                }
                if ((actions & 0x04) != 0) info.Gamemode = buffer.ReadVarInt();
                if ((actions & 0x08) != 0) info.Listed = buffer.ReadBool();
                if ((actions & 0x10) != 0) info.Latency = buffer.ReadVarInt();
                if ((actions & 0x20) != 0 && buffer.ReadBool()) info.DisplayName = buffer.ReadNbtText().ToPlain();
                updates.Add(info);
            }
            pending.Enqueue(game =>
            {
                foreach (var update in updates)
                {
                    if (!game.Entities.Players.TryGetValue(update.Uuid, out var info))
                    {
                        if ((actions & 0x01) == 0) continue;
                        game.Entities.Players[update.Uuid] = info = new PlayerInfo { Uuid = update.Uuid };
                    }
                    if ((actions & 0x01) != 0)
                    {
                        info.Name = update.Name;
                        info.SkinUrl = update.SkinUrl;
                        info.Slim = update.Slim;
                    }
                    if ((actions & 0x04) != 0) info.Gamemode = update.Gamemode;
                    if ((actions & 0x08) != 0) info.Listed = update.Listed;
                    if ((actions & 0x10) != 0) info.Latency = update.Latency;
                    if ((actions & 0x20) != 0) info.DisplayName = update.DisplayName;
                }
            });
        }

        /// <summary>The "textures" property: base64 json with the skin url and model (slim for "alex" arms).</summary>
        private static void ReadSkin(PlayerInfo info, string value)
        {
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                var skin = json["textures"]?["SKIN"];
                info.SkinUrl = (string)skin?["url"];
                info.Slim = (string)skin?["metadata"]?["model"] == "slim";
            }
            catch (Exception)
            {
                // invalid textures property: default skin
            }
        }

        private ItemStack ReadSlot(InBuffer buffer)
        {
            if (!buffer.ReadBool()) return null;
            var item = items.Get(buffer.ReadVarInt());
            var count = buffer.ReadByte();
            var nbt = buffer.ReadNbt();
            return item == null ? null : new ItemStack { Item = item, Count = count, Nbt = nbt };
        }

        /// <summary>Add Resource Pack (configuration and play): id, url, SHA-1, forced, optional prompt.</summary>
        private void ReadAddResourcePack(InBuffer buffer)
        {
            var offer = new ResourcePackOffer
            {
                Id = buffer.ReadUuid(),
                Url = buffer.ReadString(),
                Hash = buffer.ReadString(40),
                Forced = buffer.ReadBool(),
            };
            if (buffer.ReadBool()) offer.Prompt = buffer.ReadNbtText()?.ToPlain();
            pending.Enqueue(game => game.OnResourcePackOffer(offer));
        }

        private void ReadRemoveResourcePack(InBuffer buffer)
        {
            Guid? id = null;
            if (buffer.ReadBool()) id = buffer.ReadUuid();
            pending.Enqueue(game => game.OnResourcePackRemove(id));
        }

        /// <summary>Resource pack status (0 loaded, 1 declined, 2 download failed, 3 accepted, 4 downloaded, 5 invalid url, 6 reload failed, 7 discarded).</summary>
        public void SendResourcePackStatus(Guid id, int status)
        {
            var packet = State == ProtocolState.Configuration ? Protocol.ConfigC2S.ResourcePack : Protocol.PlayC2S.ResourcePack;
            connection.Send(packet, new OutBuffer().WriteUuid(id).WriteVarInt(status));
        }

        // ------------------------------------------------------------------ sending (game thread)

        private void SendClientInformation(int packetId)
        {
            connection.Send(packetId, new OutBuffer()
                .WriteString("en_us")
                .WriteByte(ViewDistance)
                .WriteVarInt(0) // chat: enabled
                .WriteBool(true) // chat colors
                .WriteByte(0x7F) // skin parts
                .WriteVarInt(1) // main hand: right
                .WriteBool(false) // text filtering
                .WriteBool(true)); // server listings
        }

        public void SendConfirmTeleport(int id) => connection.Send(Protocol.PlayC2S.ConfirmTeleport, new OutBuffer().WriteVarInt(id));

        public void SendPosition(double x, double y, double z, bool onGround) =>
            connection.Send(Protocol.PlayC2S.Position, new OutBuffer().WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteBool(onGround));

        public void SendPositionRotation(double x, double y, double z, float yaw, float pitch, bool onGround) =>
            connection.Send(Protocol.PlayC2S.PositionRotation, new OutBuffer().WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteFloat(yaw).WriteFloat(pitch).WriteBool(onGround));

        public void SendRotation(float yaw, float pitch, bool onGround) =>
            connection.Send(Protocol.PlayC2S.Rotation, new OutBuffer().WriteFloat(yaw).WriteFloat(pitch).WriteBool(onGround));

        public void SendOnGround(bool onGround) => connection.Send(Protocol.PlayC2S.OnGround, new OutBuffer().WriteBool(onGround));

        /// <summary>Player Command: 0 start sneaking, 1 stop sneaking, 3 start sprinting, 4 stop sprinting.</summary>
        public void SendPlayerCommand(int action, int data = 0) => connection.Send(Protocol.PlayC2S.PlayerCommand, new OutBuffer().WriteVarInt(EntityId).WriteVarInt(action).WriteVarInt(data));

        public void SendFlying(bool flying) => connection.Send(Protocol.PlayC2S.PlayerAbilities, new OutBuffer().WriteByte(flying ? 0x02 : 0x00));

        /// <summary>Use Item (right click with an item, not on a block): eating, bows, buckets...</summary>
        public void SendUseItem() => connection.Send(Protocol.PlayC2S.UseItem, new OutBuffer().WriteVarInt(0).WriteVarInt(++sequence));

        /// <summary>Player Action: 0 start digging, 1 cancel, 2 finish, 3 drop stack, 4 drop item, 5 release use item, 6 swap hands.</summary>
        public void SendPlayerAction(int status, BlockPos position, Direction face) =>
            connection.Send(Protocol.PlayC2S.PlayerAction, new OutBuffer().WriteVarInt(status).WritePosition(position).WriteByte((int)face).WriteVarInt(++sequence));

        public void SendUseItemOn(BlockPos position, Direction face, float cursorX, float cursorY, float cursorZ) =>
            connection.Send(Protocol.PlayC2S.UseItemOn, new OutBuffer().WriteVarInt(0).WritePosition(position).WriteVarInt((int)face)
                .WriteFloat(cursorX).WriteFloat(cursorY).WriteFloat(cursorZ).WriteBool(false).WriteVarInt(++sequence));

        public void SendClickContainer(int windowId, int stateId, int slot, int button, int mode, Dictionary<int, ItemStack> changed, ItemStack carried)
        {
            var buffer = new OutBuffer().WriteByte(windowId).WriteVarInt(stateId).WriteShort(slot).WriteByte(button).WriteVarInt(mode).WriteVarInt(changed.Count);
            foreach (var pair in changed)
            {
                buffer.WriteShort(pair.Key);
                WriteSlot(buffer, pair.Value);
            }
            WriteSlot(buffer, carried);
            connection.Send(Protocol.PlayC2S.ClickContainer, buffer);
        }

        /// <summary>Update Sign: the four lines of the front or back text.</summary>
        public void SendUpdateSign(BlockPos position, bool front, string[] lines)
        {
            var buffer = new OutBuffer().WritePosition(position).WriteBool(front);
            for (var i = 0; i < 4; i++) buffer.WriteString(i < lines.Length ? lines[i] ?? "" : "");
            connection.Send(Protocol.PlayC2S.UpdateSign, buffer);
        }

        /// <summary>Edit Book: pages of the book and quill in a hotbar slot (40 = off hand), signed when a title is given.</summary>
        public void SendEditBook(int slot, IList<string> pages, string title)
        {
            var buffer = new OutBuffer().WriteVarInt(slot).WriteVarInt(pages.Count);
            foreach (var page in pages) buffer.WriteString(page);
            buffer.WriteBool(title != null);
            if (title != null) buffer.WriteString(title);
            connection.Send(Protocol.PlayC2S.EditBook, buffer);
        }

        public void SendContainerButton(int windowId, int button) => connection.Send(Protocol.PlayC2S.ClickContainerButton, new OutBuffer().WriteByte(windowId).WriteByte(button));

        public void SendRenameItem(string name) => connection.Send(Protocol.PlayC2S.RenameItem, new OutBuffer().WriteString(name));

        public void SendSelectTrade(int index) => connection.Send(Protocol.PlayC2S.SelectTrade, new OutBuffer().WriteVarInt(index));

        public void SendCloseContainer(int windowId) => connection.Send(Protocol.PlayC2S.CloseContainer, new OutBuffer().WriteByte(windowId));

        /// <summary>Set Creative Mode Slot: player inventory slot number (-1 drops the stack).</summary>
        public void SendCreativeSlot(int slot, ItemStack stack)
        {
            var buffer = new OutBuffer().WriteShort(slot);
            WriteSlot(buffer, stack);
            connection.Send(Protocol.PlayC2S.SetCreativeSlot, buffer);
        }

        private static void WriteSlot(OutBuffer buffer, ItemStack stack)
        {
            if (stack == null || stack.Count <= 0)
            {
                buffer.WriteBool(false);
                return;
            }
            buffer.WriteBool(true).WriteVarInt(stack.Item.Id).WriteByte(stack.Count);
            Nbt.WriteNetwork(buffer, stack.Nbt);
        }

        /// <summary>Interact: 0 interact (hand), 1 attack, 2 interact at (target position, hand).</summary>
        public void SendInteract(int entityId, int type, bool sneaking, float x = 0, float y = 0, float z = 0)
        {
            var buffer = new OutBuffer().WriteVarInt(entityId).WriteVarInt(type);
            if (type == 2) buffer.WriteFloat(x).WriteFloat(y).WriteFloat(z);
            if (type != 1) buffer.WriteVarInt(0);
            buffer.WriteBool(sneaking);
            connection.Send(Protocol.PlayC2S.Interact, buffer);
        }

        /// <summary>Player Input while riding: flags 0x01 jump, 0x02 unmount.</summary>
        public void SendPlayerInput(float sideways, float forward, bool jump, bool unmount) =>
            connection.Send(Protocol.PlayC2S.PlayerInput, new OutBuffer().WriteFloat(sideways).WriteFloat(forward).WriteByte((jump ? 1 : 0) | (unmount ? 2 : 0)));

        public void SendMoveVehicle(double x, double y, double z, float yaw, float pitch) =>
            connection.Send(Protocol.PlayC2S.MoveVehicle, new OutBuffer().WriteDouble(x).WriteDouble(y).WriteDouble(z).WriteFloat(yaw).WriteFloat(pitch));

        public void SendPaddleBoat(bool left, bool right) => connection.Send(Protocol.PlayC2S.PaddleBoat, new OutBuffer().WriteBool(left).WriteBool(right));

        public void SendSwingArm() => connection.Send(Protocol.PlayC2S.SwingArm, new OutBuffer().WriteVarInt(0));

        public void SendHeldItem(int slot) => connection.Send(Protocol.PlayC2S.SetHeldItem, new OutBuffer().WriteShort(slot));

        /// <summary>Client Command 0: respawn.</summary>
        public void SendRespawn() => connection.Send(Protocol.PlayC2S.ClientCommand, new OutBuffer().WriteVarInt(0));

        /// <summary>Chat message or, with a leading slash, a command. Unsigned (offline accounts have no chat keys).</summary>
        public void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (text.StartsWith("/"))
            {
                connection.Send(Protocol.PlayC2S.ChatCommand, new OutBuffer()
                    .WriteString(text.Substring(1))
                    .WriteLong(timestamp)
                    .WriteLong(0) // salt
                    .WriteVarInt(0) // argument signatures
                    .WriteVarInt(0) // acknowledged message count
                    .WriteBytes(new byte[3])); // acknowledged bitset (20 bits)
                return;
            }
            if (text.Length > 256) text = text.Substring(0, 256);
            connection.Send(Protocol.PlayC2S.ChatMessage, new OutBuffer()
                .WriteString(text)
                .WriteLong(timestamp)
                .WriteLong(0) // salt
                .WriteBool(false) // no signature
                .WriteVarInt(0)
                .WriteBytes(new byte[3]));
        }
    }
}
