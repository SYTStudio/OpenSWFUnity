using System;
using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // Turns raw DoABC bytes into an AbcFile.
    //
    // Structural only: it decodes and validates the container, resolves names for
    // diagnostics, and never executes or interprets bytecode. Anything malformed
    // raises AbcFormatException with a byte offset; anything well-formed but not yet
    // acted upon is recorded in AbcFile.UnsupportedStructures rather than dropped.
    public static class AbcParser
    {
        // exception_info gained its var_name field in ABC 46.16. Reading it from an
        // older file would consume a byte that belongs to the next record.
        private const ushort ExceptionVarNameMajor = 46;
        private const ushort ExceptionVarNameMinor = 16;

        public static AbcFile Parse(byte[] data, string name = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            AbcReader reader = new AbcReader(data);
            AbcFile file = new AbcFile { Name = name ?? string.Empty };

            file.MinorVersion = reader.ReadU16();
            file.MajorVersion = reader.ReadU16();

            ReadConstantPool(reader, file);
            ReadMethods(reader, file);
            ReadMetadata(reader, file);
            ReadClasses(reader, file);
            ReadScripts(reader, file);
            ReadMethodBodies(reader, file);

            ResolveNames(file);
            return file;
        }

        // ---- constant pool ----------------------------------------------------

        private static void ReadConstantPool(AbcReader reader, AbcFile file)
        {
            AbcConstantPool pool = file.ConstantPool;

            // Each pool stores count-1 entries; index 0 is implicit and means
            // "none" (or zero for the numeric pools), so slot 0 is left at default.
            int intCount = reader.ReadEntryCount("Integer pool", AbcLimits.MaxConstantPoolEntries);
            pool.Integers = new int[Math.Max(1, intCount)];
            for (int i = 1; i < intCount; i++)
                pool.Integers[i] = reader.ReadS32();

            int uintCount = reader.ReadEntryCount("UInteger pool", AbcLimits.MaxConstantPoolEntries);
            pool.UnsignedIntegers = new uint[Math.Max(1, uintCount)];
            for (int i = 1; i < uintCount; i++)
                pool.UnsignedIntegers[i] = reader.ReadU32();

            int doubleCount = reader.ReadEntryCount("Double pool", AbcLimits.MaxConstantPoolEntries, 8);
            pool.Doubles = new double[Math.Max(1, doubleCount)];
            pool.Doubles[0] = double.NaN;
            for (int i = 1; i < doubleCount; i++)
                pool.Doubles[i] = reader.ReadD64();

            int stringCount = reader.ReadEntryCount("String pool", AbcLimits.MaxConstantPoolEntries);
            pool.Strings = new string[Math.Max(1, stringCount)];
            pool.Strings[0] = string.Empty;
            for (int i = 1; i < stringCount; i++)
                pool.Strings[i] = reader.ReadString();

            int namespaceCount = reader.ReadEntryCount("Namespace pool", AbcLimits.MaxConstantPoolEntries, 2);
            pool.Namespaces = new AbcNamespace[Math.Max(1, namespaceCount)];
            for (int i = 1; i < namespaceCount; i++)
                pool.Namespaces[i] = ReadNamespace(reader);

            int namespaceSetCount = reader.ReadEntryCount("Namespace set pool", AbcLimits.MaxConstantPoolEntries);
            pool.NamespaceSets = new AbcNamespaceSet[Math.Max(1, namespaceSetCount)];
            for (int i = 1; i < namespaceSetCount; i++)
                pool.NamespaceSets[i] = ReadNamespaceSet(reader);

            int multinameCount = reader.ReadEntryCount("Multiname pool", AbcLimits.MaxConstantPoolEntries);
            pool.Multinames = new AbcMultiname[Math.Max(1, multinameCount)];
            for (int i = 1; i < multinameCount; i++)
                pool.Multinames[i] = ReadMultiname(reader);
        }

        private static AbcNamespace ReadNamespace(AbcReader reader)
        {
            int start = reader.Position;
            byte kind = reader.ReadU8();

            if (!IsKnownNamespaceKind(kind))
                throw new AbcFormatException("Unknown namespace kind 0x" + kind.ToString("X2"), start);

            return new AbcNamespace
            {
                Kind = (AbcNamespaceKind)kind,
                NameIndex = reader.ReadIndex()
            };
        }

        private static bool IsKnownNamespaceKind(byte kind)
        {
            switch (kind)
            {
                case (byte)AbcNamespaceKind.Namespace:
                case (byte)AbcNamespaceKind.PackageNamespace:
                case (byte)AbcNamespaceKind.PackageInternalNs:
                case (byte)AbcNamespaceKind.ProtectedNamespace:
                case (byte)AbcNamespaceKind.ExplicitNamespace:
                case (byte)AbcNamespaceKind.StaticProtectedNs:
                case (byte)AbcNamespaceKind.PrivateNs:
                    return true;
                default:
                    return false;
            }
        }

        private static AbcNamespaceSet ReadNamespaceSet(AbcReader reader)
        {
            int count = reader.ReadEntryCount("Namespace set", AbcLimits.MaxNamespaceSetEntries);
            int[] indices = new int[count];

            for (int i = 0; i < count; i++)
                indices[i] = reader.ReadIndex();

            return new AbcNamespaceSet { NamespaceIndices = indices };
        }

        private static AbcMultiname ReadMultiname(AbcReader reader)
        {
            int start = reader.Position;
            byte kind = reader.ReadU8();
            AbcMultiname multiname = new AbcMultiname { Kind = (AbcMultinameKind)kind };

            switch ((AbcMultinameKind)kind)
            {
                case AbcMultinameKind.QName:
                case AbcMultinameKind.QNameA:
                    multiname.NamespaceIndex = reader.ReadIndex();
                    multiname.NameIndex = reader.ReadIndex();
                    break;

                case AbcMultinameKind.RTQName:
                case AbcMultinameKind.RTQNameA:
                    multiname.NameIndex = reader.ReadIndex();
                    break;

                // Both the namespace and the name come off the runtime stack, so the
                // constant pool entry carries no operands at all.
                case AbcMultinameKind.RTQNameL:
                case AbcMultinameKind.RTQNameLA:
                    break;

                case AbcMultinameKind.Multiname:
                case AbcMultinameKind.MultinameA:
                    multiname.NameIndex = reader.ReadIndex();
                    multiname.NamespaceSetIndex = reader.ReadIndex();
                    break;

                // Name is supplied at runtime; only the namespace set is stored.
                case AbcMultinameKind.MultinameL:
                case AbcMultinameKind.MultinameLA:
                    multiname.NamespaceSetIndex = reader.ReadIndex();
                    break;

                case AbcMultinameKind.TypeName:
                {
                    multiname.TypeDefinitionIndex = reader.ReadIndex();
                    int count = reader.ReadEntryCount(
                        "Type parameter",
                        AbcLimits.MaxMultinameTypeParameters
                    );
                    int[] parameters = new int[count];

                    for (int i = 0; i < count; i++)
                        parameters[i] = reader.ReadIndex();

                    multiname.TypeParameterIndices = parameters;
                    break;
                }

                default:
                    throw new AbcFormatException(
                        "Unknown multiname kind 0x" + kind.ToString("X2"),
                        start
                    );
            }

            return multiname;
        }

        // ---- methods ----------------------------------------------------------

        private static void ReadMethods(AbcReader reader, AbcFile file)
        {
            int count = reader.ReadEntryCount("Method", AbcLimits.MaxMethods, 4);
            file.Methods.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                int parameterCount = reader.ReadEntryCount("Parameter", AbcLimits.MaxParameters);
                AbcMethodInfo method = new AbcMethodInfo
                {
                    Index = i,
                    ReturnTypeIndex = reader.ReadIndex(),
                    ParameterTypeIndices = new int[parameterCount]
                };

                for (int p = 0; p < parameterCount; p++)
                    method.ParameterTypeIndices[p] = reader.ReadIndex();

                method.NameIndex = reader.ReadIndex();
                method.Flags = reader.ReadU8();

                if (method.HasOptional)
                {
                    int optionalCount = reader.ReadEntryCount("Optional parameter", AbcLimits.MaxParameters, 2);
                    method.Optionals = new AbcOptionalDetail[optionalCount];

                    for (int o = 0; o < optionalCount; o++)
                    {
                        method.Optionals[o] = new AbcOptionalDetail
                        {
                            ValueIndex = reader.ReadIndex(),
                            ValueKind = reader.ReadU8()
                        };
                    }
                }

                if ((method.Flags & AbcMethodFlags.HasParamNames) != 0)
                {
                    method.ParameterNameIndices = new int[parameterCount];

                    for (int p = 0; p < parameterCount; p++)
                        method.ParameterNameIndices[p] = reader.ReadIndex();
                }

                file.Methods.Add(method);
            }
        }

        private static void ReadMetadata(AbcReader reader, AbcFile file)
        {
            int count = reader.ReadEntryCount("Metadata", AbcLimits.MaxConstantPoolEntries, 2);
            file.Metadata.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                AbcMetadata metadata = new AbcMetadata { NameIndex = reader.ReadIndex() };
                int itemCount = reader.ReadEntryCount("Metadata item", AbcLimits.MaxConstantPoolEntries, 2);
                metadata.Items = new AbcMetadataItem[itemCount];

                for (int item = 0; item < itemCount; item++)
                {
                    metadata.Items[item] = new AbcMetadataItem
                    {
                        KeyIndex = reader.ReadIndex(),
                        ValueIndex = reader.ReadIndex()
                    };
                }

                file.Metadata.Add(metadata);
            }
        }

        // ---- classes ----------------------------------------------------------

        private static void ReadClasses(AbcReader reader, AbcFile file)
        {
            int count = reader.ReadEntryCount("Class", AbcLimits.MaxClasses, 6);
            file.Instances.Capacity = count;
            file.Classes.Capacity = count;

            for (int i = 0; i < count; i++)
                file.Instances.Add(ReadInstance(reader, i));

            // The static halves follow every instance half, not interleaved.
            for (int i = 0; i < count; i++)
            {
                file.Classes.Add(new AbcClassInfo
                {
                    Index = i,
                    StaticInitialiserIndex = reader.ReadIndex(),
                    Traits = ReadTraits(reader)
                });
            }
        }

        private static AbcInstanceInfo ReadInstance(AbcReader reader, int index)
        {
            AbcInstanceInfo instance = new AbcInstanceInfo
            {
                Index = index,
                NameIndex = reader.ReadIndex(),
                SuperNameIndex = reader.ReadIndex(),
                Flags = reader.ReadU8()
            };

            if (instance.HasProtectedNamespace)
                instance.ProtectedNamespaceIndex = reader.ReadIndex();

            int interfaceCount = reader.ReadEntryCount("Interface", AbcLimits.MaxInterfaces);
            instance.InterfaceIndices = new int[interfaceCount];

            for (int i = 0; i < interfaceCount; i++)
                instance.InterfaceIndices[i] = reader.ReadIndex();

            instance.InitialiserIndex = reader.ReadIndex();
            instance.Traits = ReadTraits(reader);
            return instance;
        }

        private static void ReadScripts(AbcReader reader, AbcFile file)
        {
            int count = reader.ReadEntryCount("Script", AbcLimits.MaxScripts, 2);
            file.Scripts.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                file.Scripts.Add(new AbcScriptInfo
                {
                    Index = i,
                    InitialiserIndex = reader.ReadIndex(),
                    Traits = ReadTraits(reader)
                });
            }
        }

        // ---- traits -----------------------------------------------------------

        private static List<AbcTrait> ReadTraits(AbcReader reader)
        {
            int count = reader.ReadEntryCount("Trait", AbcLimits.MaxTraitsPerOwner, 2);
            List<AbcTrait> traits = new List<AbcTrait>(count);

            for (int i = 0; i < count; i++)
                traits.Add(ReadTrait(reader));

            return traits;
        }

        private static AbcTrait ReadTrait(AbcReader reader)
        {
            int start = reader.Position;
            AbcTrait trait = new AbcTrait { NameIndex = reader.ReadIndex() };

            // Low nibble selects the trait kind, high nibble carries its attributes.
            byte packed = reader.ReadU8();
            trait.Kind = (AbcTraitKind)(packed & 0x0F);
            trait.Attributes = (byte)(packed >> 4);

            switch (trait.Kind)
            {
                // Slot and Const share a shape; the value kind byte is present only
                // when a default value was supplied.
                case AbcTraitKind.Slot:
                case AbcTraitKind.Const:
                    trait.SlotId = reader.ReadIndex();
                    trait.TypeNameIndex = reader.ReadIndex();
                    trait.ValueIndex = reader.ReadIndex();

                    if (trait.ValueIndex != 0)
                        trait.ValueKind = reader.ReadU8();
                    break;

                case AbcTraitKind.Method:
                case AbcTraitKind.Getter:
                case AbcTraitKind.Setter:
                    trait.DispatchId = reader.ReadIndex();
                    trait.MethodIndex = reader.ReadIndex();
                    break;

                case AbcTraitKind.Class:
                    trait.SlotId = reader.ReadIndex();
                    trait.ClassIndex = reader.ReadIndex();
                    break;

                case AbcTraitKind.Function:
                    trait.SlotId = reader.ReadIndex();
                    trait.MethodIndex = reader.ReadIndex();
                    break;

                default:
                    throw new AbcFormatException(
                        "Unknown trait kind " + (packed & 0x0F),
                        start
                    );
            }

            if (trait.HasMetadata)
            {
                int metadataCount = reader.ReadEntryCount("Trait metadata", AbcLimits.MaxTraitsPerOwner);
                trait.MetadataIndices = new int[metadataCount];

                for (int i = 0; i < metadataCount; i++)
                    trait.MetadataIndices[i] = reader.ReadIndex();
            }

            return trait;
        }

        // ---- method bodies ----------------------------------------------------

        private static void ReadMethodBodies(AbcReader reader, AbcFile file)
        {
            int count = reader.ReadEntryCount("Method body", AbcLimits.MaxMethods, 7);
            file.MethodBodies.Capacity = count;

            bool exceptionsCarryVariableName =
                file.MajorVersion > ExceptionVarNameMajor ||
                (file.MajorVersion == ExceptionVarNameMajor &&
                 file.MinorVersion >= ExceptionVarNameMinor);

            for (int i = 0; i < count; i++)
            {
                AbcMethodBody body = new AbcMethodBody
                {
                    MethodIndex = reader.ReadIndex(),
                    MaxStack = reader.ReadIndex(),
                    LocalCount = reader.ReadIndex(),
                    InitScopeDepth = reader.ReadIndex(),
                    MaxScopeDepth = reader.ReadIndex()
                };

                int codeStart = reader.Position;
                uint codeLength = reader.ReadU30();

                if (codeLength > AbcLimits.MaxMethodCodeLength)
                {
                    throw new AbcFormatException(
                        "Method body code length " + codeLength + " exceeds the supported maximum",
                        codeStart
                    );
                }

                body.Code = reader.ReadBytes((int)codeLength);

                int exceptionCount = reader.ReadEntryCount(
                    "Exception handler",
                    AbcLimits.MaxExceptionHandlers,
                    exceptionsCarryVariableName ? 5 : 4
                );
                body.Exceptions = new AbcExceptionInfo[exceptionCount];

                for (int e = 0; e < exceptionCount; e++)
                {
                    AbcExceptionInfo handler = new AbcExceptionInfo
                    {
                        From = reader.ReadIndex(),
                        To = reader.ReadIndex(),
                        Target = reader.ReadIndex(),
                        ExceptionTypeIndex = reader.ReadIndex()
                    };

                    if (exceptionsCarryVariableName)
                        handler.VariableNameIndex = reader.ReadIndex();

                    ValidateExceptionRange(handler, body, e);
                    body.Exceptions[e] = handler;
                }

                body.Traits = ReadTraits(reader);

                AbcMethodInfo owner = file.GetMethod(body.MethodIndex);

                if (owner == null)
                {
                    throw new AbcFormatException(
                        "Method body references method #" + body.MethodIndex +
                        " but the file declares only " + file.MethodCount,
                        codeStart
                    );
                }

                // A second body for one method means the file is inconsistent; keep the
                // first and record the conflict rather than silently overwriting.
                if (owner.Body != null)
                    file.UnsupportedStructures.Add("duplicate body for method #" + body.MethodIndex);
                else
                    owner.Body = body;

                file.MethodBodies.Add(body);
            }
        }

        private static void ValidateExceptionRange(AbcExceptionInfo handler, AbcMethodBody body, int index)
        {
            int codeLength = body.CodeLength;

            if (handler.From < 0 || handler.To < handler.From || handler.To > codeLength ||
                handler.Target < 0 || handler.Target >= codeLength)
            {
                throw new AbcFormatException(
                    "Exception handler " + index + " covers [" + handler.From + "," + handler.To +
                    ") targeting " + handler.Target + ", outside a " + codeLength + " byte body",
                    0
                );
            }
        }

        // ---- name resolution --------------------------------------------------

        // Names are attached after the whole file is read because a structure may
        // reference a pool entry that appears later in the stream.
        private static void ResolveNames(AbcFile file)
        {
            AbcConstantPool pool = file.ConstantPool;

            for (int i = 1; i < pool.Namespaces.Length; i++)
            {
                AbcNamespace ns = pool.Namespaces[i];

                if (ns != null)
                    ns.Name = pool.GetString(ns.NameIndex);
            }

            for (int i = 1; i < pool.Multinames.Length; i++)
            {
                AbcMultiname multiname = pool.Multinames[i];

                if (multiname != null)
                    multiname.Name = pool.GetString(multiname.NameIndex);
            }

            for (int i = 0; i < file.Methods.Count; i++)
            {
                AbcMethodInfo method = file.Methods[i];
                method.Name = pool.GetString(method.NameIndex);
                method.OwnerFile = file;
            }

            for (int i = 0; i < file.Metadata.Count; i++)
                file.Metadata[i].Name = pool.GetString(file.Metadata[i].NameIndex);

            for (int i = 0; i < file.Instances.Count; i++)
            {
                AbcInstanceInfo instance = file.Instances[i];
                instance.Name = pool.DescribeMultiname(instance.NameIndex);
                ResolveTraitNames(pool, instance.Traits);
            }

            for (int i = 0; i < file.Classes.Count; i++)
                ResolveTraitNames(pool, file.Classes[i].Traits);

            for (int i = 0; i < file.Scripts.Count; i++)
                ResolveTraitNames(pool, file.Scripts[i].Traits);

            for (int i = 0; i < file.MethodBodies.Count; i++)
                ResolveTraitNames(pool, file.MethodBodies[i].Traits);
        }

        private static void ResolveTraitNames(AbcConstantPool pool, List<AbcTrait> traits)
        {
            if (traits == null)
                return;

            for (int i = 0; i < traits.Count; i++)
                traits[i].Name = pool.DescribeMultiname(traits[i].NameIndex);
        }
    }
}
