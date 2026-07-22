using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenSWFUnity.Runtime.AVM2.Abc;
using OpenSWFUnity.Runtime.AVM2.Bytecode;
using OpenSWFUnity.Runtime.Script;

namespace OpenSWFUnity.Runtime.AVM2
{
    // ActionScript 3 runtime.
    //
    // Scope as of this phase: DoABC blocks are fully parsed into a validated model
    // and every method body is decoded and checked, but no ActionScript 3 executes.
    // That boundary is deliberate and visible - Execute reports the gap rather than
    // returning a value that would make callers believe AS3 ran.
    //
    // Shares no code with AVM1. The two runtimes meet only at ISwfScriptRuntime,
    // because the execution models differ fundamentally: AVM1 is a stack machine
    // over prototype objects, AVM2 is a register machine over a class/trait model.
    public sealed partial class Avm2Runtime : ISwfScriptRuntime
    {
        private readonly Dictionary<string, object> globals;
        private readonly List<AbcFile> abcFiles = new List<AbcFile>();

        // Parsed files are keyed by the byte array they came from, so re-registering
        // the same DoABC block (a reload, or the same block referenced twice) reuses
        // the parse instead of repeating it.
        private readonly Dictionary<byte[], AbcFile> parsedFileCache =
            new Dictionary<byte[], AbcFile>();

        private readonly Dictionary<byte, int> opCodeHistogram = new Dictionary<byte, int>();

        private readonly Avm2Domain domain;
        private readonly Avm2Builtins builtins;
        private readonly Avm2Interpreter interpreter;

        public Avm2Domain Domain => domain;
        public Avm2Builtins Builtins => builtins;
        public Avm2Interpreter Interpreter => interpreter;

        // Runs each file's entry script as it is registered. Disable to parse and
        // validate without executing anything.
        public bool ExecuteScripts { get; set; } = true;

        private bool byteEntryReported;

        public int ScriptsRun { get; private set; }
        public int FailedScriptCount { get; private set; }

        public object RootObject { get; }
        public bool VerboseLogging { get; set; }
        public Func<string, IReadOnlyList<object>, object> ExternalFunction { get; set; }
        public Func<object, string, IReadOnlyList<object>, object> ExternalMethod { get; set; }
        public Action<string> Trace { get; set; }

        public Action<string> Warning
        {
            get => Diagnostics.Warning;
            set => Diagnostics.Warning = value;
        }

        public Avm2Diagnostics Diagnostics { get; } = new Avm2Diagnostics();
        public IReadOnlyList<AbcFile> AbcFiles => abcFiles;

        // Total methods declared across every registered block.
        public int DefinedFunctionCount { get; private set; }

        public int DecodedMethodCount { get; private set; }
        public int DecodedInstructionCount { get; private set; }

        // Decoding every body is the validation this phase provides. It is linear in
        // code size and runs once per load, but can be disabled for very large files.
        public bool VerifyMethodBodies { get; set; } = true;

        public Avm2Runtime()
        {
            globals = new Dictionary<string, object>(StringComparer.Ordinal);

            domain = new Avm2Domain(Diagnostics);
            builtins = new Avm2Builtins(domain, (callable, args) =>
                interpreter.CallFunction(callable, args));
            interpreter = new Avm2Interpreter(domain, builtins, Diagnostics);
            domain.Interpreter = interpreter;

            builtins.Trace = message => Trace?.Invoke(message);
            builtins.RegisterAll();

            // The AS3 global object is the script scope, so it is what host code sees
            // as the runtime's root.
            RootObject = domain.Global;
        }

        // ---- registration -----------------------------------------------------

        public void RegisterFunctions(byte[] actionBytes)
        {
            RegisterFunctions(actionBytes, string.Empty);
        }

        public void RegisterFunctions(byte[] abcData, string name)
        {
            if (abcData == null || abcData.Length == 0)
                return;

            if (parsedFileCache.TryGetValue(abcData, out AbcFile cached))
            {
                if (!abcFiles.Contains(cached))
                    AddFile(cached);

                return;
            }

            AbcFile file;

            try
            {
                file = AbcParser.Parse(abcData, name);
            }
            catch (AbcFormatException exception)
            {
                // A damaged block is dropped, but the ones around it still load.
                Diagnostics.ReportMalformedFile(name, exception.Message);
                return;
            }
            catch (Exception exception)
            {
                Diagnostics.ReportMalformedFile(name, exception.GetType().Name + ": " + exception.Message);
                return;
            }

            parsedFileCache[abcData] = file;
            AddFile(file);
        }

        private void AddFile(AbcFile file)
        {
            abcFiles.Add(file);
            DefinedFunctionCount += file.MethodCount;

            for (int i = 0; i < file.UnsupportedStructures.Count; i++)
                Diagnostics.ReportUnsupportedStructure(file.UnsupportedStructures[i]);

            if (VerifyMethodBodies)
                VerifyBodies(file);

            if (VerboseLogging)
                Trace?.Invoke(file.Describe());

            domain.RegisterFile(file);

            if (ExecuteScripts)
                RunEntryScript(file);
        }

        // AS3 runs the last script of a file eagerly; the rest initialise lazily when
        // one of the names they define is first resolved.
        private void RunEntryScript(AbcFile file)
        {
            AbcScriptInfo entry = file.EntryScript;

            if (entry == null)
                return;

            try
            {
                interpreter.RunScriptInitialiser(file, entry);
                ScriptsRun++;
            }
            catch (Avm2ThrownException thrown)
            {
                FailedScriptCount++;
                Warning?.Invoke(
                    "Uncaught ActionScript 3 exception while initialising '" + file.Name + "': " +
                    Values.Avm2Convert.ToString(thrown.Value));
            }
            catch (Avm2AbortException abort)
            {
                FailedScriptCount++;
                Warning?.Invoke("AVM2 execution stopped in '" + file.Name + "': " + abort.Message);
            }
            catch (Avm2UnsupportedException unsupported)
            {
                FailedScriptCount++;
                Diagnostics.ReportUnsupportedOpCode(unsupported.OpCode, "script init of '" + file.Name + "'");
            }
            catch (Exception exception)
            {
                // An interpreter fault must not take the player down with it: the
                // movie still renders its timeline.
                FailedScriptCount++;
                Warning?.Invoke(
                    "AVM2 internal error while initialising '" + file.Name + "': " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        // Decodes every method body so malformed bytecode is found at load time
        // rather than at some unpredictable point during playback. The opcode
        // histogram it builds records exactly which instructions the content needs.
        private void VerifyBodies(AbcFile file)
        {
            for (int i = 0; i < file.MethodBodies.Count; i++)
            {
                AbcMethodBody body = file.MethodBodies[i];
                Avm2MethodCode decoded = Avm2CodeReader.Decode(body);

                if (!decoded.IsValid)
                {
                    AbcMethodInfo owner = file.GetMethod(body.MethodIndex);
                    string ownerName = owner != null && !string.IsNullOrEmpty(owner.Name)
                        ? "'" + owner.Name + "'"
                        : "#" + body.MethodIndex;

                    Diagnostics.ReportUndecodableMethod(
                        ownerName + " in " + file.Name,
                        decoded.FailureReason
                    );
                    continue;
                }

                DecodedMethodCount++;
                DecodedInstructionCount += decoded.InstructionCount;

                for (int instruction = 0; instruction < decoded.Instructions.Length; instruction++)
                {
                    byte opcode = decoded.Instructions[instruction].OpCode;
                    opCodeHistogram.TryGetValue(opcode, out int seen);
                    opCodeHistogram[opcode] = seen + 1;
                }
            }
        }

        // ---- execution boundary ----------------------------------------------

        public object Execute(byte[] actionBytes, object thisObject)
        {
            // AVM2 code is reached through script initialisers and named calls, not
            // through a byte array the way AVM1 DoAction blocks are. Reported once so
            // a per-frame caller cannot spam the console.
            if (!byteEntryReported)
            {
                byteEntryReported = true;
                Warning?.Invoke(
                    "Avm2Runtime.Execute(byte[]) is not how ActionScript 3 is invoked. " +
                    "AS3 runs through script initialisers at load and through named calls; " +
                    "this call did nothing.");
            }

            return null;
        }

        public bool TryCallFunction(string functionName, IReadOnlyList<object> arguments, out object result)
        {
            result = null;

            if (string.IsNullOrEmpty(functionName))
                return false;

            if (!domain.TryGetGlobal(Values.Avm2QName.Public(functionName), out object callable) ||
                !(callable is Values.Avm2Function || callable is Values.Avm2Class))
            {
                // Reporting every miss would be noise: the player probes for handlers
                // such as onEnterFrame each frame and false already says "absent".
                return false;
            }

            return TryInvoke(callable, null, arguments, functionName, out result);
        }

        public bool TryCallMethod(object receiver, string methodName, IReadOnlyList<object> arguments, out object result)
        {
            result = null;

            if (receiver == null || string.IsNullOrEmpty(methodName))
                return false;

            object member;

            try
            {
                member = interpreter.GetProperty(receiver, Values.Avm2QName.Public(methodName));
            }
            catch (Avm2ThrownException)
            {
                return false;
            }

            if (!(member is Values.Avm2Function || member is Values.Avm2Class))
                return false;

            return TryInvoke(member, receiver, arguments, methodName, out result);
        }

        // Shared guard for every host-initiated call: an AS3 fault must surface as a
        // diagnostic, never as an exception crossing back into Unity's update loop.
        private bool TryInvoke(
            object callable,
            object receiver,
            IReadOnlyList<object> arguments,
            string description,
            out object result
        )
        {
            result = null;
            object[] args = ToArgumentArray(arguments);

            try
            {
                result = interpreter.CallValue(callable, receiver, args);
                return true;
            }
            catch (Avm2ThrownException thrown)
            {
                Warning?.Invoke(
                    "Uncaught ActionScript 3 exception from '" + description + "': " +
                    Values.Avm2Convert.ToString(thrown.Value));
            }
            catch (Avm2AbortException abort)
            {
                Warning?.Invoke("AVM2 execution stopped in '" + description + "': " + abort.Message);
            }
            catch (Avm2UnsupportedException unsupported)
            {
                Diagnostics.ReportUnsupportedOpCode(unsupported.OpCode, "'" + description + "'");
            }
            catch (Exception exception)
            {
                Warning?.Invoke(
                    "AVM2 internal error in '" + description + "': " +
                    exception.GetType().Name + ": " + exception.Message);
            }

            return false;
        }

        private static object[] ToArgumentArray(IReadOnlyList<object> arguments)
        {
            if (arguments == null || arguments.Count == 0)
                return Array.Empty<object>();

            object[] args = new object[arguments.Count];

            for (int i = 0; i < args.Length; i++)
                args[i] = arguments[i];

            return args;
        }

        public bool ApplyRegisteredClass(string linkageName, object instance)
        {
            if (!string.IsNullOrEmpty(linkageName))
            {
                Diagnostics.ReportUnsupportedStructure(
                    "class linkage binding (SymbolClass) for '" + linkageName + "'"
                );
            }

            return false;
        }

        // ---- object / variable surface ---------------------------------------

        public object CreateObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public object GetVariable(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            globals.TryGetValue(name, out object value);
            return value;
        }

        public void SetVariable(string name, object value)
        {
            if (string.IsNullOrEmpty(name))
                return;

            globals[name] = value;
        }

        // ---- diagnostics ------------------------------------------------------

        public string DescribeDiagnostics()
        {
            return "AVM2 diagnostics: blocks=" + abcFiles.Count +
                   ", methods=" + DefinedFunctionCount +
                   ", decodedMethods=" + DecodedMethodCount +
                   ", decodedInstructions=" + DecodedInstructionCount +
                   ", distinctOpcodes=" + opCodeHistogram.Count +
                   ", scriptsRun=" + ScriptsRun +
                   ", scriptsFailed=" + FailedScriptCount +
                   ", executedInstructions=" + interpreter.ExecutedInstructionCount +
                   ", classesDefined=" + domain.Global.DynamicCount +
                   ", " + Diagnostics.Describe();
        }

        // Which instructions the loaded content actually uses, most frequent first.
        // This is the concrete input for deciding what an interpreter must cover.
        public string DescribeOpCodeUsage(int limit = 24)
        {
            if (opCodeHistogram.Count == 0)
                return "AVM2 opcode usage: none decoded.";

            List<KeyValuePair<byte, int>> ordered =
                new List<KeyValuePair<byte, int>>(opCodeHistogram);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));

            StringBuilder builder = new StringBuilder();
            builder.Append("AVM2 opcode usage (")
                   .Append(opCodeHistogram.Count)
                   .Append(" distinct, top ")
                   .Append(Math.Min(limit, ordered.Count))
                   .Append("):");

            for (int i = 0; i < ordered.Count && i < limit; i++)
            {
                builder.Append(' ')
                       .Append(Avm2OpCode.GetName(ordered[i].Key))
                       .Append('=')
                       .Append(ordered[i].Value);
            }

            return builder.ToString();
        }

        public IReadOnlyDictionary<byte, int> OpCodeHistogram => opCodeHistogram;

        private static string FormatArguments(IReadOnlyList<object> arguments)
        {
            if (arguments == null || arguments.Count == 0)
                return string.Empty;

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');

                object value = arguments[i];

                if (value == null)
                    builder.Append("null");
                else if (value is IFormattable formattable)
                    builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                else
                    builder.Append(value.ToString());
            }

            return builder.ToString();
        }
    }
}
