using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // One fully parsed DoABC block. Purely a data model: it holds no runtime state
    // and never executes anything, so a parsed file can be cached and shared.
    public sealed class AbcFile
    {
        public string Name = string.Empty;
        public ushort MinorVersion;
        public ushort MajorVersion;

        public AbcConstantPool ConstantPool = new AbcConstantPool();
        public List<AbcMethodInfo> Methods = new List<AbcMethodInfo>();
        public List<AbcMetadata> Metadata = new List<AbcMetadata>();
        public List<AbcInstanceInfo> Instances = new List<AbcInstanceInfo>();
        public List<AbcClassInfo> Classes = new List<AbcClassInfo>();
        public List<AbcScriptInfo> Scripts = new List<AbcScriptInfo>();
        public List<AbcMethodBody> MethodBodies = new List<AbcMethodBody>();

        // Structures the parser understood but this runtime does not act on yet,
        // recorded during parsing so they can be reported once rather than silently
        // dropped.
        public readonly List<string> UnsupportedStructures = new List<string>();

        public int MethodCount => Methods.Count;
        public int ClassCount => Instances.Count;
        public int ScriptCount => Scripts.Count;
        public int BodyCount => MethodBodies.Count;

        // The AVM runs the final script in a file as its entry point.
        public AbcScriptInfo EntryScript =>
            Scripts.Count > 0 ? Scripts[Scripts.Count - 1] : null;

        public AbcMethodInfo GetMethod(int index)
        {
            return index >= 0 && index < Methods.Count ? Methods[index] : null;
        }

        public AbcInstanceInfo GetInstance(int index)
        {
            return index >= 0 && index < Instances.Count ? Instances[index] : null;
        }

        public string Describe()
        {
            return "ABC '" + (string.IsNullOrEmpty(Name) ? "<anonymous>" : Name) + "' " +
                   MajorVersion + "." + MinorVersion +
                   " constants=" + ConstantPool.TotalEntries +
                   " methods=" + MethodCount +
                   " bodies=" + BodyCount +
                   " classes=" + ClassCount +
                   " scripts=" + ScriptCount +
                   " metadata=" + Metadata.Count;
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}
