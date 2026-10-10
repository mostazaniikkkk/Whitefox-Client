// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace WhiteFox.Client
{
    /// <summary>
    /// Encrypts saved account tokens for the current Windows user (DPAPI, like browsers and launchers do), so the
    /// accounts file can not be used on another machine or by another user. Other platforms store them as base64
    /// with a "plain:" marker (their user folder is the protection).
    /// </summary>
    public static class TokenProtection
    {
        private const string DpapiPrefix = "dpapi:";
        private const string PlainPrefix = "plain:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WhiteFox accounts");

        private static bool Windows => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

        public static string Protect(string text)
        {
            var data = Encoding.UTF8.GetBytes(text ?? "");
            if (Windows)
            {
                try
                {
                    return DpapiPrefix + Convert.ToBase64String(Dpapi(data, true));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("WhiteFox: DPAPI is not available (" + exception.Message + "), the tokens are saved unencrypted");
                }
            }
            return PlainPrefix + Convert.ToBase64String(data);
        }

        public static string Unprotect(string stored)
        {
            if (stored.StartsWith(DpapiPrefix)) return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(stored.Substring(DpapiPrefix.Length)), false));
            if (stored.StartsWith(PlainPrefix)) return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(PlainPrefix.Length)));
            throw new FormatException("unknown token format");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static byte[] Dpapi(byte[] data, bool protect)
        {
            var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            var entropy = new Blob { Size = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
            var output = new Blob();
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
                const int uiForbidden = 0x1;
                var ok = protect
                    ? CryptProtectData(ref input, "WhiteFox", ref entropy, IntPtr.Zero, IntPtr.Zero, uiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, uiForbidden, ref output);
                if (!ok) throw new InvalidOperationException("DPAPI error " + Marshal.GetLastWin32Error());
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                Marshal.FreeHGlobal(entropy.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}
