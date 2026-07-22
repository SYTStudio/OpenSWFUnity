namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    public enum AbcTraitKind : byte
    {
        Slot = 0,
        Method = 1,
        Getter = 2,
        Setter = 3,
        Class = 4,
        Function = 5,
        Const = 6
    }

    public static class AbcTraitAttributes
    {
        public const byte Final = 0x01;
        public const byte Override = 0x02;
        public const byte Metadata = 0x04;
    }

    // One declared member of a class, instance, script, or method body. The union of
    // fields below is discriminated by Kind: slots and consts carry a type and an
    // optional default value, methods/getters/setters carry a method index, a class
    // trait carries a class index, and a function trait carries a method index.
    public sealed class AbcTrait
    {
        public int NameIndex;
        public AbcTraitKind Kind;
        public byte Attributes;

        // Slot / Const.
        public int SlotId;
        public int TypeNameIndex;
        public int ValueIndex;
        public byte ValueKind;

        // Method / Getter / Setter / Function.
        public int DispatchId;
        public int MethodIndex;

        // Class.
        public int ClassIndex;

        public int[] MetadataIndices;

        public string Name;

        public bool IsFinal => (Attributes & AbcTraitAttributes.Final) != 0;
        public bool IsOverride => (Attributes & AbcTraitAttributes.Override) != 0;
        public bool HasMetadata => (Attributes & AbcTraitAttributes.Metadata) != 0;

        public bool IsExecutable =>
            Kind == AbcTraitKind.Method || Kind == AbcTraitKind.Getter ||
            Kind == AbcTraitKind.Setter || Kind == AbcTraitKind.Function;

        public override string ToString()
        {
            return Kind + " '" + (string.IsNullOrEmpty(Name) ? "?" : Name) + "'";
        }
    }

    public sealed class AbcMetadataItem
    {
        public int KeyIndex;
        public int ValueIndex;
    }

    public sealed class AbcMetadata
    {
        public int NameIndex;
        public AbcMetadataItem[] Items;
        public string Name;

        public override string ToString()
        {
            return "metadata '" + Name + "' items=" + (Items != null ? Items.Length : 0);
        }
    }
}
