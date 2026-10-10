// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Physics;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Container screens with the vanilla textures (Minosoft's ContainerGUIManager): the survival inventory
    /// (container/inventory.png), chests (generic_54.png), a generic layout for other containers, and the creative
    /// item search (creative_inventory/tab_item_search.png). Items are drawn with their icons, counts and tooltips.
    /// </summary>
    public sealed partial class InventoryScreen
    {
        private const string Container = "assets/minecraft/textures/gui/container/";

        private readonly GuiRenderer gui;
        private readonly ItemIcons icons;
        private readonly List<Item> catalog = new List<Item>();
        private readonly List<Item> filtered = new List<Item>();
        private readonly GuiTextField search = new GuiTextField { MaxLength = 50 };
        private string lastSearch;
        private int scrollRow;

        // slots of the current frame: rect (gui pixels) -> slot index
        private readonly List<KeyValuePair<Rect, int>> slots = new List<KeyValuePair<Rect, int>>();
        private readonly List<KeyValuePair<Rect, Item>> catalogSlots = new List<KeyValuePair<Rect, Item>>();

        public InventoryScreen(GuiRenderer gui, ItemIcons icons, ItemRegistry items)
        {
            this.gui = gui;
            this.icons = icons;
            for (var id = 1; id < items.Count; id++)
            {
                var item = items.Get(id);
                if (item != null && item.Name.Path != "air") catalog.Add(item);
            }
        }

        /// <summary>Creative mode (or the local test world) uses the creative item search.</summary>
        private static bool Creative(GameSession session) => session.Inventory.Open == null && (!session.IsRemote || session.Player.Gamemode == Gamemode.Creative);

        /// <summary>Draws the player doll into a texture (pixel size, mouse relative to its center in gui pixels).</summary>
        public System.Func<int, int, Vector2, Texture> DollRenderer;

        public void Draw(GameSession session)
        {
            var inventory = session.Inventory;
            if (inventory.Open == null || inventory.Open.Type != 8) rename.Focused = false;
            slots.Clear();
            catalogSlots.Clear();
            gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));

            Rect panel;
            if (Creative(session)) panel = DrawCreative(session);
            else if (inventory.Open == null) panel = DrawPlayerInventory(session, inventory);
            else if (inventory.Open.ChestRows > 0) panel = DrawChest(inventory.Open);
            else panel = DrawContainer(session, inventory.Open) ?? DrawGeneric(inventory.Open);

            HandleClicks(session, panel);
            DrawCarriedAndTooltip(inventory);
            if (recipeTooltip != null)
            {
                var width = gui.Font.Width(recipeTooltip) + 6;
                var rect = new Rect(gui.Mouse.x + 12, gui.Mouse.y - 12, width, 14);
                gui.Fill(rect, new Color32(0x10, 0x00, 0x10, 0xF0));
                gui.Text(recipeTooltip, rect.x + 3, rect.y + 3);
                recipeTooltip = null;
            }
        }

        // ------------------------------------------------------------------ layouts

        private Rect DrawPlayerInventory(GameSession session, Inventory inventory)
        {
            var panel = Centered(176, 166);
            Region(Container + "inventory.png", new Rect(0, 0, 176, 166), panel);
            var window = inventory.Player;
            // the player in the black window (vanilla: 26, 8, 49 x 70)
            var dollRect = new Rect(panel.x + 26, panel.y + 8, 49, 70);
            if (DollRenderer != null && gui.Repaint)
            {
                var doll = DollRenderer(Mathf.RoundToInt(49 * gui.Scale), Mathf.RoundToInt(70 * gui.Scale), gui.Mouse - new Vector2(dollRect.center.x, dollRect.y + 20));
                if (doll != null) GUI.DrawTexture(dollRect, doll);
            }
            Slot(window, 0, panel, 154, 28);
            for (var i = 0; i < 4; i++) Slot(window, 1 + i, panel, 98 + i % 2 * 18, 18 + i / 2 * 18);
            for (var i = 0; i < 4; i++) Slot(window, 5 + i, panel, 8, 8 + i * 18);
            for (var i = 0; i < 27; i++) Slot(window, 9 + i, panel, 8 + i % 9 * 18, 84 + i / 9 * 18);
            for (var i = 0; i < 9; i++) Slot(window, 36 + i, panel, 8 + i * 18, 142);
            Slot(window, 45, panel, 77, 62);
            gui.Text("Crafting", panel.x + 97, panel.y + 6, new Color32(0x40, 0x40, 0x40, 0xFF), false);
            DrawRecipeBook(session, window, panel, recipe => FitsGrid(recipe, 2), new Vector2(104, 61));
            return panel;
        }

        private Rect DrawChest(ContainerWindow window)
        {
            var rows = window.ChestRows;
            var height = 114 + rows * 18;
            var panel = Centered(176, height);
            var top = 17 + rows * 18;
            Region(Container + "generic_54.png", new Rect(0, 0, 176, top), new Rect(panel.x, panel.y, 176, top));
            Region(Container + "generic_54.png", new Rect(0, 126, 176, 96), new Rect(panel.x, panel.y + top, 176, 96));
            for (var i = 0; i < rows * 9 && i < window.Slots.Length; i++) Slot(window, i, panel, 8 + i % 9 * 18, 18 + i / 9 * 18);
            PlayerSlots(window, panel, top + 14);
            gui.Text(window.Title, panel.x + 8, panel.y + 6, new Color32(0x40, 0x40, 0x40, 0xFF), false);
            gui.Text("Inventory", panel.x + 8, panel.y + top + 3, new Color32(0x40, 0x40, 0x40, 0xFF), false);
            return panel;
        }

        /// <summary>Containers without a dedicated layout yet (furnace, crafting table, ...): container slots in rows.</summary>
        private Rect DrawGeneric(ContainerWindow window)
        {
            var count = window.ContainerSlots;
            var rows = Mathf.Max(1, (count + 8) / 9);
            var top = 17 + rows * 18;
            var panel = Centered(176, top + 96);
            Region(Container + "generic_54.png", new Rect(0, 0, 176, 17), new Rect(panel.x, panel.y, 176, 17));
            for (var row = 0; row < rows; row++)
            {
                Region(Container + "generic_54.png", new Rect(0, 17, 176, 18), new Rect(panel.x, panel.y + 17 + row * 18, 176, 18));
            }
            Region(Container + "generic_54.png", new Rect(0, 126, 176, 96), new Rect(panel.x, panel.y + top, 176, 96));
            for (var i = 0; i < count; i++) Slot(window, i, panel, 8 + i % 9 * 18, 18 + i / 9 * 18);
            PlayerSlots(window, panel, top + 14);
            gui.Text(window.Title, panel.x + 8, panel.y + 6, new Color32(0x40, 0x40, 0x40, 0xFF), false);
            return panel;
        }

        private void PlayerSlots(ContainerWindow window, Rect panel, float y, float x = 8)
        {
            // the crafter has its result slot after the player inventory
            var first = window.Type == 7 ? 9 : window.ContainerSlots;
            for (var i = 0; i < 27; i++) Slot(window, first + i, panel, x + i % 9 * 18, y + i / 9 * 18);
            for (var i = 0; i < 9; i++) Slot(window, first + 27 + i, panel, x + i * 18, y + 58);
        }

        private Rect DrawCreative(GameSession session)
        {
            var panel = Centered(195, 136);
            Region(Container + "creative_inventory/tab_item_search.png", new Rect(0, 0, 195, 136), panel);

            if (search.Text != lastSearch)
            {
                lastSearch = search.Text;
                scrollRow = 0;
                filtered.Clear();
                var query = search.Text.Trim().ToLowerInvariant();
                foreach (var item in catalog)
                {
                    if (query.Length == 0 || item.DisplayName.ToLowerInvariant().Contains(query) || item.Name.Path.Contains(query.Replace(' ', '_'))) filtered.Add(item);
                }
            }
            search.Focused = true;
            search.Draw(gui, new Rect(panel.x + 80, panel.y + 4, 90, 12), false);

            var rows = (filtered.Count + 8) / 9;
            var maxRow = Mathf.Max(0, rows - 5);
            var current = Event.current;
            if (current.type == EventType.ScrollWheel)
            {
                scrollRow = Mathf.Clamp(scrollRow + (current.delta.y > 0 ? 1 : -1), 0, maxRow);
                current.Use();
            }
            for (var i = 0; i < 45; i++)
            {
                var index = scrollRow * 9 + i;
                if (index >= filtered.Count) break;
                var rect = new Rect(panel.x + 9 + i % 9 * 18, panel.y + 18 + i / 9 * 18, 16, 16);
                DrawItem(filtered[index], 1, rect);
                Hover(rect);
                catalogSlots.Add(new KeyValuePair<Rect, Item>(rect, filtered[index]));
            }
            // scroller
            var scroller = maxRow == 0 ? "container/creative_inventory/scroller_disabled" : "container/creative_inventory/scroller";
            var scrollY = maxRow == 0 ? 0 : (112 - 15) * scrollRow / (float)maxRow;
            gui.Sprite(scroller, new Rect(panel.x + 175, panel.y + 18 + Mathf.Round(scrollY), 12, 15));

            // hotbar (player inventory slots 36..44)
            var window = session.Inventory.Player;
            for (var i = 0; i < 9; i++) Slot(window, 36 + i, panel, 9 + i * 18, 112);
            gui.Text("Search", panel.x + 8, panel.y + 6, new Color32(0x40, 0x40, 0x40, 0xFF), false);
            return panel;
        }

        // ------------------------------------------------------------------ slots, items, input

        private Rect Centered(float width, float height) => new Rect(Mathf.Floor((gui.Width - width) / 2), Mathf.Floor((gui.Height - height) / 2), width, height);

        private void Region(string texture, Rect source, Rect target)
        {
            var sprite = gui.Assets.Get(texture);
            if (sprite == null || !gui.Repaint) return;
            float w = sprite.Width, h = sprite.Height;
            GUI.DrawTextureWithTexCoords(target, sprite.Texture, new Rect(source.x / w, 1 - (source.y + source.height) / h, source.width / w, source.height / h));
        }

        private void Slot(ContainerWindow window, int slot, Rect panel, float x, float y)
        {
            var rect = new Rect(panel.x + x, panel.y + y, 16, 16);
            if (slot < window.Slots.Length)
            {
                var stack = window.Slots[slot];
                if (stack != null) DrawItem(stack.Item, stack.Count, rect, stack);
            }
            Hover(rect);
            slots.Add(new KeyValuePair<Rect, int>(rect, slot));
        }

        private void Hover(Rect rect)
        {
            if (rect.Contains(gui.Mouse)) gui.Fill(rect, new Color(1, 1, 1, 0.5f));
        }

        private void DrawItem(Item item, int count, Rect rect, ItemStack stack = null)
        {
            var icon = stack != null ? icons.Get(stack) : icons.Get(item.Name.Path);
            if (icon != null && gui.Repaint)
            {
                GUI.DrawTexture(rect, icon);
                if (stack != null && ItemProperties.HasFoil(stack)) icons.DrawGlint(rect, icon);
            }
            else if (icon == null) gui.TextCentered(item.Name.Path.Substring(0, Mathf.Min(2, item.Name.Path.Length)), rect.center.x, rect.y + 4);
            if (count > 1) gui.TextRight(count.ToString(), rect.x + 17, rect.y + 9);
        }

        private void HandleClicks(GameSession session, Rect panel)
        {
            var current = Event.current;
            if (current.type != EventType.MouseDown || (current.button != 0 && current.button != 1)) return;
            var inventory = session.Inventory;
            var mouse = gui.Mouse;
            var shift = current.shift;
            var creative = Creative(session);

            foreach (var pair in catalogSlots)
            {
                if (!pair.Key.Contains(mouse)) continue;
                inventory.CreativePick(pair.Value, current.button);
                current.Use();
                return;
            }
            foreach (var pair in slots)
            {
                if (!pair.Key.Contains(mouse)) continue;
                if (creative) inventory.CreativeClick(pair.Value, current.button);
                else inventory.Click(pair.Value, current.button, shift);
                current.Use();
                return;
            }
            if (!panel.Contains(mouse) && !(recipeBookOpen && bookRect.Contains(mouse)))
            {
                // outside of the window: drop (survival) or delete (creative)
                if (creative) inventory.Carried = null;
                else inventory.Click(-999, current.button, false);
                current.Use();
            }
        }

        private void DrawCarriedAndTooltip(Inventory inventory)
        {
            var mouse = gui.Mouse;
            if (inventory.Carried != null)
            {
                DrawItem(inventory.Carried.Item, inventory.Carried.Count, new Rect(mouse.x - 8, mouse.y - 8, 16, 16), inventory.Carried);
                return;
            }
            Item hovered = null;
            foreach (var pair in catalogSlots)
            {
                if (pair.Key.Contains(mouse)) hovered = pair.Value;
            }
            if (hovered == null)
            {
                var window = inventory.Active;
                foreach (var pair in slots)
                {
                    if (!pair.Key.Contains(mouse) || pair.Value >= window.Slots.Length) continue;
                    hovered = window.Slots[pair.Value]?.Item;
                }
            }
            if (hovered == null) return;
            // vanilla tooltip: dark purple background with a purple border
            var text = hovered.DisplayName;
            var width = gui.Font.Width(text) + 6;
            var rect = new Rect(mouse.x + 12, mouse.y - 12, width, 14);
            if (rect.xMax > gui.Width) rect.x = mouse.x - 12 - width;
            gui.Fill(rect, new Color32(0x10, 0x00, 0x10, 0xF0));
            gui.Fill(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, 1), new Color32(0x50, 0x00, 0xFF, 0x50));
            gui.Fill(new Rect(rect.x + 1, rect.yMax - 2, rect.width - 2, 1), new Color32(0x28, 0x00, 0x7F, 0x50));
            gui.Text(text, rect.x + 3, rect.y + 3);
        }
    }
}
