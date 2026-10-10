// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using WhiteFox.Core.Network;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Accounts
{
    public enum AccountType
    {
        /// <summary>Only a name (offline mode servers); the UUID is derived from it like vanilla.</summary>
        Offline,
        /// <summary>A Microsoft account with a Minecraft: Java Edition license (online mode servers).</summary>
        Microsoft,
    }

    /// <summary>An account to join servers with (Minosoft's Account / MicrosoftAccount / OfflineAccount).</summary>
    public sealed class Account
    {
        public AccountType Type;
        public string Name;
        public Guid Uuid;

        // Microsoft only: the rotating refresh token (kept between runs) and the Minecraft token (24 hours)
        public string RefreshToken;
        public string MinecraftToken;
        public DateTime MinecraftTokenExpires;

        public bool IsOnline => Type == AccountType.Microsoft;

        /// <summary>Stable id in the account list: the profile UUID for Microsoft accounts, the name for offline ones.</summary>
        public string Id => IsOnline ? Uuid.ToString("N") : "offline:" + Name;

        /// <summary>The Minecraft token is still valid for a while (no refresh needed before joining).</summary>
        public bool HasValidToken => !string.IsNullOrEmpty(MinecraftToken) && MinecraftTokenExpires > DateTime.UtcNow.AddMinutes(5);

        public static Account Offline(string name) => new Account { Type = AccountType.Offline, Name = name, Uuid = UuidUtil.Offline(name) };
    }

    /// <summary>
    /// The saved accounts and the selected one, in a TOML file. Microsoft tokens go through <see cref="Protect"/> /
    /// <see cref="Unprotect"/> (the client encrypts them for the current Windows user), so the file alone is useless to
    /// someone else; the refresh token keeps the account logged in for months without asking again.
    /// </summary>
    public sealed class AccountStore
    {
        public readonly List<Account> Accounts = new List<Account>();
        public string SelectedId;
        public readonly string Path;

        public Func<string, string> Protect = text => text;
        public Func<string, string> Unprotect = text => text;

        public AccountStore(string path)
        {
            Path = path;
        }

        public Account Selected
        {
            get
            {
                foreach (var account in Accounts)
                {
                    if (account.Id == SelectedId) return account;
                }
                return null;
            }
        }

        /// <summary>Adds an account, or replaces the one with the same id (a new login of the same profile).</summary>
        public void AddOrReplace(Account account)
        {
            Accounts.RemoveAll(other => other.Id == account.Id);
            Accounts.Add(account);
            SelectedId = account.Id;
        }

        public void Remove(Account account)
        {
            Accounts.Remove(account);
            if (SelectedId == account.Id) SelectedId = Accounts.Count > 0 ? Accounts[0].Id : null;
        }

        public void Load()
        {
            Accounts.Clear();
            if (!File.Exists(Path)) return;
            try
            {
                var root = Toml.Parse(File.ReadAllText(Path));
                SelectedId = root.GetString("selected");
                foreach (var table in root.Tables("account"))
                {
                    var account = new Account
                    {
                        Type = table.GetString("type") == "microsoft" ? AccountType.Microsoft : AccountType.Offline,
                        Name = table.GetString("name", "WhiteFox"),
                    };
                    account.Uuid = Guid.TryParse(table.GetString("uuid", ""), out var uuid) ? uuid : UuidUtil.Offline(account.Name);
                    if (account.IsOnline)
                    {
                        account.RefreshToken = Read(table.GetString("refresh_token"));
                        account.MinecraftToken = Read(table.GetString("minecraft_token"));
                        account.MinecraftTokenExpires = DateTime.TryParse(table.GetString("minecraft_token_expires", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires) ? expires : DateTime.MinValue;
                        // a token that can not be decrypted (another Windows user or machine) needs a new login
                        if (account.RefreshToken == null) continue;
                    }
                    Accounts.Add(account);
                }
            }
            catch (Exception exception)
            {
                Log.Warn("Can not read the accounts (" + Path + "): " + exception.Message);
            }
        }

        public void Save()
        {
            var root = new TomlTable();
            if (SelectedId != null) root["selected"] = SelectedId;
            var tables = new List<TomlTable>();
            foreach (var account in Accounts)
            {
                var table = new TomlTable
                {
                    ["type"] = account.IsOnline ? "microsoft" : "offline",
                    ["name"] = account.Name,
                    ["uuid"] = account.Uuid.ToString(),
                };
                if (account.IsOnline)
                {
                    table["refresh_token"] = Protect(account.RefreshToken ?? "");
                    if (!string.IsNullOrEmpty(account.MinecraftToken))
                    {
                        table["minecraft_token"] = Protect(account.MinecraftToken);
                        table["minecraft_token_expires"] = account.MinecraftTokenExpires.ToString("o");
                    }
                }
                tables.Add(table);
            }
            if (tables.Count > 0) root["account"] = tables;
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                // write a temporary file first: a crash while saving must not lose the accounts
                File.WriteAllText(Path + ".tmp", "# WhiteFox accounts: the tokens are encrypted for this Windows user\n" + Toml.Write(root));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(Path + ".tmp", Path);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not save the accounts (" + Path + "): " + exception.Message);
            }
        }

        private string Read(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                return Unprotect(stored);
            }
            catch (Exception exception)
            {
                Log.Warn("Can not decrypt a saved token: " + exception.Message);
                return null;
            }
        }
    }
}
