// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// The remaining vanilla menus (stonecutter, loom, cartography table, beacon, crafter, lectern) and the recipe
    /// book of the inventory, the crafting table and the furnaces (vanilla RecipeBookComponent, sends Place Recipe).
    /// </summary>
    public sealed partial class InventoryScreen
    {
        private int stonecutterScroll;
        private int loomScroll;
        private int beaconPrimary = -1, beaconSecondary = -1, beaconWindow = -1;

        // recipe book
        private bool recipeBookOpen;
        private int recipePage;
        private readonly GuiTextField recipeSearch = new GuiTextField { MaxLength = 50 };
        private readonly List<Recipe> recipeList = new List<Recipe>();

        private bool Pressed(Rect rect)
        {
            var current = Event.current;
            if (!rect.Contains(gui.Mouse) || current.type != EventType.MouseDown || current.button != 0) return false;
            current.Use();
            return true;
        }

        // ------------------------------------------------------------------ stonecutter

        /// <summary>Stonecutter: the recipes of the input item (4 x 3 visible), the click selects one (Click Container Button).</summary>
        private Rect DrawStonecutter(GameSession session, ContainerWindow window)
        {
            var panel = Standard("stonecutter", window);
            var input = window.Slots.Length > 0 ? window.Slots[0] : null;
            var recipes = session.Recipes.StonecuttingFor(input?.Item, session.Resources.Items, session.Resources.Blocks);
            var selected = window.Properties[0];
            var rows = (recipes.Count + 3) / 4;
            if (Event.current.type == EventType.ScrollWheel && new Rect(panel.x + 52, panel.y + 15, 80, 54).Contains(gui.Mouse))
            {
                stonecutterScroll = Mathf.Clamp(stonecutterScroll + (Event.current.delta.y > 0 ? 1 : -1), 0, Mathf.Max(0, rows - 3));
                Event.current.Use();
            }
            stonecutterScroll = Mathf.Clamp(stonecutterScroll, 0, Mathf.Max(0, rows - 3));
            Part(rows > 3 ? "container/stonecutter/scroller" : "container/stonecutter/scroller_disabled", panel, 119, 15 + (rows > 3 ? 41f * stonecutterScroll / (rows - 3) : 0), 12, 15);
            for (var i = stonecutterScroll * 4; i < recipes.Count && i < stonecutterScroll * 4 + 12; i++)
            {
                var index = i - stonecutterScroll * 4;
                var rect = new Rect(panel.x + 52 + index % 4 * 16, panel.y + 15 + index / 4 * 18, 16, 18);
                var hovered = rect.Contains(gui.Mouse);
                gui.Sprite(i == selected ? "container/stonecutter/recipe_selected" : hovered ? "container/stonecutter/recipe_highlighted" : "container/stonecutter/recipe", rect);
                var result = recipes[i].Result;
                if (result != null) DrawItem(result.Item, result.Count, new Rect(rect.x, rect.y + 1, 16, 16));
                if (Pressed(rect)) session.Server?.SendContainerButton(window.Id, i);
            }
            Slot(window, 0, panel, 20, 33);
            Slot(window, 1, panel, 143, 33);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        // ------------------------------------------------------------------ loom

        /// <summary>Banner pattern registry order (1.20.4); patterns without an item are selectable with any banner.</summary>
        private static readonly string[] LoomPatterns =
        {
            "square_bottom_left", "square_bottom_right", "square_top_left", "square_top_right", "stripe_bottom", "stripe_top", "stripe_left",
            "stripe_right", "stripe_center", "stripe_middle", "stripe_downright", "stripe_downleft", "small_stripes", "cross", "straight_cross",
            "triangle_bottom", "triangle_top", "triangles_bottom", "triangles_top", "diagonal_left", "diagonal_up_right", "diagonal_up_left",
            "diagonal_right", "circle", "rhombus", "half_vertical", "half_horizontal", "half_vertical_right", "half_horizontal_bottom", "border",
            "curly_border", "gradient", "gradient_up", "bricks",
        };

        /// <summary>Loom: banner, dye and optional pattern item; the pattern grid sends Click Container Button.</summary>
        private Rect DrawLoom(GameSession session, ContainerWindow window)
        {
            var panel = Standard("loom", window);
            var banner = window.Slots.Length > 0 ? window.Slots[0] : null;
            var dye = window.Slots.Length > 1 ? window.Slots[1] : null;
            var patternItem = window.Slots.Length > 2 ? window.Slots[2] : null;
            string[] patterns = LoomPatterns;
            var special = patternItem?.Item?.Name.Path;
            if (special != null && special.EndsWith("_banner_pattern"))
            {
                var name = special.Substring(0, special.Length - "_banner_pattern".Length);
                patterns = new[] { name == "flower" ? "flower" : name == "globe" ? "globe" : name == "piglin" ? "piglin" : name == "mojang" ? "mojang" : name == "skull" ? "skull" : name == "creeper" ? "creeper" : name };
            }
            if (banner != null && dye != null)
            {
                var rows = (patterns.Length + 3) / 4;
                if (Event.current.type == EventType.ScrollWheel && new Rect(panel.x + 60, panel.y + 13, 56, 56).Contains(gui.Mouse))
                {
                    loomScroll = Mathf.Clamp(loomScroll + (Event.current.delta.y > 0 ? 1 : -1), 0, Mathf.Max(0, rows - 4));
                    Event.current.Use();
                }
                for (var i = loomScroll * 4; i < patterns.Length && i < loomScroll * 4 + 16; i++)
                {
                    var index = i - loomScroll * 4;
                    var rect = new Rect(panel.x + 60 + index % 4 * 14, panel.y + 13 + index / 4 * 14, 14, 14);
                    var hovered = rect.Contains(gui.Mouse);
                    gui.Sprite(i == window.Properties[0] ? "container/loom/pattern_selected" : hovered ? "container/loom/pattern_highlighted" : "container/loom/pattern", rect);
                    // the pattern's front area of the banner texture (20 x 40 pixels at 1, 1)
                    var texture = gui.Assets.Get("assets/minecraft/textures/entity/banner/" + patterns[i] + ".png");
                    if (texture != null && gui.Repaint) GUI.DrawTextureWithTexCoords(new Rect(rect.x + 4, rect.y + 2, 5, 10), texture.Texture, new Rect(1 / 64f, 1 - 41 / 64f, 20 / 64f, 40 / 64f));
                    if (Pressed(rect)) session.Server?.SendContainerButton(window.Id, i);
                }
            }
            Slot(window, 0, panel, 13, 26);
            Slot(window, 1, panel, 33, 26);
            Slot(window, 2, panel, 23, 45);
            Slot(window, 3, panel, 143, 57);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        private Rect DrawCartography(ContainerWindow window)
        {
            var panel = Standard("cartography_table", window);
            Slot(window, 0, panel, 15, 15);
            Slot(window, 1, panel, 15, 52);
            Slot(window, 2, panel, 145, 39);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        // ------------------------------------------------------------------ beacon

        private static readonly int[][] BeaconEffects = { new[] { 0, 2 }, new[] { 10, 7 }, new[] { 4 }, new[] { 9 } };

        /// <summary>Beacon: powers by pyramid level (property 0), confirm sends Set Beacon Effect and pays with the slot.</summary>
        private Rect DrawBeacon(GameSession session, ContainerWindow window)
        {
            var panel = Centered(230, 219);
            Region(Container + "beacon.png", new Rect(0, 0, 230, 219), panel);
            var levels = window.Properties[0];
            if (beaconWindow != window.Id)
            {
                beaconWindow = window.Id;
                beaconPrimary = window.Properties[1] - 1;
                beaconSecondary = window.Properties[2] - 1;
            }
            gui.TextCentered("Primary Power", panel.x + 62, panel.y + 10, new Color32(0xE0, 0xE0, 0xE0, 0xFF), false);
            gui.TextCentered("Secondary Power", panel.x + 169, panel.y + 10, new Color32(0xE0, 0xE0, 0xE0, 0xFF), false);
            for (var tier = 0; tier < 4; tier++)
            {
                var effects = BeaconEffects[tier];
                var secondary = tier == 3;
                var options = new List<int>(effects);
                if (secondary && beaconPrimary >= 0) options.Add(beaconPrimary); // level 4: upgrade the primary power
                for (var j = 0; j < options.Count; j++)
                {
                    var width = options.Count * 22 + (options.Count - 1) * 2;
                    var x = (secondary ? 167 : 76) + j * 24 - width / 2f;
                    var y = secondary ? 47 : 22 + tier * 25;
                    var rect = new Rect(panel.x + x, panel.y + y, 22, 22);
                    var unlocked = levels > tier;
                    var effect = options[j];
                    var chosen = secondary ? beaconSecondary == effect : beaconPrimary == effect;
                    var hovered = rect.Contains(gui.Mouse);
                    gui.Sprite(!unlocked ? "container/beacon/button_disabled" : chosen ? "container/beacon/button_selected" : hovered ? "container/beacon/button_highlighted" : "container/beacon/button", rect);
                    var name = effect >= 0 && effect < Core.Physics.Effects.Names.Length ? Core.Physics.Effects.Names[effect] : null;
                    var icon = name != null ? gui.Assets.Get("assets/minecraft/textures/mob_effect/" + name + ".png") : null;
                    if (icon != null) gui.Sprite(icon, new Rect(rect.x + 2, rect.y + 2, 18, 18), unlocked ? Color.white : new Color(1, 1, 1, 0.4f));
                    if (unlocked && Pressed(rect))
                    {
                        if (secondary) beaconSecondary = effect;
                        else beaconPrimary = effect;
                    }
                }
            }
            var payment = window.Slots.Length > 0 && window.Slots[0] != null;
            var confirm = new Rect(panel.x + 164, panel.y + 107, 22, 22);
            gui.Sprite(payment && beaconPrimary >= 0 ? (confirm.Contains(gui.Mouse) ? "container/beacon/button_highlighted" : "container/beacon/button") : "container/beacon/button_disabled", confirm);
            gui.Sprite("container/beacon/confirm", new Rect(confirm.x + 2, confirm.y + 2, 18, 18));
            if (payment && beaconPrimary >= 0 && Pressed(confirm))
            {
                session.Server?.SendBeaconEffect(beaconPrimary, beaconSecondary);
                session.Inventory.Close();
            }
            var cancel = new Rect(panel.x + 190, panel.y + 107, 22, 22);
            gui.Sprite(cancel.Contains(gui.Mouse) ? "container/beacon/button_highlighted" : "container/beacon/button", cancel);
            gui.Sprite("container/beacon/cancel", new Rect(cancel.x + 2, cancel.y + 2, 18, 18));
            if (Pressed(cancel)) session.Inventory.Close();
            Slot(window, 0, panel, 136, 110);
            PlayerSlots(window, panel, 137, 36);
            return panel;
        }

        // ------------------------------------------------------------------ crafter

        /// <summary>Crafter: 3 x 3 grid where empty slots can be switched off (Change Container Slot State).</summary>
        private Rect DrawCrafter(GameSession session, ContainerWindow window)
        {
            var panel = Standard("crafter", window, 166, true);
            for (var i = 0; i < 9; i++)
            {
                var x = 26 + i % 3 * 18;
                var y = 17 + i / 3 * 18;
                var disabled = window.Properties[i] == 1;
                if (disabled) Part("container/crafter/disabled_slot", panel, x - 1, y - 1, 18, 18);
                var empty = i >= window.Slots.Length || window.Slots[i] == null;
                var rect = new Rect(panel.x + x, panel.y + y, 16, 16);
                if (empty && session.Inventory.Carried == null && Pressed(rect))
                {
                    window.Properties[i] = disabled ? 0 : 1;
                    session.Server?.SendSlotState(i, window.Id, disabled);
                    continue;
                }
                if (!disabled) Slot(window, i, panel, x, y);
            }
            Part(window.Properties[9] == 1 ? "container/crafter/powered_redstone" : "container/crafter/unpowered_redstone", panel, 97, 38, 16, 16);
            Slot(window, 45, panel, 134, 35);
            PlayerSlots(window, panel, 84);
            return panel;
        }

        // ------------------------------------------------------------------ lectern

        /// <summary>Lectern: the book of slot 0, page from property 0; buttons 1/2 turn pages, 3 takes the book.</summary>
        private Rect DrawLectern(GameSession session, ContainerWindow window)
        {
            var x = Mathf.Floor((gui.Width - 192) / 2);
            const float y = 2;
            var book = gui.Assets.Get("assets/minecraft/textures/gui/book.png");
            if (book != null && gui.Repaint) GUI.DrawTextureWithTexCoords(new Rect(x, y, 192, 192), book.Texture, new Rect(0, 1 - 192f / book.Height, 192f / book.Width, 192f / book.Height));
            var pages = new List<string>();
            var stack = window.Slots.Length > 0 ? window.Slots[0] : null;
            if (Nbt.Get(stack?.Nbt, "pages") is List<object> list)
            {
                foreach (var entry in list)
                {
                    var text = entry as string ?? "";
                    pages.Add(stack.Item.Name.Path == "written_book" ? TextComponent.FromJson(text).ToLegacy() : text);
                }
            }
            if (pages.Count == 0) pages.Add("");
            var page = Mathf.Clamp(window.Properties[0], 0, pages.Count - 1);
            gui.TextRight("Page " + (page + 1) + " of " + pages.Count, x + 192 - 44, y + 18, Color.black, false);
            var lines = gui.Wrap(pages[page], 114);
            for (var i = 0; i < lines.Length && i < 14; i++) gui.Text(lines[i], x + 36, y + 32 + i * 9, Color.black, false);
            if (page < pages.Count - 1 && Pressed(new Rect(x + 116, y + 159, 23, 13))) session.Server?.SendContainerButton(window.Id, 2);
            if (page > 0 && Pressed(new Rect(x + 43, y + 159, 23, 13))) session.Server?.SendContainerButton(window.Id, 1);
            if (page < pages.Count - 1) gui.Sprite("widget/page_forward", new Rect(x + 116, y + 159, 23, 13));
            if (page > 0) gui.Sprite("widget/page_backward", new Rect(x + 43, y + 159, 23, 13));
            if (gui.Button(new Rect(gui.Width / 2 - 100, 196, 98, 20), "Done")) session.Inventory.Close();
            if (gui.Button(new Rect(gui.Width / 2 + 2, 196, 98, 20), "Take Book")) session.Server?.SendContainerButton(window.Id, 3);
            return new Rect(x, y, 192, 216);
        }

        // ------------------------------------------------------------------ recipe book

        /// <summary>
        /// Recipe book panel to the left of a crafting screen: unlocked recipes of the kind (crafting that fits the grid,
        /// or one cooking type), craftable first (green), search and pages; a click places the recipe (shift: all).
        /// </summary>
        private void DrawRecipeBook(GameSession session, ContainerWindow window, Rect panel, Func<Recipe, bool> filter, Vector2 toggle)
        {
            var button = new Rect(panel.x + toggle.x, panel.y + toggle.y, 20, 18);
            gui.Sprite(button.Contains(gui.Mouse) ? "recipe_book/button_highlighted" : "recipe_book/button", button);
            if (Pressed(button)) recipeBookOpen = !recipeBookOpen;
            if (!recipeBookOpen) return;

            var book = new Rect(panel.x - 147 - 2, panel.y, 147, 166);
            bookRect = book;
            Region("assets/minecraft/textures/gui/recipe_book.png", new Rect(1, 1, 147, 166), book);
            recipeSearch.Draw(gui, new Rect(book.x + 25, book.y + 14, 81, 11), false);
            if (string.IsNullOrEmpty(recipeSearch.Text) && !recipeSearch.Focused) gui.Text("Search...", book.x + 26, book.y + 15, GuiRenderer.Gray, false);

            var available = new List<ItemStack>(session.Inventory.Player.Slots);
            for (var i = 0; i < window.ContainerSlots && i < window.Slots.Length; i++) available.Add(window.Slots[i]);
            recipeList.Clear();
            var search = recipeSearch.Text.Trim().ToLowerInvariant();
            foreach (var recipe in session.Recipes.All.Values)
            {
                if (!session.Recipes.Known.Contains(recipe.Id) || recipe.Result?.Item == null || !filter(recipe)) continue;
                if (search.Length > 0 && !recipe.Result.Item.DisplayName.ToLowerInvariant().Contains(search) && !recipe.Result.Item.Name.Path.Contains(search)) continue;
                recipeList.Add(recipe);
            }
            var craftable = new HashSet<Recipe>();
            foreach (var recipe in recipeList)
            {
                if (RecipeBook.CanCraft(recipe, available)) craftable.Add(recipe);
            }
            recipeList.Sort((a, b) =>
            {
                var order = craftable.Contains(b).CompareTo(craftable.Contains(a));
                return order != 0 ? order : string.CompareOrdinal(a.Result.Item.Name.Path, b.Result.Item.Name.Path);
            });

            const int perPage = 20;
            var pages = Mathf.Max(1, (recipeList.Count + perPage - 1) / perPage);
            recipePage = Mathf.Clamp(recipePage, 0, pages - 1);
            Recipe hoveredRecipe = null;
            for (var i = recipePage * perPage; i < recipeList.Count && i < (recipePage + 1) * perPage; i++)
            {
                var index = i - recipePage * perPage;
                var rect = new Rect(book.x + 11 + index % 5 * 25, book.y + 31 + index / 5 * 25, 25, 25);
                var recipe = recipeList[i];
                gui.Sprite(craftable.Contains(recipe) ? "recipe_book/slot_craftable" : "recipe_book/slot_uncraftable", rect);
                DrawItem(recipe.Result.Item, recipe.Result.Count, new Rect(rect.x + 4, rect.y + 4, 16, 16));
                if (rect.Contains(gui.Mouse)) hoveredRecipe = recipe;
                var current = Event.current;
                if (rect.Contains(gui.Mouse) && current.type == EventType.MouseDown && current.button == 0)
                {
                    current.Use();
                    session.Server?.SendPlaceRecipe(window.Id, recipe.Id, current.shift);
                }
            }
            if (pages > 1)
            {
                gui.TextCentered((recipePage + 1) + "/" + pages, book.x + 73, book.y + 141, Color.white, false);
                var back = new Rect(book.x + 38, book.y + 137, 12, 17);
                var forward = new Rect(book.x + 93, book.y + 137, 12, 17);
                if (recipePage > 0)
                {
                    gui.Sprite(back.Contains(gui.Mouse) ? "recipe_book/page_backward_highlighted" : "recipe_book/page_backward", back);
                    if (Pressed(back)) recipePage--;
                }
                if (recipePage < pages - 1)
                {
                    gui.Sprite(forward.Contains(gui.Mouse) ? "recipe_book/page_forward_highlighted" : "recipe_book/page_forward", forward);
                    if (Pressed(forward)) recipePage++;
                }
            }
            if (hoveredRecipe != null) recipeTooltip = hoveredRecipe.Result.Item.DisplayName;
        }

        private string recipeTooltip;
        private Rect bookRect;

        /// <summary>Recipes that fit a crafting grid of the given size.</summary>
        private static bool FitsGrid(Recipe recipe, int size)
        {
            if (recipe.Type == "crafting_shaped") return recipe.Width <= size && recipe.Height <= size;
            if (recipe.Type == "crafting_shapeless") return recipe.Ingredients.Count <= size * size;
            return false;
        }

        private static string CookingType(int menu) => menu == 10 ? "blasting" : menu == 22 ? "smoking" : "smelting";
    }
}
