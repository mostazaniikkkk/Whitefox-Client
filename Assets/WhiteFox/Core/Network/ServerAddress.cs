// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Network
{
    /// <summary>
    /// A server address as typed ("host", "host:port"), resolved like vanilla's ServerNameResolver: without a port the
    /// SRV record <c>_minecraft._tcp.host</c> gives the real host and port (hosts like Aternos, Minehut or Realms-style
    /// proxies only work through it), otherwise port 25565.
    /// </summary>
    public static class ServerAddress
    {
        public static void Split(string address, out string host, out int port, out bool explicitPort)
        {
            address = address.Trim();
            host = address;
            port = 25565;
            explicitPort = false;
            var colon = address.LastIndexOf(':');
            // "[::1]:25565" and plain IPv6 addresses
            if (address.StartsWith("["))
            {
                var close = address.IndexOf(']');
                if (close > 0)
                {
                    host = address.Substring(1, close - 1);
                    if (close + 1 < address.Length && address[close + 1] == ':' && int.TryParse(address.Substring(close + 2), out var p))
                    {
                        port = p;
                        explicitPort = true;
                    }
                }
                return;
            }
            if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address.Substring(colon + 1), out var parsed))
            {
                host = address.Substring(0, colon);
                port = parsed;
                explicitPort = true;
            }
        }

        /// <summary>The host and port to connect to (and to send in the handshake).</summary>
        public static async Task<(string host, int port)> ResolveAsync(string address)
        {
            Split(address, out var host, out var port, out var explicitPort);
            if (explicitPort || IPAddress.TryParse(host, out _) || host == "localhost") return (host, port);
            try
            {
                var srv = await QuerySrvAsync("_minecraft._tcp." + host).ConfigureAwait(false);
                if (srv.HasValue)
                {
                    Log.Info("SRV " + host + " -> " + srv.Value.target + ":" + srv.Value.port);
                    return (srv.Value.target, srv.Value.port);
                }
            }
            catch (Exception exception)
            {
                Log.Warn("SRV lookup for " + host + " failed: " + exception.Message);
            }
            return (host, port);
        }

        private static IEnumerable<IPAddress> DnsServers()
        {
            var seen = new HashSet<string>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception)
            {
                interfaces = new NetworkInterface[0];
            }
            foreach (var adapter in interfaces)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                IPAddressCollection addresses;
                try
                {
                    addresses = adapter.GetIPProperties().DnsAddresses;
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && seen.Add(address.ToString())) yield return address;
                }
            }
            // public resolvers when the system ones are unknown (phones) or do not answer
            foreach (var fallback in new[] { "1.1.1.1", "8.8.8.8" })
            {
                if (seen.Add(fallback)) yield return IPAddress.Parse(fallback);
            }
        }

        /// <summary>A DNS SRV query over UDP; the record with the lowest priority, or null when there is none.</summary>
        private static async Task<(string target, int port)?> QuerySrvAsync(string name)
        {
            var query = BuildQuery(name, out var id);
            foreach (var server in DnsServers())
            {
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    try
                    {
                        await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53)).ConfigureAwait(false);
                        var receive = udp.ReceiveAsync();
                        if (await Task.WhenAny(receive, Task.Delay(2000)).ConfigureAwait(false) != receive) continue;
                        var answer = Parse(receive.Result.Buffer, id, out var answered);
                        if (answered) return answer;
                    }
                    catch (SocketException)
                    {
                        // try the next server
                    }
                }
            }
            return null;
        }

        private static byte[] BuildQuery(string name, out ushort id)
        {
            id = (ushort)new Random().Next(1, 65535);
            var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var data = Encoding.ASCII.GetBytes(label);
                bytes.Add((byte)data.Length);
                bytes.AddRange(data);
            }
            bytes.Add(0);
            bytes.AddRange(new byte[] { 0, 33, 0, 1 }); // type SRV, class IN
            return bytes.ToArray();
        }

        /// <param name="answered">The server answered the question (with records or "no such name").</param>
        private static (string, int)? Parse(byte[] data, ushort id, out bool answered)
        {
            answered = false;
            if (data.Length < 12 || (data[0] << 8 | data[1]) != id) return null;
            var rcode = data[3] & 0x0F;
            answered = rcode == 0 || rcode == 3;
            if (rcode != 0) return null;
            var questions = data[4] << 8 | data[5];
            var answers = data[6] << 8 | data[7];
            var position = 12;
            for (var i = 0; i < questions; i++)
            {
                ReadName(data, ref position);
                position += 4;
            }
            (string, int)? best = null;
            var bestPriority = int.MaxValue;
            for (var i = 0; i < answers && position + 10 <= data.Length; i++)
            {
                ReadName(data, ref position);
                var type = data[position] << 8 | data[position + 1];
                var length = data[position + 8] << 8 | data[position + 9];
                position += 10;
                var end = position + length;
                if (type == 33 && length >= 7)
                {
                    var priority = data[position] << 8 | data[position + 1];
                    var port = data[position + 4] << 8 | data[position + 5];
                    var target = position + 6;
                    var host = ReadName(data, ref target).TrimEnd('.').ToLowerInvariant();
                    if (host.Length > 0 && priority < bestPriority)
                    {
                        bestPriority = priority;
                        best = (host, port);
                    }
                }
                position = end;
            }
            return best;
        }

        /// <summary>A (possibly compressed) domain name.</summary>
        private static string ReadName(byte[] data, ref int position)
        {
            var name = new StringBuilder();
            var jumped = false;
            var cursor = position;
            for (var guard = 0; guard < 128 && cursor < data.Length; guard++)
            {
                int length = data[cursor];
                if (length == 0)
                {
                    cursor++;
                    break;
                }
                if ((length & 0xC0) == 0xC0)
                {
                    var pointer = (length & 0x3F) << 8 | data[cursor + 1];
                    if (!jumped) position = cursor + 2;
                    jumped = true;
                    cursor = pointer;
                    continue;
                }
                if (name.Length > 0) name.Append('.');
                name.Append(Encoding.ASCII.GetString(data, cursor + 1, length));
                cursor += length + 1;
            }
            if (!jumped) position = cursor;
            return name.ToString();
        }
    }
}
