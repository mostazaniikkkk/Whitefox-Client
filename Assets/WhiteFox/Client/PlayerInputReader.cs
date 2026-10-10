// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;
using UnityEngine.InputSystem;
using WhiteFox.Core.Game;

namespace WhiteFox.Client
{
    /// <summary>
    /// Reads keyboard and mouse every frame (Minosoft's CameraInput + key bindings) through the rebindable
    /// <see cref="Settings"/> bindings and writes the result into the session input, consumed by the next tick.
    /// </summary>
    public sealed class PlayerInputReader
    {
        private readonly Settings settings;

        public bool DebugScreen;
        /// <summary>Set when the graphics key was pressed (classic / modern rendering).</summary>
        public bool ToggleRenderMode;
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;

        public PlayerInputReader(Settings settings)
        {
            this.settings = settings;
        }

        /// <summary>Automated tests (-wf-holduse): right click is held down (eating, drawing a bow).</summary>
        public static bool HoldUseForTest;

        public void Read(GameSession session, ChatInput chat)
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var input = session.Input;
            if (HoldUseForTest)
            {
                input.UsePressed |= !input.UseHeld;
                input.UseHeld = true;
                return;
            }
            if (keyboard == null || mouse == null) return;

            if (chat.Open || !CursorLocked)
            {
                input.Movement = default;
                input.AttackHeld = false;
                input.UseHeld = false;
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                SetCursorLocked(false);
                return;
            }
            if (settings.Chat.WasPressed(keyboard) || settings.Command.WasPressed(keyboard))
            {
                chat.Show(settings.Command.WasPressed(keyboard) ? "/" : "");
                input.Movement = default;
                return;
            }

            var delta = mouse.delta.ReadValue();
            session.Rotate(delta.x * settings.MouseSensitivity, -delta.y * settings.MouseSensitivity);

            var movement = input.Movement;
            movement.Forwards = (settings.Forward.IsPressed(keyboard) ? 1 : 0) - (settings.Back.IsPressed(keyboard) ? 1 : 0);
            movement.Sideways = (settings.Left.IsPressed(keyboard) ? 1 : 0) - (settings.Right.IsPressed(keyboard) ? 1 : 0);
            movement.Jump = settings.Jump.IsPressed(keyboard);
            movement.Sneak = settings.Sneak.IsPressed(keyboard);
            movement.Sprint = settings.Sprint.IsPressed(keyboard);
            movement.Upwards = (movement.Jump ? 1 : 0) - (movement.Sneak ? 1 : 0);
            input.Movement = movement;

            input.JumpPressed |= settings.Jump.WasPressed(keyboard);
            input.ForwardPressed |= settings.Forward.WasPressed(keyboard);
            if (settings.Drop.WasPressed(keyboard))
            {
                input.DropPressed = true;
                input.DropStack = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            }
            input.SwapPressed |= settings.SwapHands.WasPressed(keyboard);
            input.AttackHeld = mouse.leftButton.isPressed;
            input.AttackPressed |= mouse.leftButton.wasPressedThisFrame;
            input.UseHeld = mouse.rightButton.isPressed;
            input.UsePressed |= mouse.rightButton.wasPressedThisFrame;
            input.PickPressed |= mouse.middleButton.wasPressedThisFrame;

            var scroll = mouse.scroll.ReadValue().y;
            if (scroll < 0) session.SelectedSlot = (session.SelectedSlot + 1) % 9;
            else if (scroll > 0) session.SelectedSlot = (session.SelectedSlot + 8) % 9;
            for (var i = 0; i < 9; i++)
            {
                if (settings.Hotbar[i].WasPressed(keyboard)) session.SelectedSlot = i;
            }

            if (settings.Debug.WasPressed(keyboard)) DebugScreen = !DebugScreen;
            if (settings.ToggleGraphics.WasPressed(keyboard)) ToggleRenderMode = true;
            if (settings.Perspective.WasPressed(keyboard)) Perspective = (Perspective + 1) % 3;
            PlayerList = settings.PlayerList.IsPressed(keyboard);
        }

        /// <summary>0 = first person, 1 = third person behind, 2 = third person in front (vanilla CameraType).</summary>
        public int Perspective;
        public bool PlayerList;

        public void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>The key pressed this frame (for rebinding), or Key.None.</summary>
        public static Key PressedKey()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return Key.None;
            foreach (var key in keyboard.allKeys)
            {
                if (key.wasPressedThisFrame && key.keyCode != Key.None) return key.keyCode;
            }
            return Key.None;
        }
    }
}
