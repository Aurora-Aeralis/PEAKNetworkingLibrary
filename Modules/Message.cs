using System;
using System.Buffers.Binary;
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
        public const byte PROTOCOL_VERSION = 3;
        public static int MaxSize = 64 * 1024;
        public static int MaxLogicalSize => MaxSize * 16;

        public byte ProtocolVersion;
        public uint ModID;
        public string MethodName = string.Empty;
        public int Mask;
        public string? OverloadKey;

        private List<byte> buffer = new();
        internal byte[] readableBuffer = Array.Empty<byte>();
        internal int readPos = 0;
        private bool _disposed;
        private bool UsesReferencePresenceFlags => ProtocolVersion >= 2;

        public Message(uint modId, string methodName, int mask) : this(modId, methodName, mask, null)
        {
        }

        public Message(uint modId, string methodName, int mask, string? overloadKey)
        {
            ProtocolVersion = overloadKey == null ? (byte)2 : PROTOCOL_VERSION;
            ModID = modId;
            MethodName = methodName;
            Mask = mask;
            OverloadKey = overloadKey;

            WriteByte(ProtocolVersion);
            WriteUInt(ModID);
            WriteString(MethodName);
            WriteInt(Mask);
            if (ProtocolVersion >= 3)
            {
                WriteBool(true);
                WriteString(overloadKey!);
            }
        }

        public Message(byte[] data)
        {
            SetBytes(data);
            ProtocolVersion = ReadByte();
            ModID = ReadUInt();
            MethodName = ReadString();
            Mask = ReadInt();
            if (ProtocolVersion >= 3)
            {
                var hasOverloadKey = ReadBool();
                OverloadKey = hasOverloadKey ? ReadString() : null;
            }
            else
            {
                OverloadKey = null;
            }
        }

        public void SetBytes(byte[] data)
        {
            ThrowIfDisposed();
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > MaxLogicalSize)
            {
                throw new Exception($"Message payload exceeds max allowed size {MaxLogicalSize}");
            }
            buffer.Clear();
            buffer.AddRange(data);
            readableBuffer = buffer.ToArray();
            readPos = 0;
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            readableBuffer = buffer.ToArray();
            return readableBuffer;
        }

        public int Length()
        {
            ThrowIfDisposed();
            return buffer.Count;
        }

        public int UnreadLength()
        {
            ThrowIfDisposed();
            return Length() - readPos;
        }

        public void Reset(bool zero = true)
        {
            ThrowIfDisposed();
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
        private static byte[] WriteInt32LE(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] WriteUInt32LE(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] WriteInt64LE(long value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] WriteUInt64LE(ulong value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] WriteSingleLE(float value) => WriteInt32LE(BitConverter.SingleToInt32Bits(value));

        private static int ReadInt32LE(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static uint ReadUInt32LE(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static long ReadInt64LE(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
        private static ulong ReadUInt64LE(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        private static float ReadSingleLE(byte[] bytes, int offset) => BitConverter.Int32BitsToSingle(ReadInt32LE(bytes, offset));

        public Message WriteByte(byte v) { ThrowIfDisposed(); buffer.Add(v); return this; }
        public Message WriteBytes(byte[] v)
        {
            ThrowIfDisposed();
            if (v == null) throw new ArgumentNullException(nameof(v));
            WriteInt(v.Length);
            buffer.AddRange(v);
            return this;
        }
        public Message WriteInt(int v) { ThrowIfDisposed(); buffer.AddRange(WriteInt32LE(v)); return this; }
        public Message WriteUInt(uint v) { ThrowIfDisposed(); buffer.AddRange(WriteUInt32LE(v)); return this; }
        public Message WriteLong(long v) { ThrowIfDisposed(); buffer.AddRange(WriteInt64LE(v)); return this; }
        public Message WriteULong(ulong v) { ThrowIfDisposed(); buffer.AddRange(WriteUInt64LE(v)); return this; }
        public Message WriteFloat(float v) { ThrowIfDisposed(); buffer.AddRange(WriteSingleLE(v)); return this; }
        public Message WriteBool(bool v) { ThrowIfDisposed(); buffer.Add(v ? (byte)1 : (byte)0); return this; }
        public Message WriteString(string v)
        {
            ThrowIfDisposed();
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
            ThrowIfDisposed();
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
            readCasters[typeof(byte[])] = (m) => {
                int len = m.ReadCollectionLength(nameof(Byte[]));
                if (len == 0) return Array.Empty<byte>();
                m.EnsureReadable(len, nameof(Byte[]));
                var arr = new byte[len];
                Array.Copy(m.readableBuffer, m.readPos, arr, 0, len);
                m.readPos += len;
                return arr;
            };
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
                int len = m.ReadCollectionLength(nameof(Int32[]));
                if (len == 0) return Array.Empty<int>();
                var a = new int[len];
                for (int i = 0; i < len; i++) a[i] = m.ReadInt();
                return a;
            };
            readCasters[typeof(string[])] = (m) => {
                int len = m.ReadCollectionLength(nameof(String[]));
                if (len == 0) return Array.Empty<string>();
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

        private int ReadCollectionLength(string opName)
        {
            int len = ReadInt();
            if (len < 0)
            {
                throw new Exception($"{opName} length out of range");
            }
            if (len > MaxLogicalSize)
            {
                throw new Exception($"{opName} length exceeds max {MaxLogicalSize}");
            }
            return len;
        }

        private static bool IsConcreteConstructible(Type type)
        {
            if (type.IsAbstract || type.IsInterface) return false;
            if (type.IsValueType) return true;
            return type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static Type? TryGetGenericListElementType(Type type)
        {
            if (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IList<>))
            {
                return type.GetGenericArguments()[0];
            }

            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                {
                    return i.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static bool TryCopyItemsToListTarget(object target, Type elementType, IList source)
        {
            if (target is IList targetList)
            {
                for (int i = 0; i < source.Count; i++) targetList.Add(source[i]);
                return true;
            }

            var addMethod = target.GetType().GetMethod("Add", new[] { elementType });
            if (addMethod == null) return false;

            for (int i = 0; i < source.Count; i++) addMethod.Invoke(target, new[] { source[i] });
            return true;
        }

        public byte ReadByte()
        {
            ThrowIfDisposed();
            EnsureReadable(1, nameof(ReadByte));
            byte v = readableBuffer[readPos];
            readPos++;
            return v;
        }
        public int ReadInt()
        {
            ThrowIfDisposed();
            EnsureReadable(4, nameof(ReadInt));
            int v = ReadInt32LE(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public uint ReadUInt()
        {
            ThrowIfDisposed();
            EnsureReadable(4, nameof(ReadUInt));
            uint v = ReadUInt32LE(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public long ReadLong()
        {
            ThrowIfDisposed();
            EnsureReadable(8, nameof(ReadLong));
            long v = ReadInt64LE(readableBuffer, readPos);
            readPos += 8;
            return v;
        }
        public ulong ReadULong()
        {
            ThrowIfDisposed();
            EnsureReadable(8, nameof(ReadULong));
            ulong v = ReadUInt64LE(readableBuffer, readPos);
            readPos += 8;
            return v;
        }
        public float ReadFloat()
        {
            ThrowIfDisposed();
            EnsureReadable(4, nameof(ReadFloat));
            float v = ReadSingleLE(readableBuffer, readPos);
            readPos += 4;
            return v;
        }
        public bool ReadBool()
        {
            ThrowIfDisposed();
            EnsureReadable(1, nameof(ReadBool));
            bool v = readableBuffer[readPos] != 0;
            readPos += 1;
            return v;
        }
        public string ReadString()
        {
            ThrowIfDisposed();
            int len = ReadInt();
            if (len < 0)
            {
                throw new Exception("ReadString out of range");
            }
            if (len > MaxLogicalSize)
            {
                throw new Exception($"ReadString length exceeds max {MaxLogicalSize}");
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
            ThrowIfDisposed();
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
                int len = ReadCollectionLength(type.FullName ?? nameof(Array));
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
                    int len = ReadCollectionLength(type.FullName ?? "List");
                    var listType = typeof(List<>).MakeGenericType(elemType);
                    var list = (IList)Activator.CreateInstance(listType)!;
                    for (int i = 0; i < len; i++)
                    {
                        var item = ReadObject(elemType);
                        list.Add(item);
                    }
                    return list;
                }

                var elemType = TryGetGenericListElementType(type);
                bool isListLike = typeof(IList).IsAssignableFrom(type) || type.GetInterface(typeof(IList<>).Name) != null;
                if (elemType != null && isListLike)
                {
                    int len = ReadCollectionLength(type.FullName ?? "List");
                    var tempListType = typeof(List<>).MakeGenericType(elemType);
                    var tempList = (IList)Activator.CreateInstance(tempListType)!;
                    for (int i = 0; i < len; i++)
                    {
                        tempList.Add(ReadObject(elemType));
                    }

                    if (!IsConcreteConstructible(type)) return tempList;
                    var target = Activator.CreateInstance(type)!;
                    if (!TryCopyItemsToListTarget(target, elemType, tempList)) return tempList;
                    return target;
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

        public static byte[] DecompressPayload(byte[] compressed, int maxOutputSize = -1)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (maxOutputSize < 0) maxOutputSize = MaxLogicalSize;
            using var ms = new MemoryStream(compressed);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var buffer = new byte[8 * 1024];
            var totalRead = 0;
            while (true)
            {
                var bytesRead = gz.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
                if (totalRead > maxOutputSize)
                {
                    throw new InvalidDataException($"Decompressed payload exceeds max allowed size {maxOutputSize}");
                }
                outMs.Write(buffer, 0, bytesRead);
            }
            return outMs.ToArray();
        }
        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            buffer.Clear();
            buffer = new List<byte>();
            readableBuffer = Array.Empty<byte>();
            readPos = 0;
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Message));
        }
    }
}
