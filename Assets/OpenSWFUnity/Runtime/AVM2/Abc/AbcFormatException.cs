using System;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // Raised when ABC data cannot be parsed: truncated streams, counts the file is
    // too small to satisfy, out-of-range constant pool indices, or structures the
    // format does not allow. Carries the byte offset so a damaged file can be
    // located rather than merely reported.
    public sealed class AbcFormatException : Exception
    {
        public int Position { get; }

        public AbcFormatException(string message, int position)
            : base(message + " (at byte " + position + ")")
        {
            Position = position;
        }
    }
}
