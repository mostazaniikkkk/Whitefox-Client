// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;
using WhiteFox.Client.Entities;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Game;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Network;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>
    /// Resource packs: the packs chosen in the options (resourcepacks folder) and the ones the server sends
    /// (Add/Remove Resource Pack), applied with a reload like vanilla's (models, textures, sounds, fonts).
    /// </summary>
    public sealed partial class WhiteFoxClient
    {
        private static string PacksDirectory => CommandLine.Get("-wf-packs") ?? Path.Combine(Application.persistentDataPath, "resourcepacks");

        private readonly List<KeyValuePair<Guid, IAssetSource>> serverPacks = new List<KeyValuePair<Guid, IAssetSource>>();
        private readonly Queue<ResourcePackOffer> packOffers = new Queue<ResourcePackOffer>();
        private bool reloading;
        private bool reloadAgain;
        private string reloadStatus;

        // resource pack screen state
        private List<string> packSelection;
        private static readonly HttpClient PackHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        /// <summary>Folders and zips of the resourcepacks folder.</summary>
        private static List<string> AvailablePacks()
        {
            var result = new List<string>();
            if (!Directory.Exists(PacksDirectory)) return result;
            foreach (var entry in Directory.GetFileSystemEntries(PacksDirectory))
            {
                if (Directory.Exists(entry) || entry.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFileName(entry));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>The asset sources to stack, lowest priority first: the chosen packs (last in the list first), then the server packs.</summary>
        private List<IAssetSource> PackSources()
        {
            var sources = new List<IAssetSource>();
            var selected = Settings.ResourcePacks ?? AvailablePacks();
            for (var i = selected.Count - 1; i >= 0; i--)
            {
                var path = Path.Combine(PacksDirectory, selected[i]);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                try
                {
                    sources.Add(ClientResources.OpenPack(path));
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not open resource pack " + path + ": " + exception.Message);
                }
            }
            foreach (var pack in serverPacks) sources.Add(pack.Value);
            return sources;
        }

        // ------------------------------------------------------------------ reload

        /// <summary>Reloads the assets with the current packs and rebuilds everything made from them.</summary>
        private async void ReloadResources(Action<bool> done = null)
        {
            if (reloading)
            {
                reloadAgain = true;
                done?.Invoke(true);
                return;
            }
            reloading = true;
            reloadStatus = "Preparando";
            var ok = true;
            try
            {
                do
                {
                    reloadAgain = false;
                    if (Renderer != null)
                    {
                        Renderer.Paused = true;
                        while (Renderer != null && Renderer.RunningCount > 0)
                        {
                            Renderer.Update(viewCamera.transform.position); // collects the running jobs
                            await Task.Yield();
                        }
                    }
                    var sources = PackSources();
                    await Task.Run(() => resources.Reload(sources, message => reloadStatus = message));
                    if (this == null) return;
                    RebuildFromResources();
                }
                while (reloadAgain);
            }
            catch (Exception exception)
            {
                ok = false;
                Debug.LogException(exception);
            }
            finally
            {
                if (Renderer != null) Renderer.Paused = false;
                reloading = false;
            }
            done?.Invoke(ok);
        }

        private void RebuildFromResources()
        {
            textures?.Dispose();
            textures = new BlockTextureArray(resources.Textures);
            foreach (var material in classicMaterials) SetTextures(material);
            foreach (var material in realisticMaterials) SetTextures(material);
            particles?.Dispose();
            particles = new ParticleEngine(resources, textures.Texture);

            itemModels?.Dispose();
            icons?.Dispose();
            gui?.Dispose();
            gui = new GuiRenderer(resources.Assets);
            weather?.Dispose();
            weather = new WeatherRenderer(gui.Assets);
            icons = new ItemIcons(resources, gui.Assets, classicMaterials[0]);
            blockEntityModels?.Dispose();
            blockEntityModels = new BlockEntityRenderer(gui.Assets, gui.Font, transform);
            icons.SpecialParts = (name, tag) => blockEntityModels.ItemParts(resources, name, tag);
            itemModels = new ItemModels(resources, icons, classicMaterials[0]);
            hud = new Hud(gui, icons, resources.Items);
            inventoryScreen = new InventoryScreen(gui, icons, resources.Items);
            textScreens = new TextScreens(gui);
            sounds?.Dispose();
            sounds = new Audio.SoundManager(resources, transform) { Volumes = Settings.Volumes };

            if (Session != null)
            {
                DisposeSessionRenderers();
                CreateSessionRenderers();
                Renderer.SetMesher(new SectionMesher(resources.Colors, resources.Fluids));
                ApplyRenderMode();
            }
        }

        // ------------------------------------------------------------------ options screen

        private void OpenResourcePacks()
        {
            packSelection = new List<string>(Settings.ResourcePacks ?? AvailablePacks());
            overlay = Overlay.ResourcePacks;
        }

        /// <summary>Vanilla PackSelectionScreen: available packs on the left, selected ones (top wins) on the right.</summary>
        private void DrawResourcePacks(bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextCentered("Paquetes de recursos", center, 8);
            gui.TextCentered("Arriba tiene prioridad. Clic para mover de una lista a la otra.", center, 20, GuiRenderer.Gray);

            var available = AvailablePacks();
            available.RemoveAll(packSelection.Contains);
            packSelection.RemoveAll(name => !File.Exists(Path.Combine(PacksDirectory, name)) && !Directory.Exists(Path.Combine(PacksDirectory, name)));

            const float top = 44;
            gui.TextCentered("Disponibles", center - 80, top - 10, GuiRenderer.Yellow);
            gui.TextCentered("Seleccionados", center + 80, top - 10, GuiRenderer.Yellow);
            var rows = Mathf.Max(1, Mathf.FloorToInt((gui.Height - top - 64) / 22));
            for (var i = 0; i < available.Count && i < rows; i++)
            {
                if (gui.Button(new Rect(center - 155, top + i * 22, 150, 20), Shorten(available[i])))
                {
                    packSelection.Insert(0, available[i]);
                    break;
                }
            }
            for (var i = 0; i < packSelection.Count && i < rows; i++)
            {
                var y = top + i * 22;
                if (gui.Button(new Rect(center + 5, y, i > 0 ? 126 : 150, 20), Shorten(packSelection[i])))
                {
                    packSelection.RemoveAt(i);
                    break;
                }
                if (i > 0 && gui.Button(new Rect(center + 135, y, 20, 20), "^"))
                {
                    var name = packSelection[i];
                    packSelection.RemoveAt(i);
                    packSelection.Insert(i - 1, name);
                    break;
                }
            }
            if (available.Count == 0 && packSelection.Count == 0)
            {
                gui.TextCentered("No hay paquetes: copia .zip o carpetas en la carpeta de paquetes.", center, top + 10, GuiRenderer.Gray);
            }

            var serverModes = new[] { "Preguntar", "Sí", "No" };
            if (gui.Button(new Rect(center - 155, gui.Height - 54, 310, 20), "Paquetes del servidor: " + serverModes[Mathf.Clamp(Settings.ServerPacks, 0, 2)]))
            {
                Settings.ServerPacks = (Settings.ServerPacks + 1) % 3;
            }
            if (gui.Button(new Rect(center - 155, gui.Height - 30, 150, 20), "Abrir carpeta"))
            {
                Directory.CreateDirectory(PacksDirectory);
                Application.OpenURL(new Uri(PacksDirectory).AbsoluteUri);
            }
            if (gui.Button(new Rect(center + 5, gui.Height - 30, 150, 20), "Listo"))
            {
                var previous = Settings.ResourcePacks ?? AvailablePacks();
                var changed = previous.Count != packSelection.Count;
                for (var i = 0; !changed && i < previous.Count; i++) changed = previous[i] != packSelection[i];
                Settings.ResourcePacks = new List<string>(packSelection);
                Settings.Save();
                overlay = Overlay.Options;
                if (changed) ReloadResources();
            }
        }

        private static string Shorten(string name)
        {
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name.Length > 22 ? name.Substring(0, 21) + "…" : name;
        }

        // ------------------------------------------------------------------ server packs

        private void OnResourcePackOffered(ResourcePackOffer offer)
        {
            if (Settings.ServerPacks == 1) AcceptServerPack(offer);
            else if (Settings.ServerPacks == 2 && !offer.Forced) Session?.Server?.SendResourcePackStatus(offer.Id, 1);
            else packOffers.Enqueue(offer);
        }

        /// <summary>Vanilla ConfirmScreen for server packs (required packs disconnect when declined).</summary>
        private void DrawResourcePackPrompt(bool overGame)
        {
            var offer = packOffers.Peek();
            var text = offer.Forced
                ? "Este servidor requiere un paquete de recursos personalizado."
                : "Este servidor recomienda usar un paquete de recursos personalizado.";
            if (!string.IsNullOrEmpty(offer.Prompt)) text += "\n" + LegacyFormatting.Strip(offer.Prompt);
            MessageScreen.Draw(gui, "Paquete de recursos del servidor", text, null);
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 2 + 40);
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Sí"))
            {
                packOffers.Dequeue();
                AcceptServerPack(offer);
            }
            else if (gui.Button(new Rect(center + 5, y, 150, 20), offer.Forced ? "No (desconectar)" : "No"))
            {
                packOffers.Dequeue();
                Session?.Server?.SendResourcePackStatus(offer.Id, 1);
                if (offer.Forced) StopSession("Has rechazado el paquete de recursos obligatorio del servidor.");
            }
        }

        /// <summary>Accepted (3), downloaded and verified (4), applied (0); or failed download (2) / reload (6).</summary>
        private async void AcceptServerPack(ResourcePackOffer offer)
        {
            var session = Session;
            var server = session?.Server;
            if (server == null) return;
            server.SendResourcePackStatus(offer.Id, 3);
            IAssetSource pack;
            try
            {
                if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    server.SendResourcePackStatus(offer.Id, 5);
                    return;
                }
                pack = await Task.Run(() => DownloadServerPack(uri, offer.Hash));
            }
            catch (Exception exception)
            {
                Log.Warn("Can not download the server resource pack " + offer.Url + ": " + exception.Message);
                if (Session == session) server.SendResourcePackStatus(offer.Id, 2);
                return;
            }
            if (Session != session) return;
            server.SendResourcePackStatus(offer.Id, 4);
            serverPacks.RemoveAll(entry => entry.Key == offer.Id);
            serverPacks.Add(new KeyValuePair<Guid, IAssetSource>(offer.Id, pack));
            ReloadResources(ok =>
            {
                if (Session == session) server.SendResourcePackStatus(offer.Id, ok ? 0 : 6);
            });
        }

        /// <summary>Downloads (or takes from the cache) a server pack, checking the SHA-1 when the server gave one.</summary>
        private IAssetSource DownloadServerPack(Uri uri, string hash)
        {
            hash = (hash ?? "").Trim().ToLowerInvariant();
            var directory = Path.Combine(resources.CacheDirectory, "server-resource-packs");
            Directory.CreateDirectory(directory);
            if (hash.Length == 40)
            {
                var cached = Path.Combine(directory, hash + ".zip");
                if (File.Exists(cached) && Hashing.Sha1(File.ReadAllBytes(cached)) == hash) return new ZipAssetSource("server:" + hash, File.ReadAllBytes(cached));
            }
            var data = PackHttp.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            if (data.Length > 250 * 1024 * 1024) throw new InvalidDataException("pack larger than 250 MiB");
            var actual = Hashing.Sha1(data);
            if (hash.Length == 40 && actual != hash) throw new InvalidDataException("SHA-1 mismatch: expected " + hash + ", got " + actual);
            File.WriteAllBytes(Path.Combine(directory, actual + ".zip"), data);
            return new ZipAssetSource("server:" + actual, data);
        }

        private void OnResourcePackRemoved(Guid? id)
        {
            var removed = serverPacks.RemoveAll(entry => !id.HasValue || entry.Key == id.Value);
            if (removed > 0) ReloadResources();
        }

        /// <summary>Server packs only live as long as the connection (vanilla goes back to the chosen packs).</summary>
        private void OnSessionStoppedPacks()
        {
            packOffers.Clear();
            if (serverPacks.Count == 0) return;
            serverPacks.Clear();
            ReloadResources();
        }
    }
}
