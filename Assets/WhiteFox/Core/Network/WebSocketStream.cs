// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WhiteFox.Core.Network
{
    /// <summary>
    /// A WebSocket client (RFC 6455) as the byte stream of a connection: BambiettaServer's transport
    /// (docs/WEBSOCKET.md), where the binary messages carry the same bytes as the TCP stream, so the Minecraft framing
    /// reads across message boundaries. Each write is sent as one binary message. wss:// runs the WebSocket over TLS;
    /// written on top of TcpClient and SslStream (instead of ClientWebSocket) to decide about the certificate ourselves.
    /// </summary>
    public sealed class WebSocketStream : Stream
    {
        private readonly TcpClient client;
        private readonly Stream stream;
        private readonly object writeLock = new object();
        private readonly byte[] header = new byte[14];
        private readonly RandomNumberGenerator random = RandomNumberGenerator.Create();
        // the payload of the binary message being read
        private long remaining;
        private bool masked;
        private readonly byte[] mask = new byte[4];
        private long maskOffset;
        private volatile bool closed;

        private WebSocketStream(TcpClient client, Stream stream)
        {
            this.client = client;
            this.stream = stream;
        }

        public bool Open => !closed && client.Connected;

        /// <summary>Connects, negotiates TLS for wss:// and upgrades to a WebSocket (subprotocol "minecraft").</summary>
        public static async Task<WebSocketStream> ConnectAsync(Uri uri, int timeoutMilliseconds)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                var connect = client.ConnectAsync(uri.Host, uri.Port);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != connect) throw new TimeoutException("Connection to " + uri + " timed out");
                await connect.ConfigureAwait(false);
                Stream stream = client.GetStream();
                if (uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
                {
                    var tls = new SslStream(stream, false, (sender, certificate, chain, errors) => AcceptCertificate(uri.Host, errors));
                    try
                    {
                        await tls.AuthenticateAsClientAsync(uri.Host).ConfigureAwait(false);
                    }
                    catch (System.Security.Authentication.AuthenticationException exception)
                    {
                        throw new IOException("TLS failed (the server's certificate is not trusted?): " + exception.Message, exception);
                    }
                    stream = tls;
                }
                await UpgradeAsync(stream, uri).ConfigureAwait(false);
                return new WebSocketStream(client, stream);
            }
            catch (Exception)
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Valid certificates, and the self-signed certificate of a server on this computer or the local network
        /// (BambiettaServer's "tls-self-signed" for development); servers on the internet need a valid one.
        /// </summary>
        private static bool AcceptCertificate(string host, SslPolicyErrors errors) => errors == SslPolicyErrors.None || IsLocalHost(host);

        /// <summary>localhost and the private address ranges (10/8, 172.16/12, 192.168/16, loopback, link local).</summary>
        public static bool IsLocalHost(string host)
        {
            if (host == "localhost" || host.EndsWith(".local")) return true;
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }

        /// <summary>The HTTP upgrade: GET with a random key, the server answers 101 with the matching accept hash.</summary>
        private static async Task UpgradeAsync(Stream stream, Uri uri)
        {
            var keyBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keyBytes);
            var key = Convert.ToBase64String(keyBytes);
            var hostHeader = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
            var request = "GET " + (string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery) + " HTTP/1.1\r\n" +
                          "Host: " + hostHeader + "\r\n" +
                          "Upgrade: websocket\r\n" +
                          "Connection: Upgrade\r\n" +
                          "Sec-WebSocket-Key: " + key + "\r\n" +
                          "Sec-WebSocket-Version: 13\r\n" +
                          "Sec-WebSocket-Protocol: minecraft\r\n" +
                          "User-Agent: WhiteFox\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            // the response head, byte by byte up to the blank line (the frames may follow right after it)
            var response = new StringBuilder();
            var one = new byte[1];
            while (!response.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) throw new IOException("The server closed the connection during the WebSocket upgrade");
                response.Append((char)one[0]);
                if (response.Length > 16384) throw new IOException("Invalid WebSocket upgrade response");
            }
            var lines = response.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].Contains(" 101")) throw new IOException("The server refused the WebSocket upgrade: " + (lines.Length > 0 ? lines[0] : "no answer"));
            string expected;
            using (var sha1 = SHA1.Create()) expected = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            foreach (var line in lines)
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase) && line.Substring(colon + 1).Trim() != expected)
                {
                    throw new IOException("Invalid WebSocket accept key");
                }
            }
        }

        // ------------------------------------------------------------------ frames

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (remaining == 0)
            {
                if (!ReadFrameHeader(out var opcode, out var length)) return 0;
                if (opcode == 0x8)
                {
                    // close: answer it and end the stream
                    Skip(length);
                    SendFrame(0x8, new byte[0], 0, 0);
                    closed = true;
                    return 0;
                }
                if (opcode == 0x9)
                {
                    // ping: pong with the same payload
                    var payload = ReadPayload(length);
                    SendFrame(0xA, payload, 0, payload.Length);
                    continue;
                }
                if (opcode == 0xA || opcode == 0x1)
                {
                    Skip(length); // pongs and text messages are ignored
                    continue;
                }
                remaining = length; // binary (0x2) or a continuation (0x0)
            }
            var read = stream.Read(buffer, offset, (int)Math.Min(count, remaining));
            if (read <= 0) return 0;
            if (masked)
            {
                for (var i = 0; i < read; i++) buffer[offset + i] ^= mask[(maskOffset + i) & 3];
                maskOffset += read;
            }
            remaining -= read;
            return read;
        }

        private bool ReadFrameHeader(out int opcode, out long length)
        {
            opcode = 0;
            length = 0;
            if (!ReadExactly(header, 0, 2)) return false;
            opcode = header[0] & 0x0F;
            masked = (header[1] & 0x80) != 0;
            length = header[1] & 0x7F;
            if (length == 126)
            {
                if (!ReadExactly(header, 2, 2)) return false;
                length = (header[2] << 8) | header[3];
            }
            else if (length == 127)
            {
                if (!ReadExactly(header, 2, 8)) return false;
                length = 0;
                for (var i = 2; i < 10; i++) length = (length << 8) | header[i];
            }
            if (masked && !ReadExactly(mask, 0, 4)) return false;
            maskOffset = 0;
            return true;
        }

        private bool ReadExactly(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        private byte[] ReadPayload(long length)
        {
            var payload = new byte[length];
            if (!ReadExactly(payload, 0, payload.Length)) throw new EndOfStreamException();
            if (masked)
            {
                for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            }
            return payload;
        }

        private void Skip(long length) => ReadPayload(length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            SendFrame(0x2, buffer, offset, count);
        }

        /// <summary>One final frame; client frames are masked with a random key (RFC 6455).</summary>
        private void SendFrame(int opcode, byte[] buffer, int offset, int count)
        {
            lock (writeLock)
            {
                var frame = new byte[14 + count];
                var position = 0;
                frame[position++] = (byte)(0x80 | opcode);
                if (count < 126)
                {
                    frame[position++] = (byte)(0x80 | count);
                }
                else if (count <= 0xFFFF)
                {
                    frame[position++] = 0x80 | 126;
                    frame[position++] = (byte)(count >> 8);
                    frame[position++] = (byte)count;
                }
                else
                {
                    frame[position++] = 0x80 | 127;
                    for (var i = 7; i >= 0; i--) frame[position++] = (byte)((long)count >> (i * 8));
                }
                var key = new byte[4];
                random.GetBytes(key);
                Buffer.BlockCopy(key, 0, frame, position, 4);
                position += 4;
                for (var i = 0; i < count; i++) frame[position + i] = (byte)(buffer[offset + i] ^ key[i & 3]);
                stream.Write(frame, 0, position + count);
                stream.Flush();
            }
        }

        public override void Flush()
        {
        }

        public override void Close()
        {
            closed = true;
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // already closed
            }
            base.Close();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
