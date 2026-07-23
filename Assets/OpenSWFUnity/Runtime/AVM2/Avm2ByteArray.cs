using System;
using System.Collections.Generic;
using System.Text;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // Native flash.utils.ByteArray support. Storage is kept as hidden dynamic
    // properties so user classes such as Flex's ByteArrayAsset inherit the same
    // behaviour even though their instances are allocated by AVM2 bytecode.
    public sealed partial class Avm2Builtins
    {
        private const string UtilsPackage = "flash.utils";
        private static readonly Avm2QName ByteStorageName = Avm2QName.Public("__byteStorage");
        private static readonly Avm2QName BytePositionName = Avm2QName.Public("__bytePosition");
        private static readonly Avm2QName ByteEndianName = Avm2QName.Public("__byteEndian");

        public Avm2Class ByteArrayClass { get; private set; }

        private void DefineUtilityClasses()
        {
            ByteArrayClass = DefinePackageClass(UtilsPackage, "ByteArray", ObjectClass, dynamic: true);
            ByteArrayClass.NativeConstruct = args => new Avm2Object(ByteArrayClass);

            DefineGetter(ByteArrayClass, "length",
                (receiver, args) => GetByteStorage(receiver).Count,
                (receiver, args) =>
                {
                    List<byte> bytes = GetByteStorage(receiver);
                    int length = args.Length > 0 ? Math.Max(0, Avm2Convert.ToInt32(args[0])) : 0;

                    if (length < bytes.Count)
                        bytes.RemoveRange(length, bytes.Count - length);
                    else
                        while (bytes.Count < length)
                            bytes.Add(0);

                    if (GetBytePosition(receiver) > length)
                        SetBytePosition(receiver, length);

                    return Avm2Undefined.Value;
                });

            DefineGetter(ByteArrayClass, "position",
                (receiver, args) => GetBytePosition(receiver),
                (receiver, args) =>
                {
                    SetBytePosition(receiver,
                        args.Length > 0 ? Math.Max(0, Avm2Convert.ToInt32(args[0])) : 0);
                    return Avm2Undefined.Value;
                });

            DefineGetter(ByteArrayClass, "bytesAvailable",
                (receiver, args) => Math.Max(
                    0, GetByteStorage(receiver).Count - GetBytePosition(receiver)));

            DefineGetter(ByteArrayClass, "endian",
                (receiver, args) => GetByteEndian(receiver),
                (receiver, args) =>
                {
                    if (receiver is Avm2Object obj && args.Length > 0)
                        obj.SetDynamic(ByteEndianName, Avm2Convert.ToString(args[0]));
                    return Avm2Undefined.Value;
                });

            DefineMethod(ByteArrayClass, "clear", (receiver, args) =>
            {
                GetByteStorage(receiver).Clear();
                SetBytePosition(receiver, 0);
                return Avm2Undefined.Value;
            });

            DefineMethod(ByteArrayClass, "readUnsignedByte",
                (receiver, args) => ReadByte(receiver, false));
            DefineMethod(ByteArrayClass, "readByte",
                (receiver, args) => ReadByte(receiver, true));
            DefineMethod(ByteArrayClass, "readUnsignedShort",
                (receiver, args) => (uint)ReadInteger(receiver, 2, false));
            DefineMethod(ByteArrayClass, "readShort",
                (receiver, args) => (int)(short)ReadInteger(receiver, 2, false));
            DefineMethod(ByteArrayClass, "readUnsignedInt",
                (receiver, args) => (uint)ReadInteger(receiver, 4, false));
            DefineMethod(ByteArrayClass, "readInt",
                (receiver, args) => unchecked((int)ReadInteger(receiver, 4, false)));

            DefineMethod(ByteArrayClass, "readUTFBytes", (receiver, args) =>
            {
                int count = args.Length > 0 ? Math.Max(0, Avm2Convert.ToInt32(args[0])) : 0;
                return ReadUtfBytes(receiver, count);
            });
            DefineMethod(ByteArrayClass, "readUTF", (receiver, args) =>
            {
                int count = (int)ReadInteger(receiver, 2, false);
                return ReadUtfBytes(receiver, count);
            });

            DefineMethod(ByteArrayClass, "writeByte", (receiver, args) =>
            {
                WriteByte(receiver, args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0);
                return Avm2Undefined.Value;
            });
            DefineMethod(ByteArrayClass, "writeUTFBytes", (receiver, args) =>
            {
                byte[] encoded = Encoding.UTF8.GetBytes(
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty);
                WriteBytes(receiver, encoded);
                return Avm2Undefined.Value;
            });
            DefineMethod(ByteArrayClass, "writeUTF", (receiver, args) =>
            {
                byte[] encoded = Encoding.UTF8.GetBytes(
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty);
                WriteInteger(receiver, (uint)Math.Min(ushort.MaxValue, encoded.Length), 2);
                WriteBytes(receiver, encoded);
                return Avm2Undefined.Value;
            });

            Avm2Class endian = DefinePackageClass(UtilsPackage, "Endian", ObjectClass);
            DefineStaticConstant(endian, "BIG_ENDIAN", "bigEndian");
            DefineStaticConstant(endian, "LITTLE_ENDIAN", "littleEndian");

            domain.SetGlobal(new Avm2QName(UtilsPackage, "getDefinitionByName"),
                Avm2Function.FromNative("getDefinitionByName", (receiver, args) =>
                {
                    if (args.Length == 0)
                        return Avm2Undefined.Value;
                    Avm2QName name = QualifiedName(Avm2Convert.ToString(args[0]));
                    return domain.TryGetGlobal(name, out object value)
                        ? value
                        : Avm2Undefined.Value;
                }));
            domain.SetGlobal(new Avm2QName(UtilsPackage, "getQualifiedClassName"),
                Avm2Function.FromNative("getQualifiedClassName", (receiver, args) =>
                {
                    object value = args.Length > 0 ? args[0] : null;
                    if (value is Avm2Class type)
                        return type.Name.ToString();
                    if (value is Avm2Object obj && obj.Class != null)
                        return obj.Class.Name.ToString();
                    return value?.GetType().Name ?? "null";
                }));
            domain.SetGlobal(new Avm2QName(UtilsPackage, "getTimer"),
                Avm2Function.FromNative("getTimer",
                    (receiver, args) => Environment.TickCount & int.MaxValue));
        }

        private static List<byte> GetByteStorage(object receiver)
        {
            if (receiver is Avm2Object obj)
            {
                if (obj.TryGetDynamic(ByteStorageName, out object stored) &&
                    stored is List<byte> existing)
                {
                    return existing;
                }

                List<byte> created = new List<byte>();
                obj.SetDynamic(ByteStorageName, created);
                return created;
            }

            return new List<byte>();
        }

        private static int GetBytePosition(object receiver)
        {
            if (receiver is Avm2Object obj &&
                obj.TryGetDynamic(BytePositionName, out object value))
            {
                return Math.Max(0, Avm2Convert.ToInt32(value));
            }

            return 0;
        }

        private static void SetBytePosition(object receiver, int position)
        {
            if (receiver is Avm2Object obj)
                obj.SetDynamic(BytePositionName, Math.Max(0, position));
        }

        private static string GetByteEndian(object receiver)
        {
            if (receiver is Avm2Object obj &&
                obj.TryGetDynamic(ByteEndianName, out object value))
            {
                return Avm2Convert.ToString(value);
            }

            return "bigEndian";
        }

        private static int ReadByte(object receiver, bool signed)
        {
            List<byte> bytes = GetByteStorage(receiver);
            int position = GetBytePosition(receiver);
            byte value = position < bytes.Count ? bytes[position] : (byte)0;
            SetBytePosition(receiver, position + 1);
            return signed ? (sbyte)value : value;
        }

        private static uint ReadInteger(object receiver, int width, bool unused)
        {
            uint value = 0;
            bool little = GetByteEndian(receiver) == "littleEndian";

            for (int i = 0; i < width; i++)
            {
                uint next = (uint)ReadByte(receiver, false);
                int shift = little ? i * 8 : (width - 1 - i) * 8;
                value |= next << shift;
            }

            return value;
        }

        private static string ReadUtfBytes(object receiver, int count)
        {
            byte[] bytes = new byte[count];

            for (int i = 0; i < count; i++)
                bytes[i] = (byte)ReadByte(receiver, false);

            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteByte(object receiver, int value)
        {
            List<byte> bytes = GetByteStorage(receiver);
            int position = GetBytePosition(receiver);

            while (bytes.Count <= position)
                bytes.Add(0);

            bytes[position] = (byte)value;
            SetBytePosition(receiver, position + 1);
        }

        private static void WriteBytes(object receiver, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
                WriteByte(receiver, bytes[i]);
        }

        private static void WriteInteger(object receiver, uint value, int width)
        {
            bool little = GetByteEndian(receiver) == "littleEndian";

            for (int i = 0; i < width; i++)
            {
                int shift = little ? i * 8 : (width - 1 - i) * 8;
                WriteByte(receiver, (int)((value >> shift) & 0xFF));
            }
        }
    }
}
