// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;

namespace WhiteFox.Core.Game
{
    public sealed class BossBar
    {
        public string Title = "";
        public float Health = 1;
        /// <summary>0 pink, 1 blue, 2 red, 3 green, 4 yellow, 5 purple, 6 white.</summary>
        public int Color;
        /// <summary>0 none, 1 = 6 notches, 2 = 10, 3 = 12, 4 = 20.</summary>
        public int Division;
        /// <summary>Flags: 0x01 darken the sky, 0x02 boss music (the dragon), 0x04 world fog.</summary>
        public int Flags;
    }

    public sealed class Objective
    {
        public string Name;
        public string DisplayName = "";
        /// <summary>0 integer, 1 hearts.</summary>
        public int Type;
        /// <summary>-1 default, 0 blank, 1 styled, 2 fixed (<see cref="FixedFormat"/>).</summary>
        public int NumberFormat = -1;
        public string FixedFormat;
    }

    public sealed class Score
    {
        public string Holder;
        public int Value;
        public string DisplayName;
        public int NumberFormat = -1;
        public string FixedFormat;
    }

    public sealed class Team
    {
        public string Name;
        public string DisplayName = "";
        public string Prefix = "";
        public string Suffix = "";
        /// <summary>Chat formatting id (0..15 colors, 21 reset).</summary>
        public int Color = 21;
        public string NameTagVisibility = "always";
        public readonly HashSet<string> Members = new HashSet<string>();

        /// <summary>The § code of the team color, or "".</summary>
        public string ColorCode => Color >= 0 && Color < 16 ? "§" + "0123456789abcdef"[Color] : "";

        public string Decorate(string name) => Prefix + ColorCode + name + "§r" + Suffix;
    }

    public sealed class Toast
    {
        public string Title;
        public string Description;
        public ItemStack Icon;
        /// <summary>0 task, 1 challenge, 2 goal.</summary>
        public int Frame;
        /// <summary>Environment.TickCount when it appeared on screen (0: waiting).</summary>
        public int Shown;
    }

    public sealed class Advancement
    {
        public string Title;
        public string Description;
        public ItemStack Icon;
        public int Frame;
        public bool ShowToast;
        public List<string[]> Requirements = new List<string[]>();
        public readonly HashSet<string> Achieved = new HashSet<string>();

        public bool Done
        {
            get
            {
                if (Requirements.Count == 0) return false;
                foreach (var group in Requirements)
                {
                    var any = false;
                    foreach (var criterion in group) any |= Achieved.Contains(criterion);
                    if (!any) return false;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// Server driven HUD state (Minosoft's TitleElement, BossbarLayout, ScoreboardSideElement, TabList header/footer,
    /// advancement toasts). Texts are legacy formatted strings (§ codes) made from the text components.
    /// </summary>
    public sealed class ServerHud
    {
        // ---- titles
        public string Title;
        public string Subtitle;
        public int FadeIn = 10, Stay = 70, FadeOut = 20;
        /// <summary>Game tick when the title was set, -1 without title.</summary>
        public long TitleStart = -1;

        // ---- boss bars
        public readonly Dictionary<Guid, BossBar> BossBars = new Dictionary<Guid, BossBar>();
        public readonly List<Guid> BossBarOrder = new List<Guid>();

        // ---- scoreboard
        public readonly Dictionary<string, Objective> Objectives = new Dictionary<string, Objective>();
        /// <summary>Objective per display slot: 0 list, 1 sidebar, 2 below name, 3..18 sidebar of team color.</summary>
        public readonly string[] Displays = new string[19];
        public readonly Dictionary<string, Dictionary<string, Score>> Scores = new Dictionary<string, Dictionary<string, Score>>();
        public readonly Dictionary<string, Team> Teams = new Dictionary<string, Team>();
        public readonly Dictionary<string, Team> TeamOf = new Dictionary<string, Team>();

        // ---- tab list
        public string TabHeader;
        public string TabFooter;

        // ---- advancements
        public readonly Dictionary<string, Advancement> Advancements = new Dictionary<string, Advancement>();
        public readonly List<Toast> Toasts = new List<Toast>();

        public void SetTitle(string title, long tick)
        {
            Title = title;
            TitleStart = tick;
        }

        public void SetSubtitle(string subtitle) => Subtitle = subtitle;

        public void SetTimes(int fadeIn, int stay, int fadeOut)
        {
            FadeIn = fadeIn;
            Stay = stay;
            FadeOut = fadeOut;
        }

        public void ClearTitles(bool reset)
        {
            Title = null;
            Subtitle = null;
            TitleStart = -1;
            if (reset) SetTimes(10, 70, 20);
        }

        public void Clear()
        {
            ClearTitles(true);
            BossBars.Clear();
            BossBarOrder.Clear();
            Objectives.Clear();
            Array.Clear(Displays, 0, Displays.Length);
            Scores.Clear();
            Teams.Clear();
            TeamOf.Clear();
            TabHeader = TabFooter = null;
            Advancements.Clear();
            Toasts.Clear();
        }

        public BossBar GetOrAddBossBar(Guid id)
        {
            if (!BossBars.TryGetValue(id, out var bar))
            {
                BossBars[id] = bar = new BossBar();
                BossBarOrder.Add(id);
            }
            return bar;
        }

        /// <summary>Combined flags of all boss bars.</summary>
        public int BossFlags
        {
            get
            {
                var flags = 0;
                foreach (var bar in BossBars.Values) flags |= bar.Flags;
                return flags;
            }
        }

        public void RemoveBossBar(Guid id)
        {
            BossBars.Remove(id);
            BossBarOrder.Remove(id);
        }

        public void RemoveObjective(string name)
        {
            Objectives.Remove(name);
            Scores.Remove(name);
            for (var i = 0; i < Displays.Length; i++)
            {
                if (Displays[i] == name) Displays[i] = null;
            }
        }

        public void SetScore(string objective, Score score)
        {
            if (!Scores.TryGetValue(objective, out var scores)) Scores[objective] = scores = new Dictionary<string, Score>();
            scores[score.Holder] = score;
        }

        public void ResetScore(string holder, string objective)
        {
            if (objective != null)
            {
                if (Scores.TryGetValue(objective, out var scores)) scores.Remove(holder);
                return;
            }
            foreach (var scores in Scores.Values) scores.Remove(holder);
        }

        public void SetTeamMembers(Team team, IEnumerable<string> members, bool add)
        {
            foreach (var member in members)
            {
                if (add)
                {
                    team.Members.Add(member);
                    TeamOf[member] = team;
                }
                else
                {
                    team.Members.Remove(member);
                    if (TeamOf.TryGetValue(member, out var current) && current == team) TeamOf.Remove(member);
                }
            }
        }

        public void RemoveTeam(string name)
        {
            if (!Teams.TryGetValue(name, out var team)) return;
            SetTeamMembers(team, new List<string>(team.Members), false);
            Teams.Remove(name);
        }

        /// <summary>The objective of the sidebar for a player: the team color slot first, then the normal sidebar.</summary>
        public Objective SidebarFor(string player)
        {
            if (player != null && TeamOf.TryGetValue(player, out var team) && team.Color >= 0 && team.Color < 16)
            {
                var name = Displays[3 + team.Color];
                if (name != null && Objectives.TryGetValue(name, out var colored)) return colored;
            }
            return Displays[1] != null && Objectives.TryGetValue(Displays[1], out var objective) ? objective : null;
        }

        public Objective DisplayObjective(int slot) => Displays[slot] != null && Objectives.TryGetValue(Displays[slot], out var objective) ? objective : null;

        public Score GetScore(string objective, string holder) => Scores.TryGetValue(objective, out var scores) && scores.TryGetValue(holder, out var score) ? score : null;

        /// <summary>Update Advancements: new toasts for advancements completed by this update (not on the initial reset).</summary>
        public void OnAdvancements(bool reset, Dictionary<string, Advancement> added, List<string> removed, Dictionary<string, List<KeyValuePair<string, bool>>> progress)
        {
            if (reset) Advancements.Clear();
            foreach (var pair in added) Advancements[pair.Key] = pair.Value;
            foreach (var id in removed) Advancements.Remove(id);
            foreach (var pair in progress)
            {
                if (!Advancements.TryGetValue(pair.Key, out var advancement)) continue;
                var before = advancement.Done;
                foreach (var criterion in pair.Value)
                {
                    if (criterion.Value) advancement.Achieved.Add(criterion.Key);
                    else advancement.Achieved.Remove(criterion.Key);
                }
                if (!reset && !before && advancement.Done && advancement.ShowToast && advancement.Title != null)
                {
                    Toasts.Add(new Toast { Title = advancement.Title, Description = advancement.Description, Icon = advancement.Icon, Frame = advancement.Frame });
                }
            }
        }
    }
}
