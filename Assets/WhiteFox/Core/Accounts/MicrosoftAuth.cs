// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Accounts
{
    /// <summary>A login problem with a message for the user.</summary>
    public sealed class AuthException : Exception
    {
        public AuthException(string message) : base(message)
        {
        }
    }

    /// <summary>The code the user enters at <see cref="VerificationUri"/> (OAuth device code flow).</summary>
    public sealed class DeviceCode
    {
        public string UserCode;
        public string VerificationUri;
        public string Message;
        internal string Code;
        internal int Interval;
        internal DateTime Expires;
    }

    /// <summary>
    /// Microsoft account login (Minosoft's MicrosoftOAuthUtils): device code, Microsoft token → Xbox Live → XSTS →
    /// Minecraft token → profile. The refresh token rotates and keeps the account logged in; the Minecraft token
    /// (24 hours) is renewed from it silently before joining a server.
    /// </summary>
    public sealed class MicrosoftAuth
    {
        private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
        private const string Scope = "XboxLive.signin offline_access";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// The Azure app of WhiteFox (registered once by the developers, approved by Mojang for the Minecraft login
        /// API): shipped in the game so players just press "Add Microsoft Account". config.toml can override it.
        /// </summary>
        public const string BuiltInClientId = "";

        /// <summary>The client id to use: the one from config.toml, else the built-in one; null when there is none.</summary>
        public static string ClientId(string configured)
        {
            var id = string.IsNullOrWhiteSpace(configured) ? BuiltInClientId : configured.Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        private readonly string clientId;

        /// <param name="clientId">Application (client) id of an Azure app registration for personal Microsoft accounts with public client flows enabled.</param>
        public MicrosoftAuth(string clientId)
        {
            this.clientId = clientId;
        }

        public async Task<DeviceCode> StartAsync(CancellationToken cancel = default)
        {
            var json = await PostForm(Authority + "devicecode", new Dictionary<string, string> { { "client_id", clientId }, { "scope", Scope } }, cancel).ConfigureAwait(false);
            return new DeviceCode
            {
                UserCode = (string)json["user_code"],
                VerificationUri = (string)json["verification_uri"] ?? "https://www.microsoft.com/link",
                Message = (string)json["message"],
                Code = (string)json["device_code"],
                Interval = Math.Max(1, (int?)json["interval"] ?? 5),
                Expires = DateTime.UtcNow.AddSeconds((int?)json["expires_in"] ?? 900),
            };
        }

        /// <summary>Waits until the user entered the code and accepted, then logs into Minecraft.</summary>
        public async Task<Account> CompleteAsync(DeviceCode code, CancellationToken cancel = default)
        {
            var form = new Dictionary<string, string>
            {
                { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" },
                { "client_id", clientId },
                { "device_code", code.Code },
            };
            var interval = code.Interval;
            while (DateTime.UtcNow < code.Expires)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancel).ConfigureAwait(false);
                var response = await Http.PostAsync(Authority + "token", new FormUrlEncodedContent(form), cancel).ConfigureAwait(false);
                var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var error = (string)json["error"];
                if (error == "authorization_pending") continue;
                if (error == "slow_down")
                {
                    interval += 5;
                    continue;
                }
                if (error == "authorization_declined") throw new AuthException("The sign-in was declined.");
                if (error == "expired_token") break;
                if (error != null) throw new AuthException("Microsoft: " + ((string)json["error_description"] ?? error));
                var account = new Account { Type = AccountType.Microsoft, RefreshToken = (string)json["refresh_token"] };
                await LoginMinecraft(account, (string)json["access_token"], cancel).ConfigureAwait(false);
                return account;
            }
            throw new AuthException("The code expired; please try again.");
        }

        /// <summary>Makes sure the Minecraft token is valid, renewing everything from the refresh token when needed.</summary>
        public async Task RefreshAsync(Account account, CancellationToken cancel = default)
        {
            if (!account.IsOnline || account.HasValidToken) return;
            if (string.IsNullOrEmpty(account.RefreshToken)) throw new AuthException("The session of " + account.Name + " expired; please sign in again.");
            JObject json;
            try
            {
                json = await PostForm(Authority + "token", new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "client_id", clientId },
                    { "refresh_token", account.RefreshToken },
                    { "scope", Scope },
                }, cancel).ConfigureAwait(false);
            }
            catch (AuthException)
            {
                // revoked or older than 90 days unused: only a new login helps
                account.RefreshToken = null;
                throw new AuthException("The session of " + account.Name + " expired; please sign in again.");
            }
            account.RefreshToken = (string)json["refresh_token"] ?? account.RefreshToken;
            await LoginMinecraft(account, (string)json["access_token"], cancel).ConfigureAwait(false);
        }

        private static async Task LoginMinecraft(Account account, string microsoftToken, CancellationToken cancel)
        {
            // Xbox Live user token
            var xbox = await PostJson("https://user.auth.xboxlive.com/user/authenticate", new JObject
            {
                ["Properties"] = new JObject { ["AuthMethod"] = "RPS", ["SiteName"] = "user.auth.xboxlive.com", ["RpsTicket"] = "d=" + microsoftToken },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT",
            }, null, cancel).ConfigureAwait(false);
            var userHash = (string)xbox["DisplayClaims"]?["xui"]?[0]?["uhs"];

            // XSTS token for Minecraft services
            var xsts = await PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", new JObject
            {
                ["Properties"] = new JObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JArray((string)xbox["Token"]) },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT",
            }, null, cancel).ConfigureAwait(false);

            var minecraft = await PostJson("https://api.minecraftservices.com/authentication/login_with_xbox", new JObject
            {
                ["identityToken"] = "XBL3.0 x=" + userHash + ";" + (string)xsts["Token"],
            }, null, cancel).ConfigureAwait(false);
            account.MinecraftToken = (string)minecraft["access_token"];
            account.MinecraftTokenExpires = DateTime.UtcNow.AddSeconds((int?)minecraft["expires_in"] ?? 86400);

            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.MinecraftToken);
            var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new AuthException("This Microsoft account does not own Minecraft: Java Edition.");
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AuthException("Minecraft: can not read the profile (" + (int)response.StatusCode + ").");
            var profile = JObject.Parse(body);
            account.Name = (string)profile["name"];
            account.Uuid = Guid.ParseExact((string)profile["id"], "N");
            Log.Info("Logged in as " + account.Name);
        }

        /// <summary>
        /// Session server join (vanilla MinecraftSessionService.joinServer): tells Mojang this account joins the server
        /// with that hash, which the server then checks before accepting the encryption.
        /// </summary>
        public static async Task JoinServerAsync(Account account, string serverHash, CancellationToken cancel = default)
        {
            var body = new JObject { ["accessToken"] = account.MinecraftToken, ["selectedProfile"] = account.Uuid.ToString("N"), ["serverId"] = serverHash };
            var response = await Http.PostAsync("https://sessionserver.mojang.com/session/minecraft/join", new StringContent(body.ToString(), Encoding.UTF8, "application/json"), cancel).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)response.StatusCode == 403 || text.Contains("InsufficientPrivileges")) throw new AuthException("Mojang does not allow this account to join servers (multiplayer disabled or account banned).");
            if ((int)response.StatusCode == 401) throw new AuthException("The Minecraft session is not valid; please sign in again.");
            throw new AuthException("Mojang rejected the connection (" + (int)response.StatusCode + "): " + text);
        }

        private static async Task<JObject> PostForm(string url, Dictionary<string, string> form, CancellationToken cancel)
        {
            var response = await Http.PostAsync(url, new FormUrlEncodedContent(form), cancel).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var json = text.Length > 0 ? JObject.Parse(text) : new JObject();
            if (!response.IsSuccessStatusCode) throw new AuthException("Microsoft: " + ((string)json["error_description"] ?? (string)json["error"] ?? response.StatusCode.ToString()));
            return json;
        }

        private static async Task<JObject> PostJson(string url, JObject body, string bearer, CancellationToken cancel)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            request.Headers.Accept.ParseAdd("application/json");
            if (bearer != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return JObject.Parse(text);
            JObject error = null;
            try
            {
                if (text.Length > 0) error = JObject.Parse(text);
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
            // XSTS explains why an account can not use Xbox Live
            switch ((long?)error?["XErr"])
            {
                case 2148916233: throw new AuthException("This Microsoft account has no Xbox profile; create one at xbox.com and try again.");
                case 2148916235: throw new AuthException("Xbox Live is not available in the country of this account.");
                case 2148916236:
                case 2148916237: throw new AuthException("This account needs adult verification at xbox.com.");
                case 2148916238: throw new AuthException("This is a child account: an adult must add it to a Microsoft family.");
            }
            if (url.Contains("login_with_xbox") && (int)response.StatusCode == 403)
            {
                throw new AuthException("Mojang has not authorized this application (client id) to sign in to Minecraft; API access has to be requested.");
            }
            throw new AuthException("Sign-in error (" + (int)response.StatusCode + ") at " + new Uri(url).Host + ".");
        }
    }
}
