// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Network;

namespace WhiteFox.Core.Game
{
    /// <summary>
    /// A container window (Minosoft's Container): window 0 is the player inventory (46 slots: 0 crafting result,
    /// 1-4 crafting grid, 5-8 armor, 9-35 main, 36-44 hotbar, 45 offhand). Other windows are opened by the server and
    /// end with the 36 player slots (27 main + 9 hotbar).
    /// </summary>
    /// <summary>A villager trade (Merchant Offers).</summary>
    public sealed class TradeOffer
    {
        public ItemStack CostA;
        public ItemStack Result;
        public ItemStack CostB;
        public bool OutOfStock;
        public int Uses;
        public int MaxUses;
    }

    public sealed class ContainerWindow
    {
        public const int PlayerSlotCount = 46;

        public int Id;
        /// <summary>Menu type id (minecraft:menu registry), -1 for the player inventory.</summary>
        public int Type = -1;
        public string Title = "";
        public ItemStack[] Slots;
        public int StateId;
        /// <summary>Set Container Property values (furnace progress, enchantment costs, anvil cost, brewing time...).</summary>
        public readonly int[] Properties = new int[16];
        /// <summary>Villager trades (Merchant Offers), or null.</summary>
        public List<TradeOffer> Offers;
        public int SelectedTrade;

        public bool IsPlayer => Id == 0;

        /// <summary>Slots before the player inventory part.</summary>
        public int ContainerSlots => IsPlayer ? PlayerSlotCount : System.Math.Max(0, Slots.Length - 36);

        /// <summary>Player inventory (window 0) slot of a slot of this window, or -1 if it is a container slot.</summary>
        public int ToPlayerSlot(int slot)
        {
            if (IsPlayer) return slot;
            var index = slot - ContainerSlots;
            if (index < 0) return -1;
            return index < 27 ? 9 + index : 36 + (index - 27);
        }

        /// <summary>Rows of a generic chest (generic_9x1 .. generic_9x6, shulker box), 0 otherwise.</summary>
        public int ChestRows => Type >= 0 && Type <= 5 ? Type + 1 : Type == 20 ? 3 : 0;
    }

    /// <summary>
    /// Inventory and container handling (Minosoft's ContainerManager + InventoryActions): keeps the windows in sync
    /// with the server, predicts clicks like vanilla and sends Click Container / Set Creative Mode Slot packets.
    /// </summary>
    public sealed class Inventory
    {
        private readonly GameSession game;

        public readonly ContainerWindow Player = new ContainerWindow { Id = 0, Slots = new ItemStack[ContainerWindow.PlayerSlotCount], Title = "Inventory" };
        /// <summary>Window opened by the server (chest, furnace, ...), or null.</summary>
        public ContainerWindow Open;
        /// <summary>Stack held by the mouse cursor.</summary>
        public ItemStack Carried;
        /// <summary>The client opened its own inventory (E).</summary>
        public bool PlayerScreenOpen;

        public ContainerWindow Active => Open ?? Player;
        public bool AnyOpen => Open != null || PlayerScreenOpen;

        public Inventory(GameSession game)
        {
            this.game = game;
        }

        private ServerSession Server => game.Server;

        // ------------------------------------------------------------------ server updates

        public void OnContent(int windowId, int stateId, ItemStack[] slots, ItemStack carried)
        {
            var window = windowId == 0 ? Player : Open != null && Open.Id == windowId ? Open : null;
            if (window == null) return;
            window.StateId = stateId;
            if (window.IsPlayer)
            {
                for (var i = 0; i < Player.Slots.Length; i++) Player.Slots[i] = i < slots.Length ? slots[i] : null;
            }
            else
            {
                window.Slots = slots;
                for (var i = window.ContainerSlots; i < slots.Length; i++) Player.Slots[window.ToPlayerSlot(i)] = slots[i];
            }
            Carried = carried;
            game.UpdateHotbar();
        }

        public void OnSlot(int windowId, int stateId, int slot, ItemStack stack)
        {
            if (windowId == -1 && slot == -1)
            {
                Carried = stack;
                return;
            }
            if (windowId == -2)
            {
                // player inventory index: 0-8 hotbar, 9-35 main, 36-39 armor (boots first), 40 offhand
                var playerSlot = slot < 9 ? 36 + slot : slot < 36 ? slot : slot < 40 ? 8 - (slot - 36) : slot == 40 ? 45 : -1;
                if (playerSlot >= 0) SetPlayerSlot(playerSlot, stack);
                return;
            }
            var window = windowId == 0 ? Player : Open != null && Open.Id == windowId ? Open : null;
            if (window == null || slot < 0 || slot >= window.Slots.Length) return;
            window.StateId = stateId;
            window.Slots[slot] = stack;
            if (!window.IsPlayer && window.ToPlayerSlot(slot) >= 0) Player.Slots[window.ToPlayerSlot(slot)] = stack;
            game.UpdateHotbar();
        }

        public void OnOpenScreen(int windowId, int type, string title)
        {
            Open = new ContainerWindow { Id = windowId, Type = type, Title = title, Slots = new ItemStack[0] };
            PlayerScreenOpen = false;
        }

        public void OnProperty(int windowId, int property, int value)
        {
            if (Open == null || Open.Id != windowId || property < 0 || property >= Open.Properties.Length) return;
            Open.Properties[property] = value;
        }

        public void OnOffers(int windowId, List<TradeOffer> offers)
        {
            if (Open == null || Open.Id != windowId) return;
            Open.Offers = offers;
        }

        public void OnCloseScreen(int windowId)
        {
            if (Open != null && Open.Id == windowId) Open = null;
            Carried = null;
        }

        private void SetPlayerSlot(int slot, ItemStack stack)
        {
            Player.Slots[slot] = stack;
            if (Open != null)
            {
                for (var i = Open.ContainerSlots; i < Open.Slots.Length; i++)
                {
                    if (Open.ToPlayerSlot(i) == slot) Open.Slots[i] = stack;
                }
            }
            game.UpdateHotbar();
        }

        // ------------------------------------------------------------------ client actions

        public void OpenPlayerScreen()
        {
            if (Open == null) PlayerScreenOpen = true;
        }

        /// <summary>Closes whatever is open (Close Container); in creative the carried stack is discarded.</summary>
        public void Close()
        {
            var id = Open?.Id ?? 0;
            Open = null;
            PlayerScreenOpen = false;
            if (Server != null) Server.SendCloseContainer(id);
            Carried = null;
        }

        /// <summary>
        /// Left (button 0) or right (button 1) click on a slot of the active window, or shift click; slot -999 is
        /// outside the window (drops). Survival: predicted locally and sent as Click Container.
        /// </summary>
        public void Click(int slot, int button, bool shift)
        {
            var window = Active;
            if (shift && slot >= 0)
            {
                // shift click: let the server move the stack, it sends the result
                Server?.SendClickContainer(window.Id, window.StateId, slot, button, 1, new Dictionary<int, ItemStack>(), Carried);
                return;
            }

            var changed = new Dictionary<int, ItemStack>();
            if (slot == -999)
            {
                if (Carried == null) return;
                if (button == 0) Carried = null;
                else Carried = Carried.Count > 1 ? Copy(Carried, Carried.Count - 1) : null;
            }
            else
            {
                if (slot < 0 || slot >= window.Slots.Length) return;
                var current = window.Slots[slot];
                if (window.IsPlayer && slot == 0)
                {
                    // crafting result: only taking is possible, the server calculates it
                    Server?.SendClickContainer(window.Id, window.StateId, slot, button, 0, changed, Carried);
                    return;
                }
                var result = ClickSlot(current, button);
                window.Slots[slot] = result;
                changed[slot] = result;
                if (!window.IsPlayer && window.ToPlayerSlot(slot) >= 0) Player.Slots[window.ToPlayerSlot(slot)] = result;
            }

            if (Server != null) Server.SendClickContainer(window.Id, window.StateId, slot, button, 0, changed, Carried);
            game.UpdateHotbar();
        }

        /// <summary>Vanilla pickup click: returns the new slot content and updates the carried stack.</summary>
        private ItemStack ClickSlot(ItemStack slot, int button)
        {
            if (Carried == null)
            {
                if (slot == null) return null;
                if (button == 0)
                {
                    Carried = slot;
                    return null;
                }
                var take = (slot.Count + 1) / 2;
                Carried = Copy(slot, take);
                return slot.Count - take > 0 ? Copy(slot, slot.Count - take) : null;
            }

            if (slot == null)
            {
                if (button == 0)
                {
                    var placed = Carried;
                    Carried = null;
                    return placed;
                }
                var one = Copy(Carried, 1);
                Carried = Carried.Count > 1 ? Copy(Carried, Carried.Count - 1) : null;
                return one;
            }

            if (SameItem(slot, Carried))
            {
                var max = slot.Item.StackSize;
                var move = System.Math.Min(button == 0 ? Carried.Count : 1, max - slot.Count);
                if (move <= 0) return slot;
                var merged = Copy(slot, slot.Count + move);
                Carried = Carried.Count - move > 0 ? Copy(Carried, Carried.Count - move) : null;
                return merged;
            }

            // different items: swap
            var swapped = Carried;
            Carried = slot;
            return swapped;
        }

        /// <summary>
        /// Creative inventory: the client decides the slot content and tells the server with Set Creative Mode Slot
        /// (player inventory slot numbers). Works locally too.
        /// </summary>
        public void CreativeClick(int playerSlot, int button)
        {
            if (playerSlot < 5 || playerSlot >= Player.Slots.Length) return; // no crafting slots in creative
            var result = ClickSlot(Player.Slots[playerSlot], button);
            Player.Slots[playerSlot] = result;
            Server?.SendCreativeSlot(playerSlot, result);
            game.UpdateHotbar();
        }

        /// <summary>Takes an item from the creative catalog into the cursor (left: full stack, right: one).</summary>
        public void CreativePick(Item item, int button)
        {
            if (Carried != null && Carried.Item == item && button == 1)
            {
                if (Carried.Count < item.StackSize) Carried = Copy(Carried, Carried.Count + 1);
                return;
            }
            Carried = Carried != null ? null : new ItemStack { Item = item, Count = button == 0 ? item.StackSize : 1 };
        }

        public static bool SameItem(ItemStack a, ItemStack b) => a != null && b != null && a.Item == b.Item && Equals(a.Nbt, b.Nbt);

        private static ItemStack Copy(ItemStack stack, int count) => new ItemStack { Item = stack.Item, Count = count, Nbt = stack.Nbt };
    }
}
