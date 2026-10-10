// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WhiteFox.Core.Network
{
    /// <summary>Result of a server list ping.</summary>
    public sealed class ServerStatus
    {
        public string VersionName;
        public int ProtocolVersion;
        public int OnlinePlayers;
        public int MaxPlayers;
        public string Motd;
        public long PingMilliseconds;
        /// <summary>PNG bytes of the server icon, if any.</summary>
        public byte[] Favicon;

        public bool Compatible => ProtocolVersion == Protocol.Version;

        /// <summary>
        /// Server list ping (Minosoft's StatusSession): handshake with next state 1, status request, ping/pong.
        /// </summary>
        public static async Task<ServerStatus> PingAsync(string host, int port, int timeoutMilliseconds = 7000)
        {
            using (var connection = new NetworkConnection())
            {
                await connection.ConnectAsync(host, port, timeoutMilliseconds).ConfigureAwait(false);
                var result = new TaskCompletionSource<ServerStatus>();
                ServerStatus status = null;
                var watch = new Stopwatch();

                connection.Disconnected += reason => result.TrySetException(new Exception(reason));
                connection.PacketReceived += (id, buffer) =>
                {
                    try
                    {
                        if (id == Protocol.StatusS2C.Response && status == null)
                        {
                            status = Parse(JObject.Parse(buffer.ReadString()));
                            watch.Start();
                            connection.Send(Protocol.StatusC2S.Ping, new OutBuffer().WriteLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        }
                        else if (id == Protocol.StatusS2C.Pong && status != null)
                        {
                            status.PingMilliseconds = watch.ElapsedMilliseconds;
                            result.TrySetResult(status);
                        }
                    }
                    catch (Exception exception)
                    {
                        result.TrySetException(exception);
                    }
                };
                connection.StartReading();
                connection.Send(Protocol.Handshake.Intention, new OutBuffer().WriteVarInt(Protocol.Version).WriteString(host).WriteShort(port).WriteVarInt(1));
                connection.Send(Protocol.StatusC2S.Request, new OutBuffer());

                if (await Task.WhenAny(result.Task, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != result.Task) throw new TimeoutException("No status response");
                return await result.Task.ConfigureAwait(false);
            }
        }

        private static ServerStatus Parse(JObject json)
        {
            var status = new ServerStatus
            {
                VersionName = (string)json["version"]?["name"] ?? "?",
                ProtocolVersion = (int?)json["version"]?["protocol"] ?? -1,
                OnlinePlayers = (int?)json["players"]?["online"] ?? 0,
                MaxPlayers = (int?)json["players"]?["max"] ?? 0,
                Motd = TextComponent.FromJson(json["description"]).ToPlain(),
            };
            var favicon = (string)json["favicon"];
            const string prefix = "data:image/png;base64,";
            if (favicon != null && favicon.StartsWith(prefix))
            {
                try
                {
                    status.Favicon = Convert.FromBase64String(favicon.Substring(prefix.Length).Replace("\n", ""));
                }
                catch (FormatException)
                {
                    // ignore invalid icons
                }
            }
            return status;
        }
    }
}
