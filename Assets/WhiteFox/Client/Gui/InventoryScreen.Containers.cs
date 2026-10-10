// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Vanilla container screens (1.20.4 textures and slot positions): dispenser/dropper, crafting table, furnaces,
    /// brewing stand, anvil, enchanting table, villager trading, grindstone, smithing table and hopper.
    /// </summary>
    public sealed partial class InventoryScreen
    {
        private static readonly Color Label = new Color32(0x40, 0x40, 0x40, 0xFF);

        private readonly GuiTextField rename = new GuiTextField { MaxLength = 50 };
        private string sentName;
        private int renameWindow = -1;
        private int tradeScroll;

        /// <summary>A text field has the keyboard (the inventory key must not close the screen).</summary>
        public bool TypingText => rename.Focused && renameWindow >= 0;

        /// <summary>The screen of a menu type, or null for the generic fallback.</summary>
        private Rect? DrawContainer(GameSession session, ContainerWindow window)
        {
            switch (window.Type)
            {
                case 6: return DrawDispenser(window);
                case 7: return DrawCrafter(session, window);
                case 9: return DrawBeacon(session, window);
                case 17: return DrawLectern(session, window);
                case 18: return DrawLoom(session, window);
                case 23: return DrawCartography(window);
                case 24: return DrawStonecutter(session, window);
                case 8: return DrawAnvil(session, window);
                case 10: return DrawFurnace(session, window, "blast_furnace");
                case 11: return DrawBrewing(window);
                case 12: return DrawCrafting(session, window);
                case 13: return DrawEnchanting(session, window);
                case 14: return DrawFurnace(session, window, "furnace");
                case 15: return DrawGrindstone(window);
                case 16: return DrawHopper(window);
                case 19: return DrawMerchant(session, window);
                case 21: return DrawSmithing(window);
                case 22: return DrawFurnace(session, window, "smoker");
                default: return null;
            }
        }

        private Rect Standard(string texture, ContainerWindow window, float height = 166, bool centeredTitle = false)
        {
            var panel = Centered(176, height);
            Region(Container + texture + ".png", new Rect(0, 0, 176, height), panel);
            if (centeredTitle) gui.TextCentered(window.Title, panel.x + 88, panel.y + 6, Label, false);
            else gui.Text(window.Title, panel.x + 8, panel.y + 6, Label, false);
            gui.Text("Inventario", panel.x + 8, panel.y + height - 94, Label, false);
            return panel;
        }

        /// <summary>A gui sprite (1.20.2+ sprites folder), optionally only a part of it (progress bars).</summary>
        private void Part(string sprite, Rect panel, float x, float y, float width, float height, float fromX = 0, float fromY = 0, float partWidth = -1, float partHeight = -1)
        {
            var asset = gui.Assets.Sprite(sprite);
            if (asset == null || !gui.Repaint) return;
            if (partWidth < 0) partWidth = width;
            if (partHeight < 0) partHeight = height;
            if (partWidth <= 0 || partHeight <= 0) return;
            var uv = new Rect(fromX / width, 1 - (fromY + partHeight) / height, partWidth / width, partHeight / height);
            GUI.DrawTextureWithTexCoords(new Rect(panel.x + x + fromX, panel.y + y + fromY, partWidth, partHeight), asset.Texture, uv);
        }

        private Rect DrawDispenser(ContainerWindow window)
        {
            var panel = Standard("dispenser", window, 166, true);
            for (var i = 0; i < 9; i++) Slot(window, i, panel, 62 + i % 3 * 18, 17 + i / 3 * 18);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        private Rect DrawCrafting(GameSession session, ContainerWindow window)
        {
            var panel = Centered(176, 166);
            Region(Container + "crafting_table.png", new Rect(0, 0, 176, 166), panel);
            gui.Text(window.Title, panel.x + 29, panel.y + 6, Label, false);
            gui.Text("Inventario", panel.x + 8, panel.y + 72, Label, false);
            Slot(window, 0, panel, 124, 35);
            for (var i = 0; i < 9; i++) Slot(window, 1 + i, panel, 30 + i % 3 * 18, 17 + i / 3 * 18);
            PlayerSlots(window, panel, 84);
            DrawRecipeBook(session, window, panel, recipe => FitsGrid(recipe, 3), new Vector2(5, 34));
            return panel;
        }

        /// <summary>Furnace, blast furnace, smoker: flame (fuel left) and arrow (cooking progress) from the properties.</summary>
        private Rect DrawFurnace(GameSession session, ContainerWindow window, string kind)
        {
            var panel = Standard(kind, window, 166, true);
            var p = window.Properties;
            if (p[0] > 0)
            {
                var total = p[1] > 0 ? p[1] : 200;
                var lit = Mathf.Ceil(Mathf.Clamp01((float)p[0] / total) * 13) + 1;
                Part("container/" + kind + "/lit_progress", panel, 56, 36, 14, 14, 0, 14 - lit, 14, lit);
            }
            if (p[2] > 0 && p[3] > 0)
            {
                var progress = Mathf.Ceil(Mathf.Clamp01((float)p[2] / p[3]) * 24);
                Part("container/" + kind + "/burn_progress", panel, 79, 34, 24, 16, 0, 0, progress, 16);
            }
            Slot(window, 0, panel, 56, 17);
            Slot(window, 1, panel, 56, 53);
            Slot(window, 2, panel, 116, 35);
            PlayerSlots(window, panel, 84);
            var cooking = CookingType(window.Type);
            DrawRecipeBook(session, window, panel, recipe => recipe.Type == cooking, new Vector2(20, 34));
            return panel;
        }

        private Rect DrawBrewing(ContainerWindow window)
        {
            var panel = Standard("brewing_stand", window, 166, true);
            var p = window.Properties;
            var fuel = Mathf.Clamp((18 * p[1] + 20 - 1) / 20, 0, 18);
            if (fuel > 0) Part("container/brewing_stand/fuel_length", panel, 60, 44, 18, 4, 0, 0, fuel, 4);
            if (p[0] > 0)
            {
                var progress = Mathf.Round(28 * (1 - p[0] / 400f));
                Part("container/brewing_stand/brew_progress", panel, 97, 16, 9, 28, 0, 0, 9, progress);
            }
            Slot(window, 0, panel, 56, 51);
            Slot(window, 1, panel, 79, 58);
            Slot(window, 2, panel, 102, 51);
            Slot(window, 3, panel, 79, 17);
            Slot(window, 4, panel, 17, 17);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        /// <summary>Anvil: the name field sends Rename Item, the cost comes from property 0.</summary>
        private Rect DrawAnvil(GameSession session, ContainerWindow window)
        {
            var panel = Standard("anvil", window);
            if (renameWindow != window.Id)
            {
                renameWindow = window.Id;
                rename.Text = "";
                sentName = null;
            }
            var input = window.Slots.Length > 0 ? window.Slots[0] : null;
            Part(input != null ? "container/anvil/text_field" : "container/anvil/text_field_disabled", panel, 59, 20, 110, 16);
            if (input != null)
            {
                if (sentName == null)
                {
                    rename.Text = input.Item.DisplayName;
                    sentName = rename.Text;
                }
                rename.Draw(gui, new Rect(panel.x + 61, panel.y + 24, 103, 12), false);
                if (rename.Text != sentName)
                {
                    sentName = rename.Text;
                    session.Server?.SendRenameItem(rename.Text);
                }
            }
            else
            {
                sentName = null;
            }
            var cost = window.Properties[0];
            if (cost > 0)
            {
                var creative = session.Player.Gamemode == Core.Physics.Gamemode.Creative;
                var color = cost >= 40 && !creative ? GuiRenderer.Red : creative || session.ExperienceLevel >= cost ? new Color32(0x80, 0xFF, 0x20, 0xFF) : (Color)GuiRenderer.Red;
                var text = cost >= 40 && !creative ? "¡Demasiado caro!" : "Coste: " + cost;
                gui.Fill(new Rect(panel.x + 176 - 8 - gui.Font.Width(text) - 2, panel.y + 67, gui.Font.Width(text) + 4, 12), new Color(0, 0, 0, 0.3f));
                gui.TextRight(text, panel.x + 176 - 8, panel.y + 69, color);
            }
            Slot(window, 0, panel, 27, 47);
            Slot(window, 1, panel, 76, 47);
            Slot(window, 2, panel, 134, 47);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        /// <summary>Enchanting table: three options (properties 0-2 cost, 4-6 hint) that send Click Container Button.</summary>
        private Rect DrawEnchanting(GameSession session, ContainerWindow window)
        {
            var panel = Standard("enchanting_table", window);
            var p = window.Properties;
            var lapis = window.Slots.Length > 1 && window.Slots[1] != null ? window.Slots[1].Count : 0;
            var creative = session.Player.Gamemode == Core.Physics.Gamemode.Creative;
            for (var i = 0; i < 3; i++)
            {
                var rect = new Rect(panel.x + 60, panel.y + 14 + 19 * i, 108, 19);
                var cost = p[i];
                if (cost <= 0)
                {
                    Part("container/enchanting_table/enchantment_slot_disabled", panel, 60, 14 + 19 * i, 108, 19);
                    continue;
                }
                var affordable = creative || (lapis >= i + 1 && session.ExperienceLevel >= cost);
                var hovered = rect.Contains(gui.Mouse);
                Part(!affordable ? "container/enchanting_table/enchantment_slot_disabled" : hovered ? "container/enchanting_table/enchantment_slot_highlighted" : "container/enchanting_table/enchantment_slot", panel, 60, 14 + 19 * i, 108, 19);
                Part("container/enchanting_table/level_" + (i + 1) + (affordable ? "" : "_disabled"), panel, 61, 15 + 19 * i, 16, 16);
                var text = cost.ToString();
                gui.Text(text, rect.xMax - 2 - gui.Font.Width(text), rect.y + 9, affordable ? new Color32(0x80, 0xFF, 0x20, 0xFF) : new Color32(0x40, 0x70, 0x20, 0xFF));
                var current = Event.current;
                if (affordable && hovered && current.type == EventType.MouseDown && current.button == 0)
                {
                    session.Server?.SendContainerButton(window.Id, i);
                    current.Use();
                }
            }
            Slot(window, 0, panel, 15, 47);
            Slot(window, 1, panel, 35, 47);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        private Rect DrawGrindstone(ContainerWindow window)
        {
            var panel = Standard("grindstone", window);
            Slot(window, 0, panel, 49, 19);
            Slot(window, 1, panel, 49, 40);
            Slot(window, 2, panel, 129, 34);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        private Rect DrawSmithing(ContainerWindow window)
        {
            var panel = Standard("smithing", window);
            Slot(window, 0, panel, 8, 48);
            Slot(window, 1, panel, 26, 48);
            Slot(window, 2, panel, 44, 48);
            Slot(window, 3, panel, 98, 48);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        private Rect DrawHopper(ContainerWindow window)
        {
            var panel = Standard("hopper", window, 133);
            for (var i = 0; i < 5; i++) Slot(window, i, panel, 44 + i * 18, 20);
            PlayerSlots(window, panel, 51);
            return panel;
        }

        /// <summary>Villager trades (villager.png, 276 wide): the offer list selects a trade (Select Trade).</summary>
        private Rect DrawMerchant(GameSession session, ContainerWindow window)
        {
            var panel = Centered(276, 166);
            var texture = gui.Assets.Get(Container + "villager.png");
            if (texture != null && gui.Repaint)
            {
                float w = texture.Width, h = texture.Height;
                GUI.DrawTextureWithTexCoords(panel, texture.Texture, new Rect(0, 1 - 166 / h, 276 / w, 166 / h));
            }
            gui.TextCentered(window.Title, panel.x + 49 + 188 / 2f, panel.y + 6, Label, false);
            gui.Text("Inventario", panel.x + 108, panel.y + 72, Label, false);
            gui.Text("Comercios", panel.x + 5 + (88 - gui.Font.Width("Comercios")) / 2, panel.y + 6, Label, false);

            var offers = window.Offers;
            if (offers != null)
            {
                var scroll = Event.current.type == EventType.ScrollWheel && new Rect(panel.x + 4, panel.y + 17, 97, 140).Contains(gui.Mouse) ? Event.current.delta.y : 0;
                if (scroll != 0)
                {
                    tradeScroll = Mathf.Clamp(tradeScroll + (scroll > 0 ? 1 : -1), 0, Mathf.Max(0, offers.Count - 7));
                    Event.current.Use();
                }
                for (var i = tradeScroll; i < offers.Count && i < tradeScroll + 7; i++)
                {
                    var offer = offers[i];
                    var y = panel.y + 18 + (i - tradeScroll) * 20;
                    var rect = new Rect(panel.x + 5, y, 88, 20);
                    var hovered = rect.Contains(gui.Mouse);
                    gui.Sprite(hovered || i == window.SelectedTrade ? "widget/button_highlighted" : "widget/button", rect);
                    if (offer.CostA != null) DrawItem(offer.CostA.Item, offer.CostA.Count, new Rect(rect.x + 4, y + 2, 16, 16));
                    if (offer.CostB != null) DrawItem(offer.CostB.Item, offer.CostB.Count, new Rect(rect.x + 30, y + 2, 16, 16));
                    gui.Sprite(offer.OutOfStock ? "container/villager/trade_arrow_out_of_stock" : "container/villager/trade_arrow", new Rect(rect.x + 53, y + 6, 10, 9));
                    if (offer.Result != null) DrawItem(offer.Result.Item, offer.Result.Count, new Rect(rect.x + 68, y + 2, 16, 16));
                    var current = Event.current;
                    if (hovered && current.type == EventType.MouseDown && current.button == 0)
                    {
                        window.SelectedTrade = i;
                        session.Server?.SendSelectTrade(i);
                        current.Use();
                    }
                }
            }
            Slot(window, 0, panel, 136, 37);
            Slot(window, 1, panel, 162, 37);
            Slot(window, 2, panel, 220, 37);
            PlayerSlots(window, panel, 84, 108);
            return panel;
        }
    }
}
