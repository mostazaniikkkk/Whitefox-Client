// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace WhiteFox.Core.Assets
{
    /// <summary>A source of pack files addressed by their path inside the pack ("assets/minecraft/models/block/stone.json").</summary>
    public interface IAssetSource
    {
        string Name { get; }
        bool TryGet(string path, out byte[] data);
        IEnumerable<string> List(string prefix);
    }

    /// <summary>
    /// Layered asset lookup (first source wins), like Minosoft's PriorityAssetsManager:
    /// resource packs are added in front of the vanilla client jar.
    /// </summary>
    public sealed class AssetManager
    {
        private readonly List<IAssetSource> sources = new List<IAssetSource>();

        public IReadOnlyList<IAssetSource> Sources => sources;

        /// <summary>Adds a source with the highest priority.</summary>
        public void Push(IAssetSource source) => sources.Insert(0, source);

        /// <summary>Adds a source with the lowest priority.</summary>
        public void Append(IAssetSource source) => sources.Add(source);

        public bool TryGet(string path, out byte[] data)
        {
            foreach (var source in sources)
            {
                if (source.TryGet(path, out data)) return true;
            }
            data = null;
            return false;
        }

        public byte[] Get(string path)
        {
            if (TryGet(path, out var data)) return data;
            throw new FileNotFoundException("Asset not found: " + path);
        }

        public string GetText(string path) => Encoding.UTF8.GetString(Get(path));

        public bool TryGetText(string path, out string text)
        {
            if (TryGet(path, out var data))
            {
                text = Encoding.UTF8.GetString(data);
                return true;
            }
            text = null;
            return false;
        }

        public IEnumerable<string> List(string prefix)
        {
            var seen = new HashSet<string>();
            foreach (var source in sources)
            {
                foreach (var path in source.List(prefix))
                {
                    if (seen.Add(path)) yield return path;
                }
            }
        }
    }

    /// <summary>
    /// Zip backed source (client.jar or a zipped resource pack). Only entries below "assets/" are kept in memory,
    /// the same stripping Minosoft does for the client jar.
    /// </summary>
    public sealed class ZipAssetSource : IAssetSource
    {
        private readonly Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>();

        public string Name { get; }

        public ZipAssetSource(string name, byte[] zip, Func<string, bool> filter = null)
        {
            Name = name;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue;
                    if (!entry.FullName.StartsWith("assets/")) continue;
                    if (filter != null && !filter(entry.FullName)) continue;
                    using (var stream = entry.Open())
                    {
                        var buffer = new MemoryStream((int)entry.Length);
                        stream.CopyTo(buffer);
                        entries[entry.FullName] = buffer.ToArray();
                    }
                }
            }
        }

        public int Count => entries.Count;

        public bool TryGet(string path, out byte[] data) => entries.TryGetValue(path, out data);

        public IEnumerable<string> List(string prefix)
        {
            foreach (var key in entries.Keys)
            {
                if (key.StartsWith(prefix)) yield return key;
            }
        }
    }

    /// <summary>Files held in memory (for example downloaded asset index objects), keyed by pack path.</summary>
    public sealed class MemoryAssetSource : IAssetSource
    {
        private readonly Dictionary<string, byte[]> files;

        public string Name { get; }

        public MemoryAssetSource(string name, Dictionary<string, byte[]> files)
        {
            Name = name;
            this.files = files;
        }

        public bool TryGet(string path, out byte[] data) => files.TryGetValue(path, out data);

        public IEnumerable<string> List(string prefix)
        {
            foreach (var key in files.Keys)
            {
                if (key.StartsWith(prefix)) yield return key;
            }
        }
    }

    /// <summary>Unzipped resource pack folder.</summary>
    public sealed class DirectoryAssetSource : IAssetSource
    {
        private readonly string root;

        public string Name { get; }

        public DirectoryAssetSource(string root)
        {
            this.root = root;
            Name = Path.GetFileName(root);
        }

        public bool TryGet(string path, out byte[] data)
        {
            var file = Path.Combine(root, path);
            if (!File.Exists(file))
            {
                data = null;
                return false;
            }
            data = File.ReadAllBytes(file);
            return true;
        }

        public IEnumerable<string> List(string prefix)
        {
            var dir = Path.Combine(root, prefix);
            if (!Directory.Exists(dir)) yield break;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                yield return file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            }
        }
    }
}
