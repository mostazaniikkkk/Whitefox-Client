// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    public enum GraphicsMode
    {
        /// <summary>Minosoft's look: Minecraft light levels, no real lights.</summary>
        Classic,
        /// <summary>Sun shadows, real point lights, PBR (LabPBR), water, sky and post processing (not on phones).</summary>
        Modern,
        /// <summary>The classic look plus motion blur, volumetric clouds and leaves and plants in the wind.</summary>
        Enhanced,
    }

    /// <summary>A rebindable action (Minosoft's KeyBinding, keyboard only).</summary>
    public sealed class KeyBinding
    {
        public readonly string Id;
        public readonly string Label;
        public readonly Key Default;
        public Key Key;

        public KeyBinding(string id, string label, Key key)
        {
            Id = id;
            Label = label;
            Default = key;
            Key = key;
        }

        public bool IsPressed(Keyboard keyboard) => Key != Key.None && keyboard[Key].isPressed;
        public bool WasPressed(Keyboard keyboard) => Key != Key.None && keyboard[Key].wasPressedThisFrame;

        public string DisplayName
        {
            get
            {
                if (Key == Key.None) return "-";
                // the layout's character for printable keys (AZERTY shows A where QWERTY has Q); the OS names of the
                // other keys are localized ("Barra espaciadora"), so those use English names from the key code
                var keyboard = Keyboard.current;
                var name = keyboard != null ? keyboard[Key].displayName : null;
                if (!string.IsNullOrEmpty(name) && name.Length == 1) return name.ToUpperInvariant();
                return EnglishName(Key);
            }
        }

        private static string EnglishName(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl: return "Left Control";
                case Key.RightCtrl: return "Right Control";
                case Key.Backquote: return "`";
                case Key.Slash: return "/";
                case Key.Backslash: return "\\";
                case Key.Period: return ".";
                case Key.Comma: return ",";
                case Key.Semicolon: return ";";
                case Key.Quote: return "'";
                case Key.Minus: return "-";
                case Key.Equals: return "=";
                case Key.LeftBracket: return "[";
                case Key.RightBracket: return "]";
                case Key.PageUp: return "Page Up";
                case Key.PageDown: return "Page Down";
                case Key.UpArrow: return "Up";
                case Key.DownArrow: return "Down";
                case Key.LeftArrow: return "Left";
                case Key.RightArrow: return "Right";
            }
            var text = key.ToString();
            if (text.StartsWith("Digit")) return text.Substring(5);
            if (text.StartsWith("Numpad")) return "Keypad " + text.Substring(6);
            // LeftShift -> Left Shift, CapsLock -> Caps Lock
            var result = new System.Text.StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1])) result.Append(' ');
                result.Append(text[i]);
            }
            return result.ToString();
        }
    }

    /// <summary>Client options (Minosoft's profiles), stored in config.toml.</summary>
    /// <summary>A server of the multiplayer screen.</summary>
    public sealed class ServerEntry
    {
        public string Name;
        public string Address;
    }

    public sealed class Settings
    {
        public GraphicsMode Graphics = GraphicsMode.Modern;

        /// <summary>The realistic (modern) mode needs a desktop GPU; phones get classic and enhanced.</summary>
        public static bool ModernAvailable => !Application.isMobilePlatform;

        public static string GraphicsName(GraphicsMode mode) => mode == GraphicsMode.Modern ? "Modern" : mode == GraphicsMode.Enhanced ? "Enhanced" : "Classic";

        /// <summary>The next mode of the graphics button and key: classic, enhanced, modern (where available).</summary>
        public static GraphicsMode NextGraphics(GraphicsMode mode)
        {
            if (mode == GraphicsMode.Classic) return GraphicsMode.Enhanced;
            if (mode == GraphicsMode.Enhanced && ModernAvailable) return GraphicsMode.Modern;
            return GraphicsMode.Classic;
        }

        private static GraphicsMode ParseGraphics(string value) =>
            value == "classic" ? GraphicsMode.Classic : value == "enhanced" ? GraphicsMode.Enhanced : GraphicsMode.Modern;

        /// <summary>A modern setting on a phone (an old config, a test override) falls back to enhanced.</summary>
        public void ClampGraphics()
        {
            if (Graphics == GraphicsMode.Modern && !ModernAvailable) Graphics = GraphicsMode.Enhanced;
        }
        public int ViewDistance = 8;
        public float FieldOfView = 70;
        public float MouseSensitivity = 0.15f;
        public float Brightness = 0.5f;

        // modern graphics quality (each improvement can be lowered or turned off)
        /// <summary>0 off, 1 low (48 blocks), 2 medium (96), 3 high (128).</summary>
        public int SunShadows = 3;
        /// <summary>0 off, 1 few (16), 2 normal (48), 3 many (96) real point lights from light emitting blocks.</summary>
        public int BlockLights = 2;
        /// <summary>How many of the nearest block lights cast shadows (0, 2, 4 or 8).</summary>
        public int BlockLightShadows = 4;
        /// <summary>0 off, 1 soft, 2 strong (LabPBR / generated normal maps).</summary>
        public int Relief = 1;
        public bool RealisticWater = true;
        public bool Bloom = true;
        public bool PostProcessing = true;
        /// <summary>Camera motion blur: 0 off, 1 low, 2 high (realistic mode).</summary>
        public int MotionBlur;
        /// <summary>Screen space reflections on the realistic water: 0 off, 1 low (16 steps), 2 high (32 steps).</summary>
        public int WaterReflections = 2;
        /// <summary>Volumetric sun light (god rays): 0 off, 1 low, 2 high.</summary>
        public int VolumetricLight = 1;
        /// <summary>Clouds of the realistic mode: 0 the vanilla boxes, 1 volumetric (low), 2 volumetric (high).</summary>
        public int VolumetricClouds = 1;
        /// <summary>Volumetric ground mist: 0 off, 1 light, 2 dense.</summary>
        public int Mist = 1;
        /// <summary>Leaves and plants sway in the wind (enhanced and modern).</summary>
        public bool WavingPlants = true;
        /// <summary>Enhanced mode: motion blur 0 off, 1 low, 2 high.</summary>
        public int EnhancedMotionBlur = 1;
        /// <summary>Enhanced mode: clouds 0 the vanilla boxes, 1 volumetric, 2 volumetric (high).</summary>
        public int EnhancedClouds = 1;
        /// <summary>Enhanced mode: light emitting blocks glow (HDR colors and bloom).</summary>
        public bool EnhancedHdr;

        /// <summary>Volume per sound category (master, music, records, weather, blocks, hostile, neutral, players, ambient, voice).</summary>
        public readonly float[] Volumes = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        /// <summary>Chosen resource packs (file names in the resourcepacks folder, highest priority first); null = every pack there.</summary>
        public List<string> ResourcePacks;
        /// <summary>Server resource packs: 0 ask, 1 always accept, 2 decline (required packs are still asked).</summary>
        public int ServerPacks;

        /// <summary>Internal resolution of the 3D view, 0.25 .. 1 of the window (the GUI stays sharp).</summary>
        public float RenderScale = 1;
        /// <summary>Frame rate limit, 0 = unlimited.</summary>
        public int MaxFps = 60;
        /// <summary>The steps of the frame rate slider (the last one, 0, is unlimited).</summary>
        public static readonly int[] FpsSteps = { 15, 20, 25, 30, 40, 50, 60, 75, 90, 100, 120, 144, 165, 180, 240, 0 };

        /// <summary>Applies the frame rate limit (vsync off, so the limit is exact).</summary>
        public void ApplyFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = MaxFps <= 0 ? -1 : MaxFps;
        }

        /// <summary>Touch controls: 0 auto (phones and tablets), 1 always, 2 never.</summary>
        public int TouchControls;

        /// <summary>Title screen: the last direct connection address and the name of offline accounts.</summary>
        public string Address = "localhost";
        public string OfflineName = "WhiteFox";
        /// <summary>Split screen (Windows): a second player joins the same server on the other half of the screen.</summary>
        public bool SplitScreen;
        public bool SplitHorizontal;
        public string Player2Name = "";

        /// <summary>Player 2's name: the chosen one, else the own name with "2" (both must differ on a server).</summary>
        public string Player2NameOrDefault
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Player2Name) && Player2Name.Trim() != OfflineName) return Player2Name.Trim();
                var name = string.IsNullOrEmpty(OfflineName) ? "Player" : OfflineName;
                return (name.Length > 15 ? name.Substring(0, 15) : name) + "2";
            }
        }
        /// <summary>The saved servers of the multiplayer screen.</summary>
        public readonly List<ServerEntry> Servers = new List<ServerEntry>();

        public readonly KeyBinding Forward = new KeyBinding("forward", "Walk Forwards", Key.W);
        public readonly KeyBinding Back = new KeyBinding("back", "Walk Backwards", Key.S);
        public readonly KeyBinding Left = new KeyBinding("left", "Strafe Left", Key.A);
        public readonly KeyBinding Right = new KeyBinding("right", "Strafe Right", Key.D);
        public readonly KeyBinding Jump = new KeyBinding("jump", "Jump / Fly Up", Key.Space);
        public readonly KeyBinding Sneak = new KeyBinding("sneak", "Sneak / Fly Down", Key.LeftShift);
        public readonly KeyBinding Sprint = new KeyBinding("sprint", "Sprint", Key.LeftCtrl);
        public readonly KeyBinding Inventory = new KeyBinding("inventory", "Open/Close Inventory", Key.E);
        public readonly KeyBinding Drop = new KeyBinding("drop", "Drop Selected Item (Ctrl: Stack)", Key.Q);
        public readonly KeyBinding SwapHands = new KeyBinding("swap", "Swap Item With Offhand", Key.F);
        public readonly KeyBinding Chat = new KeyBinding("chat", "Open Chat", Key.T);
        public readonly KeyBinding Command = new KeyBinding("command", "Open Command", Key.Slash);
        public readonly KeyBinding Debug = new KeyBinding("debug", "Debug Screen", Key.F3);
        public readonly KeyBinding ToggleGraphics = new KeyBinding("graphics", "Toggle Graphics Mode", Key.F6);
        public readonly KeyBinding Perspective = new KeyBinding("perspective", "Toggle Perspective", Key.F5);
        public readonly KeyBinding PlayerList = new KeyBinding("playerlist", "List Players", Key.Tab);
        public readonly KeyBinding HideGui = new KeyBinding("hidegui", "Hide GUI", Key.F1);
        public readonly KeyBinding Fullscreen = new KeyBinding("fullscreen", "Toggle Fullscreen", Key.F11);
        public readonly KeyBinding[] Hotbar = new KeyBinding[9];

        public readonly List<KeyBinding> Bindings = new List<KeyBinding>();

        public Settings()
        {
            Bindings.AddRange(new[] { Forward, Back, Left, Right, Jump, Sneak, Sprint, Inventory, Drop, SwapHands, Chat, Command, Debug, ToggleGraphics, Perspective, PlayerList, HideGui, Fullscreen });
            for (var i = 0; i < 9; i++)
            {
                Hotbar[i] = new KeyBinding("hotbar" + (i + 1), "Hotbar Slot " + (i + 1), Key.Digit1 + i);
                Bindings.Add(Hotbar[i]);
            }
            Load();
        }

        /// <summary>config.toml in the user data folder (next to the cache and the resource packs).</summary>
        public static string ConfigPath => System.IO.Path.Combine(Application.persistentDataPath, "config.toml");

        public void Load()
        {
            var path = ConfigPath;
            if (!System.IO.File.Exists(path))
            {
                // phones start with the light classic look and a shorter render distance
                if (Application.isMobilePlatform)
                {
                    Graphics = GraphicsMode.Classic;
                    ViewDistance = 6;
                    RenderScale = 0.75f;
                    // motion blur needs a depth copy and a full screen pass: too much for most phones
                    EnhancedMotionBlur = 0;
                }
                // first run with the TOML configuration: take over what earlier versions kept in PlayerPrefs
                LoadPlayerPrefs();
                Save();
                return;
            }
            TomlTable root;
            try
            {
                root = Toml.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("WhiteFox: can not read " + path + " (" + exception.Message + "), using the defaults");
                return;
            }

            var video = root.Table("video");
            Graphics = ParseGraphics(video.GetString("graphics"));
            ViewDistance = Mathf.Clamp(video.GetInt("view_distance", ViewDistance), 2, 64);
            FieldOfView = video.GetFloat("fov", FieldOfView);
            Brightness = video.GetFloat("brightness", Brightness);
            RenderScale = Mathf.Clamp(video.GetFloat("render_scale", RenderScale), 0.25f, 1);
            MaxFps = video.GetInt("max_fps", MaxFps);
            if (MaxFps != 0) MaxFps = Mathf.Clamp(MaxFps, 15, 1000);
            var quality = video.Table("quality");
            SunShadows = quality.GetInt("sun_shadows", SunShadows);
            BlockLights = quality.GetInt("block_lights", BlockLights);
            BlockLightShadows = quality.GetInt("block_light_shadows", BlockLightShadows);
            Relief = quality.GetInt("relief", Relief);
            RealisticWater = quality.GetBool("realistic_water", RealisticWater);
            Bloom = quality.GetBool("bloom", Bloom);
            PostProcessing = quality.GetBool("post_processing", PostProcessing);
            MotionBlur = Mathf.Clamp(quality.GetInt("motion_blur", MotionBlur), 0, 2);
            WaterReflections = Mathf.Clamp(quality.GetInt("water_reflections", WaterReflections), 0, 2);
            VolumetricLight = Mathf.Clamp(quality.GetInt("volumetric_light", VolumetricLight), 0, 2);
            VolumetricClouds = Mathf.Clamp(quality.GetInt("volumetric_clouds", VolumetricClouds), 0, 2);
            Mist = Mathf.Clamp(quality.GetInt("mist", Mist), 0, 2);
            WavingPlants = quality.GetBool("waving_plants", WavingPlants);
            var enhanced = root.Table("enhanced");
            EnhancedMotionBlur = Mathf.Clamp(enhanced.GetInt("motion_blur", EnhancedMotionBlur), 0, 2);
            EnhancedClouds = Mathf.Clamp(enhanced.GetInt("clouds", EnhancedClouds), 0, 2);
            EnhancedHdr = enhanced.GetBool("hdr", EnhancedHdr);

            var controls = root.Table("controls");
            MouseSensitivity = controls.GetFloat("mouse_sensitivity", MouseSensitivity);
            TouchControls = Mathf.Clamp(controls.GetInt("touch_controls", TouchControls), 0, 2);
            var keys = controls.Table("keys");
            foreach (var binding in Bindings)
            {
                var stored = keys.GetString(binding.Id);
                if (stored != null && Enum.TryParse<Key>(stored, out var key)) binding.Key = key;
            }

            if (root.Table("sound")["volumes"] is List<object> volumes)
            {
                for (var i = 0; i < Volumes.Length && i < volumes.Count; i++) Volumes[i] = volumes[i] is double d ? (float)d : volumes[i] is long l ? l : Volumes[i];
            }

            var packs = root.Table("resource_packs");
            ResourcePacks = packs.ContainsKey("selected") ? packs.GetStrings("selected") : null;
            ServerPacks = packs.GetInt("server_packs", ServerPacks);

            var multiplayer = root.Table("multiplayer");
            Address = multiplayer.GetString("address", Address);
            OfflineName = multiplayer.GetString("offline_name", OfflineName);
            SplitScreen = multiplayer.GetBool("split_screen", SplitScreen);
            SplitHorizontal = multiplayer.GetBool("split_horizontal", SplitHorizontal);
            Player2Name = multiplayer.GetString("player2_name", Player2Name);
            Servers.Clear();
            foreach (var server in multiplayer.Tables("servers"))
            {
                var address = server.GetString("address");
                if (!string.IsNullOrWhiteSpace(address)) Servers.Add(new ServerEntry { Name = server.GetString("name", "Minecraft Server"), Address = address });
            }
            // earlier versions only had the address field: it becomes the first saved server
            if (!multiplayer.GetBool("server_list", false) && !string.IsNullOrWhiteSpace(Address) && Address != "localhost")
            {
                Servers.Add(new ServerEntry { Name = "Minecraft Server", Address = Address });
            }
        }

        /// <summary>False for automated test runs: their command line overrides must not end up in the user's config.</summary>
        public bool Persist = true;

        public void Save()
        {
            if (!Persist) return;
            var root = new TomlTable();
            var video = root.Table("video");
            video["graphics"] = Graphics == GraphicsMode.Classic ? "classic" : Graphics == GraphicsMode.Enhanced ? "enhanced" : "modern";
            video["view_distance"] = (long)ViewDistance;
            video["fov"] = Real(FieldOfView);
            video["brightness"] = Real(Brightness);
            video["render_scale"] = Real(RenderScale);
            video["max_fps"] = (long)MaxFps;
            var quality = video.Table("quality");
            quality["sun_shadows"] = (long)SunShadows;
            quality["block_lights"] = (long)BlockLights;
            quality["block_light_shadows"] = (long)BlockLightShadows;
            quality["relief"] = (long)Relief;
            quality["realistic_water"] = RealisticWater;
            quality["bloom"] = Bloom;
            quality["post_processing"] = PostProcessing;
            quality["motion_blur"] = (long)MotionBlur;
            quality["water_reflections"] = (long)WaterReflections;
            quality["volumetric_light"] = (long)VolumetricLight;
            quality["volumetric_clouds"] = (long)VolumetricClouds;
            quality["mist"] = (long)Mist;
            quality["waving_plants"] = WavingPlants;
            var enhanced = root.Table("enhanced");
            enhanced["motion_blur"] = (long)EnhancedMotionBlur;
            enhanced["clouds"] = (long)EnhancedClouds;
            enhanced["hdr"] = EnhancedHdr;

            var controls = root.Table("controls");
            controls["mouse_sensitivity"] = Real(MouseSensitivity);
            controls["touch_controls"] = (long)TouchControls;
            var keys = controls.Table("keys");
            foreach (var binding in Bindings) keys[binding.Id] = binding.Key.ToString();

            var volumes = new List<object>();
            foreach (var volume in Volumes) volumes.Add(Real(volume));
            root.Table("sound")["volumes"] = volumes;

            var packs = root.Table("resource_packs");
            if (ResourcePacks != null) packs["selected"] = new List<object>(ResourcePacks);
            packs["server_packs"] = (long)ServerPacks;

            var multiplayer = root.Table("multiplayer");
            multiplayer["address"] = Address ?? "";
            multiplayer["offline_name"] = OfflineName ?? "";
            multiplayer["split_screen"] = SplitScreen;
            multiplayer["split_horizontal"] = SplitHorizontal;
            multiplayer["player2_name"] = Player2Name ?? "";
            var servers = new List<TomlTable>();
            foreach (var server in Servers)
            {
                var table = new TomlTable();
                table["name"] = server.Name ?? "";
                table["address"] = server.Address ?? "";
                servers.Add(table);
            }
            multiplayer["servers"] = servers;
            multiplayer["server_list"] = true; // the list exists (an empty list writes no [[servers]])

            var path = ConfigPath;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var text = "# WhiteFox configuration (written by the client; edits are kept while it is closed)\n" +
                           Toml.Write(root);
                // write a temporary file first so a crash while saving can not leave a broken configuration
                System.IO.File.WriteAllText(path + ".tmp", text);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(path + ".tmp", path);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("WhiteFox: can not save " + path + ": " + exception.Message);
            }
        }

        /// <summary>Test runs (-wf-set "sun_shadows=0,bloom=0,..."): settings changed for this run only.</summary>
        public void Override(string spec)
        {
            foreach (var pair in spec.Split(','))
            {
                var parts = pair.Split('=');
                if (parts.Length != 2) continue;
                var key = parts[0].Trim();
                var value = parts[1].Trim();
                int.TryParse(value, out var number);
                float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real);
                switch (key)
                {
                    case "graphics": Graphics = ParseGraphics(value); break;
                    case "view": ViewDistance = number; break;
                    case "sun_shadows": SunShadows = number; break;
                    case "block_lights": BlockLights = number; break;
                    case "block_light_shadows": BlockLightShadows = number; break;
                    case "relief": Relief = number; break;
                    case "water": RealisticWater = number != 0; break;
                    case "bloom": Bloom = number != 0; break;
                    case "post": PostProcessing = number != 0; break;
                    case "motion_blur": MotionBlur = number; break;
                    case "water_reflections": WaterReflections = number; break;
                    case "volumetric": VolumetricLight = number; break;
                    case "clouds": VolumetricClouds = number; break;
                    case "mist": Mist = number; break;
                    case "split": SplitScreen = number != 0; break;
                    case "waving_plants": WavingPlants = number != 0; break;
                    case "enhanced_blur": EnhancedMotionBlur = number; break;
                    case "enhanced_clouds": EnhancedClouds = number; break;
                    case "enhanced_hdr": EnhancedHdr = number != 0; break;
                    case "render_scale": RenderScale = real; break;
                }
            }
        }

        /// <summary>A float as the double with the same short decimal text (0.15, not 0.15000000596046448).</summary>
        private static double Real(float value) => double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The options of versions before config.toml (PlayerPrefs).</summary>
        private void LoadPlayerPrefs()
        {
            const string prefix = "whitefox.";
            if (!PlayerPrefs.HasKey(prefix + "graphics") && !PlayerPrefs.HasKey(prefix + "address")) return;
            Graphics = (GraphicsMode)PlayerPrefs.GetInt(prefix + "graphics", (int)Graphics);
            ViewDistance = PlayerPrefs.GetInt(prefix + "viewDistance", ViewDistance);
            FieldOfView = PlayerPrefs.GetFloat(prefix + "fov", FieldOfView);
            MouseSensitivity = PlayerPrefs.GetFloat(prefix + "sensitivity", MouseSensitivity);
            Brightness = PlayerPrefs.GetFloat(prefix + "brightness", Brightness);
            SunShadows = PlayerPrefs.GetInt(prefix + "sunShadows", SunShadows);
            BlockLights = PlayerPrefs.GetInt(prefix + "blockLights", BlockLights);
            BlockLightShadows = PlayerPrefs.GetInt(prefix + "blockLightShadows", BlockLightShadows);
            Relief = PlayerPrefs.GetInt(prefix + "relief", Relief);
            RealisticWater = PlayerPrefs.GetInt(prefix + "realisticWater", RealisticWater ? 1 : 0) != 0;
            Bloom = PlayerPrefs.GetInt(prefix + "bloom", Bloom ? 1 : 0) != 0;
            PostProcessing = PlayerPrefs.GetInt(prefix + "postProcessing", PostProcessing ? 1 : 0) != 0;
            for (var i = 0; i < Volumes.Length; i++) Volumes[i] = PlayerPrefs.GetFloat(prefix + "volume." + i, Volumes[i]);
            if (PlayerPrefs.HasKey(prefix + "resourcePacks"))
            {
                var packs = PlayerPrefs.GetString(prefix + "resourcePacks", "");
                ResourcePacks = new List<string>(packs.Length == 0 ? new string[0] : packs.Split('|'));
            }
            ServerPacks = PlayerPrefs.GetInt(prefix + "serverPacks", ServerPacks);
            foreach (var binding in Bindings)
            {
                var stored = PlayerPrefs.GetString(prefix + "key." + binding.Id, "");
                if (stored.Length > 0 && Enum.TryParse<Key>(stored, out var key)) binding.Key = key;
            }
            Address = PlayerPrefs.GetString(prefix + "address", Address);
            OfflineName = PlayerPrefs.GetString(prefix + "username", OfflineName);
        }

        /// <summary>Presets for the modern graphics: 0 low, 1 medium, 2 high, 3 ultra.</summary>
        /// <summary>Enhanced presets: low (vanilla clouds, no blur), medium, high.</summary>
        public void ApplyEnhancedPreset(int preset)
        {
            EnhancedMotionBlur = preset;
            EnhancedClouds = preset;
            EnhancedHdr = preset >= 1;
            WavingPlants = true;
        }

        public void ApplyPreset(int preset)
        {
            SunShadows = new[] { 1, 2, 3, 3 }[preset];
            BlockLights = new[] { 1, 2, 2, 3 }[preset];
            BlockLightShadows = new[] { 0, 2, 4, 8 }[preset];
            Relief = new[] { 0, 1, 1, 2 }[preset];
            RealisticWater = preset > 0;
            Bloom = preset > 0;
            PostProcessing = true;
        }

        public void ResetBindings()
        {
            foreach (var binding in Bindings) binding.Key = binding.Default;
        }
    }
}
