// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Network
{
    /// <summary>
    /// TCP connection with Minecraft framing (Minosoft's NettyClient + pipeline): VarInt length prefix, optional zlib
    /// compression above a threshold, optional AES/CFB8 encryption of the whole stream. Packets are read on a
    /// dedicated thread and handed to <see cref="PacketReceived"/> (still on that thread).
    /// </summary>
    public sealed class NetworkConnection : IDisposable
    {
        public const int MaxPacketLength = 8 * 1024 * 1024;

        private TcpClient client;
        private Stream input;
        private CipherStream decryptor;
        private CipherStream encryptor;
        private Thread reader;
        private readonly object sendLock = new object();
        private volatile bool closed;
        private int compressionThreshold = -1;

        /// <summary>(packet id, body) on the network thread.</summary>
        public event Action<int, InBuffer> PacketReceived;
        /// <summary>Raised once, with the reason, when the connection ends for any reason.</summary>
        public event Action<string> Disconnected;

        public bool Connected => !closed && client != null && client.Connected;
        public long BytesReceived { get; private set; }
        public long BytesSent { get; private set; }

        public async Task ConnectAsync(string host, int port, int timeoutMilliseconds = 10000)
        {
            client = new TcpClient { NoDelay = true };
            var connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeoutMilliseconds)).ConfigureAwait(false) != connect)
            {
                client.Dispose();
                throw new TimeoutException($"Connection to {host}:{port} timed out");
            }
            await connect.ConfigureAwait(false);
            var stream = client.GetStream();
            // the ciphers sit below the read buffer and pass data through until a key is set
            decryptor = new CipherStream(stream, false);
            encryptor = new CipherStream(stream, true);
            input = new BufferedStream(decryptor, 65536);
        }

        public void StartReading()
        {
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "WhiteFox network reader" };
            reader.Start();
        }

        /// <summary>Set Compression: packets of at least <paramref name="threshold"/> bytes are zlib compressed; -1 disables it.</summary>
        public void SetCompression(int threshold) => compressionThreshold = threshold;

        /// <summary>
        /// Enables AES/CFB8 with the shared secret as key and IV (vanilla encryption). Call right after sending the
        /// encryption response: the server encrypts everything after it, and does not send anything before it.
        /// </summary>
        public void EnableEncryption(byte[] sharedSecret)
        {
            decryptor.SetKey(sharedSecret);
            lock (sendLock) encryptor.SetKey(sharedSecret);
        }

        private void ReadLoop()
        {
            try
            {
                while (!closed)
                {
                    var length = ReadVarInt(input);
                    if (length <= 0 || length > MaxPacketLength) throw new InvalidDataException("Invalid packet length " + length);
                    var frame = ReadFully(input, length);
                    BytesReceived += length;

                    byte[] payload;
                    if (compressionThreshold >= 0)
                    {
                        var frameBuffer = new InBuffer(frame);
                        var dataLength = frameBuffer.ReadVarInt();
                        payload = dataLength == 0 ? frameBuffer.ReadRemaining() : Inflate(frame, frameBuffer.Position, dataLength);
                    }
                    else
                    {
                        payload = frame;
                    }

                    var buffer = new InBuffer(payload);
                    var id = buffer.ReadVarInt();
                    PacketReceived?.Invoke(id, buffer);
                }
            }
            catch (Exception exception)
            {
                if (!closed) Close(exception is EndOfStreamException || exception is IOException ? "Connection lost: " + exception.Message : exception.GetType().Name + ": " + exception.Message);
            }
        }

        public void Send(int id, OutBuffer body)
        {
            if (closed) return;
            var packet = new OutBuffer().WriteVarInt(id).WriteBytes(body.ToArray()).ToArray();
            byte[] data;
            if (compressionThreshold >= 0)
            {
                var frame = new OutBuffer();
                if (packet.Length >= compressionThreshold) frame.WriteVarInt(packet.Length).WriteBytes(Deflate(packet));
                else frame.WriteVarInt(0).WriteBytes(packet);
                data = frame.ToArray();
            }
            else
            {
                data = packet;
            }
            var framed = new OutBuffer().WriteVarInt(data.Length).WriteBytes(data).ToArray();
            try
            {
                lock (sendLock)
                {
                    encryptor.Write(framed, 0, framed.Length);
                    encryptor.Flush();
                    BytesSent += framed.Length;
                }
            }
            catch (Exception exception)
            {
                Close("Connection lost: " + exception.Message);
            }
        }

        public void Close(string reason)
        {
            if (closed) return;
            closed = true;
            try
            {
                client?.Close();
            }
            catch (Exception)
            {
                // already closed
            }
            Log.Info("Disconnected: " + reason);
            Disconnected?.Invoke(reason);
        }

        public void Dispose() => Close("Closed");

        // ---- helpers

        private static int ReadVarInt(Stream stream)
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException("Server closed the connection");
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("VarInt too big");
        }

        private static byte[] ReadFully(Stream stream, int length)
        {
            var data = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = stream.Read(data, read, length - read);
                if (count <= 0) throw new EndOfStreamException("Server closed the connection");
                read += count;
            }
            return data;
        }

        /// <summary>zlib = 2 byte header + raw deflate + adler32; DeflateStream only handles the middle part.</summary>
        private static byte[] Inflate(byte[] data, int offset, int expected)
        {
            if (expected > MaxPacketLength) throw new InvalidDataException("Packet too big: " + expected);
            var result = new byte[expected];
            using (var stream = new DeflateStream(new MemoryStream(data, offset + 2, data.Length - offset - 2), CompressionMode.Decompress))
            {
                var read = 0;
                while (read < expected)
                {
                    var count = stream.Read(result, read, expected - read);
                    if (count <= 0) throw new InvalidDataException("Compressed packet too short");
                    read += count;
                }
            }
            return result;
        }

        private static byte[] Deflate(byte[] data)
        {
            var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var stream = new DeflateStream(output, CompressionLevel.Fastest, true)) stream.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (var value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            var adler = (b << 16) | a;
            output.WriteByte((byte)(adler >> 24));
            output.WriteByte((byte)(adler >> 16));
            output.WriteByte((byte)(adler >> 8));
            output.WriteByte((byte)adler);
            return output.ToArray();
        }
    }

    /// <summary>AES/CFB8 stream cipher (Minosoft's PacketEncryptor/PacketDecryptor), built on AES-ECB.</summary>
    public sealed class CipherStream : Stream
    {
        private readonly Stream inner;
        private readonly bool encrypt;
        private volatile ICryptoTransform aes;
        private readonly byte[] register = new byte[16];
        private readonly byte[] block = new byte[16];

        /// <summary>Passes data through unchanged until <see cref="SetKey"/> is called.</summary>
        public CipherStream(Stream inner, bool encrypt)
        {
            this.inner = inner;
            this.encrypt = encrypt;
        }

        public void SetKey(byte[] key)
        {
            var algorithm = Aes.Create();
            algorithm.Mode = CipherMode.ECB;
            algorithm.Padding = PaddingMode.None;
            algorithm.Key = key;
            Buffer.BlockCopy(key, 0, register, 0, 16);
            aes = algorithm.CreateEncryptor();
        }

        private byte Process(byte value)
        {
            aes.TransformBlock(register, 0, 16, block, 0);
            var result = (byte)(value ^ block[0]);
            Buffer.BlockCopy(register, 1, register, 0, 15);
            register[15] = encrypt ? result : value;
            return result;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (aes == null) return read;
            for (var i = 0; i < read; i++) buffer[offset + i] = Process(buffer[offset + i]);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (aes == null)
            {
                inner.Write(buffer, offset, count);
                return;
            }
            var copy = new byte[count];
            for (var i = 0; i < count; i++) copy[i] = Process(buffer[offset + i]);
            inner.Write(copy, 0, count);
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
