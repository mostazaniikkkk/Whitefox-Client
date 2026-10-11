// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using WhiteFox.Core.Network;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Title screen (a vanilla styled version of Minosoft's Eros server list): server address, offline name, ping,
    /// connect, the local test world and quit, over the rotating panorama.
    /// </summary>
    public sealed class TitleScreen
    {
        public enum Action
        {
            None,
            Connect,
            Options,
            Quit,
        }

        public readonly GuiTextField Username = new GuiTextField { MaxLength = 16, Placeholder = "WhiteFox" };
        private readonly ServerListScreen servers;
        private bool serverList;
        private string splash;
        private readonly System.Collections.Generic.List<string> splashes = new System.Collections.Generic.List<string>();

        private readonly Settings settings;

        public TitleScreen(string splashes, Settings settings)
        {
            this.settings = settings;
            servers = new ServerListScreen(settings);
            Username.Text = settings.OfflineName;
            foreach (var line in (splashes ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.Trim();
                if (text.Length > 0 && !text.Contains("Minecraft")) this.splashes.Add(text);
            }
            this.splashes.Add("Also try 6B6T!");
            this.splashes.Add("Minosoft in Unity!");
        }

        /// <summary>The server chosen in the multiplayer screen (after <see cref="Action.Connect"/>).</summary>
        public string AddressText { get; private set; }

        /// <summary>Opens the multiplayer screen (the Multiplayer button, or -wf-servers for tests).</summary>
        public void OpenServerList()
        {
            serverList = true;
            servers.Open();
        }

        /// <summary>Esc: back one page of the multiplayer screen, or from it to the title. False on the title itself.</summary>
        public bool Back()
        {
            if (!serverList) return false;
            if (!servers.Back()) serverList = false;
            return true;
        }

        public Action Draw(GuiRenderer gui, string message)
        {
            if (serverList)
            {
                switch (servers.Draw(gui))
                {
                    case ServerListScreen.Result.Join:
                        AddressText = servers.JoinAddress;
                        return Submit();
                    case ServerListScreen.Result.Back:
                        serverList = false;
                        break;
                }
                return Action.None;
            }
            var center = Mathf.Floor(gui.Width / 2);
            var top = Mathf.Floor(gui.Height / 4);
            if (splash == null)
            {
                // only splashes the bitmap font can draw (some need the unicode font)
                for (var attempt = 0; attempt < 50 && splash == null; attempt++)
                {
                    var candidate = splashes[UnityEngine.Random.Range(0, splashes.Count)];
                    if (gui.Font.CanDisplay(candidate)) splash = candidate;
                }
                if (splash == null) splash = "Also try 6B6T!";
            }

            // logo: the name in the vanilla font, scaled, with the yellow splash text next to it
            gui.TextScaled("WhiteFox", center, top - 30, 4, Color.white);
            DrawSplash(gui, center + 100, top + 6);

            var y = top + 14;
            gui.Text("Name", center - 100, y + 36, GuiRenderer.Gray);
            Username.Draw(gui, new Rect(center - 100, y + 46, 200, 20));

            var action = Action.None;
            if (gui.Button(new Rect(center - 100, y + 74, 200, 20), "Multiplayer"))
            {
                Save();
                OpenServerList();
            }
            if (gui.Button(new Rect(center - 100, y + 98, 98, 20), "Options...")) action = Action.Options;
            if (gui.Button(new Rect(center + 2, y + 98, 98, 20), "Quit Game")) action = Action.Quit;

            // split screen (Windows): a second player with a controller joins the same server
            var messageY = y + 126;
            if (WhiteFox.Client.SplitScreen.Supported)
            {
                if (gui.Button(new Rect(center - 100, y + 122, 98, 20), "Split Screen: " + (settings.SplitScreen ? "ON" : "OFF")))
                {
                    settings.SplitScreen = !settings.SplitScreen;
                    settings.Save();
                }
                if (settings.SplitScreen && gui.Button(new Rect(center + 2, y + 122, 98, 20), settings.SplitHorizontal ? "Top / Bottom" : "Left / Right"))
                {
                    settings.SplitHorizontal = !settings.SplitHorizontal;
                    settings.Save();
                }
                if (settings.SplitScreen)
                {
                    gui.TextCentered("In a server, player 2 presses Start on a controller to join", center, y + 148, GuiRenderer.Gray);
                    messageY = y + 162;
                }
                else
                {
                    messageY = y + 150;
                }
            }

            if (message != null)
            {
                var lineY = messageY;
                foreach (var line in gui.Wrap(message, 300))
                {
                    gui.TextCentered(line, center, lineY, GuiRenderer.Red);
                    lineY += GuiFont.LineHeight + 1;
                }
            }

            gui.Text("WhiteFox 2.3 (" + Protocol.VersionName + ")", 2, gui.Height - 10);
            gui.TextRight("Based on Minosoft. Not affiliated with Mojang", gui.Width - 2, gui.Height - 10);
            if (action != Action.None) Save();
            return action;
        }

        private Action Submit()
        {
            Save();
            return Action.Connect;
        }

        private void Save()
        {
            if (settings.OfflineName == UsernameText) return;
            settings.OfflineName = UsernameText;
            settings.Save();
        }

        public string UsernameText => string.IsNullOrWhiteSpace(Username.Text) ? Username.Placeholder : Username.Text.Trim();

        /// <summary>Vanilla splash: yellow, rotated by -20 degrees, pulsing.</summary>
        private void DrawSplash(GuiRenderer gui, float x, float y)
        {
            var matrix = GUI.matrix;
            var pulse = 1.8f - Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * Mathf.PI * 2) * 0.1f);
            var scale = pulse * 100 / (gui.Font.Width(splash) + 32);
            GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(x, y, 0), Quaternion.Euler(0, 0, -20), new Vector3(scale, scale, 1));
            gui.TextCentered(splash, 0, -8, GuiRenderer.Yellow);
            GUI.matrix = matrix;
        }

        public static void MainAddress(string address, out string host, out int port)
        {
            address = address.Trim();
            host = address;
            port = 25565;
            var colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out var parsed))
            {
                host = address.Substring(0, colon);
                port = parsed;
            }
        }
    }

    /// <summary>Full screen message over the dirt background (connecting, disconnected), like vanilla.</summary>
    public static class MessageScreen
    {
        /// <summary>Returns true when the button was pressed.</summary>
        public static bool Draw(GuiRenderer gui, string title, string text, string button)
        {
            DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 2 - 40);
            gui.TextCentered(title, center, y, GuiRenderer.Gray);
            y += 20;
            foreach (var line in gui.Wrap(text ?? "", Mathf.Min(gui.Width - 40, 360)))
            {
                gui.TextCentered(line, center, y);
                y += GuiFont.LineHeight + 1;
            }
            return button != null && gui.Button(new Rect(center - 100, Mathf.Max(y + 20, gui.Height / 2 + 30), 200, 20), button);
        }

        public static void DirtBackground(GuiRenderer gui)
        {
            var dirt = gui.Assets.Get("assets/minecraft/textures/gui/options_background.png");
            if (dirt != null) gui.Tiled(dirt, new Rect(0, 0, gui.Width, gui.Height), 32, new Color(0.25f, 0.25f, 0.25f, 1));
            else gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.1f, 0.08f, 0.06f, 1));
        }
    }

    /// <summary>Game menu (Esc), like vanilla's pause screen.</summary>
    public static class PauseScreen
    {
        public enum Action
        {
            None,
            Resume,
            Options,
            Leave,
        }

        public static Action Draw(GuiRenderer gui, bool remote)
        {
            gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.6f));
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 4 + 8);
            gui.TextCentered("Game Menu", center, y - 24);
            if (gui.Button(new Rect(center - 102, y, 204, 20), "Back to Game")) return Action.Resume;
            if (gui.Button(new Rect(center - 102, y + 24, 204, 20), "Options...")) return Action.Options;
            if (gui.Button(new Rect(center - 102, y + 72, 204, 20), remote ? "Disconnect" : "Quit to Title")) return Action.Leave;
            return Action.None;
        }
    }
}

namespace WhiteFox.Client.Gui
{
    /// <summary>Options: graphics mode, render distance, field of view, sensitivity, brightness and controls.</summary>
    public static class OptionsScreen
    {
        public enum Action
        {
            None,
            Controls,
            Quality,
            Sounds,
            ResourcePacks,
            Done,
            GraphicsChanged,
        }

        public static Action Draw(GuiRenderer gui, Settings settings, bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 6);
            gui.TextCentered("Options", center, y - 18);
            var action = Action.None;

            if (gui.Button(new Rect(center - 155, y, 150, 20), "Graphics: " + Settings.GraphicsName(settings.Graphics)))
            {
                settings.Graphics = Settings.NextGraphics(settings.Graphics);
                action = Action.GraphicsChanged;
            }
            var distance = gui.Slider(new Rect(center + 5, y, 150, 20), "Render Distance: " + settings.ViewDistance + " chunks", (settings.ViewDistance - 2) / 62f);
            settings.ViewDistance = 2 + Mathf.RoundToInt(distance * 62);

            var fov = gui.Slider(new Rect(center - 155, y + 24, 150, 20), "FOV: " + Mathf.RoundToInt(settings.FieldOfView), (settings.FieldOfView - 30) / 80f);
            settings.FieldOfView = 30 + Mathf.Round(fov * 80);
            var sensitivity = gui.Slider(new Rect(center + 5, y + 24, 150, 20), "Sensitivity: " + Mathf.RoundToInt(settings.MouseSensitivity / 0.15f * 100) + "%", settings.MouseSensitivity / 0.6f);
            settings.MouseSensitivity = Mathf.Max(0.01f, sensitivity * 0.6f);

            if (gui.Button(new Rect(center - 155, y + 48, 150, 20), "Controls...")) action = Action.Controls;
            // classic is Minosoft's look as it is (only the render distance changes); enhanced and modern have quality settings
            if (gui.Button(new Rect(center + 5, y + 48, 150, 20), "Graphics Quality...", settings.Graphics != GraphicsMode.Classic)) action = Action.Quality;

            if (gui.Button(new Rect(center - 155, y + 72, 150, 20), "Music & Sounds...")) action = Action.Sounds;
            if (gui.Button(new Rect(center + 5, y + 72, 150, 20), "Resource Packs...")) action = Action.ResourcePacks;

            var touchModes = new[] { "Auto", "ON", "OFF" };
            if (gui.Button(new Rect(center - 155, y + 96, 150, 20), "Touch Controls: " + touchModes[Mathf.Clamp(settings.TouchControls, 0, 2)])) settings.TouchControls = (settings.TouchControls + 1) % 3;
            if (!Application.isMobilePlatform && gui.Button(new Rect(center + 5, y + 96, 150, 20), "Fullscreen: " + (WhiteFoxClient.Fullscreen ? "ON" : "OFF"))) WhiteFoxClient.ToggleFullscreen();

            // internal resolution: 50 .. 100 % in steps of 5
            var scale = gui.Slider(new Rect(center - 155, y + 120, 310, 20), "Resolution: " + Mathf.RoundToInt(settings.RenderScale * 100) + "%", (settings.RenderScale - 0.25f) / 0.75f);
            settings.RenderScale = Mathf.Round((0.25f + scale * 0.75f) * 20) / 20;

            // frame rate limit: 15 .. 240, the far right is unlimited
            var steps = Settings.FpsSteps;
            var current = System.Array.IndexOf(steps, settings.MaxFps);
            if (current < 0)
            {
                current = steps.Length - 2;
                for (var i = 0; i < steps.Length - 1; i++)
                {
                    if (steps[i] >= settings.MaxFps)
                    {
                        current = i;
                        break;
                    }
                }
            }
            var label = settings.MaxFps <= 0 ? "Max Framerate: Unlimited" : "Max Framerate: " + settings.MaxFps + " fps";
            var position = gui.Slider(new Rect(center - 155, y + 144, 310, 20), label, current / (float)(steps.Length - 1));
            var chosen = steps[Mathf.Clamp(Mathf.RoundToInt(position * (steps.Length - 1)), 0, steps.Length - 1)];
            if (chosen != settings.MaxFps)
            {
                settings.MaxFps = chosen;
                settings.ApplyFrameRate();
            }


            if (gui.Button(new Rect(center - 100, gui.Height - 30, 200, 20), "Done"))
            {
                settings.Save();
                action = Action.Done;
            }
            return action;
        }
    }

    /// <summary>Key bindings, like vanilla's controls screen: click a key, press the new one (Esc cancels).</summary>
    public sealed class ControlsScreen
    {
        /// <summary>The binding waiting for a key, or null.</summary>
        public KeyBinding Waiting;
        private float scroll;

        public bool Draw(GuiRenderer gui, Settings settings, bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextCentered("Controls", center, 8);

            var current = Event.current;
            if (current.type == EventType.ScrollWheel)
            {
                scroll += current.delta.y * 8;
                current.Use();
            }
            const float rowHeight = 24;
            const float listTop = 24;
            var listBottom = gui.Height - 36;
            var maxScroll = Mathf.Max(0, settings.Bindings.Count * rowHeight - (listBottom - listTop));
            scroll = Mathf.Clamp(scroll, 0, maxScroll);

            GUI.BeginGroup(new Rect(0, listTop, gui.Width, listBottom - listTop));
            var y = -scroll;
            foreach (var binding in settings.Bindings)
            {
                if (y > -rowHeight && y < listBottom - listTop)
                {
                    gui.Text(binding.Label, center - 154, y + 6);
                    var conflict = false;
                    foreach (var other in settings.Bindings)
                    {
                        if (other != binding && other.Key == binding.Key && binding.Key != UnityEngine.InputSystem.Key.None) conflict = true;
                    }
                    var label = Waiting == binding ? "> " + binding.DisplayName + " <" : binding.DisplayName;
                    if (conflict && Waiting != binding) label = "\u00a7c" + label;
                    if (gui.Button(new Rect(center + 2, y, 98, 20), label)) Waiting = binding;
                    if (gui.Button(new Rect(center + 104, y, 50, 20), "Reset", binding.Key != binding.Default)) binding.Key = binding.Default;
                }
                y += rowHeight;
            }
            GUI.EndGroup();

            if (gui.Button(new Rect(center - 155, gui.Height - 28, 150, 20), "Reset Keys")) settings.ResetBindings();
            if (gui.Button(new Rect(center + 5, gui.Height - 28, 150, 20), "Done"))
            {
                Waiting = null;
                settings.Save();
                return true;
            }
            return false;
        }

        /// <summary>Called every frame by the client (Input System) while a binding waits for a key.</summary>
        public void Capture()
        {
            if (Waiting == null) return;
            var key = PlayerInputReader.PressedKey();
            if (key == UnityEngine.InputSystem.Key.None) return;
            if (key != UnityEngine.InputSystem.Key.Escape) Waiting.Key = key;
            Waiting = null;
        }
    }
}

namespace WhiteFox.Client.Gui
{
    /// <summary>Quality of every modern graphics feature, with presets.</summary>
    /// <summary>Volume sliders per sound category, like vanilla's "Music &amp; Sounds" screen.</summary>
    public static class SoundScreen
    {
        private static readonly string[] Names =
        {
            "Master Volume", "Music", "Jukebox/Note Blocks", "Weather", "Blocks", "Hostile Creatures", "Friendly Creatures", "Players", "Ambient/Environment", "Voice/Speech",
        };

        /// <summary>Returns true when done.</summary>
        public static bool Draw(GuiRenderer gui, Settings settings, bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 6) - 12;
            gui.TextCentered("Music & Sound Options", center, y - 18);
            var master = gui.Slider(new Rect(center - 155, y, 310, 20), Names[0] + ": " + Percent(settings.Volumes[0]), settings.Volumes[0]);
            settings.Volumes[0] = master;
            for (var i = 1; i < Names.Length; i++)
            {
                var column = (i - 1) % 2;
                var row = (i - 1) / 2;
                var rect = new Rect(center - 155 + column * 160, y + 24 + row * 24, 150, 20);
                settings.Volumes[i] = gui.Slider(rect, Names[i] + ": " + Percent(settings.Volumes[i]), settings.Volumes[i]);
            }
            if (gui.Button(new Rect(center - 100, gui.Height - 30, 200, 20), "Done"))
            {
                settings.Save();
                return true;
            }
            return false;
        }

        private static string Percent(float value) => value <= 0 ? "OFF" : Mathf.RoundToInt(value * 100) + "%";
    }

    public static class QualityScreen
    {
        private static readonly string[] ShadowNames = { "Off", "Low", "Medium", "High" };
        private static readonly string[] LightNames = { "Off", "Few (16)", "Normal (48)", "Many (96)" };
        private static readonly int[] LightShadowCounts = { 0, 2, 4, 8 };
        private static readonly string[] ReliefNames = { "Off", "Soft", "Strong" };
        private static readonly string[] PresetNames = { "Low", "Medium", "High", "Ultra" };

        /// <summary>Returns 0 nothing, 1 changed (apply), 2 done.</summary>
        public static int Draw(GuiRenderer gui, Settings settings, bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            if (settings.Graphics == GraphicsMode.Enhanced) return DrawEnhanced(gui, settings);
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 6) - 8;
            gui.TextCentered("Graphics Quality (Modern)", center, y - 18);
            var changed = false;

            for (var preset = 0; preset < 4; preset++)
            {
                if (gui.Button(new Rect(center - 155 + preset * 78, y, 74, 20), PresetNames[preset]))
                {
                    settings.ApplyPreset(preset);
                    changed = true;
                }
            }
            y += 28;

            if (gui.Button(new Rect(center - 155, y, 150, 20), "Sun Shadows: " + ShadowNames[settings.SunShadows]))
            {
                settings.SunShadows = (settings.SunShadows + 1) % 4;
                changed = true;
            }
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Lights: " + LightNames[settings.BlockLights]))
            {
                settings.BlockLights = (settings.BlockLights + 1) % 4;
                changed = true;
            }
            y += 24;
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Light Shadows: " + settings.BlockLightShadows, settings.BlockLights > 0))
            {
                var index = System.Array.IndexOf(LightShadowCounts, settings.BlockLightShadows);
                settings.BlockLightShadows = LightShadowCounts[(index + 1) % LightShadowCounts.Length];
                changed = true;
            }
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Relief: " + ReliefNames[settings.Relief]))
            {
                settings.Relief = (settings.Relief + 1) % 3;
                changed = true;
            }
            y += 24;
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Water: " + (settings.RealisticWater ? "Realistic" : "Simple")))
            {
                settings.RealisticWater = !settings.RealisticWater;
                changed = true;
            }
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Bloom: " + (settings.Bloom ? "ON" : "OFF"), settings.PostProcessing))
            {
                settings.Bloom = !settings.Bloom;
                changed = true;
            }
            y += 24;
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Post-processing: " + (settings.PostProcessing ? "ON" : "OFF")))
            {
                settings.PostProcessing = !settings.PostProcessing;
                changed = true;
            }
            y += 24;
            var reflectionNames = new[] { "OFF", "Low", "High" };
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Reflections: " + reflectionNames[Mathf.Clamp(settings.WaterReflections, 0, 2)], settings.RealisticWater))
            {
                settings.WaterReflections = (settings.WaterReflections + 1) % 3;
                changed = true;
            }
            var volumetricNames = new[] { "OFF", "Low", "High" };
            y += 24;
            if (gui.Button(new Rect(center - 155, y, 150, 20), "God Rays: " + volumetricNames[Mathf.Clamp(settings.VolumetricLight, 0, 2)], settings.SunShadows > 0))
            {
                settings.VolumetricLight = (settings.VolumetricLight + 1) % 3;
                changed = true;
            }
            var cloudNames = new[] { "Vanilla", "Volumetric", "Volumetric HQ" };
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Clouds: " + cloudNames[Mathf.Clamp(settings.VolumetricClouds, 0, 2)]))
            {
                settings.VolumetricClouds = (settings.VolumetricClouds + 1) % 3;
                changed = true;
            }
            y += 24;
            var mistNames = new[] { "OFF", "Light", "Dense" };
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Mist: " + mistNames[Mathf.Clamp(settings.Mist, 0, 2)], settings.SunShadows > 0))
            {
                settings.Mist = (settings.Mist + 1) % 3;
                changed = true;
            }
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Waving Plants: " + (settings.WavingPlants ? "ON" : "OFF")))
            {
                settings.WavingPlants = !settings.WavingPlants;
                changed = true;
            }
            y -= 48;
            var blurNames = new[] { "OFF", "Low", "High" };
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Motion Blur: " + blurNames[Mathf.Clamp(settings.MotionBlur, 0, 2)], settings.PostProcessing))
            {
                settings.MotionBlur = (settings.MotionBlur + 1) % 3;
                changed = true;
            }

            if (gui.Button(new Rect(center - 100, gui.Height - 30, 200, 20), "Done"))
            {
                settings.Save();
                return 2;
            }
            return changed ? 1 : 0;
        }

        private static readonly string[] EnhancedPresetNames = { "Low", "Medium", "High" };

        /// <summary>The enhanced mode: the classic look with motion blur, volumetric clouds and wind.</summary>
        private static int DrawEnhanced(GuiRenderer gui, Settings settings)
        {
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 6) - 8;
            gui.TextCentered("Graphics Quality (Enhanced)", center, y - 18);
            var changed = false;

            for (var preset = 0; preset < 3; preset++)
            {
                if (gui.Button(new Rect(center - 155 + preset * 105, y, 100, 20), EnhancedPresetNames[preset]))
                {
                    settings.ApplyEnhancedPreset(preset);
                    changed = true;
                }
            }
            y += 28;

            var blurNames = new[] { "OFF", "Low", "High" };
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Motion Blur: " + blurNames[Mathf.Clamp(settings.EnhancedMotionBlur, 0, 2)]))
            {
                settings.EnhancedMotionBlur = (settings.EnhancedMotionBlur + 1) % 3;
                changed = true;
            }
            var cloudNames = new[] { "Vanilla", "Volumetric", "Volumetric HQ" };
            if (gui.Button(new Rect(center + 5, y, 150, 20), "Clouds: " + cloudNames[Mathf.Clamp(settings.EnhancedClouds, 0, 2)]))
            {
                settings.EnhancedClouds = (settings.EnhancedClouds + 1) % 3;
                changed = true;
            }
            y += 24;
            if (gui.Button(new Rect(center - 155, y, 150, 20), "Waving Plants: " + (settings.WavingPlants ? "ON" : "OFF")))
            {
                settings.WavingPlants = !settings.WavingPlants;
                changed = true;
            }
            if (gui.Button(new Rect(center + 5, y, 150, 20), "HDR Glow: " + (settings.EnhancedHdr ? "ON" : "OFF")))
            {
                settings.EnhancedHdr = !settings.EnhancedHdr;
                changed = true;
            }

            if (gui.Button(new Rect(center - 100, gui.Height - 30, 200, 20), "Done"))
            {
                settings.Save();
                return 2;
            }
            return changed ? 1 : 0;
        }
    }
}

namespace WhiteFox.Client.Gui
{
    /// <summary>Vanilla death screen: red overlay, "You died!", the death message, respawn and title menu.</summary>
    public static class DeathScreen
    {
        /// <summary>Returns 1 respawn, 2 title menu, 0 nothing.</summary>
        public static int Draw(GuiRenderer gui, string message, int score)
        {
            gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.31f, 0, 0, 0.5f));
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextScaled("You Died!", center, 30, 2, Color.white);
            var y = 85f;
            foreach (var line in gui.Wrap(message ?? "", 300))
            {
                gui.TextCentered(line, center, y);
                y += GuiFont.LineHeight + 1;
            }
            gui.TextCentered("Score: §e" + score, center, 100);
            if (gui.Button(new Rect(center - 100, gui.Height / 4 + 72, 200, 20), "Respawn")) return 1;
            if (gui.Button(new Rect(center - 100, gui.Height / 4 + 96, 200, 20), "Title Screen")) return 2;
            return 0;
        }
    }
}
