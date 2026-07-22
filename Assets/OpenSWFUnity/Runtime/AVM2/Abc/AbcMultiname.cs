namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    public enum AbcMultinameKind : byte
    {
        QName = 0x07,
        QNameA = 0x0D,
        RTQName = 0x0F,
        RTQNameA = 0x10,
        RTQNameL = 0x11,
        RTQNameLA = 0x12,
        Multiname = 0x09,
        MultinameA = 0x0E,
        MultinameL = 0x1B,
        MultinameLA = 0x1C,
        TypeName = 0x1D
    }

    // A name as it appears in the constant pool. Which fields carry meaning depends
    // on Kind: a QName pins one namespace, a Multiname carries a set to search, the
    // RT* forms take their namespace from the runtime stack, the *L forms take their
    // name from the stack too, and TypeName is a parameterised type such as
    // Vector.<int>.
    public sealed class AbcMultiname
    {
        public AbcMultinameKind Kind;
        public int NameIndex;
        public int NamespaceIndex;
        public int NamespaceSetIndex;

        // TypeName only: the generic being applied and its parameters.
        public int TypeDefinitionIndex;
        public int[] TypeParameterIndices;

        public string Name;

        public bool HasRuntimeNamespace =>
            Kind == AbcMultinameKind.RTQName || Kind == AbcMultinameKind.RTQNameA ||
            Kind == AbcMultinameKind.RTQNameL || Kind == AbcMultinameKind.RTQNameLA;

        public bool HasRuntimeName =>
            Kind == AbcMultinameKind.RTQNameL || Kind == AbcMultinameKind.RTQNameLA ||
            Kind == AbcMultinameKind.MultinameL || Kind == AbcMultinameKind.MultinameLA;

        public bool IsTypeName => Kind == AbcMultinameKind.TypeName;

        // Number of extra stack operands the name consumes when resolved at runtime.
        // Callers use it to compute an instruction's true stack effect.
        public int RuntimeOperandCount
        {
            get
            {
                int count = 0;

                if (HasRuntimeNamespace)
                    count++;

                if (HasRuntimeName)
                    count++;

                return count;
            }
        }

        public override string ToString()
        {
            if (IsTypeName)
                return "TypeName<" + (TypeParameterIndices?.Length ?? 0) + ">";

            return string.IsNullOrEmpty(Name) ? Kind.ToString() : Kind + ":" + Name;
        }
    }
}
