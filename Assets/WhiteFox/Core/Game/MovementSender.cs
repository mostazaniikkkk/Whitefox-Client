// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using WhiteFox.Core.Network;
using WhiteFox.Core.Physics;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Game
{
    /// <summary>
    /// Sends the local player state to the server once per tick after physics (Minosoft's MovementPacketSender):
    /// sprint/sneak changes, flying, and position/rotation only when they changed (position at least every second).
    /// </summary>
    public sealed class MovementSender
    {
        /// <summary>Vanilla resends the position if it moved more than 2.0E-4 blocks.</summary>
        private const double MinMovementSquared = 2.0E-4 * 2.0E-4;
        private const int MaxPositionInterval = 20;

        private readonly ServerSession server;
        private readonly LocalPlayer player;

        private bool sprinting;
        private bool sneaking;
        private bool flying;
        private Vec3d position;
        private float yaw = float.NaN;
        private float pitch = float.NaN;
        private bool onGround;
        private int ticksSincePosition;

        public MovementSender(ServerSession server, LocalPlayer player)
        {
            this.server = server;
            this.player = player;
        }

        /// <summary>After a server teleport the confirmed position is the reference.</summary>
        public void Reset()
        {
            position = player.Position;
            yaw = player.Yaw;
            pitch = player.Pitch;
            onGround = player.OnGround;
            ticksSincePosition = 0;
        }

        public void Tick()
        {
            if (player.Sprinting != sprinting)
            {
                sprinting = player.Sprinting;
                server.SendPlayerCommand(sprinting ? 3 : 4);
            }
            var sneak = player.Pose == Pose.Sneaking;
            if (sneak != sneaking)
            {
                sneaking = sneak;
                server.SendPlayerCommand(sneaking ? 0 : 1);
            }
            if (player.Flying != flying)
            {
                flying = player.Flying;
                server.SendFlying(flying);
            }

            ticksSincePosition++;
            var current = player.Position;
            var moved = (current - position).LengthSquared > MinMovementSquared;
            var sendPosition = moved || ticksSincePosition >= MaxPositionInterval;
            var sendRotation = player.Yaw != yaw || player.Pitch != pitch;

            if (sendPosition && sendRotation) server.SendPositionRotation(current.X, current.Y, current.Z, player.Yaw, player.Pitch, player.OnGround);
            else if (sendPosition) server.SendPosition(current.X, current.Y, current.Z, player.OnGround);
            else if (sendRotation) server.SendRotation(player.Yaw, player.Pitch, player.OnGround);
            else if (player.OnGround != onGround) server.SendOnGround(player.OnGround);

            if (sendPosition)
            {
                ticksSincePosition = 0;
                position = current;
            }
            if (sendRotation)
            {
                yaw = player.Yaw;
                pitch = player.Pitch;
            }
            onGround = player.OnGround;
        }
    }
}
