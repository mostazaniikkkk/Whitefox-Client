// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace WhiteFox.Client
{
    /// <summary>
    /// Touch controls like Minecraft: Pocket/Bedrock Edition with "split controls": a D-pad (with the diagonal
    /// buttons while walking forward and the sneak toggle in the middle), jump / fly buttons, tappable hotbar,
    /// inventory, chat, perspective and pause buttons. Dragging anywhere else looks around; blocks are still aimed with
    /// the crosshair in the middle of the screen: holding the finger still breaks (or uses the held food, bow…),
    /// a short tap places / uses, or attacks the entity under the crosshair.
    /// </summary>
    public sealed class TouchControls
    {
        private enum Control
        {
            None,
            Look,
            DPad,
            Jump,
            FlyDown,
            Hotbar,
            Inventory,
            Chat,
            Perspective,
            Pause,
        }

        private enum Pad
        {
            None,
            Forward,
            Back,
            Left,
            Right,
            ForwardLeft,
            ForwardRight,
            Sneak,
        }

        private sealed class Finger
        {
            public Control Control;
            public Pad Pad;
            public Vector2 Start;
            public double StartTime;
            public bool Moved;
            public bool Holding;
        }

        private const float Button = 26;
        private const float Gap = 2;
        private const float Margin = 10;
        /// <summary>Pixels a finger may move and still count as a tap or a hold.</summary>
        private const float TapSlop = 14;
        private const float HoldSeconds = 0.3f;
        private const float TapSeconds = 0.25f;

        private readonly Settings settings;
        private readonly Dictionary<int, Finger> fingers = new Dictionary<int, Finger>();
        private readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        private bool sneakToggled;
        private bool wasForward;

        // read by the client to do what a key would do
        public bool OpenInventory;
        public bool OpenChat;
        public bool TogglePerspective;
        public bool Pause;

        /// <summary>Forced by -wf-touch: the mouse simulates a touchscreen (testing on a PC).</summary>
        public static bool Simulated;

        public TouchControls(Settings settings)
        {
            this.settings = settings;
            EnhancedTouchSupport.Enable();
            if (Simulated) TouchSimulation.Enable();
        }

        /// <summary>The touch controls are used: phones and tablets (setting Auto), always (On) or never (Off).</summary>
        public bool Enabled => settings.TouchControls == 1 || settings.TouchControls == 0 && (Application.isMobilePlatform || Simulated);

        private float scale = 2;
        private float width => Screen.width / scale;
        private float height => Screen.height / scale;

        /// <summary>Reads the fingers and writes walking, jumping, looking, breaking and using into the session input.</summary>
        public void Apply(GameSession session, float guiScale)
        {
            scale = Mathf.Max(1, guiScale);
            var input = session.Input;
            var movement = input.Movement;
            float forwards = 0, sideways = 0;
            bool jump = false, flyDown = false, attack = false, use = false;
            var flying = session.Player.Flying;

            foreach (var touch in Touch.activeTouches)
            {
                var position = ToGui(touch.screenPosition);
                if (touch.phase == TouchPhase.Began || !fingers.TryGetValue(touch.touchId, out var finger))
                {
                    finger = new Finger { Start = touch.screenPosition, StartTime = touch.startTime };
                    fingers[touch.touchId] = finger;
                    finger.Control = HitControl(position, session, out finger.Pad);
                    OnBegan(finger, position, session);
                }
                if ((touch.screenPosition - finger.Start).magnitude > TapSlop * Mathf.Max(1, Screen.dpi / 160f)) finger.Moved = true;
                var ended = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;

                switch (finger.Control)
                {
                    case Control.DPad:
                        // sliding over the pad changes the direction, like the PE D-pad
                        var pad = PadAt(position, out _);
                        if (pad != Pad.None && pad != Pad.Sneak) finger.Pad = pad;
                        if (ended) break;
                        switch (finger.Pad)
                        {
                            case Pad.Forward: forwards = 1; break;
                            case Pad.Back: forwards = -1; break;
                            case Pad.Left: sideways = 1; break;
                            case Pad.Right: sideways = -1; break;
                            case Pad.ForwardLeft: forwards = 1; sideways = 1; break;
                            case Pad.ForwardRight: forwards = 1; sideways = -1; break;
                        }
                        break;
                    case Control.Jump:
                        if (!ended) jump = true;
                        break;
                    case Control.FlyDown:
                        if (!ended) flyDown = true;
                        break;
                    case Control.Look:
                    {
                        var delta = touch.delta;
                        var sensitivity = settings.MouseSensitivity * 2.2f * 160f / Mathf.Max(120f, Screen.dpi);
                        session.Rotate(delta.x * sensitivity, -delta.y * sensitivity);
                        var age = Time.realtimeSinceStartupAsDouble - finger.StartTime;
                        if (!finger.Moved && age >= HoldSeconds) finger.Holding = true;
                        if (finger.Holding && !ended)
                        {
                            // holding: break the block under the crosshair, or use food / bows / shields
                            var held = session.Inventory.Player.Slots[36 + session.SelectedSlot]?.Item;
                            if (held != null && session.Resources.ItemProperties.IsUsable(held, session.Food) && session.Resources.Blocks.Get(held.Name) == null) use = true;
                            else attack = true;
                        }
                        if (ended && !finger.Holding && !finger.Moved && age < TapSeconds)
                        {
                            // a tap: hit the entity under the crosshair, otherwise place / interact
                            if (session.TargetEntity != null) input.AttackPressed = true;
                            else input.UsePressed = true;
                            tapUseFrames = 4;
                        }
                        break;
                    }
                }
                if (ended) fingers.Remove(touch.touchId);
            }
            // fingers that vanished without an end phase (app paused)
            if (Touch.activeTouches.Count == 0) fingers.Clear();

            if (tapUseFrames > 0)
            {
                tapUseFrames--;
                use = true;
            }

            var forward = forwards > 0;
            if (forward && !wasForward) input.ForwardPressed = true; // double tap forward sprints
            wasForward = forward;
            if (flying) sneakToggled = false;

            movement.Forwards = Mathf.Clamp(movement.Forwards + forwards, -1, 1);
            movement.Sideways = Mathf.Clamp(movement.Sideways + sideways, -1, 1);
            movement.Jump |= jump;
            movement.Sneak |= sneakToggled || flyDown;
            movement.Upwards = (movement.Jump ? 1 : 0) - (movement.Sneak ? 1 : 0);
            input.Movement = movement;
            input.AttackHeld |= attack;
            input.UseHeld |= use;
        }

        private int tapUseFrames;

        private void OnBegan(Finger finger, Vector2 position, GameSession session)
        {
            switch (finger.Control)
            {
                case Control.DPad when finger.Pad == Pad.Sneak:
                    sneakToggled = !sneakToggled;
                    finger.Control = Control.None;
                    break;
                case Control.Jump:
                    session.Input.JumpPressed = true; // double tap: start or stop flying
                    break;
                case Control.Hotbar:
                    var slot = Mathf.FloorToInt((position.x - (Mathf.Floor(width / 2) - 91)) / 20);
                    if (slot >= 0 && slot < 9) session.SelectedSlot = slot;
                    break;
                case Control.Inventory:
                    OpenInventory = true;
                    break;
                case Control.Chat:
                    OpenChat = true;
                    break;
                case Control.Perspective:
                    TogglePerspective = true;
                    break;
                case Control.Pause:
                    Pause = true;
                    break;
            }
        }

        private Vector2 ToGui(Vector2 screen) => new Vector2(screen.x / scale, (Screen.height - screen.y) / scale);

        // ------------------------------------------------------------------ layout (GUI units)

        private Rect PadRect(int column, int row)
        {
            var top = height - Margin - 3 * Button - 2 * Gap;
            return new Rect(Margin + column * (Button + Gap), top + row * (Button + Gap), Button, Button);
        }

        private Rect JumpRect => new Rect(width - Margin - 34, height - Margin - 34 - (Flying ? 30 : 0), 34, 34);
        private Rect FlyDownRect => new Rect(width - Margin - 34, height - Margin - 28, 34, 28);
        private Rect HotbarRect => new Rect(Mathf.Floor(width / 2) - 91, height - 22, 182, 22);
        private Rect InventoryRect => new Rect(Mathf.Floor(width / 2) + 93, height - 22, 22, 22);
        private Rect PauseRect => new Rect(width - Margin - 20, 4, 20, 20);
        private Rect ChatRect => new Rect(width - Margin - 44, 4, 20, 20);
        private Rect PerspectiveRect => new Rect(width - Margin - 68, 4, 20, 20);

        private bool Flying;

        private Pad PadAt(Vector2 position, out Rect rect)
        {
            var pads = new[]
            {
                (Pad.Forward, PadRect(1, 0)), (Pad.Left, PadRect(0, 1)), (Pad.Sneak, PadRect(1, 1)), (Pad.Right, PadRect(2, 1)), (Pad.Back, PadRect(1, 2)),
                (Pad.ForwardLeft, PadRect(0, 0)), (Pad.ForwardRight, PadRect(2, 0)),
            };
            foreach (var (pad, area) in pads)
            {
                // the diagonals only exist while walking forward
                if ((pad == Pad.ForwardLeft || pad == Pad.ForwardRight) && !wasForward) continue;
                if (area.Contains(position))
                {
                    rect = area;
                    return pad;
                }
            }
            rect = default;
            return Pad.None;
        }

        private Control HitControl(Vector2 position, GameSession session, out Pad pad)
        {
            Flying = session.Player.Flying;
            pad = PadAt(position, out _);
            if (pad != Pad.None) return Control.DPad;
            if (JumpRect.Contains(position)) return Control.Jump;
            if (Flying && FlyDownRect.Contains(position)) return Control.FlyDown;
            if (HotbarRect.Contains(position)) return Control.Hotbar;
            if (InventoryRect.Contains(position)) return Control.Inventory;
            if (PauseRect.Contains(position)) return Control.Pause;
            if (ChatRect.Contains(position)) return Control.Chat;
            if (PerspectiveRect.Contains(position)) return Control.Perspective;
            return Control.Look;
        }

        // ------------------------------------------------------------------ drawing

        public void Draw(GuiRenderer gui, GameSession session)
        {
            scale = gui.Scale;
            Flying = session.Player.Flying;
            var active = new HashSet<Pad>();
            bool jumpDown = false, downDown = false;
            foreach (var finger in fingers.Values)
            {
                if (finger.Control == Control.DPad) active.Add(finger.Pad);
                if (finger.Control == Control.Jump) jumpDown = true;
                if (finger.Control == Control.FlyDown) downDown = true;
            }
            if (sneakToggled) active.Add(Pad.Sneak);

            DrawButton(gui, PadRect(1, 0), "up", active.Contains(Pad.Forward));
            DrawButton(gui, PadRect(0, 1), "left", active.Contains(Pad.Left));
            DrawButton(gui, PadRect(2, 1), "right", active.Contains(Pad.Right));
            DrawButton(gui, PadRect(1, 2), "down", active.Contains(Pad.Back));
            if (!Flying) DrawButton(gui, PadRect(1, 1), "sneak", active.Contains(Pad.Sneak));
            if (wasForward)
            {
                DrawButton(gui, PadRect(0, 0), "upleft", active.Contains(Pad.ForwardLeft));
                DrawButton(gui, PadRect(2, 0), "upright", active.Contains(Pad.ForwardRight));
            }
            DrawButton(gui, JumpRect, Flying ? "up" : "jump", jumpDown);
            if (Flying) DrawButton(gui, FlyDownRect, "down", downDown);
            DrawButton(gui, InventoryRect, "dots", false);
            DrawButton(gui, PauseRect, "pause", false);
            DrawButton(gui, ChatRect, "chat", false);
            DrawButton(gui, PerspectiveRect, "eye", false);
        }

        private void DrawButton(GuiRenderer gui, Rect rect, string icon, bool pressed)
        {
            if (!gui.Repaint) return;
            gui.Fill(rect, pressed ? new Color(1, 1, 1, 0.45f) : new Color(0.1f, 0.1f, 0.1f, 0.35f));
            gui.Fill(new Rect(rect.x, rect.y, rect.width, 1), new Color(1, 1, 1, 0.25f));
            gui.Fill(new Rect(rect.x, rect.yMax - 1, rect.width, 1), new Color(0, 0, 0, 0.35f));
            var texture = Icon(icon);
            var size = Mathf.Min(rect.width, rect.height) * 0.6f;
            GUI.DrawTexture(new Rect(rect.center.x - size / 2, rect.center.y - size / 2, size, size), texture);
        }

        // 9x9 pixel icons ('#' white, '+' shadow)
        private static readonly Dictionary<string, string[]> IconPixels = new Dictionary<string, string[]>
        {
            { "up", new[] { "....#....", "...###...", "..#####..", ".#######.", "#########", "...###...", "...###...", "...###...", "........." } },
            { "down", new[] { ".........", "...###...", "...###...", "...###...", "#########", ".#######.", "..#####..", "...###...", "....#...." } },
            { "left", new[] { "....#....", "...##....", "..###....", ".########", "#########", ".########", "..###....", "...##....", "....#...." } },
            { "right", new[] { "....#....", "....##...", "....###..", "########.", "#########", "########.", "....###..", "....##...", "....#...." } },
            { "upleft", new[] { "######...", "#####....", "#####....", "######...", "##.####..", "#...####.", ".....####", "......###", ".......#." } },
            { "upright", new[] { "...######", "....#####", "....#####", "...######", "..####.##", ".####...#", "####.....", "###......", ".#......." } },
            { "jump", new[] { "....#....", "...###...", "..#####..", ".###.###.", "##.....##", "....#....", "...###...", "..#####..", ".###.###." } },
            { "sneak", new[] { "....#....", "...###...", "..#####..", ".#######.", "#########", ".#######.", "..#####..", "...###...", "....#...." } },
            { "dots", new[] { ".........", ".........", ".........", ".........", "##.##.##.", "##.##.##.", ".........", ".........", "........." } },
            { "pause", new[] { ".........", ".##...##.", ".##...##.", ".##...##.", ".##...##.", ".##...##.", ".##...##.", ".##...##.", "........." } },
            { "chat", new[] { ".........", "#########", "#.......#", "#.##.##.#", "#.......#", "#########", "..##.....", ".#.......", "........." } },
            { "eye", new[] { ".........", ".........", "..#####..", ".#..#..#.", "#..###..#", ".#..#..#.", "..#####..", ".........", "........." } },
        };

        private Texture2D Icon(string name)
        {
            if (icons.TryGetValue(name, out var texture)) return texture;
            var rows = IconPixels[name];
            texture = new Texture2D(9, 9, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Touch " + name };
            for (var y = 0; y < 9; y++)
            {
                for (var x = 0; x < 9; x++)
                {
                    var c = rows[y][x];
                    texture.SetPixel(x, 8 - y, c == '#' ? new Color(1, 1, 1, 0.95f) : new Color(0, 0, 0, 0));
                }
            }
            texture.Apply(false, true);
            icons[name] = texture;
            return texture;
        }

        public void Dispose()
        {
            foreach (var texture in icons.Values) Object.Destroy(texture);
            icons.Clear();
            if (Simulated) TouchSimulation.Disable();
            EnhancedTouchSupport.Disable();
        }
    }
}
