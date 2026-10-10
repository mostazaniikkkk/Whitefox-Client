// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Client.Entities;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Accounts;
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
        public bool daylightCycle = true;

        public GameSession Session { get; private set; }
        public WorldRenderer Renderer { get; private set; }

        private volatile string status = "Starting";
        private string error;
        private BlockTextureArray textures;
        private Material[] classicMaterials;
        private Material[] realisticMaterials;
        private RealisticEnvironment environment;
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
            Accounts,
        }
        private Overlay overlay;
        private readonly AccountsScreen accountsScreen = new AccountsScreen();
        /// <summary>Saved accounts (accounts.toml, tokens encrypted for the Windows user).</summary>
        private AccountStore accounts;
        /// <summary>Shown while a Microsoft account renews its session before joining.</summary>
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
            accounts = new AccountStore(System.IO.Path.Combine(Application.persistentDataPath, "accounts.toml")) { Protect = TokenProtection.Protect, Unprotect = TokenProtection.Unprotect };
            accounts.Load();
            RefreshSelectedAccount();
            input = new PlayerInputReader(Settings);
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
            if (CommandLine.Has("-wf-screenshot")) Settings.Persist = false;
            if (CommandLine.Has("-wf-classic")) Settings.Graphics = GraphicsMode.Classic;
            if (CommandLine.Has("-wf-modern")) Settings.Graphics = GraphicsMode.Modern;

            SetupCamera();
            environment = new RealisticEnvironment(transform);
            gui = new GuiRenderer(resources.Assets);
            icons = new ItemIcons(resources, gui.Assets, classicMaterials[0]);
            blockEntityModels = new BlockEntityRenderer(gui.Assets, gui.Font, transform);
            icons.SpecialParts = (name, tag) => blockEntityModels.ItemParts(resources, name, tag);
            itemModels = new ItemModels(resources, icons, classicMaterials[0]);
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
            CreatePanorama();
            status = null;

            var connect = CommandLine.Get("-wf-connect");
            if (connect != null) Connect(connect, Account.Offline(CommandLine.Get("-wf-name") ?? "WhiteFox"));
            else if ((CommandLine.Has("-wf-screenshot") && !CommandLine.Has("-wf-title")) || CommandLine.Has("-wf-local")) StartLocal();

            if (CommandLine.Has("-wf-inventory") || CommandLine.Has("-wf-chat")) StartCoroutine(OpenInventoryWhenSpawned());
            var screen = CommandLine.Get("-wf-overlay");
            if (screen == "options") overlay = Overlay.Options;
            else if (screen == "controls") overlay = Overlay.Controls;
            else if (screen == "quality") overlay = Overlay.Quality;
            else if (screen == "sounds") overlay = Overlay.Sounds;
            else if (screen == "accounts") overlay = Overlay.Accounts;
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

        /// <summary>The selected Microsoft account, or null when joining with the offline name.</summary>
        private Account MicrosoftAccount => accounts.Selected is Account selected && selected.IsOnline ? selected : null;

        /// <summary>
        /// Renews the Minecraft session of the selected Microsoft account in the background at start, so the refresh
        /// token keeps rotating and joining later does not wait; failures only show when joining.
        /// </summary>
        private async void RefreshSelectedAccount()
        {
            var account = MicrosoftAccount;
            if (account == null || account.HasValidToken || string.IsNullOrWhiteSpace(Settings.MicrosoftClientId)) return;
            try
            {
                await new MicrosoftAuth(Settings.MicrosoftClientId.Trim()).RefreshAsync(account);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("WhiteFox: can not renew the session of " + account.Name + ": " + exception.Message);
            }
            accounts.Save();
        }

        private async void Connect(string address, Account account)
        {
            if (account.IsOnline && !account.HasValidToken)
            {
                if (string.IsNullOrWhiteSpace(Settings.MicrosoftClientId))
                {
                    disconnectMessage = "microsoft.client_id is missing in " + Settings.ConfigPath;
                    return;
                }
                loginMessage = "Signing in as " + account.Name + "...";
                try
                {
                    await new MicrosoftAuth(Settings.MicrosoftClientId.Trim()).RefreshAsync(account);
                }
                catch (Exception exception)
                {
                    loginMessage = null;
                    accounts.Save();
                    disconnectMessage = exception is AuthException ? exception.Message : "Could not sign in: " + exception.Message;
                    return;
                }
                loginMessage = null;
                accounts.Save();
            }
            TitleScreen.MainAddress(address, out var host, out var port);
            var server = new ServerSession(host, port, account, resources.Blocks, resources.Items, resources.EntityTypes) { ViewDistance = Settings.ViewDistance };
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
            if (!CommandLine.Has("-wf-screenshot")) input.SetCursorLocked(true);
        }

        private void StopSession(string reason)
        {
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
            classicMaterials = new[] { Opaque(classic, "WhiteFox classic opaque"), classicTranslucent, classicTranslucent };

            var waterMaterial = new Material(water) { name = "WhiteFox water", renderQueue = (int)RenderQueue.Transparent };
            SetTextures(waterMaterial);
            realisticMaterials = new[] { Opaque(lit, "WhiteFox lit opaque"), Translucent(lit, "WhiteFox lit translucent"), waterMaterial };
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
            var modernMaterials = new[] { realisticMaterials[0], realisticMaterials[1], Settings.RealisticWater ? realisticMaterials[2] : realisticMaterials[1] };
            Renderer.SetMaterials(Modern ? modernMaterials : classicMaterials, Modern && Settings.SunShadows > 0);
            environment.SetEnabled(ModernSky, viewCamera);
            environment.ApplyQuality(Settings, viewCamera);
            lightSources.Enabled = Modern && Settings.BlockLights > 0;
            lightSources.MaxLights = new[] { 0, 16, 48, 96 }[Mathf.Clamp(Settings.BlockLights, 0, 3)];
            lightSources.ShadowedLights = Settings.BlockLightShadows;
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

        /// <summary>E opens the inventory, E or Esc close it; windows opened by the server free the mouse.</summary>
        private void UpdateInventoryKeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || !Session.Spawned || chat.Open || overlay != Overlay.None || textScreens.Open) return;
            var inventory = Session.Inventory;
            if (inventory.AnyOpen)
            {
                var creativeSearch = inventory.Open == null && (!Session.IsRemote || Session.Player.Gamemode == Core.Physics.Gamemode.Creative) || inventoryScreen.TypingText;
                if (keyboard.escapeKey.wasPressedThisFrame || (!creativeSearch && Settings.Inventory.WasPressed(keyboard)))
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
            entityRenderer = new EntityRenderer(session, transform, gui.Assets, itemModels, skins, classicMaterials[0]) { Maps = maps };
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
            if (reloading)
            {
                Session?.ProcessNetwork(6);
                return;
            }
            sounds?.Update(Session != null && Session.Spawned ? Session : null);
            controls.Capture();
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
            if (CommandLine.Has("-wf-tab")) input.PlayerList = true;
            if (CommandLine.Has("-wf-debug")) input.DebugScreen = true;
            if (input.ToggleRenderMode || ModernSky != environment.Enabled)
            {
                if (input.ToggleRenderMode) Settings.Graphics = Modern ? GraphicsMode.Classic : GraphicsMode.Modern;
                input.ToggleRenderMode = false;
                ApplyRenderMode();
            }
            if (Session.ChunkManager != null) Session.ChunkManager.ViewDistance = Settings.ViewDistance;
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
            weather.Render(Session, viewCamera, partialTick, Settings.ViewDistance);
            var cameraPosition = viewCamera.transform.position;
            dynamicLights.Update(Session, new Vec3d(cameraPosition.x + RenderOrigin.X, cameraPosition.y, -cameraPosition.z + RenderOrigin.Z), partialTick, lightSources);
            firstPerson.Update(partialTick, input.Perspective == 0 && !input.HideGui);
            Renderer.Update(viewCamera.transform.position);
            lightSources.Exposure = environment.Exposure;
            lightSources.Update(viewCamera.transform.position, Time.time);
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
                    MessageScreen.Draw(gui, "Microsoft Account", loginMessage, null);
                    return;
                }
                switch (title.Draw(gui, null, MicrosoftAccount))
                {
                    case TitleScreen.Action.Connect:
                        Connect(title.AddressText, MicrosoftAccount ?? Account.Offline(title.UsernameText));
                        break;
                    case TitleScreen.Action.Options:
                        overlay = Overlay.Options;
                        break;
                    case TitleScreen.Action.Accounts:
                        overlay = Overlay.Accounts;
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
            if (overlay == Overlay.Accounts)
            {
                if (accountsScreen.Draw(gui, accounts, Settings, overGame)) overlay = Overlay.None;
                return;
            }
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
            accounts?.Save();
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
            lightSources?.Dispose();
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
