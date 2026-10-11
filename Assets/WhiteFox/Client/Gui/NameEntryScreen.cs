// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// The name of a new split screen player, typed with an on-screen keyboard: the D-pad or left stick moves over the
    /// keys, A types, B deletes, X switches upper / lower case, Start is done. Keys can also be clicked.
    /// </summary>
    public sealed class NameEntryScreen
    {
        private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl_", "zxcvbnm" };
        private static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]{3,16}$");

        // the last row ends with the special keys
        private const string Shift = "Shift", Delete = "Delete", Done = "Done";

        private readonly string otherName;
        private string name;
        private bool upper;
        private int row, column;
        private float nextMove;
        private string error;

        /// <summary>The chosen name once Done was pressed with a valid one.</summary>
        public string Result { get; private set; }

        public NameEntryScreen(string defaultName, string otherName)
        {
            name = defaultName ?? "";
            if (name.Length > 16) name = name.Substring(0, 16);
            this.otherName = otherName;
        }

        private static int RowLength(int index) => Rows[index].Length + (index == Rows.Length - 1 ? 3 : 0);

        private string KeyAt(int keyRow, int keyColumn)
        {
            var keys = Rows[keyRow];
            if (keyColumn < keys.Length)
            {
                var c = keys[keyColumn];
                return (upper ? char.ToUpperInvariant(c) : c).ToString();
            }
            return keyColumn == keys.Length ? Shift : keyColumn == keys.Length + 1 ? Delete : Done;
        }

        /// <summary>Controller navigation and keys (the controller of this player).</summary>
        public void Update(Gamepad pad)
        {
            if (Result != null || pad == null) return;
            var stick = pad.leftStick.ReadValue();
            var dpad = pad.dpad.ReadValue();
            var move = new Vector2(Mathf.Abs(dpad.x) > 0.5f ? Mathf.Sign(dpad.x) : Mathf.Abs(stick.x) > 0.6f ? Mathf.Sign(stick.x) : 0,
                Mathf.Abs(dpad.y) > 0.5f ? Mathf.Sign(dpad.y) : Mathf.Abs(stick.y) > 0.6f ? Mathf.Sign(stick.y) : 0);
            if (move == Vector2.zero)
            {
                nextMove = 0;
            }
            else if (Time.unscaledTime >= nextMove)
            {
                // the first step at once, then repeating while held
                nextMove = Time.unscaledTime + (nextMove == 0 ? 0.35f : 0.1f);
                row = (row - (int)move.y + Rows.Length) % Rows.Length;
                column = (column + (int)move.x + RowLength(row)) % RowLength(row);
            }
            column = Mathf.Min(column, RowLength(row) - 1);
            if (pad.buttonSouth.wasPressedThisFrame) Press(KeyAt(row, column));
            if (pad.buttonEast.wasPressedThisFrame) Press(Delete);
            if (pad.buttonWest.wasPressedThisFrame) Press(Shift);
            if (pad.startButton.wasPressedThisFrame) Press(Done);
        }

        private void Press(string key)
        {
            error = null;
            switch (key)
            {
                case Shift:
                    upper = !upper;
                    break;
                case Delete:
                    if (name.Length > 0) name = name.Substring(0, name.Length - 1);
                    break;
                case Done:
                    if (!ValidName.IsMatch(name)) error = "3 to 16 letters, digits or _";
                    else if (name == otherName) error = "Player 1 already uses that name";
                    else Result = name;
                    break;
                default:
                    if (name.Length < 16) name += key;
                    break;
            }
        }

        public void Draw(GuiRenderer gui)
        {
            MessageScreen.DirtBackground(gui);
            var center = Mathf.Floor(gui.Width / 2);
            var top = Mathf.Floor(gui.Height / 2 - 82);
            gui.TextCentered("Player 2", center, top);
            gui.TextCentered("Choose your name", center, top + 12, GuiRenderer.Gray);

            // the name being typed
            var field = new Rect(center - 80, top + 26, 160, 18);
            gui.Fill(field, new Color(0, 0, 0, 1));
            gui.Fill(new Rect(field.x, field.y, field.width, 1), new Color(0.63f, 0.63f, 0.63f));
            gui.Fill(new Rect(field.x, field.yMax - 1, field.width, 1), new Color(0.63f, 0.63f, 0.63f));
            gui.Fill(new Rect(field.x, field.y, 1, field.height), new Color(0.63f, 0.63f, 0.63f));
            gui.Fill(new Rect(field.xMax - 1, field.y, 1, field.height), new Color(0.63f, 0.63f, 0.63f));
            var cursor = (System.Environment.TickCount / 400) % 2 == 0 ? "_" : "";
            gui.Text(name + cursor, field.x + 4, field.y + 5);

            // the keys: 20 wide, the special keys wider, the selected one outlined
            const float key = 18, gap = 2;
            for (var r = 0; r < Rows.Length; r++)
            {
                var count = RowLength(r);
                float width = 0;
                for (var c = 0; c < count; c++) width += KeyWidth(r, c) + gap;
                var x = center - (width - gap) / 2;
                var y = top + 52 + r * (key + gap);
                for (var c = 0; c < count; c++)
                {
                    var label = KeyAt(r, c);
                    var rect = new Rect(x, y, KeyWidth(r, c), key);
                    if (r == row && c == column) gui.Fill(new Rect(rect.x - 1, rect.y - 1, rect.width + 2, rect.height + 2), Color.white);
                    if (gui.Button(rect, label == Shift ? (upper ? "abc" : "ABC") : label))
                    {
                        row = r;
                        column = c;
                        Press(label);
                    }
                    x += rect.width + gap;
                }
            }

            var help = top + 52 + Rows.Length * (key + gap) + 6;
            gui.TextCentered("A type   B delete   X case   Start done", center, help, GuiRenderer.Gray);
            if (error != null) gui.TextCentered(error, center, help + 12, GuiRenderer.Red);
        }

        private static float KeyWidth(int keyRow, int keyColumn) => keyColumn < Rows[keyRow].Length ? 18 : 40;
    }
}
