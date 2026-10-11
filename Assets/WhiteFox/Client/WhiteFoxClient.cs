// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Entities;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Network;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>
    /// Entry point of the Unity client (Minosoft: Eros + PlaySession + RenderContext + RenderLoop). Loads the resources
    /// on a worker thread, shows the main menu, connects to a server and then runs the 20 TPS game loop and renders the
    /// interpolated state every frame.
    /// Command line: -wf-connect host[:port] [-wf-name name] connects directly; -wf-screenshot file captures and quits.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed partial class WhiteFoxClient : MonoBehaviour
    {
        /// <summary>Options (graphics mode, view distance, FOV, sensitivity, brightness, key bindings).</summary>
        public Settings Settings { get; private set; }
        public bool Modern => Settings.Graphics == GraphicsMode.Modern;
        public bool Enhanced => Settings.Graphics == GraphicsMode.Enhanced;
        public bool daylightCycle = true;

        public GameSession Session { get; private set; }
        public WorldRenderer Renderer { get; private set; }

        private volatile string status = "Starting";
        private string error;
        private BlockTextureArray textures;
        private Material[] classicMaterials;
        // blocks drawn as entities (dropped, held, falling, in frames): lit like the world in the realistic mode
        private Material blockItemMaterial;
        private Shader classicBlockShader, litBlockShader;
        private Material[] realisticMaterials;
        private RealisticEnvironment environment;
        private EnhancedEffects enhanced;
        private readonly CloudPass cloudPass = new CloudPass();
        private int sentViewDistance = -1, pendingViewDistance = -1;
        private float viewDistanceChanged;
        private LightSourceManager lightSources;
        private long? pinnedTime;
        private Camera viewCamera;
        private BlockOutline outline;
        private PlayerInputReader input;
        private enum Overlay
        {
            None,
            Options,
            Controls,
            Quality,
            Sounds,
            ResourcePacks,
        }
        private Overlay overlay;
        /// <summary>Shown while the server address is resolved before joining.</summary>
        private string loginMessage;
        private readonly ControlsScreen controls = new ControlsScreen();
        private readonly ChatInput chat = new ChatInput();
        private TitleScreen title;
        private GuiRenderer gui;
        private ItemIcons icons;
        private Hud hud;
        private InventoryScreen inventoryScreen;
        private BreakOverlay breakOverlay;
        private ItemModels itemModels;
        private SkinManager skins;
        private EntityRenderer entityRenderer;
        private FirstPersonRenderer firstPerson;
        private BlockEntityRenderer blockEntityRenderer;
        private Audio.SoundManager sounds;
        private DynamicLights dynamicLights;
        private TextScreens textScreens;
        private readonly MapTextures maps = new MapTextures();
        private ParticleEngine particles;
        private BlockEntityRenderer blockEntityModels;
        private WeatherRenderer weather;
        private bool textScreenWasOpen;
        private float partialTick;
        private bool commandsDone;
        private Material panorama;
        private Texture2DArray panoramaFaces;
        private float panoramaYaw;
        private string disconnectMessage;
        private ClientResources resources;
        private double accumulator;
        private float fps;

        private async void Start()
        {
            Application.runInBackground = true; // keep the connection alive (keep alive, chunks) when the window loses focus
            RenderOrigin.Enabled = !CommandLine.Has("-wf-noorigin");
            Log.Sink = (level, message) =>
            {
                if (level >= LogLevel.Error) Debug.LogError(message);
                else if (level == LogLevel.Warn) Debug.LogWarning(message);
                else Debug.Log(message);
            };
            Application.targetFrameRate = -1;
            Settings = new Settings();
            input = new PlayerInputReader(Settings);
            TouchControls.Simulated = CommandLine.Has("-wf-touch");
            input.Touch = new TouchControls(Settings);
            Settings.ApplyFrameRate();
            if (Application.isMobilePlatform) Screen.sleepTimeout = SleepTimeout.NeverSleep;
            if (CommandLine.Has("-wf-acceptpacks")) Settings.ServerPacks = 1;

            var cache = Path.Combine(Application.persistentDataPath, "cache");
            Directory.CreateDirectory(PacksDirectory);
            var packSources = PackSources();

            try
            {
                resources = await Task.Run(() => ClientResources.LoadAsync(cache, packSources, message => status = message));
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogException(exception);
                return;
            }
            if (this == null) return; // destroyed while loading

            status = "Creating textures";
            textures = new BlockTextureArray(resources.Textures);
            CreateMaterials();
            // screenshot runs (-wf-classic, -wf-nohud, ...) never write the user's config.toml
            if (CommandLine.Has("-wf-screenshot") || CommandLine.Has("-wf-bench")) Settings.Persist = false;
            var overrides = CommandLine.Get("-wf-set");
            if (overrides != null) Settings.Override(overrides);
            if (CommandLine.Has("-wf-classic")) Settings.Graphics = GraphicsMode.Classic;
            if (CommandLine.Has("-wf-modern")) Settings.Graphics = GraphicsMode.Modern;
            if (CommandLine.Has("-wf-enhanced")) Settings.Graphics = GraphicsMode.Enhanced;
            Settings.ClampGraphics();

            SetupCamera();
            environment = new RealisticEnvironment(transform);
            enhanced = new EnhancedEffects(transform);
            gui = new GuiRenderer(resources.Assets);
            icons = new ItemIcons(resources, gui.Assets, classicMaterials[0]);
            blockEntityModels = new BlockEntityRenderer(gui.Assets, gui.Font, transform);
            icons.SpecialParts = (name, tag) => blockEntityModels.ItemParts(resources, name, tag);
            itemModels = new ItemModels(resources, icons, blockItemMaterial);
            sounds = new Audio.SoundManager(resources, transform) { Volumes = Settings.Volumes };
            GuiRenderer.Clicked += OnButtonClicked;
            dynamicLights = new DynamicLights(resources);
            particles = new ParticleEngine(resources, textures.Texture);
            weather = new WeatherRenderer(gui.Assets);
            textScreens = new TextScreens(gui);
            hud = new Hud(gui, icons, resources.Items);
            inventoryScreen = new InventoryScreen(gui, icons, resources.Items);
            resources.Assets.TryGetText("assets/minecraft/texts/splashes.txt", out var splashes);
            title = new TitleScreen(splashes, Settings);
            var testServer = CommandLine.Get("-wf-addserver"); // screenshot runs: a server in the list (not saved)
            if (testServer != null) Settings.Servers.Add(new ServerEntry { Name = "Test Server", Address = testServer });
            if (CommandLine.Has("-wf-servers")) title.OpenServerList();
            CreatePanorama();
            status = null;

            var connect = CommandLine.Get("-wf-connect");
            if (connect != null) Connect(connect, CommandLine.Get("-wf-name") ?? "WhiteFox");
            else if ((CommandLine.Has("-wf-screenshot") && !CommandLine.Has("-wf-title")) || CommandLine.Has("-wf-local")) StartLocal();

            if (CommandLine.Has("-wf-inventory") || CommandLine.Has("-wf-chat")) StartCoroutine(OpenInventoryWhenSpawned());
            var screen = CommandLine.Get("-wf-overlay");
            if (screen == "options") overlay = Overlay.Options;
            else if (screen == "controls") overlay = Overlay.Controls;
            else if (screen == "quality") overlay = Overlay.Quality;
            else if (screen == "sounds") overlay = Overlay.Sounds;
            else if (screen == "packs") OpenResourcePacks();

            var screenshot = CommandLine.Get("-wf-screenshot");
            if (screenshot != null) StartCoroutine(CaptureAndQuit(screenshot));
        }

        /// <summary>The local flat test world (development only, like Minosoft's LocalConnection).</summary>
        private void StartLocal()
        {
            var session = new GameSession(resources, Settings.ViewDistance);
            StartSession(session);
            session.Prepare();
        }

        private async void Connect(string address, string username)
        {
            loginMessage = "Resolving " + address + "...";
            var (host, port) = await ServerAddress.ResolveAsync(address);
            loginMessage = null;
            var server = new ServerSession(host, port, username, resources.Blocks, resources.Items, resources.EntityTypes)
            {
                ViewDistance = Settings.ViewDistance,
                WebSocket = ServerAddress.WebSocketUri(address),
            };
            var session = new GameSession(resources, Settings.ViewDistance, server) { NetworkBudget = 4 };
            StartSession(session);
            try
            {
                await server.ConnectAsync();
            }
            catch (Exception exception)
            {
                if (Session == session) StopSession("Can not connect to " + address + ": " + exception.Message);
            }
        }

        private void StartSession(GameSession session)
        {
            Session = session;
            Renderer = new WorldRenderer(session.World, new SectionMesher(resources.Colors, resources.Fluids), Modern ? realisticMaterials : classicMaterials, Modern, transform);
            outline = new BlockOutline(transform);
            lightSources = new LightSourceManager(session.World, transform);
            lightSources.SectionVisible = position => Renderer == null || Renderer.IsVisible(position);
            CreateSessionRenderers();
            session.Sound += OnSound;
            session.Particle += request => particles.Spawn(request);
            session.Record += (position, sound) => sounds?.Record(position, sound);
            session.ResourcePackOffered += OnResourcePackOffered;
            session.ResourcePackRemoved += OnResourcePackRemoved;
            session.SignEditorOpened += (position, front) => textScreens.OpenSign(position, front);
            session.BookOpened += (stack, slot, edit) => textScreens.OpenBook(stack, slot, edit);
            var perspective = CommandLine.Get("-wf-perspective");
            if (perspective != null) input.Perspective = int.Parse(perspective);
            if (CommandLine.Has("-wf-nohud")) input.HideGui = true;
            ApplyRenderMode();
            accumulator = 0;
            if (!CommandLine.Has("-wf-screenshot") || input.TouchActive) input.SetCursorLocked(true);
        }

        private void StopSession(string reason)
        {
            cloudPass.Render(null, false);
            var session = Session;
            if (session == null) return;
            Session = null;
            session.Server?.Disconnect(reason ?? "Disconnected");
            Renderer?.Dispose();
            Renderer = null;
            lightSources?.Dispose();
            lightSources = null;
            outline?.Dispose();
            outline = null;
            DisposeSessionRenderers();
            sounds?.StopAll();
            dynamicLights?.Clear();
            particles?.Clear();
            OnSessionStoppedPacks();
            chat.Open = false;
            disconnectMessage = reason;
            input.SetCursorLocked(false);
            environment.SetEnabled(false, viewCamera);
            RenderOrigin.Set(0, 0);
        }

        /// <summary>
        /// Automated visual check: "-wf-screenshot out.png [-wf-pose x y z yaw pitch] [-wf-time ticks]" waits until every
        /// section is meshed, saves a screenshot and quits.
        /// </summary>
        private System.Collections.IEnumerator CaptureAndQuit(string path)
        {
            daylightCycle = false;
            var time = CommandLine.Get("-wf-time");
            if (time != null) pinnedTime = long.Parse(time);
            var pose = CommandLine.GetValues("-wf-pose", 5);
            if (pose != null && Session != null)
            {
                Session.Player.Teleport(new Vec3d(Parse(pose[0]), Parse(pose[1]), Parse(pose[2])));
                Session.Player.Yaw = (float)Parse(pose[3]);
                Session.Player.Pitch = (float)Parse(pose[4]);
                Session.Player.Flying = true;
            }
            var frames = 0;
            if (CommandLine.Has("-wf-title"))
            {
                for (var i = 0; i < 90; i++) yield return null;
            }
            while (Session != null && !Session.Spawned && frames < 1200)
            {
                frames++;
                yield return null;
            }
            while (CommandLine.Has("-wf-chat") && !commandsDone) yield return null;
            var wait = CommandLine.Get("-wf-wait");
            if (wait != null) yield return new WaitForSecondsRealtime(float.Parse(wait, System.Globalization.CultureInfo.InvariantCulture));
            frames = 0;
            while (Session != null && frames < 600 && (frames < 60 || Renderer.QueueSize > 0 || Renderer.RunningCount > 0))
            {
                frames++;
                yield return null;
            }
            for (var i = 0; i < 60; i++) yield return null;
            var bench = CommandLine.Get("-wf-bench");
            if (bench != null)
            {
                // frame times without vsync, over the given seconds
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                for (var i = 0; i < 30; i++) yield return null;
                var seconds = float.Parse(bench, System.Globalization.CultureInfo.InvariantCulture);
                var times = new System.Collections.Generic.List<float>();
                var start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < seconds)
                {
                    yield return null;
                    times.Add(Time.unscaledDeltaTime * 1000);
                }
                times.Sort();
                var average = 0f;
                foreach (var t in times) average += t;
                average /= Mathf.Max(1, times.Count);
                Debug.Log($"Benchmark: {1000 / average:0.0} fps, {average:0.00} ms average, {times[times.Count / 2]:0.00} ms median, {times[(int)(times.Count * 0.99f)]:0.00} ms p99, {Renderer.SectionCount} sections, {lightSources.SourceCount} light sources");
            }
            yield return new WaitForEndOfFrame();
            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Debug.Log("Screenshot saved to " + path);
            Application.Quit();
        }

        private static double Parse(string value) => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Classic: Minosoft's look (lightmap, no real lights). Realistic: lit PBR shaders and the water shader.</summary>
        private void CreateMaterials()
        {
            var classic = Resources.Load<Shader>("WhiteFox/Block");
            var lit = Resources.Load<Shader>("WhiteFox/BlockLit");
            var water = Resources.Load<Shader>("WhiteFox/Water");

            Material Opaque(Shader shader, string name)
            {
                var material = new Material(shader) { name = name, renderQueue = (int)RenderQueue.AlphaTest };
                SetTextures(material);
                material.SetFloat("_Cutoff", 0.1f);
                return material;
            }

            Material Translucent(Shader shader, string name)
            {
                var material = new Material(shader) { name = name, renderQueue = (int)RenderQueue.Transparent };
                SetTextures(material);
                material.SetFloat("_Cutoff", 0.004f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0);
                material.SetOverrideTag("RenderType", "Transparent");
                return material;
            }

            var classicTranslucent = Translucent(classic, "WhiteFox classic translucent");
            // the glass part draws like any cutout block in the classic mode
            var classicOpaque = Opaque(classic, "WhiteFox classic opaque");
            classicMaterials = new[] { classicOpaque, classicTranslucent, classicTranslucent, classicOpaque };
            classicBlockShader = classic;
            litBlockShader = lit;
            blockItemMaterial = Opaque(classic, "WhiteFox block items");

            var waterMaterial = new Material(water) { name = "WhiteFox water", renderQueue = (int)RenderQueue.Transparent };
            SetTextures(waterMaterial);
            var glassMaterial = Translucent(lit, "WhiteFox lit glass");
            glassMaterial.SetFloat("_Glass", 1);
            realisticMaterials = new[] { Opaque(lit, "WhiteFox lit opaque"), Translucent(lit, "WhiteFox lit translucent"), waterMaterial, glassMaterial };
        }

        private void SetTextures(Material material)
        {
            material.SetTexture("_Textures", textures.Texture);
            if (material.HasProperty("_Normals")) material.SetTexture("_Normals", textures.Normals);
            if (material.HasProperty("_Specular")) material.SetTexture("_Specular", textures.Specular);
        }

        private void ApplyRenderMode()
        {
            // modern quality: relief strength, simple or realistic water, block lights, shadows, post processing
            var relief = new[] { 0.0f, 1.0f, 2.0f }[Mathf.Clamp(Settings.Relief, 0, 2)];
            realisticMaterials[0].SetFloat("_NormalStrength", relief);
            realisticMaterials[1].SetFloat("_NormalStrength", relief);
            // the renderers keep their per entity light (_Light), which the lit shader uses instead of the vertex light
            var itemShader = Modern ? litBlockShader : classicBlockShader;
            if (blockItemMaterial.shader != itemShader)
            {
                blockItemMaterial.shader = itemShader;
                blockItemMaterial.renderQueue = (int)RenderQueue.AlphaTest;
                SetTextures(blockItemMaterial);
            }
            blockItemMaterial.SetFloat("_NormalStrength", relief);
            realisticMaterials[3].SetFloat("_NormalStrength", relief);
            var modernMaterials = new[] { realisticMaterials[0], realisticMaterials[1], Settings.RealisticWater ? realisticMaterials[2] : realisticMaterials[1], realisticMaterials[3] };
            Renderer.SetMaterials(Modern ? modernMaterials : classicMaterials, Modern && Settings.SunShadows > 0);
            environment.SetEnabled(ModernSky, viewCamera);
            environment.ApplyQuality(Settings, viewCamera);
            // after the realistic environment: both share the cloud and camera settings
            enhanced.SetEnabled(Enhanced, viewCamera, Settings);
            if (!Modern && !Enhanced) Shader.SetGlobalFloat("_WF_WindStrength", 0);
            lightSources.Enabled = Modern && Settings.BlockLights > 0;
            lightSources.MaxLights = new[] { 0, 16, 48, 96 }[Mathf.Clamp(Settings.BlockLights, 0, 3)];
            lightSources.ShadowedLights = Settings.BlockLightShadows;
            Shader.SetGlobalFloat("_WF_WaterReflectionSteps", Settings.WaterReflections == 0 ? 0 : Settings.WaterReflections == 1 ? 20 : 40);
            Shader.SetGlobalFloat("_WF_ModernEntities", Modern ? 1 : 0);
            entityRenderer.CastShadows = Modern && Settings.SunShadows > 0;
            blockEntityRenderer.CastShadows = entityRenderer.CastShadows;
        }

        private void SetupCamera()
        {
            viewCamera = Camera.main;
            if (viewCamera == null)
            {
                viewCamera = new GameObject("Main Camera").AddComponent<Camera>();
                viewCamera.tag = "MainCamera";
            }
            viewCamera.nearClipPlane = 0.02f;
            viewCamera.clearFlags = CameraClearFlags.SolidColor;

            // the scene's directional light is not used by the block shader
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None)) light.enabled = false;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
        }

        private System.Collections.IEnumerator OpenInventoryWhenSpawned()
        {
            while (Session == null || !Session.Spawned) yield return null;
            // automated tests start alive: a test player that died in an earlier run respawns first
            for (var i = 0; i < 20; i++) yield return null;
            if (Session.Dead)
            {
                Session.Respawn();
                for (var i = 0; i < 40; i++) yield return null;
            }
            var commands = CommandLine.Get("-wf-chat");
            if (commands != null)
            {
                foreach (var command in commands.Split(';'))
                {
                    if (Session == null) yield break; // disconnected by a command
                    Session.SendChat(command.Trim());
                    for (var i = 0; i < 10; i++) yield return null;
                }
                for (var i = 0; i < 40; i++) yield return null;
            }
            if (Session == null) yield break;
            if (CommandLine.Has("-wf-inventory")) Session.Inventory.OpenPlayerScreen();
            if (CommandLine.Has("-wf-holduse")) PlayerInputReader.HoldUseForTest = true;
            // test helpers: right click a block (-wf-use x,y,z) or the nearest entity of a type (-wf-interact villager)
            var use = CommandLine.Get("-wf-use");
            if (use != null && Session.Server != null)
            {
                var parts = use.Split(',');
                Session.Server.SendUseItemOn(new Core.Util.BlockPos(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])), Core.Util.Direction.Up, 0.5f, 1, 0.5f);
                for (var i = 0; i < 30; i++) yield return null;
            }
            if (CommandLine.Has("-wf-useitem") && Session.Server != null)
            {
                Session.Server.SendUseItem();
                for (var i = 0; i < 60; i++) yield return null;
            }
            var interact = CommandLine.Get("-wf-interact");
            if (interact != null && Session.Server != null)
            {
                Core.Entities.Entity nearest = null;
                foreach (var entity in Session.Entities.Entities.Values)
                {
                    if (entity.Type?.Name.Path != interact) continue;
                    if (nearest == null || (entity.Position - Session.Player.Position).LengthSquared < (nearest.Position - Session.Player.Position).LengthSquared) nearest = entity;
                }
                if (nearest != null) Session.Server.SendInteract(nearest.Id, 0, false);
                for (var i = 0; i < 30; i++) yield return null;
            }
            commandsDone = true;
        }

        /// <summary>F11 (vanilla): borderless fullscreen on the current display, or back to a resizable window.</summary>
        private void UpdateFullscreenKey()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || Application.isMobilePlatform || !Settings.Fullscreen.WasPressed(keyboard)) return;
            ToggleFullscreen();
        }

        public static bool Fullscreen => Screen.fullScreenMode != FullScreenMode.Windowed;

        /// <summary>Borderless fullscreen at the display's resolution, or a resizable 1280x720 window (Unity remembers it).</summary>
        public static void ToggleFullscreen()
        {
            if (!Fullscreen)
            {
                var display = Screen.mainWindowDisplayInfo;
                Screen.SetResolution(display.width, display.height, FullScreenMode.FullScreenWindow);
            }
            else
            {
                Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            }
        }

        /// <summary>The inventory, chat, perspective and pause buttons of the touch controls.</summary>
        private void HandleTouchButtons()
        {
            var touch = input.Touch;
            if (touch == null) return;
            if (touch.OpenInventory && !Session.Inventory.AnyOpen)
            {
                Session.Inventory.OpenPlayerScreen();
                input.SetCursorLocked(false);
            }
            if (touch.OpenChat && !chat.Open) chat.Show("");
            if (touch.TogglePerspective) input.Perspective = (input.Perspective + 1) % 3;
            if (touch.ToggleDebug) input.DebugScreen = !input.DebugScreen;
            if (touch.Pause) input.SetCursorLocked(false);
            touch.OpenInventory = touch.OpenChat = touch.TogglePerspective = touch.ToggleDebug = touch.Pause = false;
        }

        /// <summary>
        /// Esc like vanilla: an options screen goes back (to the options, then to the game or the title), an open
        /// inventory or container closes, the pause menu resumes; only while playing does it open the pause menu
        /// (PlayerInputReader). Chat, signs and books close themselves.
        /// </summary>
        private void HandleEscape()
        {
            if (packOffers.Count > 0 || chat.Open || textScreens.Open) return;
            if (Session == null && overlay == Overlay.None && title != null && title.Back())
            {
                input.EscapeConsumedFrame = Time.frameCount;
                return;
            }
            if (overlay != Overlay.None)
            {
                Settings.Save();
                overlay = overlay == Overlay.Options ? Overlay.None : Overlay.Options;
                input.EscapeConsumedFrame = Time.frameCount;
                return;
            }
            if (Session == null || !Session.Spawned || Session.Dead) return;
            if (Session.Inventory.AnyOpen)
            {
                Session.Inventory.Close();
                input.SetCursorLocked(true);
                input.EscapeConsumedFrame = Time.frameCount;
                return;
            }
            if (!input.CursorLocked)
            {
                // the pause menu is open: back to the game
                input.SetCursorLocked(true);
                input.EscapeConsumedFrame = Time.frameCount;
            }
        }

        /// <summary>E opens the inventory, E or Esc close it; windows opened by the server free the mouse.</summary>
        private void UpdateInventoryKeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !Session.Spawned || chat.Open || overlay != Overlay.None || textScreens.Open) return;
            var inventory = Session.Inventory;
            if (inventory.AnyOpen)
            {
                var creativeSearch = inventory.Open == null && (!Session.IsRemote || Session.Player.Gamemode == Core.Physics.Gamemode.Creative) || inventoryScreen.TypingText;
                if (!creativeSearch && Settings.Inventory.WasPressed(keyboard))
                {
                    inventory.Close();
                    input.SetCursorLocked(true);
                }
                else if (input.CursorLocked)
                {
                    input.SetCursorLocked(false);
                }
                return;
            }
            if (input.CursorLocked && Settings.Inventory.WasPressed(keyboard))
            {
                inventory.OpenPlayerScreen();
                input.SetCursorLocked(false);
            }
        }

        /// <summary>The vanilla title screen panorama as a skybox (6 faces in a texture array).</summary>
        private void CreatePanorama()
        {
            var faces = new Core.Util.RgbaImage[6];
            for (var i = 0; i < 6; i++)
            {
                if (!resources.Assets.TryGet("assets/minecraft/textures/gui/title/background/panorama_" + i + ".png", out var data)) return;
                faces[i] = Core.Util.PngDecoder.Decode(data);
            }
            var size = faces[0].Width;
            if (size < 16) return; // placeholder (no asset index available)
            panoramaFaces = new Texture2DArray(size, size, 6, TextureFormat.RGBA32, false, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (var i = 0; i < 6; i++)
            {
                var image = faces[i].Scale(size, size);
                var flipped = new byte[image.Pixels.Length];
                var stride = size * 4;
                for (var y = 0; y < size; y++) Buffer.BlockCopy(image.Pixels, y * stride, flipped, (size - 1 - y) * stride, stride);
                panoramaFaces.SetPixelData(flipped, 0, i);
            }
            panoramaFaces.Apply(false, true);
            panorama = new Material(Resources.Load<Shader>("WhiteFox/Panorama")) { name = "WhiteFox panorama" };
            panorama.SetTexture("_Faces", panoramaFaces);
            panorama.SetFloat("_Brightness", 0.85f);

        }

        private void UpdateTitleBackground()
        {
            if (panorama == null || viewCamera == null) return;
            panoramaYaw += Time.deltaTime * 2.5f;
            viewCamera.clearFlags = CameraClearFlags.Skybox;
            RenderSettings.skybox = panorama;
            RenderSettings.fog = false;
            viewCamera.transform.rotation = Quaternion.Euler(10, panoramaYaw, 0);
        }

        /// <summary>F3 line: block entities, entities and sounds.</summary>
        public string DebugInfo => $"E: {entityRenderer?.Count ?? 0} entities, {blockEntityRenderer?.Count ?? 0} block entities, sounds {sounds?.Playing ?? 0} playing ({sounds?.Loaded ?? 0} loaded)";

        private void OnButtonClicked() => sounds?.PlayUi("ui.button.click");

        private void OnSound(Core.Game.SoundRequest request) => sounds?.Play(request);

        /// <summary>Renderers that depend on the session and on the loaded assets.</summary>
        private void CreateSessionRenderers()
        {
            var session = Session;
            breakOverlay = new BreakOverlay(transform, classicMaterials[1], resources.DestroyStages);
            skins = new SkinManager(session, gui.Assets);
            maps.Assets = gui.Assets;
            entityRenderer = new EntityRenderer(session, transform, gui.Assets, itemModels, skins, blockItemMaterial) { Maps = maps, Font = gui.Font };
            firstPerson = new FirstPersonRenderer(session, viewCamera, itemModels, skins) { Maps = maps };
            blockEntityRenderer = new BlockEntityRenderer(session, Renderer, transform, gui.Assets, gui.Font, skins);
        }

        private void DisposeSessionRenderers()
        {
            breakOverlay?.Dispose();
            breakOverlay = null;
            entityRenderer?.Dispose();
            entityRenderer = null;
            blockEntityRenderer?.Dispose();
            blockEntityRenderer = null;
            firstPerson?.Dispose();
            firstPerson = null;
            skins?.Dispose();
            skins = null;
        }

        private void Update()
        {
            UpdateFullscreenKey();
            // the 3D view renders at the chosen fraction of the window; URP upscales it, the GUI is drawn on top at full size
            if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset pipeline && Mathf.Abs(pipeline.renderScale - Settings.RenderScale) > 0.001f)
            {
                pipeline.renderScale = Settings.RenderScale;
            }
            if (reloading)
            {
                Session?.ProcessNetwork(6);
                return;
            }
            sounds?.Update(Session != null && Session.Spawned ? Session : null);
            // Esc while a key is being rebound only cancels the rebinding
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            var escape = keyboard != null && keyboard.escapeKey.wasPressedThisFrame && controls.Waiting == null;
            controls.Capture();
            if (escape) HandleEscape();
            if (Session == null)
            {
                UpdateTitleBackground();
                return;
            }
            RenderSettings.fog = true;
            fps = Mathf.Lerp(fps, 1.0f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);

            Session.ProcessNetwork(6);
            if (Session.DisconnectReason != null)
            {
                StopSession(Session.DisconnectReason);
                return;
            }

            controls.Capture();
            UpdateInventoryKeys();
            if (textScreens.Open)
            {
                if (input.CursorLocked) input.SetCursorLocked(false);
                textScreenWasOpen = true;
            }
            else if (textScreenWasOpen)
            {
                textScreenWasOpen = false;
                input.SetCursorLocked(true);
            }
            if (overlay == Overlay.None && !textScreens.Open) input.Read(Session, chat);
            HandleTouchButtons();
            if (CommandLine.Has("-wf-tab")) input.PlayerList = true;
            if (CommandLine.Has("-wf-debug")) input.DebugScreen = true;
            if (input.ToggleRenderMode || ModernSky != environment.Enabled)
            {
                if (input.ToggleRenderMode) Settings.Graphics = Settings.NextGraphics(Settings.Graphics);
                input.ToggleRenderMode = false;
                ApplyRenderMode();
            }
            if (Session.ChunkManager != null) Session.ChunkManager.ViewDistance = Settings.ViewDistance;
            // the server learns a changed render distance once the slider rests for half a second
            if (Settings.ViewDistance != sentViewDistance)
            {
                if (Settings.ViewDistance != pendingViewDistance)
                {
                    pendingViewDistance = Settings.ViewDistance;
                    viewDistanceChanged = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - viewDistanceChanged > 0.5f && Session.Server != null)
                {
                    Session.Server.UpdateViewDistance(Settings.ViewDistance);
                    sentViewDistance = Settings.ViewDistance;
                }
            }
            Renderer.ViewDistance = Settings.ViewDistance;
            Renderer.CullingEnabled = !CommandLine.Has("-wf-nocull");

            // fixed 20 TPS game loop (Minosoft's SessionTicker), at most 10 ticks per frame to avoid a spiral of death
            accumulator += Time.deltaTime;
            var ticks = 0;
            while (accumulator >= GameSession.TickSeconds && ticks < 10)
            {
                if (pinnedTime.HasValue) Session.World.TimeOfDay = pinnedTime.Value;
                else if (!daylightCycle) Session.World.TimeOfDay = 6000;
                Session.Tick();
                firstPerson.Tick();
                particles.Tick(Session.World);
                if (Session.Spawned)
                {
                    particles.TickAmbient(Session);
                    weather.Tick(Session, particles);
                }
                accumulator -= GameSession.TickSeconds;
                ticks++;
            }
            if (ticks == 10) accumulator = 0;
            else if (ticks == 0) Session.UpdateTarget(); // rotation changes every frame
            textures.Animate(Session.Ticks);

            partialTick = (float)(accumulator / GameSession.TickSeconds);
            UpdateCamera(partialTick);
            UpdateEnvironment();
            entityRenderer.ShowLocalPlayer = input.Perspective != 0;
            entityRenderer.Update(partialTick);
            blockEntityRenderer.Update(partialTick);
            particles.Render(viewCamera, partialTick);
            weather.RealisticSky = environment.Enabled;
            weather.CloudsEnabled = !(environment.Enabled && environment.VolumetricClouds) && !enhanced.VolumetricClouds;
            // the volumetric clouds at half resolution, for the sky drawn this frame
            var overworldSky = Session.Dimension.Effects == "minecraft:overworld";
            cloudPass.Render(viewCamera, environment.Enabled && environment.VolumetricClouds || enhanced.VolumetricClouds && overworldSky);
            weather.Render(Session, viewCamera, partialTick, Settings.ViewDistance);
            var cameraPosition = viewCamera.transform.position;
            dynamicLights.Update(Session, new Vec3d(cameraPosition.x + RenderOrigin.X, cameraPosition.y, -cameraPosition.z + RenderOrigin.Z), partialTick, lightSources);
            firstPerson.Update(partialTick, input.Perspective == 0 && !input.HideGui);
            Renderer.Update(viewCamera.transform.position);
            lightSources.Exposure = environment.Exposure;
            lightSources.Update(viewCamera.transform.position, Time.time, viewCamera);
            outline.Update(Session.Target);
            breakOverlay.Update(Session.DiggingProgress > 0 ? Session.Target?.Position : null, Session.DiggingProgress);
            if (Session.Dead && input.CursorLocked) input.SetCursorLocked(false);
        }

        private void UpdateCamera(float partialTick)
        {
            var player = Session.Player;
            var position = Vec3d.Lerp(player.PreviousPosition, player.Position, partialTick);
            var eye = Mathf.Lerp(player.PreviousEyeHeight, player.EyeHeight, partialTick);
            var cameraTransform = viewCamera.transform;
            var yaw = player.Yaw;
            var pitch = player.Pitch;
            var eyePosition = new Vec3d(position.X, position.Y + eye, position.Z);
            if (input.Perspective != 0)
            {
                // third person (vanilla Camera.setup): behind, or in front looking back, moved back up to 4 blocks until a block is hit
                if (input.Perspective == 2)
                {
                    yaw += 180;
                    pitch = -pitch;
                }
                var back = MathUtil.Front(yaw, pitch) * -1;
                var distance = 4.0;
                for (var i = 0; i < 8; i++)
                {
                    var offset = new Vec3d(((i & 1) * 2 - 1) * 0.1, ((i >> 1 & 1) * 2 - 1) * 0.1, ((i >> 2 & 1) * 2 - 1) * 0.1);
                    var hit = Core.Game.Raycast.Blocks(Session.World, eyePosition + offset, back, distance);
                    if (hit.HasValue && hit.Value.Distance < distance) distance = hit.Value.Distance;
                }
                eyePosition += back * distance;
            }
            RenderOrigin.Follow(eyePosition.X, eyePosition.Z);
            cameraTransform.position = RenderOrigin.ToRender(eyePosition.X, eyePosition.Y, eyePosition.Z);
            cameraTransform.rotation = Quaternion.Euler(pitch, yaw + 180.0f, 0);
            if (input.Perspective == 0 && firstPerson != null)
            {
                // view bobbing: the inverse of vanilla's bob of the view matrix, in render space
                var flip = Matrix4x4.Scale(new Vector3(1, 1, -1));
                var bob = (flip * firstPerson.BobMatrix(partialTick) * flip).inverse;
                cameraTransform.position += cameraTransform.rotation * (Vector3)bob.GetColumn(3);
                cameraTransform.rotation *= Quaternion.LookRotation(bob.GetColumn(2), bob.GetColumn(1));
            }

            var targetFov = Settings.FieldOfView * (player.Sprinting ? 1.15f : 1.0f);
            viewCamera.fieldOfView = Mathf.Lerp(viewCamera.fieldOfView, targetFov, 1 - Mathf.Exp(-Time.deltaTime * 12));
            viewCamera.farClipPlane = Settings.ViewDistance * 16 + 32;
        }

        /// <summary>Day light, sky color and fog from the time of day (vanilla celestial angle).</summary>
        /// <summary>The realistic sky and sun only exist in dimensions with a sky (the overworld).</summary>
        private bool ModernSky => Modern && (Session == null || Session.Dimension.Effects == "minecraft:overworld");

        private static Color Rgb(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        private float bossDarken;

        private void UpdateEnvironment()
        {
            var time = Session.World.TimeOfDay / 24000.0;
            var angle = time - 0.25;
            angle -= Math.Floor(angle);
            angle += (1.0 - (Math.Cos(angle * Math.PI) + 1.0) / 2.0 - angle) / 3.0;
            var daylight = Mathf.Clamp01((float)Math.Cos(angle * Math.PI * 2.0) * 2.0f + 0.5f);
            // rain and thunder darken the sky (vanilla Level.getSkyDarken), lightning flashes it
            var rain = Session.World.RainLevel;
            var overworldWeather = Session.Dimension.Effects == "minecraft:overworld";
            var thunder = Session.World.ThunderLevel * rain;
            daylight *= 1 - rain * 5 / 16f;
            daylight *= 1 - thunder * 5 / 16f;
            if (entityRenderer != null && entityRenderer.Lightning) daylight = 1;

            Shader.SetGlobalFloat("_WF_DayLight", 0.15f + 0.85f * daylight);
            Shader.SetGlobalFloat("_WF_Brightness", Settings.Brightness);

            // sky and fog colors of the biome at the camera (vanilla FogRenderer / Biome effects), per dimension
            var eye = Session.Player.EyePosition;
            var biome = Session.World.GetBiome((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z));
            var effects = Session.Dimension.Effects;
            Color sky, fog;
            var fogEnd = Settings.ViewDistance * 16f;
            var fogStart = fogEnd * 0.7f;
            if (effects == "minecraft:the_nether")
            {
                // no sky, thick fog in the biome's color
                sky = fog = Rgb(biome.FogColor);
                fogEnd = Mathf.Min(fogEnd, 192) * 0.5f;
                fogStart = fogEnd * 0.05f;
                Shader.SetGlobalFloat("_WF_DayLight", 1.0f);
            }
            else if (effects == "minecraft:the_end")
            {
                sky = new Color(0.157f, 0.157f, 0.157f);
                fog = Rgb(0xA080A0) * 0.15f;
                Shader.SetGlobalFloat("_WF_DayLight", 1.0f);
            }
            else
            {
                sky = Color.Lerp(new Color(0.01f, 0.01f, 0.03f), Rgb(biome.SkyColor), daylight);
                fog = Color.Lerp(new Color(0.02f, 0.02f, 0.05f), Color.Lerp(Rgb(biome.FogColor), Rgb(biome.SkyColor), 0.25f), daylight);
                if (rain > 0)
                {
                    // gray, desaturated sky in rain (vanilla getSkyColor)
                    var gray = (sky.r * 0.3f + sky.g * 0.59f + sky.b * 0.11f) * 0.6f;
                    sky = Color.Lerp(sky, new Color(gray, gray, gray), rain * 0.75f);
                    var fogGray = (fog.r * 0.3f + fog.g * 0.59f + fog.b * 0.11f) * 0.6f;
                    fog = Color.Lerp(fog, new Color(fogGray, fogGray, fogGray), rain * 0.5f);
                }
            }
            // boss bars: "darken sky" (the wither) fades the world darker, "create fog" (the dragon) brings in thick fog
            // (vanilla GameRenderer.darkenWorldAmount and FogRenderer.setupFog with shouldCreateWorldFog)
            var bossFlags = Session.ServerUi.BossFlags;
            bossDarken = Mathf.MoveTowards(bossDarken, (bossFlags & 0x01) != 0 ? 1 : 0, Time.deltaTime * ((bossFlags & 0x01) != 0 ? 1.0f : 0.25f));
            if (bossDarken > 0)
            {
                sky = Color.Lerp(sky, new Color(sky.r * 0.7f, sky.g * 0.6f, sky.b * 0.6f), bossDarken);
                fog = Color.Lerp(fog, fog * 0.7f, bossDarken);
                Shader.SetGlobalFloat("_WF_DayLight", Shader.GetGlobalFloat("_WF_DayLight") * (1 - bossDarken * 0.3f));
            }
            var bossFog = (bossFlags & 0x04) != 0;
            if (bossFog)
            {
                fogEnd = Mathf.Min(fogEnd, 192) * 0.5f;
                fogStart = fogEnd * 0.05f;
            }
            sky.a = fog.a = 1;
            viewCamera.backgroundColor = fog;
            RenderSettings.fogColor = fog;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            RenderSettings.ambientSkyColor = sky;

            // the realistic mode uses its own sky model; fog starts later because the atmosphere already fades
            environment.Rain = overworldWeather ? rain : 0;
            environment.Outdoors = (Session.World.GetLight((int)Math.Floor(eye.X), (int)Math.Floor(eye.Y), (int)Math.Floor(eye.Z)) >> 4) / 15f;
            environment.Thunder = overworldWeather ? Session.World.ThunderLevel : 0;
            environment.ViewDirection = viewCamera.transform.forward;
            enhanced.Update(Session.World.TimeOfDay, sky, fog, environment.Rain, environment.Thunder, overworldWeather);
            if (environment.Enabled) environment.Update(Session.World.TimeOfDay, bossFog ? fogStart : Settings.ViewDistance * 16 * 0.55f, bossFog ? fogEnd : Settings.ViewDistance * 16);

            // night vision lights everything up (vanilla: full brightness lightmap)
            if (Session.Player.EffectLevel(Core.Physics.Effects.NightVision) > 0)
            {
                Shader.SetGlobalFloat("_WF_DayLight", 1.0f);
                Shader.SetGlobalFloat("_WF_Brightness", 1.0f);
                Shader.SetGlobalFloat("_WF_AmbientStrength", 2.5f);
            }

            // under water / in lava: short, colored fog (vanilla FogRenderer)
            var player = Session.Player;
            if (player.EyeInWater || player.EyeInLava)
            {
                var water = player.EyeInWater;
                var day = Mathf.Clamp01(Shader.GetGlobalFloat("_WF_DayLight"));
                var color = water ? Rgb(biome.WaterFogColor) * (0.3f + 0.7f * day) : new Color(0.6f, 0.1f, 0.0f); // biome water fog color
                color.a = 1;
                RenderSettings.fogColor = color;
                viewCamera.backgroundColor = color;
                RenderSettings.fogStartDistance = water ? 0 : 0;
                RenderSettings.fogEndDistance = water ? (player.EffectLevel(Core.Physics.Effects.WaterBreathing) > 0 || player.EffectLevel(Core.Physics.Effects.ConduitPower) > 0 ? 96 : 40) : 2;
            }
        }

        private void OnGUI()
        {
            if (resources == null || gui == null)
            {
                var text = error != null ? "Error: " + error + "\n(see console)" : status + "...";
                GUI.Label(new Rect(20, Screen.height / 2 - 20, Screen.width - 40, 60), "WhiteFox\n" + text);
                return;
            }
            gui.Begin();
            if (reloading)
            {
                MessageScreen.Draw(gui, "Reloading resources", (reloadStatus ?? "") + "...", null);
                return;
            }
            if (packOffers.Count > 0)
            {
                DrawResourcePackPrompt(Session != null && Session.Spawned);
                return;
            }
            if (overlay != Overlay.None)
            {
                DrawOverlay(Session != null && Session.Spawned);
                return;
            }

            if (Session == null)
            {
                if (disconnectMessage != null)
                {
                    if (MessageScreen.Draw(gui, "Disconnected", disconnectMessage, "Back to Title Screen")) disconnectMessage = null;
                    return;
                }
                if (loginMessage != null)
                {
                    MessageScreen.Draw(gui, "Joining Server", loginMessage, null);
                    return;
                }
                switch (title.Draw(gui, null))
                {
                    case TitleScreen.Action.Connect:
                        Connect(title.AddressText, title.UsernameText);
                        break;
                    case TitleScreen.Action.Options:
                        overlay = Overlay.Options;
                        break;
                    case TitleScreen.Action.Quit:
                        Application.Quit();
                        break;
                }
                return;
            }

            if (!Session.Spawned)
            {
                var text = (Session.Server?.Status ?? "Loading") + "...";
                if (Session.World.ChunkCount > 0) text += "\n" + Session.World.ChunkCount + " chunks";
                if (MessageScreen.Draw(gui, Session.IsRemote ? "Connecting to the server" : "Loading world", text, "Cancel")) StopSession(null);
                return;
            }

            // F1 (vanilla hideGui): only the world, for screenshots
            if (input.HideGui && !chat.Open && !Session.Dead && !Session.Inventory.AnyOpen && !textScreens.Open) return;
            entityRenderer?.DrawNameTags(gui, viewCamera, partialTick);
            hud.Draw(Session, this, input, chat, fps);
            input.GuiScale = gui.Scale;
            if (input.TouchActive && input.CursorLocked && !chat.Open && !Session.Dead && !Session.Inventory.AnyOpen && !textScreens.Open) input.Touch.Draw(gui, Session);
            if (textScreens.Open)
            {
                textScreens.Draw(Session);
                return;
            }
            if (input.PlayerList && Session.IsRemote && !chat.Open) PlayerListOverlay.Draw(gui, Session);
            if (Session.Dead)
            {
                switch (DeathScreen.Draw(gui, Session.DeathMessage, Session.ExperienceTotal))
                {
                    case 1:
                        Session.Respawn();
                        input.SetCursorLocked(true);
                        break;
                    case 2:
                        StopSession(null);
                        break;
                }
                return;
            }
            if (Session.Inventory.AnyOpen)
            {
                inventoryScreen.DollRenderer = (width, height, mouse) => entityRenderer?.RenderDoll(width, height, mouse);
                inventoryScreen.Draw(Session);
                return;
            }
            if (!input.CursorLocked && !chat.Open && !CommandLine.Has("-wf-screenshot"))
            {
                switch (PauseScreen.Draw(gui, Session.IsRemote))
                {
                    case PauseScreen.Action.Resume:
                        input.SetCursorLocked(true);
                        break;
                    case PauseScreen.Action.Options:
                        overlay = Overlay.Options;
                        break;
                    case PauseScreen.Action.Leave:
                        StopSession(null);
                        break;
                }
            }
        }

        private void DrawOverlay(bool overGame)
        {
            if (overlay == Overlay.Controls)
            {
                if (controls.Draw(gui, Settings, overGame)) overlay = Overlay.Options;
                return;
            }
            if (overlay == Overlay.Sounds)
            {
                if (SoundScreen.Draw(gui, Settings, overGame)) overlay = Overlay.Options;
                return;
            }
            if (overlay == Overlay.ResourcePacks)
            {
                DrawResourcePacks(overGame);
                return;
            }
            if (overlay == Overlay.Quality)
            {
                var result = QualityScreen.Draw(gui, Settings, overGame);
                if (result == 2) overlay = Overlay.Options;
                if (result > 0 && Session != null) ApplyRenderMode();
                return;
            }
            switch (OptionsScreen.Draw(gui, Settings, overGame))
            {
                case OptionsScreen.Action.Controls:
                    overlay = Overlay.Controls;
                    break;
                case OptionsScreen.Action.Quality:
                    overlay = Overlay.Quality;
                    break;
                case OptionsScreen.Action.Sounds:
                    overlay = Overlay.Sounds;
                    break;
                case OptionsScreen.Action.ResourcePacks:
                    OpenResourcePacks();
                    break;
                case OptionsScreen.Action.Done:
                    overlay = Overlay.None;
                    break;
                case OptionsScreen.Action.GraphicsChanged:
                    if (Session != null) ApplyRenderMode();
                    break;
            }
        }

        private void OnApplicationQuit()
        {
            // nothing is lost when the window is closed while an options screen is open
            Settings?.Save();
        }

        private void OnDestroy()
        {
            Session?.Server?.Disconnect("Client closed");
            Renderer?.Dispose();
            entityRenderer?.Dispose();
            firstPerson?.Dispose();
            blockEntityRenderer?.Dispose();
            skins?.Dispose();
            itemModels?.Dispose();
            GuiRenderer.Clicked -= OnButtonClicked;
            sounds?.Dispose();
            icons?.Dispose();
            gui?.Dispose();
            if (panorama != null) Destroy(panorama);
            if (panoramaFaces != null) Destroy(panoramaFaces);
            textures?.Dispose();
            environment?.Dispose();
            enhanced?.Dispose();
            cloudPass.Dispose();
            lightSources?.Dispose();
            if (blockItemMaterial != null) Destroy(blockItemMaterial);
            foreach (var material in (classicMaterials ?? Array.Empty<Material>()))
            {
                if (material != null) Destroy(material);
            }
            foreach (var material in (realisticMaterials ?? Array.Empty<Material>()))
            {
                if (material != null) Destroy(material);
            }
        }

        /// <summary>Starts the client automatically in any scene that does not contain one.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<WhiteFoxClient>() != null) return;
            new GameObject("WhiteFox").AddComponent<WhiteFoxClient>();
        }
    }
}
