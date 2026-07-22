using System;

namespace OpenSWFUnity.Runtime.AVM2
{
    // An ActionScript `throw`. Carries the thrown value, which may be any AS3 value,
    // and is caught by the interpreter's exception-table search rather than by C#
    // code outside the loop.
    public sealed class Avm2ThrownException : Exception
    {
        public readonly object Value;

        public Avm2ThrownException(object value)
            : base("ActionScript exception")
        {
            Value = value;
        }
    }

    // Raised when a guard trips: instruction budget, call depth, or wall-clock time.
    //
    // Deliberately not an Avm2ThrownException, so AS3 `catch` blocks cannot swallow
    // it and keep a runaway script alive. It unwinds the whole invocation.
    public sealed class Avm2AbortException : Exception
    {
        public Avm2AbortException(string message)
            : base(message)
        {
        }
    }

    // Raised when the interpreter meets bytecode it cannot execute correctly.
    //
    // Separate from an abort because it is a gap in this implementation, not a fault
    // in the content. The runtime reports it and abandons the method rather than
    // continuing with a corrupted stack, which would produce plausible-looking but
    // wrong results.
    public sealed class Avm2UnsupportedException : Exception
    {
        public readonly byte OpCode;

        public Avm2UnsupportedException(byte opcode, string message)
            : base(message)
        {
            OpCode = opcode;
        }
    }
}
