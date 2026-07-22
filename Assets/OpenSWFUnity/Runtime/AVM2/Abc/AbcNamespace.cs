namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    public enum AbcNamespaceKind : byte
    {
        Namespace = 0x08,
        PackageNamespace = 0x16,
        PackageInternalNs = 0x17,
        ProtectedNamespace = 0x18,
        ExplicitNamespace = 0x19,
        StaticProtectedNs = 0x1A,
        PrivateNs = 0x05
    }

    public sealed class AbcNamespace
    {
        public AbcNamespaceKind Kind;
        public int NameIndex;
        public string Name;

        // Private namespaces are distinct per declaration even when their names match,
        // which is what keeps two classes' private members from colliding.
        public bool IsPrivate => Kind == AbcNamespaceKind.PrivateNs;

        public override string ToString()
        {
            return string.IsNullOrEmpty(Name)
                ? Kind.ToString()
                : Kind + ":" + Name;
        }
    }

    public sealed class AbcNamespaceSet
    {
        public int[] NamespaceIndices;

        public int Count => NamespaceIndices != null ? NamespaceIndices.Length : 0;

        public override string ToString()
        {
            return "NamespaceSet[" + Count + "]";
        }
    }
}
