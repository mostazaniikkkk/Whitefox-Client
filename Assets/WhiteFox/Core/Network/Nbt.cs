// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WhiteFox.Core.Network
{
    /// <summary>
    /// NBT reader. Values map to: byte/short/int/long/float/double, string, byte[]/int[]/long[],
    /// List&lt;object&gt; and Dictionary&lt;string, object&gt;.
    /// </summary>
    public static class Nbt
    {
        public const byte End = 0;
        public const byte Byte = 1;
        public const byte Short = 2;
        public const byte Int = 3;
        public const byte Long = 4;
        public const byte Float = 5;
        public const byte Double = 6;
        public const byte ByteArray = 7;
        public const byte String = 8;
        public const byte List = 9;
        public const byte Compound = 10;
        public const byte IntArray = 11;
        public const byte LongArray = 12;

        /// <summary>Network NBT since 1.20.2: type byte, then the payload without a name. TAG_End means "no value".</summary>
        public static object ReadNetwork(InBuffer buffer)
        {
            var type = buffer.ReadByte();
            return type == End ? null : ReadPayload(buffer, type, 0);
        }

        private static object ReadPayload(InBuffer buffer, byte type, int depth)
        {
            if (depth > 512) throw new InvalidDataException("NBT too deep");
            switch (type)
            {
                case Byte: return buffer.ReadSByte();
                case Short: return buffer.ReadShort();
                case Int: return buffer.ReadInt();
                case Long: return buffer.ReadLong();
                case Float: return buffer.ReadFloat();
                case Double: return buffer.ReadDouble();
                case ByteArray: return buffer.ReadBytes(buffer.ReadInt());
                case String: return ReadString(buffer);
                case List:
                {
                    var elementType = buffer.ReadByte();
                    var length = buffer.ReadInt();
                    var list = new List<object>(Math.Max(0, Math.Min(length, 4096)));
                    for (var i = 0; i < length; i++) list.Add(ReadPayload(buffer, elementType, depth + 1));
                    return list;
                }
                case Compound:
                {
                    var compound = new Dictionary<string, object>();
                    while (true)
                    {
                        var childType = buffer.ReadByte();
                        if (childType == End) return compound;
                        var name = ReadString(buffer);
                        compound[name] = ReadPayload(buffer, childType, depth + 1);
                    }
                }
                case IntArray:
                {
                    var length = buffer.ReadInt();
                    var array = new int[length];
                    for (var i = 0; i < length; i++) array[i] = buffer.ReadInt();
                    return array;
                }
                case LongArray: return buffer.ReadLongArray(buffer.ReadInt());
                default: throw new InvalidDataException("Unknown NBT tag " + type);
            }
        }

        /// <summary>NBT strings are "modified UTF-8" with an unsigned short length.</summary>
        private static string ReadString(InBuffer buffer)
        {
            var length = buffer.ReadUShort();
            var bytes = buffer.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Writes a value read by <see cref="ReadNetwork"/> back in the network format (null = TAG_End).</summary>
        public static void WriteNetwork(OutBuffer buffer, object value)
        {
            if (value == null)
            {
                buffer.WriteByte(End);
                return;
            }
            var type = TypeOf(value);
            buffer.WriteByte(type);
            WritePayload(buffer, type, value);
        }

        private static byte TypeOf(object value)
        {
            switch (value)
            {
                case sbyte _: return Byte;
                case short _: return Short;
                case int _: return Int;
                case long _: return Long;
                case float _: return Float;
                case double _: return Double;
                case byte[] _: return ByteArray;
                case string _: return String;
                case List<object> _: return List;
                case Dictionary<string, object> _: return Compound;
                case int[] _: return IntArray;
                case long[] _: return LongArray;
                default: throw new InvalidDataException("Can not write " + value.GetType() + " as NBT");
            }
        }

        private static void WritePayload(OutBuffer buffer, byte type, object value)
        {
            switch (type)
            {
                case Byte: buffer.WriteByte((sbyte)value); break;
                case Short: buffer.WriteShort((short)value); break;
                case Int: buffer.WriteInt((int)value); break;
                case Long: buffer.WriteLong((long)value); break;
                case Float: buffer.WriteFloat((float)value); break;
                case Double: buffer.WriteDouble((double)value); break;
                case ByteArray:
                {
                    var bytes = (byte[])value;
                    buffer.WriteInt(bytes.Length);
                    buffer.WriteBytes(bytes);
                    break;
                }
                case String: WriteString(buffer, (string)value); break;
                case List:
                {
                    var list = (List<object>)value;
                    var elementType = list.Count == 0 ? End : TypeOf(list[0]);
                    buffer.WriteByte(elementType);
                    buffer.WriteInt(list.Count);
                    foreach (var element in list) WritePayload(buffer, elementType, element);
                    break;
                }
                case Compound:
                    foreach (var pair in (Dictionary<string, object>)value)
                    {
                        var childType = TypeOf(pair.Value);
                        buffer.WriteByte(childType);
                        WriteString(buffer, pair.Key);
                        WritePayload(buffer, childType, pair.Value);
                    }
                    buffer.WriteByte(End);
                    break;
                case IntArray:
                {
                    var array = (int[])value;
                    buffer.WriteInt(array.Length);
                    foreach (var element in array) buffer.WriteInt(element);
                    break;
                }
                case LongArray:
                {
                    var array = (long[])value;
                    buffer.WriteInt(array.Length);
                    foreach (var element in array) buffer.WriteLong(element);
                    break;
                }
            }
        }

        private static void WriteString(OutBuffer buffer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            buffer.WriteShort(bytes.Length);
            buffer.WriteBytes(bytes);
        }

        public static Dictionary<string, object> AsCompound(object value) => value as Dictionary<string, object>;

        public static object Get(object compound, string key)
        {
            if (compound is Dictionary<string, object> map && map.TryGetValue(key, out var value)) return value;
            return null;
        }

        public static int GetInt(object compound, string key, int fallback = 0)
        {
            switch (Get(compound, key))
            {
                case sbyte b: return b;
                case short s: return s;
                case int i: return i;
                case long l: return (int)l;
                case float f: return (int)f;
                case double d: return (int)d;
                default: return fallback;
            }
        }

        public static string GetString(object compound, string key) => Get(compound, key) as string;
    }
}
