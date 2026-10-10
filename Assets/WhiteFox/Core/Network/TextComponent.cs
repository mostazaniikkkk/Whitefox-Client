// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace WhiteFox.Core.Network
{
    /// <summary>A clickable or hoverable part of a text (character range of the plain text).</summary>
    public sealed class TextLink
    {
        public int Start;
        public int End;
        /// <summary>open_url, run_command, suggest_command, copy_to_clipboard, change_page; null when only hover.</summary>
        public string Action;
        public string Value;
        /// <summary>show_text hover, as legacy formatted text.</summary>
        public string Hover;
    }

    /// <summary>
    /// Chat component (Minosoft's ChatComponent): text, translations, style (color, bold, italic, underlined,
    /// strikethrough, obfuscated) and click/hover events. Parsed from JSON (status, login, signs) or NBT (play, 1.20.3+).
    /// <see cref="ToLegacy"/> turns it into a string with § codes (§#RRGGBB for hex colors) for the bitmap font.
    /// </summary>
    public sealed class TextComponent
    {
        public string Color;
        public bool? Bold;
        public bool? Italic;
        public bool? Underlined;
        public bool? Strikethrough;
        public bool? Obfuscated;
        public string ClickAction;
        public string ClickValue;
        public TextComponent HoverText;
        /// <summary>Translation table (en_us.json), set once the assets are loaded.</summary>
        public static Dictionary<string, string> Language = new Dictionary<string, string>();

        public string Text = "";
        public string Translate;
        public List<TextComponent> With = new List<TextComponent>();
        public List<TextComponent> Extra = new List<TextComponent>();

        public static TextComponent Of(string text) => new TextComponent { Text = text ?? "" };

        public static TextComponent FromNbt(object nbt)
        {
            switch (nbt)
            {
                case null: return Of("");
                case string text: return Of(text);
                case List<object> list:
                {
                    var root = Of("");
                    foreach (var element in list) root.Extra.Add(FromNbt(element));
                    return root;
                }
                case Dictionary<string, object> compound:
                {
                    var component = new TextComponent
                    {
                        // heterogeneous NBT lists wrap their elements as {"": value}
                        Text = Nbt.GetString(compound, "text") ?? Nbt.Get(compound, "")?.ToString() ?? "",
                        Translate = Nbt.GetString(compound, "translate"),
                    };
                    switch (Nbt.Get(compound, "with"))
                    {
                        case List<object> with:
                            foreach (var element in with) component.With.Add(FromNbt(element));
                            break;
                        // NbtOps writes lists of numbers as int/long/byte arrays (setblock coordinates)
                        case int[] ints:
                            foreach (var value in ints) component.With.Add(Of(value.ToString()));
                            break;
                        case long[] longs:
                            foreach (var value in longs) component.With.Add(Of(value.ToString()));
                            break;
                        case byte[] bytes:
                            foreach (var value in bytes) component.With.Add(Of(((sbyte)value).ToString()));
                            break;
                    }
                    if (Nbt.Get(compound, "extra") is List<object> extra)
                    {
                        foreach (var element in extra) component.Extra.Add(FromNbt(element));
                    }
                    component.Color = Nbt.GetString(compound, "color");
                    component.Bold = Flag(Nbt.Get(compound, "bold"));
                    component.Italic = Flag(Nbt.Get(compound, "italic"));
                    component.Underlined = Flag(Nbt.Get(compound, "underlined"));
                    component.Strikethrough = Flag(Nbt.Get(compound, "strikethrough"));
                    component.Obfuscated = Flag(Nbt.Get(compound, "obfuscated"));
                    var click = Nbt.Get(compound, "clickEvent");
                    component.ClickAction = Nbt.GetString(click, "action");
                    component.ClickValue = Nbt.GetString(click, "value");
                    var hover = Nbt.Get(compound, "hoverEvent");
                    if (Nbt.GetString(hover, "action") == "show_text") component.HoverText = FromNbt(Nbt.Get(hover, "contents") ?? Nbt.Get(hover, "value"));
                    return component;
                }
                default: return Of(nbt.ToString());
            }
        }

        public static TextComponent FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return Of("");
            try
            {
                return FromJson(JToken.Parse(json));
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return Of(json);
            }
        }

        public static TextComponent FromJson(JToken token)
        {
            switch (token)
            {
                case null: return Of("");
                case JValue value: return Of(value.ToString());
                case JArray array:
                {
                    var root = Of("");
                    foreach (var element in array) root.Extra.Add(FromJson(element));
                    return root;
                }
                case JObject json:
                {
                    var component = new TextComponent { Text = (string)json["text"] ?? "", Translate = (string)json["translate"] };
                    if (json["with"] is JArray with)
                    {
                        foreach (var element in with) component.With.Add(FromJson(element));
                    }
                    if (json["extra"] is JArray extra)
                    {
                        foreach (var element in extra) component.Extra.Add(FromJson(element));
                    }
                    component.Color = (string)json["color"];
                    component.Bold = (bool?)json["bold"];
                    component.Italic = (bool?)json["italic"];
                    component.Underlined = (bool?)json["underlined"];
                    component.Strikethrough = (bool?)json["strikethrough"];
                    component.Obfuscated = (bool?)json["obfuscated"];
                    component.ClickAction = (string)json["clickEvent"]?["action"];
                    component.ClickValue = (string)json["clickEvent"]?["value"];
                    if ((string)json["hoverEvent"]?["action"] == "show_text") component.HoverText = FromJson(json["hoverEvent"]["contents"] ?? json["hoverEvent"]["value"]);
                    return component;
                }
                default: return Of(token.ToString());
            }
        }

        private static readonly Regex Placeholder = new Regex(@"%(?:(\d+)\$)?([sd%])");

        public string ToPlain()
        {
            var builder = new StringBuilder();
            Append(builder);
            return LegacyFormatting.Strip(builder.ToString());
        }

        private void Append(StringBuilder builder)
        {
            if (Translate != null)
            {
                var format = Language.TryGetValue(Translate, out var translated) ? translated : Translate;
                var next = 0;
                builder.Append(Placeholder.Replace(format, match =>
                {
                    if (match.Groups[2].Value == "%") return "%";
                    var index = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) - 1 : next++;
                    return index >= 0 && index < With.Count ? With[index].ToPlain() : "";
                }));
            }
            else
            {
                builder.Append(Text);
            }
            foreach (var child in Extra) child.Append(builder);
        }

        public override string ToString() => ToPlain();

        private static bool? Flag(object value)
        {
            switch (value)
            {
                case sbyte b: return b != 0;
                case bool b: return b;
                case int i: return i != 0;
                case string s: return s == "true" || s == "1";
                default: return null;
            }
        }

        private static readonly Dictionary<string, char> ColorCodes = new Dictionary<string, char>
        {
            { "black", '0' }, { "dark_blue", '1' }, { "dark_green", '2' }, { "dark_aqua", '3' }, { "dark_red", '4' }, { "dark_purple", '5' },
            { "gold", '6' }, { "gray", '7' }, { "dark_gray", '8' }, { "blue", '9' }, { "green", 'a' }, { "aqua", 'b' }, { "red", 'c' },
            { "light_purple", 'd' }, { "yellow", 'e' }, { "white", 'f' },
        };

        private struct Style
        {
            public string Color;
            public bool Bold, Italic, Underlined, Strikethrough, Obfuscated;
            public string ClickAction, ClickValue;
            public TextComponent Hover;
        }

        /// <summary>The text with § formatting codes (inherited styles resolved), without click information.</summary>
        public string ToLegacy() => ToLegacy(out _);

        /// <summary>The text with § codes, and the clickable/hoverable ranges (indices into the plain text).</summary>
        public string ToLegacy(out List<TextLink> links)
        {
            var builder = new StringBuilder();
            var result = new List<TextLink>();
            var plainLength = 0;
            AppendLegacy(builder, new Style(), result, ref plainLength);
            links = result;
            return builder.ToString();
        }

        private void AppendLegacy(StringBuilder builder, Style parent, List<TextLink> links, ref int plainLength)
        {
            var style = parent;
            if (Color != null) style.Color = Color;
            if (Bold.HasValue) style.Bold = Bold.Value;
            if (Italic.HasValue) style.Italic = Italic.Value;
            if (Underlined.HasValue) style.Underlined = Underlined.Value;
            if (Strikethrough.HasValue) style.Strikethrough = Strikethrough.Value;
            if (Obfuscated.HasValue) style.Obfuscated = Obfuscated.Value;
            if (ClickAction != null)
            {
                style.ClickAction = ClickAction;
                style.ClickValue = ClickValue;
            }
            if (HoverText != null) style.Hover = HoverText;

            string own;
            if (Translate != null)
            {
                var format = Language.TryGetValue(Translate, out var translated) ? translated : Translate;
                var next = 0;
                var linksBefore = links.Count;
                var withParts = new List<string>();
                foreach (var argument in With)
                {
                    var argumentBuilder = new StringBuilder();
                    var argumentLength = 0;
                    argument.AppendLegacy(argumentBuilder, style, links, ref argumentLength);
                    withParts.Add(argumentBuilder.ToString());
                }
                // links inside the arguments would have offsets relative to the argument: only the whole text keeps its link
                links.RemoveRange(linksBefore, links.Count - linksBefore);
                own = Placeholder.Replace(format, match =>
                {
                    if (match.Groups[2].Value == "%") return "%";
                    var index = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) - 1 : next++;
                    return index >= 0 && index < withParts.Count ? withParts[index] + StyleCodes(style) : "";
                });
            }
            else
            {
                own = Text ?? "";
            }
            if (own.Length > 0)
            {
                builder.Append(StyleCodes(style));
                builder.Append(own);
                var plain = LegacyFormatting.Strip(own).Length;
                if (style.ClickAction != null || style.Hover != null)
                {
                    links.Add(new TextLink { Start = plainLength, End = plainLength + plain, Action = style.ClickAction, Value = style.ClickValue, Hover = style.Hover?.ToLegacy() });
                }
                plainLength += plain;
            }
            foreach (var child in Extra) child.AppendLegacy(builder, style, links, ref plainLength);
        }

        private static string StyleCodes(Style style)
        {
            var codes = new StringBuilder("§r");
            if (style.Color != null)
            {
                if (ColorCodes.TryGetValue(style.Color, out var code)) codes.Append('§').Append(code);
                else if (style.Color.Length == 7 && style.Color[0] == '#') codes.Append('§').Append(style.Color);
            }
            if (style.Obfuscated) codes.Append("§k");
            if (style.Bold) codes.Append("§l");
            if (style.Strikethrough) codes.Append("§m");
            if (style.Underlined) codes.Append("§n");
            if (style.Italic) codes.Append("§o");
            return codes.ToString();
        }
    }

    public static class LegacyFormatting
    {
        private static readonly Regex Codes = new Regex("§(#[0-9a-fA-F]{6}|.)");

        public static string Strip(string text) => Codes.Replace(text, "");
    }
}
