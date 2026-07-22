namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // The five scalar pools plus namespaces, namespace sets and multinames.
    //
    // Index 0 is reserved in every pool and never stored by the file: it means "no
    // value" for strings and namespaces, and the literal 0/NaN for the numeric
    // pools. Slot 0 of each array is therefore present but always the default, so
    // callers can index directly without adjusting.
    public sealed class AbcConstantPool
    {
        public int[] Integers = System.Array.Empty<int>();
        public uint[] UnsignedIntegers = System.Array.Empty<uint>();
        public double[] Doubles = System.Array.Empty<double>();
        public string[] Strings = System.Array.Empty<string>();
        public AbcNamespace[] Namespaces = System.Array.Empty<AbcNamespace>();
        public AbcNamespaceSet[] NamespaceSets = System.Array.Empty<AbcNamespaceSet>();
        public AbcMultiname[] Multinames = System.Array.Empty<AbcMultiname>();

        public string GetString(int index)
        {
            return index > 0 && index < Strings.Length ? Strings[index] : string.Empty;
        }

        public AbcNamespace GetNamespace(int index)
        {
            return index > 0 && index < Namespaces.Length ? Namespaces[index] : null;
        }

        public AbcNamespaceSet GetNamespaceSet(int index)
        {
            return index > 0 && index < NamespaceSets.Length ? NamespaceSets[index] : null;
        }

        public AbcMultiname GetMultiname(int index)
        {
            return index > 0 && index < Multinames.Length ? Multinames[index] : null;
        }

        // Fully qualified name for diagnostics: "flash.display::Sprite" rather than
        // just "Sprite", so two same-named classes in different packages are
        // distinguishable in a log.
        public string DescribeMultiname(int index)
        {
            AbcMultiname multiname = GetMultiname(index);

            if (multiname == null)
                return index == 0 ? "*" : "<multiname#" + index + ">";

            if (multiname.IsTypeName)
            {
                string baseName = DescribeMultiname(multiname.TypeDefinitionIndex);
                int count = multiname.TypeParameterIndices != null
                    ? multiname.TypeParameterIndices.Length
                    : 0;

                if (count == 0)
                    return baseName + ".<>";

                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                builder.Append(baseName).Append(".<");

                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    builder.Append(DescribeMultiname(multiname.TypeParameterIndices[i]));
                }

                builder.Append('>');
                return builder.ToString();
            }

            string name = string.IsNullOrEmpty(multiname.Name) ? "*" : multiname.Name;
            AbcNamespace ns = GetNamespace(multiname.NamespaceIndex);

            if (ns != null && !string.IsNullOrEmpty(ns.Name))
                return ns.Name + "::" + name;

            return name;
        }

        public int TotalEntries =>
            Integers.Length + UnsignedIntegers.Length + Doubles.Length + Strings.Length +
            Namespaces.Length + NamespaceSets.Length + Multinames.Length;
    }
}
