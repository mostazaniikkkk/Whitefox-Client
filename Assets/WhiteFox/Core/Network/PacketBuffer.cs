// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Network
{
    /// <summary>Reads a received packet body (Minosoft's InByteBuffer + PlayInByteBuffer). Big endian, like Java.</summary>
    public sealed class InBuffer
    {
        private readonly byte[] data;
        private int position;
        private readonly int end;

        public InBuffer(byte[] data, int offset = 0, int length = -1)
        {
            this.data = data;
            position = offset;
            end = length < 0 ? data.Length : offset + length;
        }

        public int Remaining => end - position;
        public int Position => position;

        private void Require(int count)
        {
            if (position + count > end) throw new EndOfStreamException($"Packet underflow: need {count} bytes, {Remaining} left");
        }

        public byte ReadByte()
        {
            Require(1);
            return data[position++];
        }

        public sbyte ReadSByte() => (sbyte)ReadByte();
        public bool ReadBool() => ReadByte() != 0;

        public short ReadShort()
        {
            Require(2);
            var value = (short)((data[position] << 8) | data[position + 1]);
            position += 2;
            return value;
        }

        public ushort ReadUShort() => (ushort)ReadShort();

        public int ReadInt()
        {
            Require(4);
            var value = (data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            position += 4;
            return value;
        }

        public long ReadLong()
        {
            var high = (long)(uint)ReadInt();
            var low = (long)(uint)ReadInt();
            return (high << 32) | low;
        }

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());
        public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadLong());

        public int ReadVarInt()
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = ReadByte();
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("VarInt too big");
        }

        public long ReadVarLong()
        {
            long value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = ReadByte();
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("VarLong too big");
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0) throw new InvalidDataException("Negative length " + count);
            Require(count);
            var result = new byte[count];
            Buffer.BlockCopy(data, position, result, 0, count);
            position += count;
            return result;
        }

        public byte[] ReadByteArray() => ReadBytes(ReadVarInt());
        public byte[] ReadRemaining() => ReadBytes(Remaining);

        public void Skip(int count)
        {
            Require(count);
            position += count;
        }

        public string ReadString(int maxLength = 32767)
        {
            var length = ReadVarInt();
            if (length > maxLength * 4) throw new InvalidDataException("String too long: " + length);
            Require(length);
            var value = Encoding.UTF8.GetString(data, position, length);
            position += length;
            return value;
        }

        public ResourceLocation ReadIdentifier() => ResourceLocation.Of(ReadString());

        public Guid ReadUuid()
        {
            var most = ReadLong();
            var least = ReadLong();
            return UuidUtil.FromLongs(most, least);
        }

        /// <summary>Packed block position (1.14+): x 26 bits, z 26 bits, y 12 bits.</summary>
        public BlockPos ReadPosition()
        {
            var value = ReadLong();
            var x = (int)(value >> 38);
            var y = (int)(value << 52 >> 52);
            var z = (int)(value << 26 >> 38);
            return new BlockPos(x, y, z);
        }

        public long[] ReadLongArray(int count)
        {
            var result = new long[count];
            for (var i = 0; i < count; i++) result[i] = ReadLong();
            return result;
        }

        /// <summary>BitSet: VarInt long count + longs.</summary>
        public long[] ReadBitSet() => ReadLongArray(ReadVarInt());

        /// <summary>Network NBT (1.20.2+: the root tag has no name).</summary>
        public object ReadNbt() => Nbt.ReadNetwork(this);

        /// <summary>Chat component, sent as NBT since 1.20.3.</summary>
        public TextComponent ReadNbtText() => TextComponent.FromNbt(ReadNbt());
    }

    /// <summary>Builds a packet body (Minosoft's OutByteBuffer).</summary>
    public sealed class OutBuffer
    {
        private readonly MemoryStream stream = new MemoryStream();

        public byte[] ToArray() => stream.ToArray();
        public int Length => (int)stream.Length;

        public OutBuffer WriteByte(int value)
        {
            stream.WriteByte((byte)value);
            return this;
        }

        public OutBuffer WriteBool(bool value) => WriteByte(value ? 1 : 0);

        public OutBuffer WriteShort(int value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
            return this;
        }

        public OutBuffer WriteInt(int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
            return this;
        }

        public OutBuffer WriteLong(long value)
        {
            WriteInt((int)(value >> 32));
            WriteInt((int)value);
            return this;
        }

        public OutBuffer WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));
        public OutBuffer WriteDouble(double value) => WriteLong(BitConverter.DoubleToInt64Bits(value));

        public OutBuffer WriteVarInt(int value)
        {
            var unsigned = (uint)value;
            while ((unsigned & ~0x7Fu) != 0)
            {
                stream.WriteByte((byte)((unsigned & 0x7F) | 0x80));
                unsigned >>= 7;
            }
            stream.WriteByte((byte)unsigned);
            return this;
        }

        public OutBuffer WriteBytes(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            return this;
        }

        public OutBuffer WriteByteArray(byte[] bytes)
        {
            WriteVarInt(bytes.Length);
            return WriteBytes(bytes);
        }

        public OutBuffer WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteVarInt(bytes.Length);
            return WriteBytes(bytes);
        }

        public OutBuffer WriteUuid(Guid uuid)
        {
            UuidUtil.ToLongs(uuid, out var most, out var least);
            WriteLong(most);
            return WriteLong(least);
        }

        public OutBuffer WritePosition(BlockPos position)
        {
            var value = ((long)(position.X & 0x3FFFFFF) << 38) | ((long)(position.Z & 0x3FFFFFF) << 12) | (long)(position.Y & 0xFFF);
            return WriteLong(value);
        }

        public static int VarIntSize(int value)
        {
            var unsigned = (uint)value;
            var size = 1;
            while ((unsigned & ~0x7Fu) != 0)
            {
                size++;
                unsigned >>= 7;
            }
            return size;
        }
    }

    /// <summary>Java UUIDs are two big endian longs; System.Guid stores some groups little endian.</summary>
    public static class UuidUtil
    {
        public static Guid FromLongs(long most, long least)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 8; i++)
            {
                bytes[i] = (byte)(most >> (56 - i * 8));
                bytes[8 + i] = (byte)(least >> (56 - i * 8));
            }
            return FromBigEndian(bytes);
        }

        public static void ToLongs(Guid uuid, out long most, out long least)
        {
            var bytes = ToBigEndian(uuid);
            most = 0;
            least = 0;
            for (var i = 0; i < 8; i++)
            {
                most = (most << 8) | bytes[i];
                least = (least << 8) | bytes[8 + i];
            }
        }

        public static Guid FromBigEndian(byte[] bytes) => Guid.Parse(Hashing.ToHex(bytes));

        public static byte[] ToBigEndian(Guid uuid)
        {
            var hex = uuid.ToString("N");
            var bytes = new byte[16];
            for (var i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>Offline mode UUID, like the vanilla server: UUID v3 of "OfflinePlayer:" + name.</summary>
        public static Guid Offline(string name)
        {
            byte[] hash;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            }
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return FromBigEndian(hash);
        }
    }
}
