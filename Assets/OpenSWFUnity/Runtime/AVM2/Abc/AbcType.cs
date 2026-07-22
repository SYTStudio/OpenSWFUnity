using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    public static class AbcInstanceFlags
    {
        public const byte ClassSealed = 0x01;
        public const byte ClassFinal = 0x02;
        public const byte ClassInterface = 0x04;
        public const byte ClassProtectedNs = 0x08;
    }

    // instance_info: the per-instance half of a class - its name, supertype,
    // interfaces, constructor, and instance members.
    public sealed class AbcInstanceInfo
    {
        public int Index;
        public int NameIndex;
        public int SuperNameIndex;
        public byte Flags;
        public int ProtectedNamespaceIndex;
        public int[] InterfaceIndices;
        public int InitialiserIndex;
        public List<AbcTrait> Traits;
        public string Name;

        public bool IsSealed => (Flags & AbcInstanceFlags.ClassSealed) != 0;
        public bool IsFinal => (Flags & AbcInstanceFlags.ClassFinal) != 0;
        public bool IsInterface => (Flags & AbcInstanceFlags.ClassInterface) != 0;
        public bool HasProtectedNamespace => (Flags & AbcInstanceFlags.ClassProtectedNs) != 0;

        public int InterfaceCount => InterfaceIndices != null ? InterfaceIndices.Length : 0;

        public override string ToString()
        {
            return (IsInterface ? "interface '" : "class '") +
                   (string.IsNullOrEmpty(Name) ? "?" : Name) + "'" +
                   " traits=" + (Traits != null ? Traits.Count : 0);
        }
    }

    // class_info: the static half - the class initialiser and static members. Paired
    // with the instance_info at the same index.
    public sealed class AbcClassInfo
    {
        public int Index;
        public int StaticInitialiserIndex;
        public List<AbcTrait> Traits;

        public override string ToString()
        {
            return "class_info#" + Index + " traits=" + (Traits != null ? Traits.Count : 0);
        }
    }

    // script_info: a top-level entry point. The last script in a file is the one the
    // player runs first; its traits define that script's globals.
    public sealed class AbcScriptInfo
    {
        public int Index;
        public int InitialiserIndex;
        public List<AbcTrait> Traits;

        public override string ToString()
        {
            return "script#" + Index + " init=method#" + InitialiserIndex +
                   " traits=" + (Traits != null ? Traits.Count : 0);
        }
    }
}
