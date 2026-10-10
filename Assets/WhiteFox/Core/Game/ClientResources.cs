// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Data;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Models;
using WhiteFox.Core.Textures;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Game
{
    /// <summary>
    /// Everything that is loaded once per version (Minosoft's PlaySession.connect → registries + assets + RenderLoader):
    /// client jar assets, block registry, baked models and the block texture array.
    /// Engine independent, so it can run on a worker thread.
    /// </summary>
    public sealed class ClientResources
    {
        public const string Version = "1.20.4";

        public AssetManager Assets;
        public BlockRegistry Blocks;
        public ItemRegistry Items;
        public ItemProperties ItemProperties;
        public EntityTypeRegistry EntityTypes;
        /// <summary>minecraft:sound_event registry (id to name without namespace).</summary>
        public string[] SoundEvents;
        /// <summary>minecraft:particle_type registry (id to name without namespace).</summary>
        public string[] ParticleTypes;
        /// <summary>Sound files of the asset index ("minecraft/sounds/....ogg" to SHA-1), downloaded when first played.</summary>
        public System.Collections.Generic.Dictionary<string, string> SoundFiles = new System.Collections.Generic.Dictionary<string, string>();
        public string CacheDirectory;
        /// <summary>block/destroy_stage_0..9 (breaking cracks).</summary>
        public Textures.TextureEntry[] DestroyStages = new Textures.TextureEntry[10];
        public TextureManager Textures;
        /// <summary>The block model an item model inherits from (block items), baked: icons and held blocks use it.</summary>
        public System.Collections.Generic.Dictionary<string, BakedModel> ItemBlockModels = new System.Collections.Generic.Dictionary<string, BakedModel>();
        public BiomeColors Colors;
        public FluidTextures Fluids;

        /// <summary>The jar and asset index sources; resource packs are stacked on top of them by <see cref="Reload"/>.</summary>
        private readonly System.Collections.Generic.List<IAssetSource> baseSources = new System.Collections.Generic.List<IAssetSource>();

        /// <param name="packs">Resource packs, lowest priority first (see <see cref="OpenPack"/>).</param>
        public static async Task<ClientResources> LoadAsync(string cacheDirectory, System.Collections.Generic.IReadOnlyList<IAssetSource> packs, Action<string> progress, CancellationToken cancel = default)
        {
            var watch = Stopwatch.StartNew();
            var resources = new ClientResources { CacheDirectory = cacheDirectory };
            using (var downloader = new AssetDownloader(cacheDirectory) { Progress = progress })
            {
                progress?.Invoke("Loading client jar " + Version);
                var jar = await downloader.GetClientJarAsync(Version, cancel).ConfigureAwait(false);
                var blocksJson = await downloader.GetVerifiedAsync(AssetDownloader.BlocksJson, cancel).ConfigureAwait(false);
                var shapesJson = await downloader.GetVerifiedAsync(AssetDownloader.CollisionShapesJson, cancel).ConfigureAwait(false);
                var itemsJson = await downloader.GetVerifiedAsync(AssetDownloader.ItemsJson, cancel).ConfigureAwait(false);
                var materialsJson = await downloader.GetVerifiedAsync(AssetDownloader.MaterialsJson, cancel).ConfigureAwait(false);
                var foodsJson = await downloader.GetVerifiedAsync(AssetDownloader.FoodsJson, cancel).ConfigureAwait(false);
                var entitiesJson = await downloader.GetVerifiedAsync(AssetDownloader.EntitiesJson, cancel).ConfigureAwait(false);
                var soundsJson = await downloader.GetVerifiedAsync(AssetDownloader.SoundsJson, cancel).ConfigureAwait(false);
                var particlesJson = await downloader.GetVerifiedAsync(AssetDownloader.ParticlesJson, cancel).ConfigureAwait(false);

                progress?.Invoke("Reading assets");
                resources.baseSources.Add(new ZipAssetSource("client.jar", jar, path => path.StartsWith("assets/minecraft/blockstates/") || path.StartsWith("assets/minecraft/models/") || path.StartsWith("assets/minecraft/textures/block/") || path.StartsWith("assets/minecraft/textures/colormap/") || path.StartsWith("assets/minecraft/textures/entity/") || path.StartsWith("assets/minecraft/textures/models/") || path.StartsWith("assets/minecraft/textures/painting/") || path.StartsWith("assets/minecraft/textures/particle/") || path.StartsWith("assets/minecraft/particles/") || path.StartsWith("assets/minecraft/textures/environment/") || path == "assets/minecraft/lang/en_us.json" || path.StartsWith("assets/minecraft/textures/gui/") || path.StartsWith("assets/minecraft/textures/font/") || path.StartsWith("assets/minecraft/font/") || path.StartsWith("assets/minecraft/textures/item/") || path == "assets/minecraft/texts/splashes.txt" || path.StartsWith("assets/minecraft/textures/mob_effect/") || path.StartsWith("assets/minecraft/textures/misc/") || path.StartsWith("assets/minecraft/textures/map/")));
                // the title panorama only exists in the asset index (the jar has 1x1 placeholders)
                var indexNames = new System.Collections.Generic.List<string>();
                for (var i = 0; i < 6; i++) indexNames.Add("minecraft/textures/gui/title/background/panorama_" + i + ".png");
                indexNames.Add("minecraft/sounds.json");
                try
                {
                    var objects = await downloader.GetIndexObjectsAsync(Version, cancel).ConfigureAwait(false);
                    foreach (var pair in objects)
                    {
                        if (pair.Key.StartsWith("minecraft/sounds/")) resources.SoundFiles[pair.Key] = (string)pair.Value["hash"];
                    }
                    var index = await downloader.GetIndexAssetsAsync(Version, indexNames, cancel).ConfigureAwait(false);
                    var files = new System.Collections.Generic.Dictionary<string, byte[]>();
                    foreach (var pair in index) files["assets/" + pair.Key] = pair.Value;
                    resources.baseSources.Insert(0, new MemoryAssetSource("asset index", files));
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not load index assets: " + exception.Message);
                }
                resources.Assets = resources.BuildAssets(packs);

                progress?.Invoke("Loading block registry");
                resources.Blocks = BlockRegistry.Load(Encoding.UTF8.GetString(blocksJson), Encoding.UTF8.GetString(shapesJson));
                resources.Items = ItemRegistry.Load(Encoding.UTF8.GetString(itemsJson));
                resources.EntityTypes = EntityTypeRegistry.Load(Encoding.UTF8.GetString(entitiesJson));
                var sounds = Newtonsoft.Json.Linq.JArray.Parse(Encoding.UTF8.GetString(soundsJson));
                resources.SoundEvents = new string[sounds.Count];
                foreach (var sound in sounds)
                {
                    var soundId = (int)sound["id"];
                    if (soundId >= 0 && soundId < resources.SoundEvents.Length) resources.SoundEvents[soundId] = (string)sound["name"];
                }
                var particles = Newtonsoft.Json.Linq.JArray.Parse(Encoding.UTF8.GetString(particlesJson));
                resources.ParticleTypes = new string[particles.Count];
                foreach (var particle in particles)
                {
                    var particleId = (int)particle["id"];
                    if (particleId >= 0 && particleId < resources.ParticleTypes.Length) resources.ParticleTypes[particleId] = (string)particle["name"];
                }
                resources.ItemProperties = ItemProperties.Load(Encoding.UTF8.GetString(materialsJson), Encoding.UTF8.GetString(foodsJson), resources.Items);
            }

            cancel.ThrowIfCancellationRequested();
            resources.BakeAssets(progress);

            Log.Info($"Loaded {resources.Blocks.Blocks.Count} blocks, {resources.Blocks.StateCount} states ({resources.bakedStates} with models), {resources.Textures.Entries.Count} textures in {watch.ElapsedMilliseconds} ms");
            return resources;
        }

        private int bakedStates;

        /// <summary>
        /// Block items render the block model their item model points to (vanilla: models/item/oak_fence.json has the
        /// parent block/oak_fence_inventory). Baked before the textures load, so their textures are in the array.
        /// </summary>
        private System.Collections.Generic.Dictionary<string, BakedModel> BakeItemModels(ModelBaker baker)
        {
            var result = new System.Collections.Generic.Dictionary<string, BakedModel>();
            if (Items == null) return result;
            foreach (var item in Items.All)
            {
                if (!Assets.TryGetText("assets/minecraft/models/item/" + item.Name.Path + ".json", out var text)) continue;
                string parent;
                try
                {
                    parent = (string)Newtonsoft.Json.Linq.JObject.Parse(text)["parent"];
                }
                catch (Exception)
                {
                    continue;
                }
                if (parent == null || !parent.Replace("minecraft:", "").StartsWith("block/")) continue;
                var model = baker.BakeModel(ResourceLocation.Of(parent));
                if (model != null && model.Quads.Length > 0) result[item.Name.Path] = model;
            }
            return result;
        }

        private AssetManager BuildAssets(System.Collections.Generic.IReadOnlyList<IAssetSource> packs)
        {
            var assets = new AssetManager();
            foreach (var source in baseSources) assets.Append(source);
            if (packs != null)
            {
                foreach (var pack in packs)
                {
                    assets.Push(pack);
                    Log.Info("Using resource pack " + pack.Name);
                }
            }
            return assets;
        }

        /// <summary>Everything that comes from the assets: language, block models, textures, colors.</summary>
        private void BakeAssets(Action<string> progress)
        {
            if (Assets.TryGetText("assets/minecraft/lang/en_us.json", out var language))
            {
                WhiteFox.Core.Network.TextComponent.Language = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(language);
            }
            progress?.Invoke("Baking block models");
            var textures = new TextureManager();
            var baker = new ModelBaker(Assets, textures);
            baker.Bake(Blocks);
            ItemBlockModels = BakeItemModels(baker);
            Fluids = FluidTextures.Register(textures);
            for (var i = 0; i < 10; i++) DestroyStages[i] = textures.Get("minecraft:block/destroy_stage_" + i);

            progress?.Invoke("Loading " + textures.Entries.Count + " textures");
            textures.Load(Assets);
            textures.LoadMaterials(Assets);
            baker.FinalizeStates(Blocks);
            bakedStates = baker.BakedStates;
            Textures = textures;

            var colors = new BiomeColors();
            colors.Load(Assets);
            Colors = colors;
        }

        /// <summary>
        /// Applies another set of resource packs (lowest priority first) like vanilla's resource reload: the registries
        /// stay, models and textures are baked again. The caller must not mesh while this runs.
        /// </summary>
        public void Reload(System.Collections.Generic.IReadOnlyList<IAssetSource> packs, Action<string> progress = null)
        {
            var watch = Stopwatch.StartNew();
            Assets = BuildAssets(packs);
            BakeAssets(progress);
            Log.Info($"Reloaded resources ({packs?.Count ?? 0} packs) in {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>Opens a resource pack: a folder or a zip file (the pack.mcmeta is not required).</summary>
        public static IAssetSource OpenPack(string path)
        {
            if (Directory.Exists(path)) return new DirectoryAssetSource(path);
            return new ZipAssetSource(Path.GetFileName(path), File.ReadAllBytes(path));
        }
    }
}
