using System;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Abc;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // Per-file multiname resolution cache.
    //
    // Property access is the hottest thing an AVM2 program does, and every access
    // names its target through a constant pool index. Resolving that index once and
    // keeping the answer turns each later access into an array read. Sized to the
    // pool, so there is no per-access allocation and no dictionary hashing.
    internal sealed class Avm2NameCache
    {
        public readonly Avm2QName[] QNames;
        public readonly bool[] QNameResolved;
        public readonly string[][] NamespaceSets;

        public Avm2NameCache(AbcFile file)
        {
            int count = file.ConstantPool.Multinames.Length;
            QNames = new Avm2QName[count];
            QNameResolved = new bool[count];
            NamespaceSets = new string[count][];
        }
    }

    // Where a not-yet-initialised definition lives.
    internal struct Avm2ScriptDefinition
    {
        public AbcFile File;
        public AbcScriptInfo Script;
    }

    // The global namespace: every class and script-level definition the loaded ABC
    // files declare, plus the host builtins.
    //
    // AS3 initialises scripts lazily. Only the last script of a file runs at load;
    // the rest run the first time one of the names they define is looked up. That
    // ordering is observable - a class's static initialiser must not run before
    // something asks for the class - so it is modelled here rather than flattened.
    public sealed class Avm2Domain
    {
        private readonly Dictionary<Avm2QName, Avm2ScriptDefinition> pendingDefinitions =
            new Dictionary<Avm2QName, Avm2ScriptDefinition>();
        private readonly HashSet<AbcScriptInfo> initialisedScripts = new HashSet<AbcScriptInfo>();
        private readonly Dictionary<AbcFile, Avm2NameCache> nameCaches =
            new Dictionary<AbcFile, Avm2NameCache>();

        // Populated as script initialisers run; also holds the builtins.
        public Avm2Object Global { get; } = new Avm2Object();

        public Avm2Diagnostics Diagnostics { get; }

        // Set once the interpreter exists. Running a script initialiser means
        // executing bytecode, which only the interpreter can do.
        internal Avm2Interpreter Interpreter { get; set; }

        public Avm2Domain(Avm2Diagnostics diagnostics)
        {
            Diagnostics = diagnostics;
        }

        // ---- name resolution --------------------------------------------------

        internal Avm2NameCache GetNameCache(AbcFile file)
        {
            if (!nameCaches.TryGetValue(file, out Avm2NameCache cache))
            {
                cache = new Avm2NameCache(file);
                nameCaches[file] = cache;
            }

            return cache;
        }

        // Resolves a multiname that names exactly one namespace. Multinames carrying
        // a namespace set cannot be reduced this way - which namespace applies
        // depends on the object being searched - so those return false and the caller
        // uses GetNamespaceSet instead.
        public bool TryResolveQName(AbcFile file, int multinameIndex, out Avm2QName name)
        {
            Avm2NameCache cache = GetNameCache(file);

            if (multinameIndex > 0 && multinameIndex < cache.QNames.Length &&
                cache.QNameResolved[multinameIndex])
            {
                name = cache.QNames[multinameIndex];
                return true;
            }

            name = default;
            AbcMultiname multiname = file.ConstantPool.GetMultiname(multinameIndex);

            if (multiname == null)
                return false;

            if (multiname.Kind != AbcMultinameKind.QName &&
                multiname.Kind != AbcMultinameKind.QNameA)
            {
                return false;
            }

            AbcNamespace ns = file.ConstantPool.GetNamespace(multiname.NamespaceIndex);
            name = new Avm2QName(ns != null ? ns.Name : string.Empty, multiname.Name);

            if (multinameIndex < cache.QNames.Length)
            {
                cache.QNames[multinameIndex] = name;
                cache.QNameResolved[multinameIndex] = true;
            }

            return true;
        }

        // Candidate namespace URIs for a multiname that carries a set.
        public string[] GetNamespaceSet(AbcFile file, int multinameIndex)
        {
            Avm2NameCache cache = GetNameCache(file);

            if (multinameIndex > 0 && multinameIndex < cache.NamespaceSets.Length &&
                cache.NamespaceSets[multinameIndex] != null)
            {
                return cache.NamespaceSets[multinameIndex];
            }

            AbcMultiname multiname = file.ConstantPool.GetMultiname(multinameIndex);

            if (multiname == null)
                return Array.Empty<string>();

            AbcNamespaceSet set = file.ConstantPool.GetNamespaceSet(multiname.NamespaceSetIndex);

            if (set == null || set.NamespaceIndices == null)
                return Array.Empty<string>();

            string[] uris = new string[set.NamespaceIndices.Length];

            for (int i = 0; i < uris.Length; i++)
            {
                AbcNamespace ns = file.ConstantPool.GetNamespace(set.NamespaceIndices[i]);
                uris[i] = ns != null ? ns.Name : string.Empty;
            }

            if (multinameIndex < cache.NamespaceSets.Length)
                cache.NamespaceSets[multinameIndex] = uris;

            return uris;
        }

        // ---- registration -----------------------------------------------------

        public void RegisterFile(AbcFile file)
        {
            for (int i = 0; i < file.Scripts.Count; i++)
            {
                AbcScriptInfo script = file.Scripts[i];

                if (script.Traits == null)
                    continue;

                for (int t = 0; t < script.Traits.Count; t++)
                {
                    AbcTrait trait = script.Traits[t];

                    if (!TryResolveQName(file, trait.NameIndex, out Avm2QName name))
                        continue;

                    // First declaration wins, matching the AVM: a later file does not
                    // silently replace a definition another script already owns.
                    if (!pendingDefinitions.ContainsKey(name) && !Global.HasDynamic(name))
                    {
                        pendingDefinitions[name] = new Avm2ScriptDefinition
                        {
                            File = file,
                            Script = script
                        };
                    }
                }
            }
        }

        public bool IsScriptInitialised(AbcScriptInfo script)
        {
            return script == null || initialisedScripts.Contains(script);
        }

        public void MarkScriptInitialised(AbcScriptInfo script)
        {
            if (script != null)
                initialisedScripts.Add(script);
        }

        // Looks a global name up, running the script that defines it if that has not
        // happened yet. Returns false only when nothing in the program declares it.
        public bool TryGetGlobal(Avm2QName name, out object value)
        {
            if (Global.TryGetDynamic(name, out value))
                return true;

            if (!pendingDefinitions.TryGetValue(name, out Avm2ScriptDefinition definition))
                return false;

            // Script traits exist as global slots before their initialiser runs.
            // Generated bytecode starts with findpropstrict for the class it is
            // about to place into such a slot. Publishing undefined placeholders
            // for every trait owned by this script models that allocation phase and
            // also prevents recursive lazy initialisation.
            List<Avm2QName> scriptNames = new List<Avm2QName>();

            foreach (KeyValuePair<Avm2QName, Avm2ScriptDefinition> pending in pendingDefinitions)
            {
                if (ReferenceEquals(pending.Value.Script, definition.Script))
                    scriptNames.Add(pending.Key);
            }

            for (int i = 0; i < scriptNames.Count; i++)
            {
                Avm2QName scriptName = scriptNames[i];
                pendingDefinitions.Remove(scriptName);

                if (!Global.HasDynamic(scriptName))
                    Global.SetDynamic(scriptName, Avm2Undefined.Value);
            }

            if (!IsScriptInitialised(definition.Script) && Interpreter != null)
                Interpreter.RunScriptInitialiser(definition.File, definition.Script);

            return Global.TryGetDynamic(name, out value);
        }

        public bool HasDefinition(Avm2QName name)
        {
            return Global.HasDynamic(name) || pendingDefinitions.ContainsKey(name);
        }

        public void SetGlobal(Avm2QName name, object value)
        {
            Global.SetDynamic(name, value);
        }

        public int PendingDefinitionCount => pendingDefinitions.Count;
        public int InitialisedScriptCount => initialisedScripts.Count;
    }
}
