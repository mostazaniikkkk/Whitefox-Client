// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WhiteFox.Core.Game;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Models;
using WhiteFox.Core.Util;

namespace WhiteFox.Tests
{
    /// <summary>
    /// Smoke tests of the engine independent core: downloads/loads 1.20.4 assets, bakes every model, generates the flat
    /// world, meshes it and runs the player physics. Usage: dotnet run -- [cacheDir]
    /// </summary>
    public static class Program
    {
        private static int failures;

        public static int Main(string[] args)
        {
            var cache = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "cache");
            var resources = ClientResources.LoadAsync(cache, null, message => Console.WriteLine("  " + message)).GetAwaiter().GetResult();
            var srvIndex = Array.IndexOf(args, "--srv");
            if (srvIndex >= 0)
            {
                var resolved = WhiteFox.Core.Network.ServerAddress.ResolveAsync(args[srvIndex + 1]).GetAwaiter().GetResult();
                Console.WriteLine(args[srvIndex + 1] + " -> " + resolved.host + ":" + resolved.port);
                return 0;
            }
            var serverIndex = Array.IndexOf(args, "--server");
            if (serverIndex >= 0)
            {
                NetworkTest.Run(resources, args[serverIndex + 1], Check);
                Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
                return failures == 0 ? 0 : 1;
            }

            // --- registry ---
            var blocks = resources.Blocks;
            Check(blocks.GetState(0) == null, "air (id 0) is null");
            Check(blocks.GetState(1)?.Block.Name.Path == "stone", "state 1 is stone");
            var grass = blocks.Get("grass_block");
            Check(grass.DefaultState.Id == 9 && grass.DefaultState["snowy"] == "false", "grass_block default state is 9 [snowy=false]");
            var stairs = blocks.Get("oak_stairs");
            var stairState = stairs.DefaultState.With("facing", "east").With("half", "top");
            Check(stairState["facing"] == "east" && stairState["half"] == "top" && stairState.Block == stairs, "BlockState.With keeps the block and changes properties");
            Check(blocks.Get("stone").DefaultState.FullOpaque, "stone is full opaque");
            Check(!blocks.Get("glass").DefaultState.FullOpaque && blocks.Get("glass").DefaultState.FullSide[0], "glass is full but not opaque");
            Check(!blocks.Get("oak_leaves").DefaultState.FullOpaque, "leaves are not opaque");
            Check(blocks.Get("poppy").DefaultState.OutlineShape.Length == 1, "poppy got an outline shape from its model");
            Check(blocks.Get("oak_fence").DefaultState.CollisionShape[0].Max.Y > 1.4, "fence collision is 1.5 high");

            var stairModel = (BlockRenderModel)stairs.DefaultState.Model;
            Check(stairModel != null && stairModel.Models[0].Quads.Length > 6, "stairs baked with " + (stairModel?.Models[0].Quads.Length ?? 0) + " quads");
            var stone = (BlockRenderModel)blocks.Get("stone").DefaultState.Model;
            Check(stone.Models.Length >= 1 && stone.Models[0].Quads.Length == 6, "stone has 6 faces");

            var missing = 0;
            foreach (var entry in resources.Textures.Entries)
            {
                if (entry.Name != WhiteFox.Core.Textures.TextureManager.Missing && resources.Textures.Layers[entry.Layer].Length == 0) missing++;
            }
            Check(missing == 0, "all texture layers have data");
            Console.WriteLine($"  texture array: {resources.Textures.Layers.Count} layers @ {resources.Textures.Size}px, {resources.Textures.Animations.Count} animations");

            // --- world + light ---
            var session = new GameSession(resources, 4);
            session.Prepare();
            var world = session.World;
            Check(world.ChunkCount == 25, "prepare generated 5x5 chunks (" + world.ChunkCount + ")");
            Check(world.GetBlock(0, -61, 0)?.Block.Name.Path == "grass_block", "surface is grass at y=-61");
            Check(world.GetLight(0, -60, 0) >> 4 == 15, "sky light 15 above surface");
            Check((world.GetLight(1, -59, 16) & 0x0F) == 13, "torch block light spreads (" + (world.GetLight(1, -59, 16) & 0x0F) + ")");
            Check((world.GetLight(0, -59, 14) >> 4) < 15, "inside the hut there is no direct sky light (" + (world.GetLight(0, -59, 14) >> 4) + ")");

            // place a roof block and check that sky light below decreases and recovers
            var roof = new BlockPos(20, -55, 20);
            world.SetBlock(roof, blocks.Get("stone").DefaultState);
            Check(world.GetLight(20, -56, 20) >> 4 == 14, "sky light under a single block is 14 (" + (world.GetLight(20, -56, 20) >> 4) + ")");
            world.SetBlock(roof, null);
            Check(world.GetLight(20, -56, 20) >> 4 == 15, "sky light restored after removing it");

            // --- meshing ---
            var mesher = new SectionMesher(resources.Colors, resources.Fluids);
            var watch = Stopwatch.StartNew();
            var vertices = 0;
            var translucent = 0;
            var sections = 0;
            foreach (var chunk in world.Chunks)
            {
                if (!world.IsComplete(chunk.Position)) continue;
                for (var y = world.MinSection; y <= world.MaxSection; y++)
                {
                    var data = mesher.Mesh(SectionMesher.Snapshot(world, new SectionPos(chunk.Position.X, y, chunk.Position.Z)));
                    vertices += data.Vertices.Count;
                    translucent += data.TranslucentIndices.Count;
                    if (!data.IsEmpty) sections++;
                }
            }
            Console.WriteLine($"  meshed {sections} sections, {vertices} vertices in {watch.ElapsedMilliseconds} ms");
            var flatSection = mesher.Mesh(SectionMesher.Snapshot(world, new SectionPos(1, -4, 1)));
            Check(flatSection.Vertices.Count == 16 * 16 * 2 * 4, "a flat inner section only has top and bottom faces (" + flatSection.Vertices.Count / 4 + " quads)");
            Check(resources.Textures.Get("minecraft:block/white_stained_glass").Transparency == WhiteFox.Core.Textures.TextureTransparency.Translucent, "stained glass texture is translucent");
            Check(resources.Textures.Get("minecraft:block/glass").Transparency == WhiteFox.Core.Textures.TextureTransparency.Transparent, "glass texture is cutout");

            var poolSection = mesher.Mesh(SectionMesher.Snapshot(world, new SectionPos(-1, -4, 0)));
            Check(poolSection.WaterIndices.Count > 0, "water pool produces water geometry (" + poolSection.WaterIndices.Count / 6 + " triangles pairs)");
            Check(resources.Textures.NormalLayers.Count == resources.Textures.Layers.Count && resources.Textures.SpecularLayers.Count == resources.Textures.Layers.Count, "every layer has normal and specular data");
            var normalOk = true;
            foreach (var v in flatSection.Vertices)
            {
                var ny = (sbyte)((v.Normal >> 8) & 0xFF);
                if (v.Y > 3 && ny < 120) normalOk = false; // top faces point up
            }
            Check(normalOk, "top face normals point up");

            // --- physics ---
            var player = session.Player;
            player.Teleport(new Vec3d(8.5, -50, 8.5));
            for (var i = 0; i < 40; i++) session.Tick();
            Check(player.OnGround && Math.Abs(player.Position.Y - (-60)) < 1e-6, "player fell onto the ground at y=-60 (" + player.Position + ")");

            var start = player.Position;
            session.Input.Movement.Forwards = 1;
            for (var i = 0; i < 20; i++) session.Tick();
            var walked = (player.Position - start).Length;
            Check(walked > 4.0 && walked < 4.4, "walking speed ~4.3 blocks/s (" + walked.ToString("0.000") + ")");

            session.Input.Movement.Sprint = true;
            start = player.Position;
            for (var i = 0; i < 20; i++) session.Tick();
            walked = (player.Position - start).Length;
            Check(player.Sprinting && walked > 5.4 && walked < 5.8, "sprinting speed ~5.6 blocks/s (" + walked.ToString("0.000") + ")");
            session.Input.Movement = default;
            for (var i = 0; i < 20; i++) session.Tick();

            // jump height: vanilla max jump height is ~1.2522
            var groundY = player.Position.Y;
            session.Input.Movement.Jump = true;
            session.Tick();
            session.Input.Movement.Jump = false;
            var maxY = player.Position.Y;
            for (var i = 0; i < 20; i++)
            {
                session.Tick();
                maxY = Math.Max(maxY, player.Position.Y);
            }
            Check(Math.Abs(maxY - groundY - 1.2522) < 0.01, "jump height ~1.2522 (" + (maxY - groundY).ToString("0.0000") + ")");
            Check(player.OnGround, "landed again");

            // step up onto a slab, but not onto a full block
            player.Teleport(new Vec3d(30.5, -60, 30.5));
            for (var i = 0; i < 5; i++) session.Tick();
            world.SetBlock(new BlockPos(30, -60, 32), blocks.Get("stone_brick_slab").DefaultState);
            player.Yaw = 0;
            session.Input.Movement.Forwards = 1;
            var highest = player.Position.Y;
            for (var i = 0; i < 20; i++)
            {
                session.Tick();
                highest = Math.Max(highest, player.Position.Y);
            }
            Check(Math.Abs(highest - (-59.5)) < 1e-6 && player.Position.Z > 33, "stepped up onto a slab and walked over it (max y " + highest + ")");
            world.SetBlock(new BlockPos(30, -60, 36), blocks.Get("stone").DefaultState);
            world.SetBlock(new BlockPos(30, -59, 36), blocks.Get("stone").DefaultState);
            for (var i = 0; i < 40; i++) session.Tick();
            Check(player.Position.Z < 36 - 0.29, "blocked by a 2 high wall (" + player.Position + ")");
            session.Input.Movement = default;

            // --- water: sinking, swimming up, horizontal drag ---
            var water = blocks.Get("water").DefaultState;
            for (var x = 60; x < 66; x++)
            {
                for (var z = 60; z < 66; z++)
                {
                    for (var y = -63; y <= -58; y++) world.SetBlock(new BlockPos(x, y, z), water);
                }
            }
            player.Teleport(new Vec3d(62.5, -60, 62.5));
            player.Flying = false;
            for (var i = 0; i < 40; i++) session.Tick();
            Check(player.InWater && player.Position.Y < -60 && player.Velocity.Y > -0.1, "sinks slowly in water (y " + player.Position.Y.ToString("0.00") + ", vy " + player.Velocity.Y.ToString("0.000") + ")");
            session.Input.Movement.Jump = true;
            for (var i = 0; i < 80; i++) session.Tick();
            session.Input.Movement.Jump = false;
            Check(player.Position.Y > -59.5, "swims up holding jump (y " + player.Position.Y.ToString("0.00") + ")");
            var swimStart = player.Position;
            player.Yaw = 0;
            session.Input.Movement.Forwards = 1;
            for (var i = 0; i < 20; i++) session.Tick();
            session.Input.Movement = default;
            var swum = Math.Sqrt((player.Position - swimStart).HorizontalLengthSquared);
            Check(swum > 0.5 && swum < 3.0, "water slows walking (" + swum.ToString("0.00") + " blocks/s)");

            // --- ladder ---
            var ladder = blocks.Get("ladder").DefaultState.With("facing", "north");
            for (var y = -60; y < -54; y++)
            {
                world.SetBlock(new BlockPos(70, y, 71), blocks.Get("stone").DefaultState);
                world.SetBlock(new BlockPos(70, y, 70), ladder);
            }
            player.Teleport(new Vec3d(70.5, -60, 70.5));
            player.Yaw = 0;
            session.Input.Movement.Forwards = 1;
            for (var i = 0; i < 20; i++) session.Tick();
            session.Input.Movement = default;
            Check(player.Position.Y > -58.2, "climbs a ladder at ~2.3 blocks/s (y " + player.Position.Y.ToString("0.00") + ")");

            // --- elytra: gliding covers distance and falls much slower than a free fall ---
            {
                session.Input.Movement = default;
                var wasFlying = player.Flying;
                var wasMode = player.Gamemode;
                player.Gamemode = WhiteFox.Core.Physics.Gamemode.Survival;
                player.Flying = false;
                player.Teleport(new Vec3d(70.5, 0, 60.5)); // a loaded chunk, 60 blocks above the ground
                player.Yaw = 0;
                player.Pitch = 10;
                player.Velocity = new Vec3d(0, 0, 0.6);
                player.WearingElytra = true;
                player.FallFlying = true;
                var glideStart = player.Position;
                // the session ticks would read the (empty) chest slot: tick the physics directly
                for (var i = 0; i < 40; i++)
                {
                    player.WearingElytra = true;
                    session.Physics.Tick();
                }
                var glided = Math.Sqrt((player.Position - glideStart).HorizontalLengthSquared);
                var dropped = glideStart.Y - player.Position.Y;
                Check(player.FallFlying && glided > 15 && dropped < 15, "elytra glides " + glided.ToString("0.0") + " blocks while dropping " + dropped.ToString("0.0"));
                player.FallFlying = false;
                player.WearingElytra = false;
                player.Gamemode = wasMode;
                player.Flying = wasFlying;
            }

            // --- interaction ---
            player.Teleport(new Vec3d(40.5, -60, 40.5));
            player.Pitch = 90;
            session.Tick();
            Check(session.Target.HasValue && session.Target.Value.Position == new BlockPos(40, -61, 40) && session.Target.Value.Face == Direction.Up, "looking down targets the grass below");
            session.Input.AttackHeld = true;
            session.Input.AttackPressed = true;
            session.Tick();
            session.Input.AttackHeld = false;
            Check(world.GetBlock(40, -61, 40) == null, "creative break removes the block");

            // --- cave culling ---
            {
                Check(OcclusionGraph.Compute((x, y, z) => false) == OcclusionGraph.All, "empty section: every face sees every face");
                Check(OcclusionGraph.Compute((x, y, z) => true) == 0, "solid section: no face sees another");
                var tunnel = OcclusionGraph.Compute((x, y, z) => !(y == 8 && z == 8));
                Check(OcclusionGraph.Connected(tunnel, Direction.West, Direction.East) && !OcclusionGraph.Connected(tunnel, Direction.Up, Direction.Down)
                      && !OcclusionGraph.Connected(tunnel, Direction.West, Direction.Up), "tunnel along x connects only west and east");
                var ground = OcclusionGraph.Compute((x, y, z) => y < 8);
                Check(OcclusionGraph.Connected(ground, Direction.Up, Direction.North) && !OcclusionGraph.Connected(ground, Direction.Up, Direction.Down)
                      && !OcclusionGraph.Connected(ground, Direction.Down, Direction.Down), "half filled section: the top sees the sides, the bottom is closed");
                var sparse = OcclusionGraph.Compute((x, y, z) => y == 0 && x < 15);
                Check(sparse == OcclusionGraph.All, "fewer than 256 opaque blocks never occlude");

                // terrain: y 0..2 solid rock, y 3 the surface, y 4 air; a cave tunnel at y 1 along x
                ulong Graph(SectionPos p) => p.Y >= 4 ? OcclusionGraph.All : p.Y == 3 ? ground : p.Y == 1 && p.Z == 0 ? tunnel : 0;
                var culler = new OcclusionCuller();
                culler.Compute(new SectionPos(0, 4, 0), 0, 4, 4, c => true, Graph);
                Check(culler.Visible.Contains(new SectionPos(3, 3, -2)) && !culler.Visible.Contains(new SectionPos(0, 2, 0)) && !culler.Visible.Contains(new SectionPos(2, 1, 0)),
                    "from the surface the surface is visible, the rock and the cave below are culled (" + culler.Visible.Count + " visible)");
                culler.Compute(new SectionPos(0, 1, 0), 0, 4, 4, c => true, Graph);
                Check(culler.Visible.Contains(new SectionPos(4, 1, 0)) && culler.Visible.Contains(new SectionPos(-4, 1, 0)) && !culler.Visible.Contains(new SectionPos(0, 3, 2))
                      && !culler.Visible.Contains(new SectionPos(2, 1, 2)), "from the cave the tunnel is visible, the surface is culled (" + culler.Visible.Count + " visible)");
                culler.Compute(new SectionPos(0, 9, 0), 0, 4, 4, c => true, Graph);
                Check(culler.Visible.Contains(new SectionPos(1, 3, 1)) && !culler.Visible.Contains(new SectionPos(1, 2, 1)), "above the world the walk starts at the top layer");
                culler.Compute(new SectionPos(0, 4, 0), 0, 4, 4, c => c.X < 2, Graph);
                Check(!culler.Visible.Contains(new SectionPos(2, 4, 0)), "the walk stops at unloaded chunks");

                var cullWatch = Stopwatch.StartNew();
                culler.Compute(new SectionPos(0, 10, 0), -4, 19, 32, c => true, p => OcclusionGraph.All);
                cullWatch.Stop();
                Console.WriteLine($"  culling walk over {culler.Visible.Count} sections (32 chunks): {cullWatch.Elapsed.TotalMilliseconds:F1} ms");
            }

            // --- configuration (TOML) and online mode login crypto ---
            {
                var table = new WhiteFox.Core.Util.TomlTable { ["name"] = "Fox \"1\"\n", ["distance"] = 12L, ["fov"] = 70.5, ["bloom"] = true, ["packs"] = new List<object> { "a.zip", "b" } };
                table.Table("keys")["forward"] = "W";
                var accounts = new List<WhiteFox.Core.Util.TomlTable> { new WhiteFox.Core.Util.TomlTable { ["name"] = "one" }, new WhiteFox.Core.Util.TomlTable { ["name"] = "two" } };
                table["account"] = accounts;
                var text = WhiteFox.Core.Util.Toml.Write(table);
                var parsed = WhiteFox.Core.Util.Toml.Parse(text);
                Check(parsed.GetString("name") == "Fox \"1\"\n" && parsed.GetInt("distance", 0) == 12 && Math.Abs(parsed.GetDouble("fov", 0) - 70.5) < 1e-9 && parsed.GetBool("bloom", false), "TOML scalars round trip");
                Check(parsed.GetStrings("packs").Count == 2 && parsed.Table("keys").GetString("forward") == "W", "TOML arrays and tables round trip");
                Check(parsed.Tables("account").Count == 2 && parsed.Tables("account")[1].GetString("name") == "two", "TOML arrays of tables round trip");
                var handWritten = WhiteFox.Core.Util.Toml.Parse("# comment\nview = 8 # trailing\n[video]\nmode = 'modern'\nlist = [\n  1,\n  2, # two\n]\n[a.b]\nc = -3_000\n");
                Check(handWritten.GetInt("view", 0) == 8 && handWritten.Table("video").GetString("mode") == "modern" && handWritten.Table("video")["list"] is List<object> l && l.Count == 2 && handWritten.Table("a").Table("b").GetInt("c", 0) == -3000, "TOML written by hand (comments, literal strings, multi-line arrays, dotted tables)");

                string Hash(string name)
                {
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    return WhiteFox.Core.Network.Crypto.JavaHex(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(name)));
                }
                Check(Hash("Notch") == "4ed1f46bbe04bc756bcb17c0c7ce3e4632f06a48" && Hash("jeb_") == "-7c9d5b0044c130109a5d7b5fb5c317c02b4e28c1" && Hash("simon") == "88e16a1019277b15d58faf0541e11910eb756f6", "server hash matches the vanilla examples (negative and leading zero)");
                using var serverKey = System.Security.Cryptography.RSA.Create(1024);
                using var clientKey = WhiteFox.Core.Network.Crypto.PublicKey(serverKey.ExportSubjectPublicKeyInfo());
                var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
                var encrypted = clientKey.Encrypt(secret, System.Security.Cryptography.RSAEncryptionPadding.Pkcs1);
                Check(Convert.ToBase64String(serverKey.Decrypt(encrypted, System.Security.Cryptography.RSAEncryptionPadding.Pkcs1)) == Convert.ToBase64String(secret), "the shared secret encrypted with the parsed server key decrypts on the server");

                var path = Path.Combine(Path.GetTempPath(), "whitefox_accounts_test.toml");
                var store = new WhiteFox.Core.Accounts.AccountStore(path) { Protect = t => "x" + t, Unprotect = t => t.Substring(1) };
                store.AddOrReplace(WhiteFox.Core.Accounts.Account.Offline("Fox"));
                store.AddOrReplace(new WhiteFox.Core.Accounts.Account { Type = WhiteFox.Core.Accounts.AccountType.Microsoft, Name = "Premium", Uuid = Guid.NewGuid(), RefreshToken = "refresh", MinecraftToken = "mc", MinecraftTokenExpires = DateTime.UtcNow.AddHours(20) });
                store.Save();
                var loaded = new WhiteFox.Core.Accounts.AccountStore(path) { Protect = t => "x" + t, Unprotect = t => t.Substring(1) };
                loaded.Load();
                Check(loaded.Accounts.Count == 2 && loaded.Selected?.Name == "Premium" && loaded.Selected.RefreshToken == "refresh" && loaded.Selected.HasValidToken && !File.ReadAllText(path).Contains("\"refresh\""), "accounts are saved with protected tokens and stay logged in");
                File.Delete(path);
            }


            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        public static void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "[ OK ] " : "[FAIL] ") + message);
            if (!condition) failures++;
        }
    }
}
