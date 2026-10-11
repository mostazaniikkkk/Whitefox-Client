// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhiteFox.Core.Network
{
    public enum ProtocolState
    {
        Handshake,
        Status,
        Login,
        Configuration,
        Play,
    }

    /// <summary>
    /// Packet ids of Minecraft 1.20.4 (protocol 765). Minosoft keeps one list per version in versions.json (the
    /// position in the list is the id); WhiteFox only supports this version, so the ids are constants.
    /// </summary>
    public static class Protocol
    {
        public const int Version = 765;
        public const string VersionName = "1.20.4";

        public static class Handshake
        {
            public const int Intention = 0x00;
        }

        public static class StatusC2S
        {
            public const int Request = 0x00;
            public const int Ping = 0x01;
        }

        public static class StatusS2C
        {
            public const int Response = 0x00;
            public const int Pong = 0x01;
        }

        public static class LoginC2S
        {
            public const int Start = 0x00;
            public const int EncryptionResponse = 0x01;
            public const int PluginResponse = 0x02;
            public const int Acknowledged = 0x03;
        }

        public static class LoginS2C
        {
            public const int Disconnect = 0x00;
            public const int EncryptionRequest = 0x01;
            public const int Success = 0x02;
            public const int Compression = 0x03;
            public const int PluginRequest = 0x04;
        }

        public static class ConfigC2S
        {
            public const int ClientInformation = 0x00;
            public const int PluginMessage = 0x01;
            public const int FinishConfiguration = 0x02;
            public const int KeepAlive = 0x03;
            public const int Pong = 0x04;
            public const int ResourcePack = 0x05;
        }

        public static class ConfigS2C
        {
            public const int PluginMessage = 0x00;
            public const int Disconnect = 0x01;
            public const int FinishConfiguration = 0x02;
            public const int KeepAlive = 0x03;
            public const int Ping = 0x04;
            public const int RegistryData = 0x05;
            public const int RemoveResourcePack = 0x06;
            public const int AddResourcePack = 0x07;
            public const int FeatureFlags = 0x08;
            public const int UpdateTags = 0x09;
        }

        public static class PlayC2S
        {
            public const int ConfirmTeleport = 0x00;
            public const int ChatCommand = 0x04;
            public const int ChatMessage = 0x05;
            public const int ChunkBatchReceived = 0x07;
            public const int ClientCommand = 0x08;
            public const int ClientInformation = 0x09;
            public const int AcknowledgeConfiguration = 0x0B;
            public const int ClickContainer = 0x0D;
            public const int PlaceRecipe = 0x1F;
            public const int SetBeaconEffect = 0x2B;
            public const int ChangeContainerSlotState = 0x0F;
            public const int ClickContainerButton = 0x0C;
            public const int RenameItem = 0x27;
            public const int SelectTrade = 0x2A;
            public const int UpdateSign = 0x32;
            public const int EditBook = 0x11;
            public const int CloseContainer = 0x0E;
            public const int SetCreativeSlot = 0x2F;
            public const int Interact = 0x13;
            public const int MoveVehicle = 0x1B;
            public const int PaddleBoat = 0x1C;
            public const int PlayerInput = 0x23;
            public const int PluginMessage = 0x10;
            public const int KeepAlive = 0x15;
            public const int Position = 0x17;
            public const int PositionRotation = 0x18;
            public const int Rotation = 0x19;
            public const int OnGround = 0x1A;
            public const int Pong = 0x24;
            public const int PlayerAbilities = 0x20;
            public const int PlayerAction = 0x21;
            public const int PlayerCommand = 0x22;
            public const int ResourcePack = 0x28;
            public const int SetHeldItem = 0x2C;
            public const int SwingArm = 0x33;
            public const int UseItemOn = 0x35;
            public const int UseItem = 0x36;
        }

        public static class PlayS2C
        {
            public const int Bundle = 0x00;
            public const int SpawnEntity = 0x01;
            public const int SpawnExperienceOrb = 0x02;
            public const int EntityAnimation = 0x03;
            public const int DamageEvent = 0x19;
            public const int EntityEvent = 0x1D;
            public const int EntityPosition = 0x2C;
            public const int EntityPositionRotation = 0x2D;
            public const int EntityRotation = 0x2E;
            public const int PlayerInfoRemove = 0x3B;
            public const int PlayerInfoUpdate = 0x3C;
            public const int RemoveEntities = 0x40;
            public const int HeadRotation = 0x46;
            public const int EntityVelocity = 0x58;
            public const int SetEquipment = 0x59;
            public const int SetPassengers = 0x5D;
            public const int PickupItem = 0x6C;
            public const int TeleportEntity = 0x6D;
            public const int AcknowledgeBlockChange = 0x05;
            public const int BlockEntityData = 0x07;
            public const int UpdateRecipeBook = 0x3F;
            public const int UpdateRecipes = 0x73;
            public const int SetContainerProperty = 0x14;
            public const int MerchantOffers = 0x2B;
            public const int OpenSignEditor = 0x32;
            public const int OpenBook = 0x30;
            public const int MapData = 0x2A;
            public const int BossBar = 0x0A;
            public const int ClearTitles = 0x0F;
            public const int ResetScore = 0x42;
            public const int SetActionBar = 0x4A;
            public const int DisplayObjective = 0x55;
            public const int UpdateObjectives = 0x5C;
            public const int UpdateTeams = 0x5E;
            public const int UpdateScore = 0x5F;
            public const int SetSubtitle = 0x61;
            public const int SetTitle = 0x63;
            public const int TitleTimes = 0x64;
            public const int UpdateAdvancements = 0x70;
            public const int UpdateAttributes = 0x71;
            public const int SetCooldown = 0x16;
            public const int Explosion = 0x1E;
            public const int HurtAnimation = 0x22;
            public const int ChunkBiomes = 0x0E;
            public const int WorldEvent = 0x26;
            public const int Particle = 0x27;
            public const int EntitySoundEffect = 0x65;
            public const int SoundEffect = 0x66;
            public const int BlockAction = 0x08;
            public const int BlockUpdate = 0x09;
            public const int ChunkBatchFinished = 0x0C;
            public const int ChunkBatchStart = 0x0D;
            public const int CloseContainer = 0x12;
            public const int SetContainerContent = 0x13;
            public const int SetContainerSlot = 0x15;
            public const int Disconnect = 0x1B;
            public const int DisguisedChat = 0x1C;
            public const int UnloadChunk = 0x1F;
            public const int GameEvent = 0x20;
            public const int KeepAlive = 0x24;
            public const int ChunkData = 0x25;
            public const int Login = 0x29;
            public const int OpenScreen = 0x31;
            public const int Ping = 0x33;
            public const int PlayerAbilities = 0x36;
            public const int PlayerChat = 0x37;
            public const int SynchronizePosition = 0x3E;
            public const int AddResourcePack = 0x44;
            public const int RemoveResourcePack = 0x43;
            public const int Respawn = 0x45;
            public const int UpdateSectionBlocks = 0x47;
            public const int SetHeldItem = 0x51;
            public const int SetCenterChunk = 0x52;
            public const int SetDefaultSpawn = 0x54;
            public const int SetHealth = 0x5B;
            public const int SetExperience = 0x5A;
            public const int SetEntityMetadata = 0x56;
            public const int LinkEntities = 0x57;
            public const int EntityEffect = 0x72;
            public const int RemoveEntityEffect = 0x41;
            public const int CombatDeath = 0x3A;
            public const int UpdateTime = 0x62;
            public const int StartConfiguration = 0x67;
            public const int SystemChat = 0x69;
            public const int TabListHeader = 0x6A;
        }
    }
}
