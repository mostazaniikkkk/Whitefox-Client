// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;
using UnityEngine.InputSystem;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;

namespace WhiteFox.Client
{
    /// <summary>
    /// Game controllers (Xbox, PlayStation, Switch and generic gamepads through the Input System), laid out like the
    /// Bedrock edition. Playing: left stick walks, right stick looks, A jumps, B drops, X swaps hands, Y opens the
    /// inventory, LT uses, RT attacks, LB/RB change the hotbar slot, the left stick button sprints, the right one toggles
    /// sneaking, D-pad up changes the camera, D-pad down opens the chat, D-pad right the debug screen, Start pauses and
    /// View (Select) shows the player list. In menus a cursor follows the left stick: A clicks, X right clicks, RB held
    /// makes it a shift click, the right stick scrolls, B goes back, Y closes the inventory.
    /// </summary>
    public sealed class GamepadInput
    {
        private const float Deadzone = 0.2f;

        private readonly Settings settings;
        private bool sneakToggled;
        private bool sprintLatched;

        /// <summary>The controller was used last (the touch buttons hide, menus show the cursor).</summary>
        public bool Active { get; private set; }
        /// <summary>The menu cursor, screen pixels from the top left (GUI event coordinates).</summary>
        public Vector2 Cursor;
        // menu actions of this frame, played into the GUI as mouse events by the client
        public int PendingClick = -1;
        public bool PendingShift;
        public float PendingScroll;
        public bool BackPressed;
        public bool InventoryPressed;
        public bool PausePressed;

        public GamepadInput(Settings settings)
        {
            this.settings = settings;
            Cursor = new Vector2(Screen.width / 2f, Screen.height / 2f);
        }

        /// <summary>Player 1 shares the screen with player 2 (the controllers are assigned per player then).</summary>
        public static bool SplitActive;

        /// <summary>The controller of this player (see <see cref="SplitScreen.ControllerIndex"/>).</summary>
        public static Gamepad Pad
        {
            get
            {
                var index = SplitScreen.ControllerIndex(SplitActive);
                if (index == int.MaxValue) return Gamepad.current;
                return index >= 0 && index < Gamepad.all.Count ? Gamepad.all[index] : null;
            }
        }

        /// <summary>Every frame: who is used (controller or mouse / keyboard / touch) and the menu actions.</summary>
        public void Update(bool inMenu)
        {
            PendingClick = -1;
            PendingScroll = 0;
            BackPressed = InventoryPressed = PausePressed = false;
            var pad = Pad;
            if (pad == null)
            {
                Active = false;
                return;
            }
            // player 2 only plays with its controller (the keyboard and mouse belong to player 1)
            if (SplitScreen.IsPlayer2) Active = true;
            else if (AnyPadInput(pad)) Active = true;
            else if (OtherInput()) Active = false;
            if (!Active) return;

            BackPressed = pad.buttonEast.wasPressedThisFrame && inMenu;
            InventoryPressed = pad.buttonNorth.wasPressedThisFrame;
            PausePressed = pad.startButton.wasPressedThisFrame;
            if (!inMenu) return;

            // the cursor: faster the further the stick is pushed, the same speed at every resolution
            var stick = Stick(pad.leftStick.ReadValue());
            var speed = 1100f * Screen.height / 1080f;
            Cursor += new Vector2(stick.x, -stick.y) * speed * Time.unscaledDeltaTime;
            Cursor = new Vector2(Mathf.Clamp(Cursor.x, 0, Screen.width - 1), Mathf.Clamp(Cursor.y, 0, Screen.height - 1));
            if (pad.buttonSouth.wasPressedThisFrame) PendingClick = 0;
            else if (pad.buttonWest.wasPressedThisFrame) PendingClick = 1;
            PendingShift = pad.rightShoulder.isPressed;
            var scroll = Stick(pad.rightStick.ReadValue()).y;
            if (Mathf.Abs(scroll) > 0.5f && Time.unscaledTime - lastScroll > 0.12f)
            {
                PendingScroll = scroll > 0 ? -1 : 1;
                lastScroll = Time.unscaledTime;
            }
        }

        private float lastScroll;

        /// <summary>Playing: the controller's part of the session input (merged with keyboard, mouse and touch).</summary>
        public void Apply(GameSession session, PlayerInputReader reader)
        {
            var pad = Pad;
            if (pad == null || !Active) return;
            var input = session.Input;

            var move = Stick(pad.leftStick.ReadValue());
            var movement = input.Movement;
            if (Mathf.Abs(move.y) > Mathf.Abs(movement.Forwards)) movement.Forwards = move.y;
            if (Mathf.Abs(move.x) > Mathf.Abs(movement.Sideways)) movement.Sideways = -move.x;
            if (pad.rightStickButton.wasPressedThisFrame) sneakToggled = !sneakToggled;
            if (session.Player.Flying) sneakToggled = false;
            // the left stick button sprints until the player stops walking forward (Bedrock)
            if (pad.leftStickButton.wasPressedThisFrame) sprintLatched = true;
            if (move.y <= 0.1f) sprintLatched = false;
            movement.Jump |= pad.buttonSouth.isPressed;
            movement.Sneak |= sneakToggled || session.Player.Flying && pad.rightStickButton.isPressed;
            movement.Sprint |= sprintLatched;
            movement.Upwards = (movement.Jump ? 1 : 0) - (movement.Sneak ? 1 : 0);
            input.Movement = movement;
            input.JumpPressed |= pad.buttonSouth.wasPressedThisFrame;

            // looking: a curve for fine aiming near the middle, the mouse sensitivity scales it
            var look = Stick(pad.rightStick.ReadValue());
            var rate = 260f * (settings.MouseSensitivity / 0.15f) * Time.deltaTime;
            session.Rotate(look.x * Mathf.Abs(look.x) * rate, -look.y * Mathf.Abs(look.y) * rate * 0.8f);

            input.AttackHeld |= pad.rightTrigger.ReadValue() > 0.5f;
            input.AttackPressed |= pad.rightTrigger.wasPressedThisFrame;
            input.UseHeld |= pad.leftTrigger.ReadValue() > 0.5f;
            input.UsePressed |= pad.leftTrigger.wasPressedThisFrame;
            if (pad.buttonEast.wasPressedThisFrame) input.DropPressed = true;
            input.SwapPressed |= pad.buttonWest.wasPressedThisFrame;
            if (pad.rightShoulder.wasPressedThisFrame) session.SelectedSlot = (session.SelectedSlot + 1) % 9;
            if (pad.leftShoulder.wasPressedThisFrame) session.SelectedSlot = (session.SelectedSlot + 8) % 9;

            if (pad.dpad.up.wasPressedThisFrame) reader.Perspective = (reader.Perspective + 1) % 3;
            if (pad.dpad.right.wasPressedThisFrame) reader.DebugScreen = !reader.DebugScreen;
            reader.PlayerList |= pad.selectButton.isPressed;
        }

        /// <summary>D-pad down: open the chat (the client shows it).</summary>
        public bool ChatPressed => Active && Pad != null && Pad.dpad.down.wasPressedThisFrame;

        private static Vector2 Stick(Vector2 value)
        {
            var length = value.magnitude;
            if (length < Deadzone) return Vector2.zero;
            return value / length * Mathf.Clamp01((length - Deadzone) / (1 - Deadzone));
        }

        private static bool AnyPadInput(Gamepad pad)
        {
            if (pad.leftStick.ReadValue().magnitude > 0.5f || pad.rightStick.ReadValue().magnitude > 0.5f) return true;
            if (pad.leftTrigger.ReadValue() > 0.5f || pad.rightTrigger.ReadValue() > 0.5f) return true;
            foreach (var control in pad.allControls)
            {
                if (control is UnityEngine.InputSystem.Controls.ButtonControl button && button.wasPressedThisFrame) return true;
            }
            return false;
        }

        private static bool OtherInput()
        {
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 4 || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return true;
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame) return true;
            return UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.enabled && UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches.Count > 0;
        }

        /// <summary>The menu cursor: a small white arrow with a dark outline, in GUI units.</summary>
        public void DrawCursor(GuiRenderer gui)
        {
            if (!gui.Repaint) return;
            var position = Cursor / gui.Scale;
            // the outline first, the white arrow on top of it
            for (var row = 0; row < 10; row++) gui.Fill(new Rect(position.x - 1, position.y + row - 1, row + 2, 1), new Color(0, 0, 0, 0.85f));
            for (var row = 0; row < 8; row++) gui.Fill(new Rect(position.x, position.y + row, row + 1, 1), Color.white);
        }
    }
}
