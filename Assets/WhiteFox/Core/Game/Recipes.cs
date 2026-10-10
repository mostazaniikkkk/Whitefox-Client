// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;

namespace WhiteFox.Core.Game
{
    /// <summary>A recipe of Update Recipes (crafting, cooking, stonecutting, smithing).</summary>
    public sealed class Recipe
    {
        public string Id;
        /// <summary>Serializer without namespace: crafting_shaped, crafting_shapeless, smelting, stonecutting...</summary>
        public string Type;
        public string Group = "";
        /// <summary>Shaped recipes: grid size; 0 for the others.</summary>
        public int Width, Height;
        /// <summary>Every ingredient is a list of accepted items (empty = an empty slot of a shaped grid).</summary>
        public readonly List<ItemStack[]> Ingredients = new List<ItemStack[]>();
        public ItemStack Result;

        public bool IsCrafting => Type == "crafting_shaped" || Type == "crafting_shapeless";
        public bool IsSmelting => Type == "smelting" || Type == "blasting" || Type == "smoking";
    }

    /// <summary>
    /// The recipes the server sent and the ones the player unlocked (vanilla RecipeManager + ClientRecipeBook):
    /// used by the recipe book, the stonecutter list and Place Recipe.
    /// </summary>
    public sealed class RecipeBook
    {
        public readonly Dictionary<string, Recipe> All = new Dictionary<string, Recipe>();
        public readonly HashSet<string> Known = new HashSet<string>();
        /// <summary>Filter "only craftable" per book (crafting, furnace, blast furnace, smoker).</summary>
        public readonly bool[] Filtering = new bool[4];

        public void SetRecipes(List<Recipe> recipes)
        {
            All.Clear();
            foreach (var recipe in recipes) All[recipe.Id] = recipe;
        }

        public void OnBook(int action, IEnumerable<string> recipes, bool[] settings)
        {
            if (action == 0) Known.Clear();
            foreach (var id in recipes)
            {
                if (action == 2) Known.Remove(id);
                else Known.Add(id);
            }
            if (settings != null)
            {
                for (var i = 0; i < 4; i++) Filtering[i] = settings[i * 2 + 1];
            }
        }

        /// <summary>
        /// Whether the items of a container (inventory + grid) can pay for the recipe: every ingredient needs one
        /// matching item, counted so the same stack is not used twice.
        /// </summary>
        public static bool CanCraft(Recipe recipe, IList<ItemStack> available)
        {
            var counts = new Dictionary<Item, int>();
            foreach (var stack in available)
            {
                if (stack?.Item == null) continue;
                counts.TryGetValue(stack.Item, out var count);
                counts[stack.Item] = count + stack.Count;
            }
            foreach (var ingredient in recipe.Ingredients)
            {
                if (ingredient.Length == 0) continue;
                var found = false;
                foreach (var option in ingredient)
                {
                    if (option?.Item == null || !counts.TryGetValue(option.Item, out var count) || count <= 0) continue;
                    counts[option.Item] = count - 1;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Stonecutter recipes for an input item, in the order of the menu (sorted by the result's translation key).</summary>
        public List<Recipe> StonecuttingFor(Item input, ItemRegistry items, BlockRegistry blocks)
        {
            var result = new List<Recipe>();
            if (input == null) return result;
            foreach (var recipe in All.Values)
            {
                if (recipe.Type != "stonecutting" || recipe.Ingredients.Count == 0) continue;
                if (Array.Exists(recipe.Ingredients[0], option => option?.Item == input)) result.Add(recipe);
            }
            string Key(Recipe recipe)
            {
                var item = recipe.Result?.Item;
                if (item == null) return "";
                var name = item.Name;
                return (blocks.Get(name) != null ? "block." : "item.") + name.Namespace + "." + name.Path;
            }
            result.Sort((a, b) => string.CompareOrdinal(Key(a), Key(b)));
            return result;
        }
    }
}
