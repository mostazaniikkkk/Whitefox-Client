// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Core.Util
{
    /// <summary>Namespaced identifier ("minecraft:stone"). Port of Minosoft's ResourceLocation.</summary>
    public readonly struct ResourceLocation : IEquatable<ResourceLocation>
    {
        public const string DefaultNamespace = "minecraft";

        public readonly string Namespace;
        public readonly string Path;

        public ResourceLocation(string ns, string path)
        {
            Namespace = ns;
            Path = path;
        }

        public static ResourceLocation Of(string value)
        {
            var split = value.IndexOf(':');
            if (split < 0) return new ResourceLocation(DefaultNamespace, value);
            return new ResourceLocation(value.Substring(0, split), value.Substring(split + 1));
        }

        public static ResourceLocation Minecraft(string path) => new ResourceLocation(DefaultNamespace, path);

        /// <summary>Path inside a pack: assets/{namespace}/{prefix}{path}{suffix}</summary>
        public string ToAssetPath(string prefix, string suffix) => "assets/" + Namespace + "/" + prefix + Path + suffix;

        public ResourceLocation WithPrefix(string prefix) => new ResourceLocation(Namespace, prefix + Path);

        public bool Equals(ResourceLocation other) => Namespace == other.Namespace && Path == other.Path;
        public override bool Equals(object obj) => obj is ResourceLocation other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Namespace, Path);
        public override string ToString() => Namespace + ":" + Path;

        public static bool operator ==(ResourceLocation a, ResourceLocation b) => a.Equals(b);
        public static bool operator !=(ResourceLocation a, ResourceLocation b) => !a.Equals(b);
    }
}
