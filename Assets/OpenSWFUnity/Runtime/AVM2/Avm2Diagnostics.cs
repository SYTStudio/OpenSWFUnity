using System;
using System.Collections.Generic;
using System.Text;

namespace OpenSWFUnity.Runtime.AVM2
{
    // Collects AVM2 problems without flooding the console.
    //
    // A single ABC file can contain thousands of methods sharing the same handful of
    // unsupported constructs, so each distinct problem is reported once and counted
    // thereafter. Totals stay queryable for tests and for an end-of-load summary.
    public sealed class Avm2Diagnostics
    {
        private readonly Dictionary<byte, int> unsupportedOpCodes = new Dictionary<byte, int>();
        private readonly Dictionary<string, int> unsupportedStructures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<byte> reportedOpCodes = new HashSet<byte>();
        private readonly HashSet<string> reportedStructures =
            new HashSet<string>(StringComparer.Ordinal);

        public Action<string> Warning { get; set; }

        public int MalformedFileCount { get; private set; }
        public int UndecodableMethodCount { get; private set; }
        public IReadOnlyDictionary<byte, int> UnsupportedOpCodes => unsupportedOpCodes;
        public IReadOnlyDictionary<string, int> UnsupportedStructures => unsupportedStructures;

        public void Reset()
        {
            unsupportedOpCodes.Clear();
            unsupportedStructures.Clear();
            reportedOpCodes.Clear();
            reportedStructures.Clear();
            MalformedFileCount = 0;
            UndecodableMethodCount = 0;
        }

        public void ReportMalformedFile(string name, string reason)
        {
            MalformedFileCount++;
            Report(
                "ABC block '" + (string.IsNullOrEmpty(name) ? "<anonymous>" : name) +
                "' is malformed and was skipped: " + reason
            );
        }

        public void ReportUndecodableMethod(string owner, string reason)
        {
            UndecodableMethodCount++;

            // Keyed by reason so a systemic decoding fault reports once rather than
            // once per method.
            if (CountStructure("undecodable method: " + reason))
                Report("AVM2 method " + owner + " could not be decoded: " + reason);
        }

        public void ReportUnsupportedOpCode(byte opcode, string owner)
        {
            unsupportedOpCodes.TryGetValue(opcode, out int seen);
            unsupportedOpCodes[opcode] = seen + 1;

            if (!reportedOpCodes.Add(opcode))
                return;

            Report(
                "AVM2 opcode " + Bytecode.Avm2OpCode.GetName(opcode) +
                " (0x" + opcode.ToString("X2") + ") is not implemented; first seen in " + owner +
                ". Further occurrences are counted but not logged."
            );
        }

        public void ReportUnsupportedStructure(string description)
        {
            if (CountStructure(description))
                Report("AVM2 structure not supported: " + description);
        }

        private bool CountStructure(string description)
        {
            unsupportedStructures.TryGetValue(description, out int seen);
            unsupportedStructures[description] = seen + 1;
            return reportedStructures.Add(description);
        }

        private void Report(string message)
        {
            Warning?.Invoke(message);
        }

        public string Describe()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("malformedFiles=").Append(MalformedFileCount);
            builder.Append(", undecodableMethods=").Append(UndecodableMethodCount);
            builder.Append(", unsupportedOpcodes=").Append(unsupportedOpCodes.Count);
            builder.Append(", unsupportedStructures=").Append(unsupportedStructures.Count);
            return builder.ToString();
        }
    }
}
