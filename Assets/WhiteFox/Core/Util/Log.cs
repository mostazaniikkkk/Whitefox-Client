// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WhiteFox.Core.Util
{
    public enum LogLevel
    {
        Verbose,
        Info,
        Warn,
        Error,
    }

    /// <summary>Engine independent logger; the Unity layer redirects it to Debug.Log.</summary>
    public static class Log
    {
        public static Action<LogLevel, string> Sink = (level, message) => Console.WriteLine("[" + level + "] " + message);
        public static LogLevel MinLevel = LogLevel.Info;

        public static void Write(LogLevel level, string message)
        {
            if (level < MinLevel) return;
            Sink?.Invoke(level, message);
        }

        public static void Verbose(string message) => Write(LogLevel.Verbose, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warn(string message) => Write(LogLevel.Warn, message);
        public static void Error(string message) => Write(LogLevel.Error, message);
    }

    public static class Hashing
    {
        public static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        public static string Sha1(Stream stream)
        {
            using (var sha = SHA1.Create())
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        public static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }
    }
}
