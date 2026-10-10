// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Client
{
    public static class CommandLine
    {
        /// <summary>Value after <paramref name="name"/> or null.</summary>
        public static string Get(string name)
        {
            var values = GetValues(name, 1);
            return values?[0];
        }

        public static bool Has(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        public static string[] GetValues(string name, int count)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - count; i++)
            {
                if (args[i] != name) continue;
                var result = new string[count];
                Array.Copy(args, i + 1, result, 0, count);
                return result;
            }
            return null;
        }
    }
}
