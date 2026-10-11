// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>
    /// Split screen on Windows: player 2 is a second WhiteFox process joining the same server with its own name, its
    /// window borderless on the other half of the screen. Player 1 keeps the keyboard and mouse (and the first
    /// controller when there are two), player 2 plays with a controller only and reads it while its window is in the
    /// background (it never takes the focus). Player 2 quits when it is disconnected; player 1 ends it when it leaves.
    /// </summary>
    public sealed class SplitScreen : IDisposable
    {
        /// <summary>This process is player 2 (-wf-player2).</summary>
        public static bool IsPlayer2 => CommandLine.Has("-wf-player2");

        /// <summary>Split screen is possible in this build (Windows players, not the editor or other platforms).</summary>
        public static bool Supported => Application.platform == RuntimePlatform.WindowsPlayer;

        private Process player2;
        private bool split;

        /// <summary>
        /// Player 2 pressed Start: its window opens on the other half and asks for its name (an on-screen keyboard for
        /// the controller), then joins the server of player 1.
        /// </summary>
        public void Start(string address, string defaultName, string player1Name, bool horizontal)
        {
            if (!Supported || player2 != null) return;
            try
            {
                var executable = Process.GetCurrentProcess().MainModule?.FileName;
                if (executable == null) return;
                var log = Path.Combine(Application.persistentDataPath, "Player2.log");
                var arguments = $"-wf-player2 -wf-split {(horizontal ? "bottom" : "right")} -wf-connect \"{address}\" -wf-p2default \"{defaultName}\" -wf-p1name \"{player1Name}\" -logFile \"{log}\" -screen-fullscreen 0";
                player2 = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false });
                half = horizontal ? "top" : "left";
                Place(half);
                // again once a switch out of fullscreen has happened (it takes a frame or two)
                placeAgainFrame = Time.frameCount + 30;
                split = true;
            }
            catch (Exception exception)
            {
                Log.Warn("Can not start player 2: " + exception.Message);
                player2 = null;
            }
        }

        /// <summary>Player 2 is still running (it quits by itself when it is disconnected).</summary>
        public bool Running
        {
            get
            {
                try
                {
                    return player2 != null && !player2.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>Every frame on player 1: back to a normal window once player 2 is gone.</summary>
        public void Update()
        {
            if (split && !Running) Stop();
            if (split && placeAgainFrame > 0 && Time.frameCount >= placeAgainFrame)
            {
                placeAgainFrame = 0;
                Place(half);
            }
        }

        private string half;
        private int placeAgainFrame;

        /// <summary>Player 2: placed again a moment after the start (the player may resize its window at first).</summary>
        public static void UpdatePlayer2()
        {
            if (!IsPlayer2 || player2Placed || Time.frameCount < 60) return;
            player2Placed = true;
            Place(CommandLine.Get("-wf-split") ?? "right", true);
        }

        private static bool player2Placed;

        /// <summary>Player 1 left the server: end player 2 and restore the window.</summary>
        public void Stop()
        {
            if (player2 != null)
            {
                try
                {
                    if (!player2.HasExited) player2.Kill();
                }
                catch (Exception)
                {
                    // already gone
                }
                player2.Dispose();
                player2 = null;
            }
            if (!split) return;
            split = false;
            Restore();
        }

        public void Dispose() => Stop();

        /// <summary>The name player 2 chose last time (player 2 keeps it in this file, it saves no settings).</summary>
        public static string NameFile => Path.Combine(Application.persistentDataPath, "player2_name.txt");

        public static string LastPlayer2Name
        {
            get
            {
                try
                {
                    return File.Exists(NameFile) ? File.ReadAllText(NameFile).Trim() : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
            set
            {
                try
                {
                    File.WriteAllText(NameFile, value ?? "");
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not save the name of player 2: " + exception.Message);
                }
            }
        }

        /// <summary>The controller player 2 uses (to listen for its Start button before it exists).</summary>
        public static Gamepad Player2Controller => Gamepad.all.Count >= 2 ? Gamepad.all[1] : Gamepad.all.Count == 1 ? Gamepad.all[0] : null;

        // ------------------------------------------------------------------ controllers

        /// <summary>
        /// The controller of this process: player 2 takes the second one (or the only one), player 1 the first one only
        /// when there are two. -1: none.
        /// </summary>
        public static int ControllerIndex(bool splitActive)
        {
            var count = Gamepad.all.Count;
            if (IsPlayer2) return count >= 2 ? 1 : count == 1 ? 0 : -1;
            if (splitActive) return count >= 2 ? 0 : -1;
            return int.MaxValue; // not split: the controller used last
        }

        /// <summary>Player 2 setup: keep running and reading the controller in the background, borderless on its half.</summary>
        public static void SetupPlayer2()
        {
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            Place(CommandLine.Get("-wf-split") ?? "right", true);
        }

        // ------------------------------------------------------------------ windows

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, uint value);
        [DllImport("user32.dll")] private static extern uint GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

        private static IntPtr window;

        private static IntPtr Window
        {
            get
            {
                if (window == IntPtr.Zero) window = Process.GetCurrentProcess().MainWindowHandle;
                if (window == IntPtr.Zero) window = GetActiveWindow();
                return window;
            }
        }

        /// <summary>Borderless window on one half of the primary screen (left, right, top or bottom).</summary>
        private static void Place(string half, bool noActivate = false)
        {
            if (!Supported) return;
            try
            {
                var handle = Window;
                if (handle == IntPtr.Zero) return;
                int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
                int x = 0, y = 0, w = width, h = height;
                switch (half)
                {
                    case "left": w = width / 2; break;
                    case "right": x = width / 2; w = width - width / 2; break;
                    case "top": h = height / 2; break;
                    default: y = height / 2; h = height - height / 2; break;
                }
                Screen.fullScreenMode = FullScreenMode.Windowed;
                SetWindowLong(handle, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                if (noActivate) SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
                SetWindowPos(handle, IntPtr.Zero, x, y, w, h, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not place the window: " + exception.Message);
            }
        }

        /// <summary>A normal window again (1280x720, with its frame).</summary>
        private static void Restore()
        {
            if (!Supported) return;
            try
            {
                var handle = Window;
                if (handle == IntPtr.Zero) return;
                SetWindowLong(handle, GWL_STYLE, WS_OVERLAPPEDWINDOW | WS_VISIBLE);
                int width = GetSystemMetrics(0), height = GetSystemMetrics(1);
                SetWindowPos(handle, IntPtr.Zero, (width - 1280) / 2, (height - 720) / 2, 1280, 720, SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not restore the window: " + exception.Message);
            }
        }
    }
}
