// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Threading;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;
using WhiteFox.Core.Physics;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Tests
{
    /// <summary>Integration test against a running offline mode 1.20.4 server: dotnet run -- ./cache --server host:port</summary>
    public static class NetworkTest
    {
        public static int Run(ClientResources resources, string address, Action<bool, string> check)
        {
            var split = address.Split(':');
            var host = split[0];
            var port = split.Length > 1 ? int.Parse(split[1]) : 25565;

            var status = ServerStatus.PingAsync(host, port).GetAwaiter().GetResult();
            Console.WriteLine($"  status: {status.VersionName} ({status.ProtocolVersion}) {status.OnlinePlayers}/{status.MaxPlayers} \"{status.Motd}\" {status.PingMilliseconds} ms");
            check(status.Compatible, "server list ping: protocol " + status.ProtocolVersion);

            var server = new ServerSession(host, port, "WhiteFoxBot", resources.Blocks, resources.Items, resources.EntityTypes);
            var session = new GameSession(resources, 6, server);
            server.ConnectAsync().GetAwaiter().GetResult();

            var tickTime = new Stopwatch();
            bool Run(Func<bool> condition, int maxTicks)
            {
                for (var i = 0; i < maxTicks; i++)
                {
                    if (session.Dead)
                    {
                        Console.WriteLine("  died (" + session.DeathMessage + "), respawning");
                        session.Respawn();
                    }
                    tickTime.Start();
                    session.Tick();
                    tickTime.Stop();
                    if (session.DisconnectReason != null) return false;
                    if (condition()) return true;
                    Thread.Sleep(50);
                }
                return false;
            }

            var watch = Stopwatch.StartNew();
            var spawned = Run(() => session.Spawned && session.World.ChunkCount >= 100, 600);
            check(spawned, $"joined and received chunks: {session.World.ChunkCount} chunks in {watch.ElapsedMilliseconds} ms (game thread busy {tickTime.ElapsedMilliseconds} ms), state {server.State}, {server.BytesReceived / 1024} KiB" + (session.DisconnectReason != null ? " (disconnected: " + session.DisconnectReason + ")" : ""));
            if (!spawned) return 1;

            var player = session.Player;
            Console.WriteLine($"  spawn {player.Position} in {session.DimensionName}, world y {session.World.MinY}..{session.World.MaxY}, gamemode {player.Gamemode}");
            Run(() => player.OnGround || player.Flying, 200);
            var below = session.World.GetBlock(BlockPos.Floor(player.Position.X, player.Position.Y - 0.5, player.Position.Z));
            check(player.Flying || player.OnGround && below != null, "standing on " + below + " at " + player.Position + (player.Flying ? " (flying, kept from the last session)" : ""));

            session.SendChat("hello from whitefox");
            check(Run(() => session.Chat.Exists(line => line.Text.Contains("hello from whitefox")), 100), "chat message echoed by the server");

            session.SendChat("/gamemode creative");
            check(Run(() => player.Gamemode == Gamemode.Creative, 100), "command /gamemode creative applied (" + player.Gamemode + ")");

            // walk; the server must accept the movement (no correction teleports)
            // move to a flat stone platform in the air, away from trees and hills
            session.SendChat("/tp @s 1015 152 1015");
            Run(() => Math.Abs(player.Position.X - 1015) < 1 && session.World.GetChunk(new ChunkPos(63, 63)) != null, 200);
            session.SendChat("/fill 990 150 990 1040 150 1040 minecraft:stone");
            Run(() => session.World.GetBlock(1015, 150, 1015) != null, 100);
            player.Yaw = 0;
            player.Flying = false;
            Run(() => player.OnGround, 100);
            Run(() => false, 20); // late teleport packets of the /tp
            var teleports = session.TeleportCount;
            var start = player.Position;
            session.Input.Movement.Forwards = 1;
            Run(() => false, 40);
            session.Input.Movement = default;
            Run(() => false, 10);
            var walked = (player.Position - start).HorizontalLengthSquared;
            check(session.TeleportCount == teleports && walked > 1, $"walked {Math.Sqrt(walked):0.00} blocks without being corrected (teleports {session.TeleportCount - teleports})");

            // sprint and sneak in survival, on the ground
            session.SendChat("/gamemode survival");
            Run(() => player.Gamemode == Gamemode.Survival, 60);
            Run(() => player.OnGround, 100);
            session.Input.Movement.Forwards = 1;
            session.Input.Movement.Sprint = true;
            Run(() => false, 5);
            var sprintStart = player.Position;
            Run(() => false, 20);
            var sprinted = Math.Sqrt((player.Position - sprintStart).HorizontalLengthSquared);
            check(player.Sprinting || player.HorizontalCollision, $"sprinting={player.Sprinting} (collision={player.HorizontalCollision}), {sprinted:0.00} blocks/s, food {session.Food}");
            session.Input.Movement = default;
            session.Input.Movement.Sneak = true;
            Run(() => false, 5);
            check(player.Pose == Pose.Sneaking && Math.Abs(player.EyeHeight - LocalPlayer.SneakingEyeHeight) < 1e-6, "sneaking pose " + player.Pose + ", eye height " + player.EyeHeight);
            session.Input.Movement = default;

            session.SendChat("/gamemode creative");
            Run(() => player.Gamemode == Gamemode.Creative, 60);

            // break the block below in creative and check that the server keeps it broken
            player.Flying = false;
            player.Pitch = 90;
            Run(() => player.OnGround, 100);
            session.UpdateTarget();
            if (session.Target.HasValue)
            {
                var target = session.Target.Value.Position;
                session.Input.AttackPressed = true;
                session.Input.AttackHeld = true;
                Run(() => true, 1);
                session.Input.AttackHeld = false;
                Run(() => false, 30);
                check(session.World.GetBlock(target) == null || player.Position.Y < target.Y + 1, "broken block " + target + " stays broken: " + session.World.GetBlock(target));
            }
            else
            {
                check(false, "no block targeted below the player");
            }

            // player features in survival: tools, effects, experience
            session.SendChat("/gamemode survival");
            session.SendChat("/clear @s");
            session.SendChat("/give @s diamond_pickaxe");
            session.SendChat("/effect give @s minecraft:speed 60 1");
            session.SendChat("/xp set @s 5 levels");
            Run(() => player.Gamemode == Gamemode.Survival && Array.IndexOf(session.HotbarItems, "diamond_pickaxe") >= 0 && session.ExperienceLevel == 5 && player.EffectLevel(Effects.Speed) == 2, 100);
            session.SelectedSlot = Array.IndexOf(session.HotbarItems, "diamond_pickaxe");
            Run(() => player.OnGround, 60);
            var stoneProgress = session.DestroyProgress(resources.Blocks.Get("stone").DefaultState);
            check(Math.Abs(stoneProgress - 8.0f / 1.5f / 30.0f) < 1e-4, "diamond pickaxe mines stone at vanilla speed (" + stoneProgress.ToString("0.0000") + "/tick)");
            check(player.EffectLevel(Effects.Speed) == 2, "speed II effect received");
            check(session.ExperienceLevel == 5, "experience level " + session.ExperienceLevel);
            session.Input.DropPressed = true;
            Run(() => true, 1);
            Run(() => false, 10);
            check(Array.IndexOf(session.HotbarItems, "diamond_pickaxe") < 0, "Q dropped the pickaxe");

            // entities: summon a cow in front of the player, look at it, attack it
            session.SendChat("/kill @e[type=!player]");
            Run(() => false, 10);
            var p = player.Position;
            session.SendChat($"/summon cow {p.X:0.0} {p.Y:0.0} {p.Z + 2:0.0} {{NoAI:1b}}".Replace(',', '.'));
            WhiteFox.Core.Entities.Entity cow = null;
            Run(() =>
            {
                foreach (var entity in session.Entities.Entities.Values)
                {
                    if (entity.Type?.Name.Path == "cow" && Math.Abs(entity.Position.X - p.X) < 1.5 && Math.Abs(entity.Position.Z - (p.Z + 2)) < 1.5) cow = entity;
                }
                return cow != null;
            }, 100);
            check(cow != null, "cow spawned as entity " + cow?.Id + " (" + session.Entities.Entities.Count + " entities known)");
            if (cow != null)
            {
                player.Yaw = 0;
                player.Pitch = 20;
                Run(() => false, 5);
                check(session.TargetEntity == cow, "the cow is targeted (" + (session.TargetEntity?.Type?.Name.Path ?? "nothing") + ")");
                session.Input.AttackPressed = true;
                session.Input.AttackHeld = true;
                Run(() => true, 1);
                session.Input.AttackHeld = false;
                check(Run(() => cow.HurtTime > 0, 40), "attacking the cow hurts it (hurt " + cow.HurtTime + ", health " + cow.Health + ")");
                session.SendChat($"/tp @e[type=cow] {p.X + 3:0.0} {p.Y:0.0} {p.Z + 2:0.0}".Replace(',', '.'));
                check(Run(() => Math.Abs(cow.Position.X - (p.X + 3)) < 0.2, 60), "the cow moved with the server (" + cow.Position + ")");
            }
            var players = 0;
            foreach (var info in session.Entities.Players.Values) players++;
            check(players >= 1, "tab list has " + players + " player(s)");

            check(session.DisconnectReason == null, "still connected: " + (session.DisconnectReason ?? "yes"));
            // leave the server in a known state for the next run
            session.SendChat("/fill 990 150 990 1040 150 1040 minecraft:stone");
            session.SendChat("/tp @s 1015 151 1015");
            session.SendChat("/gamemode creative");
            Run(() => false, 20);
            server.Disconnect("Test finished");
            return 0;
        }
    }
}
