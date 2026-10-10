// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Audio
{
    /// <summary>
    /// Sound playback (Minosoft's AudioPlayer + SoundManager, vanilla SoundEngine/MusicManager): sounds.json events,
    /// weighted random variants, linear 3D attenuation over max(volume, 1) * 16 blocks, category volumes, music with
    /// vanilla delays and cave ambience. The .ogg files come from the asset index and are only downloaded the first
    /// time they are played (then cached), resource packs can replace them.
    /// </summary>
    public sealed class SoundManager : IDisposable
    {
        private sealed class Entry
        {
            public string Name;
            public float Volume = 1;
            public float Pitch = 1;
            public int Weight = 1;
            public bool Stream;
            public bool IsEvent;
        }

        private struct Waiting
        {
            public SoundRequest Request;
            public Entry Entry;
            public float Time;
            public bool Music;
            public bool Record;
        }

        private const int MaxSources = 48;

        private readonly ClientResources resources;
        private readonly AssetDownloader downloader;
        private readonly Transform root;
        private readonly Dictionary<string, List<Entry>> events = new Dictionary<string, List<Entry>>();
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private readonly Dictionary<string, List<Waiting>> waiting = new Dictionary<string, List<Waiting>>();
        private readonly ConcurrentQueue<KeyValuePair<string, string>> downloaded = new ConcurrentQueue<KeyValuePair<string, string>>();
        private readonly List<AudioSource> sources = new List<AudioSource>();
        private readonly Dictionary<Block, string> blockSounds = new Dictionary<Block, string>();
        private readonly System.Random random = new System.Random();
        private AudioSource music;
        private float musicDelay = 100 / 20f;
        private string musicEvent;
        private float mood;

        /// <summary>Volume per <see cref="SoundCategory"/> (0 master .. 9 voice), from the settings.</summary>
        public float[] Volumes = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        public int Playing { get; private set; }
        public int Loaded => clips.Count;

        public SoundManager(ClientResources resources, Transform parent)
        {
            this.resources = resources;
            downloader = new AssetDownloader(resources.CacheDirectory);
            root = new GameObject("Sounds").transform;
            root.SetParent(parent, false);
            music = root.gameObject.AddComponent<AudioSource>();
            music.spatialBlend = 0;
            music.playOnAwake = false;
            LoadEvents();
        }

        private void LoadEvents()
        {
            if (!resources.Assets.TryGetText("assets/minecraft/sounds.json", out var text)) return;
            foreach (var pair in JObject.Parse(text))
            {
                var list = new List<Entry>();
                if (pair.Value["sounds"] is JArray sounds)
                {
                    foreach (var sound in sounds)
                    {
                        var entry = new Entry();
                        if (sound.Type == JTokenType.String)
                        {
                            entry.Name = (string)sound;
                        }
                        else
                        {
                            entry.Name = (string)sound["name"];
                            entry.Volume = (float?)sound["volume"] ?? 1;
                            entry.Pitch = (float?)sound["pitch"] ?? 1;
                            entry.Weight = Math.Max(1, (int?)sound["weight"] ?? 1);
                            entry.Stream = (bool?)sound["stream"] ?? false;
                            entry.IsEvent = (string)sound["type"] == "event";
                        }
                        if (entry.Name == null) continue;
                        if (entry.Name.StartsWith("minecraft:")) entry.Name = entry.Name.Substring(10);
                        list.Add(entry);
                    }
                }
                events[pair.Key] = list;
            }
            Log.Info("Loaded " + events.Count + " sound events");
        }

        public bool Exists(string name) => name != null && events.ContainsKey(name);

        // ------------------------------------------------------------------ block sound types

        private static readonly string[] Metal = { "iron", "gold", "diamond_block", "emerald_block", "lapis_block", "redstone_block", "cauldron", "hopper", "rail", "bell" };

        /// <summary>
        /// Vanilla SoundType of a block. minecraft-data has no sound groups, so the type is found from the name: the
        /// longest part of it that has block.{part}.break and .step events (deepslate_bricks, copper, grass...), then
        /// common materials, then the preferred tool.
        /// </summary>
        private static readonly string[] NetherWoodParts = { "planks", "door", "trapdoor", "fence", "fence_gate", "sign", "wall_sign", "slab", "stairs", "button", "pressure_plate" };
        private static readonly string[] Crops = { "wheat", "carrots", "potatoes", "beetroots", "melon_stem", "pumpkin_stem", "attached_melon_stem", "attached_pumpkin_stem", "torchflower_crop", "pitcher_crop" };

        /// <summary>
        /// Vanilla block SoundTypes whose name is not part of the block name (Blocks.java): the cherry, bamboo and
        /// nether woods, hanging signs, nether blocks and crops. Null when the name based search applies.
        /// </summary>
        private static string KnownSoundType(string name)
        {
            bool Is(params string[] names) => Array.IndexOf(names, name) >= 0;
            if (name.EndsWith("hanging_sign")) return name.StartsWith("cherry") ? "cherry_wood_hanging_sign" : name.StartsWith("bamboo") ? "bamboo_wood_hanging_sign" : name.StartsWith("crimson") || name.StartsWith("warped") ? "nether_wood_hanging_sign" : "hanging_sign";
            if (name == "cherry_leaves") return "cherry_leaves";
            if (name == "cherry_sapling" || name == "potted_cherry_sapling") return "cherry_sapling";
            if (name.StartsWith("cherry_")) return "cherry_wood";
            if (name == "bamboo") return "bamboo";
            if (name == "bamboo_sapling" || name == "potted_bamboo") return "bamboo_sapling";
            if (name.StartsWith("bamboo_")) return "bamboo_wood";
            if (name.StartsWith("crimson_") || name.StartsWith("warped_"))
            {
                var part = name.Substring(name.IndexOf('_') + 1);
                if (part == "stem" || part == "hyphae" || part.StartsWith("stripped")) return "stem";
                if (part == "nylium") return "nylium";
                if (part == "roots") return "roots";
                if (part == "fungus") return "fungus";
                if (part == "wart_block") return "wart_block";
                if (Array.IndexOf(NetherWoodParts, part) >= 0) return "nether_wood";
            }
            if (name.StartsWith("stripped_crimson") || name.StartsWith("stripped_warped")) return "stem";
            if (name == "nether_wart_block") return "wart_block";
            if (name == "nether_wart") return "nether_wart";
            if (Is("nether_gold_ore", "nether_quartz_ore")) return "nether_ore";
            if (name.Contains("nether_brick")) return "nether_bricks";
            if (name == "netherrack") return "netherrack";
            if (name.StartsWith("weeping_vines") || name.StartsWith("twisting_vines")) return "weeping_vines";
            if (Array.IndexOf(Crops, name) >= 0) return "crop";
            if (Is("cave_vines", "cave_vines_plant")) return "cave_vines";
            if (Is("kelp", "kelp_plant", "seagrass", "tall_seagrass")) return "wet_grass";
            if (name.EndsWith("_coral") || name.EndsWith("coral_fan") || name.EndsWith("coral_wall_fan")) return name.StartsWith("dead_") ? "stone" : "wet_grass";
            if (name.EndsWith("coral_block")) return name.StartsWith("dead_") ? "stone" : "coral_block";
            if (name.EndsWith("_wool") || name.EndsWith("_carpet")) return "wool";
            if (name.EndsWith("concrete_powder")) return "sand";
            return null;
        }

        /// <summary>The sound event of a block sound, with the vanilla exceptions of sound types that borrow events.</summary>
        private string BlockSoundEvent(string type, BlockSoundKind kind)
        {
            var suffix = kind.ToString().ToLowerInvariant();
            var name = "block." + type + "." + suffix;
            if (events.ContainsKey(name)) return name;
            // CROP and NETHER_WART plant with the item sound and walk like grass / stone (SoundType.CROP, NETHER_WART)
            if (kind == BlockSoundKind.Place && type == "crop") return "item.crop.plant";
            if (kind == BlockSoundKind.Place && type == "nether_wart") return "item.nether_wart.plant";
            var fallback = type == "crop" || type == "cave_vines" || type == "sweet_berry_bush" || type == "wet_grass" ? "grass" : type.EndsWith("hanging_sign") ? "wood" : type == "bamboo_sapling" ? "bamboo" : "stone";
            return "block." + fallback + "." + suffix;
        }

        public string SoundType(Block block)
        {
            if (blockSounds.TryGetValue(block, out var cached)) return cached;
            var name = block.Name.Path;
            var tokens = name.Split('_');
            var result = KnownSoundType(name);
            for (var length = tokens.Length; length > 0 && result == null; length--)
            {
                for (var start = 0; start + length <= tokens.Length; start++)
                {
                    var candidate = string.Join("_", tokens, start, length);
                    if (events.ContainsKey("block." + candidate + ".break") && events.ContainsKey("block." + candidate + ".step"))
                    {
                        result = candidate;
                        break;
                    }
                }
            }
            if (result == null)
            {
                bool Has(string part) => name.Contains(part);
                if (Has("stone") || Has("brick") || Has("ore") || Has("obsidian") || Has("cobble")) result = "stone";
                else if (Array.Exists(Metal, Has)) result = "metal";
                else if (Has("glass") || Has("ice") || Has("glowstone") || Has("sea_lantern")) result = "glass";
                else if (Has("carpet") || Has("wool")) result = "wool";
                else if (Has("sand")) result = "sand";
                else if (Has("snow")) result = "snow";
                else if (Has("leaves") || Has("sapling") || Has("flower") || Has("tulip") || Has("fern") || Has("bush") || Has("vine") || Has("sugar_cane") || Has("dandelion") || Has("poppy")) result = "grass";
                else if (Has("log") || Has("planks") || Has("wood") || Has("door") || Has("fence") || Has("sign") || Has("chest") || Has("table") || Has("barrel") || Has("bookshelf") || Has("_bed")) result = "wood";
                else if (Has("dirt") || Has("farmland") || Has("path") || Has("clay") || Has("podzol") || Has("mycelium")) result = "gravel";
                else if (block.Material != null && block.Material.Contains("axe") && !block.Material.Contains("pickaxe")) result = "wood";
                else if (block.Material != null && block.Material.Contains("shovel")) result = "gravel";
                else if (block.Material != null && block.Material.Contains("hoe")) result = "grass";
                else result = "stone";
            }
            blockSounds[block] = result;
            return result;
        }

        // ------------------------------------------------------------------ playback

        public void Play(SoundRequest request)
        {
            var name = request.Name;
            var volume = request.Volume;
            var pitch = request.Pitch;
            if (request.Block != null)
            {
                var type = SoundType(request.Block.Block);
                float typeVolume = type == "anvil" ? 0.3f : 1, typePitch = type == "metal" ? 1.5f : 1;
                switch (request.BlockKind)
                {
                    case BlockSoundKind.Break:
                    case BlockSoundKind.Place:
                        volume = (typeVolume + 1) / 2;
                        pitch = typePitch * 0.8f;
                        break;
                    case BlockSoundKind.Hit:
                        volume = (typeVolume + 1) / 8;
                        pitch = typePitch * 0.5f;
                        break;
                    case BlockSoundKind.Step:
                        volume = typeVolume * 0.15f;
                        pitch = typePitch;
                        break;
                    case BlockSoundKind.Fall:
                        volume = typeVolume * 0.5f;
                        pitch = typePitch * 0.75f;
                        break;
                }
                name = BlockSoundEvent(type, request.BlockKind);
                request.Category = SoundCategory.Block;
            }
            var entry = Pick(name, 0);
            if (entry == null) return;
            request.Volume = volume * entry.Volume;
            request.Pitch = pitch * entry.Pitch;
            Load(entry, new Waiting { Request = request, Entry = entry, Time = Time.unscaledTime });
        }

        /// <summary>Weighted random sound of an event; "event" entries refer to other events (vanilla WeighedSoundEvents).</summary>
        private Entry Pick(string name, int depth)
        {
            if (name == null || depth > 8 || !events.TryGetValue(name, out var list) || list.Count == 0) return null;
            var total = 0;
            foreach (var entry in list) total += entry.Weight;
            var roll = random.Next(total);
            foreach (var entry in list)
            {
                roll -= entry.Weight;
                if (roll >= 0) continue;
                if (!entry.IsEvent) return entry;
                var nested = Pick(entry.Name, depth + 1);
                if (nested == null) return null;
                return new Entry { Name = nested.Name, Volume = nested.Volume * entry.Volume, Pitch = nested.Pitch * entry.Pitch, Stream = nested.Stream };
            }
            return null;
        }

        private void Load(Entry entry, Waiting request)
        {
            var file = entry.Name;
            if (clips.TryGetValue(file, out var clip))
            {
                if (clip != null) Start(clip, request);
                return;
            }
            if (waiting.TryGetValue(file, out var list))
            {
                list.Add(request);
                return;
            }
            waiting[file] = new List<Waiting> { request };

            // a resource pack can replace the file; otherwise it is downloaded from the asset index (once)
            var path = "assets/minecraft/sounds/" + file + ".ogg";
            if (resources.Assets.TryGet(path, out var data))
            {
                var cached = Path.Combine(resources.CacheDirectory, "packsounds", Hashing.Sha1(data) + ".ogg");
                Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cached));
                    if (!File.Exists(cached)) File.WriteAllBytes(cached, data);
                    downloaded.Enqueue(new KeyValuePair<string, string>(file, cached));
                });
                return;
            }
            if (!resources.SoundFiles.TryGetValue("minecraft/sounds/" + file + ".ogg", out var hash))
            {
                clips[file] = null;
                waiting.Remove(file);
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    downloaded.Enqueue(new KeyValuePair<string, string>(file, await downloader.GetIndexObjectFileAsync(hash).ConfigureAwait(false)));
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not download sound " + file + ": " + exception.Message);
                    downloaded.Enqueue(new KeyValuePair<string, string>(file, null));
                }
            });
        }

        /// <summary>Decodes a downloaded file with Unity's ogg vorbis decoder and plays everything that waited for it.</summary>
        private void Decode(string file, string path)
        {
            if (path == null)
            {
                clips[file] = null;
                waiting.Remove(file);
                return;
            }
            var stream = waiting.TryGetValue(file, out var list) && list.Exists(w => w.Entry.Stream);
            var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = stream;
            request.SendWebRequest().completed += _ =>
            {
                AudioClip clip = null;
                if (request.result == UnityWebRequest.Result.Success) clip = DownloadHandlerAudioClip.GetContent(request);
                else Log.Warn("Can not decode sound " + file + ": " + request.error);
                request.Dispose();
                if (clip != null) clip.name = file;
                clips[file] = clip;
                if (!waiting.TryGetValue(file, out var pending)) return;
                waiting.Remove(file);
                if (clip == null) return;
                foreach (var waitingRequest in pending)
                {
                    // short sounds that arrive too late are dropped (a step a second later sounds wrong); music always plays
                    if (waitingRequest.Music || waitingRequest.Record || Time.unscaledTime - waitingRequest.Time < 1.5f) Start(clip, waitingRequest);
                }
            };
        }

        private void Start(AudioClip clip, Waiting request)
        {
            if (request.Record)
            {
                if (!recordWaiting.TryGetValue(request.Entry.Name, out var record) || record == null) return;
                recordWaiting.Remove(request.Entry.Name);
                record.clip = clip;
                record.volume = CategoryVolume(SoundCategory.Record);
                record.Play();
                return;
            }
            if (request.Music)
            {
                music.clip = clip;
                music.volume = CategoryVolume(SoundCategory.Music);
                music.Play();
                return;
            }
            var source = FreeSource();
            if (source == null) return;
            var sound = request.Request;
            source.clip = clip;
            source.pitch = Mathf.Clamp(sound.Pitch, 0.5f, 2.0f);
            source.volume = Mathf.Clamp01(sound.Volume) * CategoryVolume(sound.Category);
            if (sound.Position.HasValue)
            {
                var p = sound.Position.Value;
                source.transform.position = RenderOrigin.ToRender(p.X, p.Y, p.Z);
                source.spatialBlend = 1;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 0.5f;
                source.maxDistance = Mathf.Max(sound.Volume, 1) * 16;
                source.dopplerLevel = 0;
            }
            else
            {
                source.spatialBlend = 0;
            }
            source.Play();
        }

        private float CategoryVolume(SoundCategory category) => Volumes[0] * (category == SoundCategory.Master ? 1 : Volumes[(int)category]);

        private AudioSource FreeSource()
        {
            foreach (var source in sources)
            {
                if (!source.isPlaying) return source;
            }
            if (sources.Count >= MaxSources) return null;
            var created = new GameObject("Sound").AddComponent<AudioSource>();
            created.transform.SetParent(root, false);
            created.playOnAwake = false;
            sources.Add(created);
            return created;
        }

        // ------------------------------------------------------------------ per frame

        /// <param name="session">The running session, or null on the title screen.</param>
        public void Update(GameSession session)
        {
            while (downloaded.TryDequeue(out var result)) Decode(result.Key, result.Value);
            var playing = 0;
            foreach (var source in sources)
            {
                if (source.isPlaying) playing++;
            }
            Playing = playing;
            if (music.isPlaying) music.volume = CategoryVolume(SoundCategory.Music);
            UpdateMusic(session);
            if (session != null && session.Spawned) UpdateAmbience(session);
        }

        /// <summary>
        /// Vanilla MusicManager + Minecraft.getSituationalMusic: menu, the End (dragon fight with the boss bar flag),
        /// under water in oceans and rivers, creative (outside the Nether), else the biome's music or the game music.
        /// Records playing nearby pause it.
        /// </summary>
        private void UpdateMusic(GameSession session)
        {
            string wanted;
            int minDelay = 12000, maxDelay = 24000;
            var replaces = false;
            if (session == null)
            {
                wanted = "music.menu";
                minDelay = 20;
                maxDelay = 600;
                replaces = true;
            }
            else if (session.Dimension.Effects == "minecraft:the_end")
            {
                var dragon = (session.ServerUi.BossFlags & 0x02) != 0;
                wanted = dragon ? "music.dragon" : "music.end";
                minDelay = dragon ? 0 : 6000;
                replaces = dragon;
            }
            else
            {
                var eye = session.Player.EyePosition;
                var biome = session.World.GetBiome((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
                var underwater = session.Player.EyeInWater && (biome.Name.Contains("ocean") || biome.Name.Contains("river"));
                if (underwater)
                {
                    wanted = "music.under_water";
                    replaces = true;
                }
                else if (session.Dimension.Effects != "minecraft:the_nether" && session.Player.Gamemode == Core.Physics.Gamemode.Creative)
                {
                    wanted = "music.creative";
                }
                else if (biome.Music != null)
                {
                    wanted = biome.Music;
                    minDelay = biome.MusicMinDelay;
                    maxDelay = biome.MusicMaxDelay;
                    replaces = biome.MusicReplaces;
                }
                else
                {
                    wanted = "music.game";
                }
            }
            if (records.Count > 0)
            {
                if (music.isPlaying) music.Pause();
                return;
            }
            if (music.clip != null && !music.isPlaying && music.time > 0 && musicEvent == wanted)
            {
                music.UnPause();
                return;
            }
            if (music.isPlaying)
            {
                if (wanted == musicEvent || !replaces && musicEvent != "music.menu") return;
                music.Stop(); // menu music stops when joining a world, biome music can replace the current one
                musicDelay = Mathf.Min(musicDelay, 5);
            }
            if (CategoryVolume(SoundCategory.Music) <= 0) return;
            musicDelay -= Time.unscaledDeltaTime;
            if (musicDelay > 0) return;
            var entry = Pick(wanted, 0);
            musicDelay = UnityEngine.Random.Range(minDelay, Math.Max(minDelay + 1, maxDelay)) / 20f;
            if (entry == null) return;
            musicEvent = wanted;
            entry.Stream = true;
            Load(entry, new Waiting { Entry = entry, Music = true, Time = Time.unscaledTime });
        }

        /// <summary>Cave ambience (vanilla AmbientSoundHandler mood): darkness without sky builds up mood, at 100% a cave sound.</summary>
        private void UpdateAmbience(GameSession session)
        {
            var eye = session.Player.EyePosition;
            var light = session.World.GetLight((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
            var ticks = Time.deltaTime * 20;
            if ((light >> 4) == 0 && (light & 0x0F) < 3) mood += ticks / 6000f;
            else mood = Mathf.Max(0, mood - ticks / 600f);
            if (mood < 1) return;
            mood = 0;
            var offset = new Vec3d(random.Next(-8, 9), random.Next(-8, 9), random.Next(-8, 9));
            session.PlaySound("ambient.cave", eye + offset, 0.7f, 0.8f + (float)random.NextDouble() * 0.2f, SoundCategory.Ambient);
        }

        private readonly Dictionary<Core.Util.BlockPos, AudioSource> records = new Dictionary<Core.Util.BlockPos, AudioSource>();

        /// <summary>Jukebox records: a positional sound heard 64 blocks away, one per jukebox; null stops it.</summary>
        public void Record(Core.Util.BlockPos position, string sound)
        {
            if (records.TryGetValue(position, out var playing))
            {
                if (playing != null)
                {
                    playing.Stop();
                    UnityEngine.Object.Destroy(playing.gameObject);
                }
                records.Remove(position);
            }
            if (sound == null) return;
            var entry = Pick(sound, 0);
            if (entry == null) return;
            var source = new GameObject("Record " + position).AddComponent<AudioSource>();
            source.transform.SetParent(root, false);
            source.transform.position = RenderOrigin.ToRender(position.X + 0.5, position.Y + 0.5, position.Z + 0.5);
            source.spatialBlend = 1;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1;
            source.maxDistance = 64;
            source.dopplerLevel = 0;
            source.playOnAwake = false;
            records[position] = source;
            entry.Stream = true;
            recordWaiting[entry.Name] = source;
            Load(entry, new Waiting { Entry = entry, Music = false, Record = true, Time = Time.unscaledTime });
        }

        private readonly Dictionary<string, AudioSource> recordWaiting = new Dictionary<string, AudioSource>();

        public void PlayUi(string name) => Play(new SoundRequest { Name = name, Volume = 0.25f, Pitch = 1, Category = SoundCategory.Master });

        public void StopAll()
        {
            foreach (var source in sources) source.Stop();
            foreach (var record in records.Values)
            {
                if (record != null) UnityEngine.Object.Destroy(record.gameObject);
            }
            records.Clear();
        }

        public void Dispose()
        {
            downloader.Dispose();
            foreach (var clip in clips.Values)
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }
    }
}
