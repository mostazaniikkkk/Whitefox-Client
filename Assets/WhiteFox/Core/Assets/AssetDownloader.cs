// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Assets
{
    /// <summary>
    /// Downloads everything the client needs and keeps it in a content addressed cache ({cache}/objects/ab/abcdef...),
    /// verified by SHA-1 like Minosoft's FileAssetsUtil. Nothing from Mojang is shipped with the project.
    /// </summary>
    public sealed class AssetDownloader : IDisposable
    {
        public const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

        // minecraft-data (MIT, https://github.com/PrismarineJS/minecraft-data) replaces Minosoft's PixLyzer data.
        // Pinned to a commit and verified by hash so the block state ids can never silently change.
        public const string MinecraftDataCommit = "33a0f3e7323e124a81960a6d6c62df797d69cd85";
        public static readonly RemoteFile BlocksJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.4/blocks.json",
            "5976fe6408c7cb87e86df8e431c0bf20ff9a7c63");
        public static readonly RemoteFile CollisionShapesJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/blockCollisionShapes.json",
            "29cdbb82b2c447d1960303833e1e99035beef8f1");

        public static readonly RemoteFile ItemsJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/items.json",
            "1f60de2310568ecddae0412c0e96f26dfaf856b7");

        public static readonly RemoteFile MaterialsJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/materials.json",
            "10b2291a87ec96e3a3799f1e4a1f01df018e8bf5");
        public static readonly RemoteFile FoodsJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/foods.json",
            "49549fa921c68b79dc9819564b17b8c647211a28");

        public static readonly RemoteFile EntitiesJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/entities.json",
            "a373175d164497b5d805f82787d1d53bb2201241");

        public static readonly RemoteFile SoundsJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.4/sounds.json",
            "736c787481cbc3b5df2dad363f38266b19116dcc");

        public static readonly RemoteFile ParticlesJson = new RemoteFile(
            "https://raw.githubusercontent.com/PrismarineJS/minecraft-data/" + MinecraftDataCommit + "/data/pc/1.20.3/particles.json",
            "b1faf41d6b076fa4f7cf9360dd6c98fd6f2faef9");

        private readonly HttpClient http;
        private readonly string cacheRoot;

        /// <summary>Optional progress callback (message).</summary>
        public Action<string> Progress;

        public AssetDownloader(string cacheRoot)
        {
            this.cacheRoot = cacheRoot;
            Directory.CreateDirectory(cacheRoot);
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WhiteFox/0.1");
        }

        public string ObjectPath(string sha1) => Path.Combine(cacheRoot, "objects", sha1.Substring(0, 2), sha1);

        /// <summary>Returns the verified file content from the cache or downloads it.</summary>
        public async Task<byte[]> GetVerifiedAsync(RemoteFile file, CancellationToken cancel = default)
        {
            var path = ObjectPath(file.Sha1);
            if (File.Exists(path))
            {
                var cached = File.ReadAllBytes(path);
                if (Hashing.Sha1(cached) == file.Sha1) return cached;
                Log.Warn("Cached file " + path + " is corrupted, downloading again");
                File.Delete(path);
            }

            Progress?.Invoke("Downloading " + file.Url);
            var data = await http.GetByteArrayAsync(file.Url).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            var hash = Hashing.Sha1(data);
            if (hash != file.Sha1) throw new InvalidDataException($"Hash mismatch for {file.Url}: expected {file.Sha1}, got {hash}");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return data;
        }

        /// <summary>Resolves the version json of a release (cached after the first successful lookup).</summary>
        public async Task<JObject> GetVersionJsonAsync(string version, CancellationToken cancel = default)
        {
            var pointer = Path.Combine(cacheRoot, "versions", version + ".sha1");
            var url = Path.Combine(cacheRoot, "versions", version + ".url");
            if (File.Exists(pointer) && File.Exists(url))
            {
                var cached = new RemoteFile(File.ReadAllText(url).Trim(), File.ReadAllText(pointer).Trim());
                var data = await GetVerifiedAsync(cached, cancel).ConfigureAwait(false);
                return JObject.Parse(System.Text.Encoding.UTF8.GetString(data));
            }

            Progress?.Invoke("Fetching version manifest");
            var manifest = JObject.Parse(await http.GetStringAsync(VersionManifestUrl).ConfigureAwait(false));
            foreach (var entry in (JArray)manifest["versions"])
            {
                if ((string)entry["id"] != version) continue;
                var file = new RemoteFile((string)entry["url"], (string)entry["sha1"]);
                var data = await GetVerifiedAsync(file, cancel).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(pointer));
                File.WriteAllText(pointer, file.Sha1);
                File.WriteAllText(url, file.Url);
                return JObject.Parse(System.Text.Encoding.UTF8.GetString(data));
            }
            throw new ArgumentException("Unknown minecraft version: " + version);
        }

        /// <summary>Downloads (or loads) the client jar of a version. Only its assets are ever used, no game code is executed.</summary>
        public async Task<byte[]> GetClientJarAsync(string version, CancellationToken cancel = default)
        {
            var json = await GetVersionJsonAsync(version, cancel).ConfigureAwait(false);
            var client = json["downloads"]["client"];
            return await GetVerifiedAsync(new RemoteFile((string)client["url"], (string)client["sha1"]), cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads files of the version's asset index (resources.download.minecraft.net, Minosoft's IndexAssetsManager).
        /// Keys are index names like "minecraft/textures/gui/title/background/panorama_0.png"; missing names are skipped.
        /// </summary>
        public async Task<System.Collections.Generic.Dictionary<string, byte[]>> GetIndexAssetsAsync(string version, System.Collections.Generic.IEnumerable<string> names, CancellationToken cancel = default)
        {
            var objects = await GetIndexObjectsAsync(version, cancel).ConfigureAwait(false);

            var result = new System.Collections.Generic.Dictionary<string, byte[]>();
            var downloads = new System.Collections.Generic.List<Task>();
            foreach (var name in names)
            {
                var entry = objects[name];
                if (entry == null) continue;
                var hash = (string)entry["hash"];
                var file = new RemoteFile("https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash, hash);
                downloads.Add(Task.Run(async () =>
                {
                    var data = await GetVerifiedAsync(file, cancel).ConfigureAwait(false);
                    lock (result) result[name] = data;
                }, cancel));
            }
            await Task.WhenAll(downloads).ConfigureAwait(false);
            return result;
        }

        /// <summary>The "objects" of the version's asset index: name to { hash, size }.</summary>
        public async Task<JObject> GetIndexObjectsAsync(string version, CancellationToken cancel = default)
        {
            var json = await GetVersionJsonAsync(version, cancel).ConfigureAwait(false);
            var indexInfo = json["assetIndex"];
            var indexData = await GetVerifiedAsync(new RemoteFile((string)indexInfo["url"], (string)indexInfo["sha1"]), cancel).ConfigureAwait(false);
            return (JObject)JObject.Parse(System.Text.Encoding.UTF8.GetString(indexData))["objects"];
        }

        /// <summary>Downloads one object of the asset index (sounds are fetched on demand) and returns its cache path.</summary>
        public async Task<string> GetIndexObjectFileAsync(string hash, CancellationToken cancel = default)
        {
            await GetVerifiedAsync(new RemoteFile("https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash, hash), cancel).ConfigureAwait(false);
            return ObjectPath(hash);
        }

        public void Dispose() => http.Dispose();
    }

    public readonly struct RemoteFile
    {
        public readonly string Url;
        public readonly string Sha1;

        public RemoteFile(string url, string sha1)
        {
            Url = url;
            Sha1 = sha1;
        }
    }
}
