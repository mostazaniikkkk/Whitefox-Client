// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Threading;
using UnityEngine;
using WhiteFox.Core.Accounts;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Accounts (Minosoft's Eros account list): the saved accounts, which one is used, and adding Microsoft accounts
    /// with the device code login (open microsoft.com/link, enter the code) or offline names. Microsoft accounts stay
    /// logged in: only the refresh token is needed to join again, even after restarting.
    /// </summary>
    public sealed class AccountsScreen
    {
        private readonly GuiTextField offlineName = new GuiTextField { MaxLength = 16, Placeholder = "WhiteFox" };
        private bool addingOffline;

        // Microsoft login in progress (set by the login task, read on the main thread)
        private CancellationTokenSource login;
        private volatile DeviceCode code;
        private volatile string loginStatus;
        private volatile string loginError;
        private volatile Account finished;
        private float scroll;

        public bool LoggingIn => login != null;

        /// <summary>Returns true when the screen is closed.</summary>
        public bool Draw(GuiRenderer gui, AccountStore store, Settings settings, bool overGame)
        {
            if (overGame) gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            else MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextCentered("Accounts", center, 8);

            if (finished != null)
            {
                store.AddOrReplace(finished);
                store.Save();
                finished = null;
            }
            if (login != null) return DrawLogin(gui, center);
            if (addingOffline) return DrawOffline(gui, store, center);

            // the list: click selects the account used to join servers
            const float rowHeight = 24;
            const float listTop = 26;
            var listBottom = gui.Height - 64;
            var current = Event.current;
            if (current.type == EventType.ScrollWheel)
            {
                scroll += current.delta.y * 8;
                current.Use();
            }
            scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, store.Accounts.Count * rowHeight - (listBottom - listTop)));
            GUI.BeginGroup(new Rect(0, listTop, gui.Width, listBottom - listTop));
            var y = -scroll;
            Account remove = null;
            foreach (var account in store.Accounts)
            {
                if (y > -rowHeight && y < listBottom - listTop)
                {
                    var selected = account.Id == store.SelectedId;
                    var label = (selected ? "> " : "") + account.Name + (account.IsOnline ? "  (Microsoft)" : "  (offline)");
                    if (gui.Button(new Rect(center - 155, y, 250, 20), label))
                    {
                        store.SelectedId = account.Id;
                        store.Save();
                    }
                    if (gui.Button(new Rect(center + 99, y, 56, 20), "Remove")) remove = account;
                }
                y += rowHeight;
            }
            GUI.EndGroup();
            if (remove != null)
            {
                store.Remove(remove);
                store.Save();
            }
            if (store.Accounts.Count == 0) gui.TextCentered("No accounts: the offline name of the title screen is used", center, listTop + 8, GuiRenderer.Gray);

            if (loginError != null) gui.TextCentered(loginError, center, listBottom + 2, GuiRenderer.Red);
            var bottom = gui.Height - 54;
            var clientId = MicrosoftAuth.ClientId(settings.MicrosoftClientId);
            if (gui.Button(new Rect(center - 155, bottom + 2, 150, 20), "Add Microsoft Account", clientId != null)) StartLogin(clientId);
            if (gui.Button(new Rect(center + 5, bottom + 2, 150, 20), "Add Offline Account"))
            {
                addingOffline = true;
                offlineName.Text = settings.OfflineName;
                loginError = null;
            }
            if (clientId == null && loginError == null) gui.TextCentered("Microsoft sign-in is not available in this build yet", center, listBottom + 2, GuiRenderer.Gray);
            if (gui.Button(new Rect(center - 100, gui.Height - 28, 200, 20), "Done"))
            {
                store.Save();
                loginError = null;
                return true;
            }
            return false;
        }

        private bool DrawOffline(GuiRenderer gui, AccountStore store, float center)
        {
            var y = Mathf.Floor(gui.Height / 3);
            gui.Text("Offline account name (for offline mode servers)", center - 150, y, GuiRenderer.Gray);
            var submit = offlineName.Draw(gui, new Rect(center - 150, y + 12, 300, 20));
            var name = offlineName.Text.Trim();
            var valid = name.Length >= 1 && name.Length <= 16;
            if (gui.Button(new Rect(center - 150, y + 44, 147, 20), "Add", valid) || submit && valid)
            {
                store.AddOrReplace(Account.Offline(name));
                store.Save();
                addingOffline = false;
            }
            if (gui.Button(new Rect(center + 3, y + 44, 147, 20), "Cancel")) addingOffline = false;
            return false;
        }

        private bool DrawLogin(GuiRenderer gui, float center)
        {
            var y = Mathf.Floor(gui.Height / 4);
            var device = code;
            if (device == null)
            {
                gui.TextCentered(loginStatus ?? "Contacting Microsoft...", center, y);
            }
            else
            {
                gui.TextCentered("Sign in with your Microsoft account:", center, y);
                gui.TextCentered("1. Open " + device.VerificationUri, center, y + 16, GuiRenderer.Gray);
                gui.TextCentered("2. Enter this code:", center, y + 28, GuiRenderer.Gray);
                gui.TextScaled(device.UserCode, center, y + 42, 2, GuiRenderer.Yellow);
                gui.TextCentered(loginStatus ?? "Waiting for you to accept in the browser...", center, y + 70, GuiRenderer.Gray);
                if (gui.Button(new Rect(center - 155, y + 88, 150, 20), "Open Browser")) Application.OpenURL(device.VerificationUri);
                if (gui.Button(new Rect(center + 5, y + 88, 150, 20), "Copy Code")) GUIUtility.systemCopyBuffer = device.UserCode;
            }
            if (gui.Button(new Rect(center - 100, gui.Height - 28, 200, 20), "Cancel"))
            {
                login.Cancel();
                login = null;
                code = null;
            }
            return false;
        }

        private async void StartLogin(string clientId)
        {
            var cancel = new CancellationTokenSource();
            login = cancel;
            code = null;
            loginStatus = null;
            loginError = null;
            var auth = new MicrosoftAuth(clientId);
            try
            {
                var device = await auth.StartAsync(cancel.Token);
                code = device;
                // like vanilla launchers: the code is already copied and the page open
                GUIUtility.systemCopyBuffer = device.UserCode;
                Application.OpenURL(device.VerificationUri);
                var account = await auth.CompleteAsync(device, cancel.Token);
                if (cancel.IsCancellationRequested) return;
                finished = account;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (cancel.IsCancellationRequested) return;
                loginError = exception is AuthException ? exception.Message : "Could not sign in: " + exception.Message;
                Debug.LogWarning("WhiteFox: Microsoft login failed: " + exception);
            }
            if (login == cancel)
            {
                login = null;
                code = null;
            }
        }
    }
}
