using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using Steamworks;

namespace NetworkingLibrary.Modules
{
    /// <summary>
    /// </summary>
    public class Message : IDisposable
    {
        public const byte PROTOCOL_VERSION = 2;
        public static int MaxSize = 64 * 1024;

        public byte ProtocolVersion;
        public uint ModID;
        public string MethodName = string.Empty;
        public int Mask;

        private List<byte> buffer = new();
        internal byte[] readableBuffer = Array.Empty<byte>();
        internal int readPos = 0;
        private bool UsesReferencePresenceFlags => ProtocolVersion >= 2;

        public bool Compressed { get; private set; } = false;

        public Message(uint modId, string methodName, int mask)
        {
            ProtocolVersion = PROTOCOL_VERSION;
            ModID = modId;
            MethodName = methodName;
            Mask = mask;

            WriteByte(ProtocolVersion);
            WriteUInt(ModID);
            WriteString(MethodName);
            WriteInt(Mask);
        }

        public Message(byte[] data)
        {
            SetBytes(data);
            ProtocolVersion = ReadByte();
            ModID = ReadUInt();
            MethodName = ReadString();
            Mask = ReadInt();
        }

        public void SetBytes(byte[] data)
        {
            buffer.Clear();
            buffer.AddRange(data);
            readableBuffer = buffer.ToArray();
            readPos = 0;
        }

        public byte[] ToArray()
        {
            readableBuffer = buffer.ToArray();
            return readableBuffer;
        }

        public int Length() => buffer.Count;
        public int UnreadLength() => Length() - readPos;

        public void Reset(bool zero = true)
        {
            if (zero)
            {
                buffer.Clear();
                readableBuffer = Array.Empty<byte>();
                readPos = 0;
            }
            else
            {
                readPos = Math.Max(0, readPos - 4);
            }
        }

        #region Write helpers
        public Message WriteByte(byte v) { buffer.Add(v); return this; }
        public Message WriteBytes(byte[] v) { WriteInt(v.Length); buffer.AddRange(v); return this; }
        public Message WriteInt(int v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteUInt(uint v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteLong(long v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteULong(ulong v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteFloat(float v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteBool(bool v) { buffer.AddRange(BitConverter.GetBytes(v)); return this; }
        public Message WriteString(string v)
        {
            var bytes = Encoding.UTF8.GetBytes(v ?? "");
            WriteInt(bytes.Length);
            buffer.AddRange(bytes);
            return this;
        }
        public Message WriteVector3(Vector3 v) { WriteFloat(v.x); WriteFloat(v.y); WriteFloat(v.z); return this; }
        public Message WriteQuaternion(Quaternion q) { WriteFloat(q.x); WriteFloat(q.y); WriteFloat(q.z); WriteFloat(q.w); return this; }

        /// <summary>
        /// </summary>
        public void WriteObject(Type type, object value)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var nt = Nullable.GetUnderlyingType(type);
            if (nt != null)
            {
                bool has = value != null;
                WriteBool(has);
                if (has) WriteObject(nt, value!);
                return;
            }

            if (!type.IsValueType && UsesReferencePresenceFlags)
            {
                bool has = value != null;
                WriteBool(has);
                if (!has) return;
            }
            else if (value == null)
            {
                throw new ArgumentNullException(nameof(value), $"Cannot serialize null for non-nullable value type {type.FullName}.");
            }

            if (writeCasters.TryGetValue(type, out var w))
            {
                w(this, value!);
                return;
            }

            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                WriteObject(underlying, Convert.ChangeType(value, underlying));
                return;
            }

            if (type.IsArray)
            {
                var elemType = type.GetElementType()!;
                var arr = value as Array ?? Array.CreateInstance(elemType, 0);
                WriteInt(arr.Length);
                for (int i = 0; i < arr.Length; i++)
                {
                    WriteObject(elemType, arr.GetValue(i)!);
                }
                return;
            }

            if (typeof(IList).IsAssignableFrom(type))
            {
                Type? elemType = null;
                if (type.IsGenericType)
                {
                    var args = type.GetGenericArguments();
                    if (args.Length == 1) elemType = args[0];
                }

                var list = (IList)value!;
                if (elemType != null)
                {
                    WriteInt(list.Count);
                    for (int i = 0; i < list.Count; i++)
                        WriteObject(elemType, list[i]!);
                    return;
                }

                throw new Exception("Cannot serialize non-generic IList (heterogeneous lists) without explicit serializer registration.");
            }

            throw new Exception($"Unsupported type for WriteObject: {type.FullName}. Register a serializer using Message.RegisterSerializer. Null handling: reference-like types (including arrays/lists/string/byte[]) are encoded with a leading presence flag and may be null; non-null values for unsupported reference types still require registration.");
        }

        public static void RegisterSerializer<T>(Action<Message, T> writer, Func<Message, T> reader)
        {
            if (writer == null || reader == null) throw new ArgumentNullException();
            writeCasters[typeof(T)] = (m, o) => writer(m, (T)o!);
            readCasters[typeof(T)] = (m) => reader(m)!;
        }

        public static void UnregisterSerializer<T>()
        {
            writeCasters.Remove(typeof(T));
            readCasters.Remove(typeof(T));
        }

        private static readonly Dictionary<Type, Action<Message, object>> writeCasters = new();
        private static readonly Dictionary<Type, Func<Message, object>> readCasters = new();

        static Message()
        {
            writeCasters[typeof(byte)] = (m, o) => m.WriteByte((byte)o);
            writeCasters[typeof(byte[])] = (m, o) => m.WriteBytes((byte[])o);
            writeCasters[typeof(int)] = (m, o) => m.WriteInt((int)o);
            writeCasters[typeof(uint)] = (m, o) => m.WriteUInt((uint)o);
            writeCasters[typeof(long)] = (m, o) => m.WriteLong((long)o);
            writeCasters[typeof(ulong)] = (m, o) => m.WriteULong((ulong)o);
            writeCasters[typeof(float)] = (m, o) => m.WriteFloat((float)o);
            writeCasters[typeof(bool)] = (m, o) => m.WriteBool((bool)o);
            writeCasters[typeof(string)] = (m, o) => m.WriteString((string)o);
            writeCasters[typeof(Vector3)] = (m, o) => m.WriteVector3((Vector3)o);
            writeCasters[typeof(Quaternion)] = (m, o) => m.WriteQuaternion((Quaternion)o);
            writeCasters[typeof(CSteamID)] = (m, o) => m.WriteULong(((CSteamID)o).m_SteamID);

            writeCasters[typeof(int[])] = (m, o) => {
                var arr = (int[])o ?? Array.Empty<int>();
                m.WriteInt(arr.Length);
                for (int i = 0; i < arr.Length; i++) m.WriteInt(arr[i]);
            };
            writeCasters[typeof(string[])] = (m, o) => {
                var arr = (string[])o ?? Array.Empty<string>();
                m.WriteInt(arr.Length);
                for (int i = 0; i < arr.Length; i++) m.WriteString(arr[i]);
            };

            readCasters[typeof(byte)] = (m) => m.ReadByte();
            readCasters[typeof(byte[])] = (m) => { int l = m.ReadInt(); if (l == 0) return new byte[0]; var arr = new byte[l]; Array.Copy(m.readableBuffer, m.readPos, arr, 0, l); m.readPos += l; return arr; };
            readCasters[typeof(int)] = (m) => m.ReadInt();
            readCasters[typeof(uint)] = (m) => m.ReadUInt();
            readCasters[typeof(long)] = (m) => m.ReadLong();
            readCasters[typeof(ulong)] = (m) => m.ReadULong();
            readCasters[typeof(float)] = (m) => m.ReadFloat();
            readCasters[typeof(bool)] = (m) => m.ReadBool();
            readCasters[typeof(string)] = (m) => m.ReadString();
            readCasters[typeof(Vector3)] = (m) => m.ReadVector3();
            readCasters[typeof(Quaternion)] = (m) => m.ReadQuaternion();
            readCasters[typeof(CSteamID)] = (m) => new CSteamID(m.ReadULong());

            readCasters[typeof(int[])] = (m) => {
                int len = m.ReadInt();
                if (len == 0) return new int[0];
                var a = new int[len];
                for (int i = 0; i < len; i++) a[i] = m.ReadInt();
                return a;
            };
            readCasters[typeof(string[])] = (m) => {
                int len = m.ReadInt();
                if (len == 0) return new string[0];
                var a = new string[len];
                for (int i = 0; i < len; i++) a[i] = m.ReadString();
                return a;
            };
        }
        #endregion

        #region Read helpers
        private void EnsureReadable(int count, string opName)
        {
            if (count < 0 || readPos < 0 || readPos + count > readableBuffer.Length)
            {
                throw new Exception($"{opName} out of range");
            }
        }

        public byte ReadByte()
        {
            EnsureReadable(1, nameof(ReadByte));
            byte v = readableBuffer[readPos];
            readPos++;
            return v;
        }
        public int ReadInt()
        {
            EnsureReadable(4, nameof(ReadInt));
            int v = BitConverter.ToInt32(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public uint ReadUInt()
        {
            EnsureReadable(4, nameof(ReadUInt));
            uint v = BitConverter.ToUInt32(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public long ReadLong()
        {
            EnsureReadable(8, nameof(ReadLong));
            long v = BitConverter.ToInt64(readableBuffer, readPos);
            readPos += 8;
            return v;
        }
        public ulong ReadULong()
        {
            EnsureReadable(8, nameof(ReadULong));
            ulong v = BitConverter.ToUInt64(readableBuffer, readPos);
            readPos += 8;
            return v;
        }
        public float ReadFloat()
        {
            EnsureReadable(4, nameof(ReadFloat));
            float v = BitConverter.ToSingle(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public bool ReadBool()
        {
            EnsureReadable(1, nameof(ReadBool));
            bool v = BitConverter.ToBoolean(readableBuffer, readPos);
            readPos += 1;
            return v;
        }
        public string ReadString()
        {
            int len = ReadInt();
            if (len < 0)
            {
                throw new Exception("ReadString out of range");
            }
            if (len == 0) return string.Empty;
            EnsureReadable(len, nameof(ReadString));
            string s = Encoding.UTF8.GetString(readableBuffer, readPos, len);
            readPos += len;
            return s;
        }
        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        /// <summary>
        /// </summary>
        public object ReadObject(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var nt = Nullable.GetUnderlyingType(type);
            if (nt != null)
            {
                bool has = ReadBool();
                if (!has) return null!;
                return ReadObject(nt);
            }

            if (!type.IsValueType && UsesReferencePresenceFlags)
            {
                bool has = ReadBool();
                if (!has) return null!;
            }

            if (readCasters.TryGetValue(type, out var r)) return r(this);

            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var raw = ReadObject(underlying);
                return Enum.ToObject(type, raw);
            }

            if (type.IsArray)
            {
                var elemType = type.GetElementType()!;
                int len = ReadInt();
                var arr = Array.CreateInstance(elemType, len);
                for (int i = 0; i < len; i++)
                {
                    var obj = ReadObject(elemType);
                    arr.SetValue(obj, i);
                }
                return arr;
            }

            if (type.IsGenericType)
            {
                var genDef = type.GetGenericTypeDefinition();
                if (genDef == typeof(List<>) || genDef == typeof(IList<>))
                {
                    var elemType = type.GetGenericArguments()[0];
                    int len = ReadInt();
                    if (len < 0 || len > MaxSize)
                    {
                        throw new Exception($"ReadObject out of range for {type.FullName}: malformed list length {len}.");
                    }

                    var listType = typeof(List<>).MakeGenericType(elemType);
                    var list = (IList)Activator.CreateInstance(listType)!;
                    for (int i = 0; i < len; i++)
                    {
                        var item = ReadObject(elemType);
                        list.Add(item);
                    }
                    return list;
                }
            }

            throw new Exception($"Unsupported read type {type.FullName}. Register a deserializer using Message.RegisterSerializer. Null handling: reference-like types (including arrays/lists/string/byte[]) are decoded from a leading presence flag and may be null; non-null payloads for unsupported reference types still require registration.");
        }
        #endregion

        #region Compression helpers
        public byte[] CompressPayload()
        {
            var data = ToArray();
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                gz.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        public static byte[] DecompressPayload(byte[] compressed)
        {
            using var ms = new MemoryStream(compressed);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }
        #endregion

        public void Dispose()
        {
            buffer = null!;
            readableBuffer = null!;
            GC.SuppressFinalize(this);
        }
    }
}
