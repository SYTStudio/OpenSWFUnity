using System;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // AS3 distinguishes undefined from null, and both from any object, so undefined
    // needs a representation that cannot collide with a real value. A singleton
    // sentinel keeps that distinction while letting every AVM2 value travel as
    // System.Object.
    public sealed class Avm2Undefined
    {
        public static readonly Avm2Undefined Value = new Avm2Undefined();

        private Avm2Undefined()
        {
        }

        public override string ToString()
        {
            return "undefined";
        }
    }

    // A fully resolved property name: one namespace URI plus one local name.
    //
    // Multinames in the constant pool may carry a whole set of candidate
    // namespaces; resolution reduces them to exactly one of these, which is what
    // trait tables and dynamic property dictionaries are keyed by. Public names use
    // the empty URI, matching the ABC encoding.
    public readonly struct Avm2QName : IEquatable<Avm2QName>
    {
        public readonly string Uri;
        public readonly string Local;

        public Avm2QName(string uri, string local)
        {
            Uri = uri ?? string.Empty;
            Local = local ?? string.Empty;
        }

        public static Avm2QName Public(string local)
        {
            return new Avm2QName(string.Empty, local);
        }

        public bool IsPublic => Uri.Length == 0;

        public bool Equals(Avm2QName other)
        {
            return string.Equals(Local, other.Local, StringComparison.Ordinal) &&
                   string.Equals(Uri, other.Uri, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is Avm2QName other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                // Local name dominates: namespaces repeat heavily across a program
                // while local names are what actually distinguish members.
                return (Local.GetHashCode() * 397) ^ Uri.GetHashCode();
            }
        }

        public override string ToString()
        {
            return Uri.Length == 0 ? Local : Uri + "::" + Local;
        }
    }

    // A namespace as a runtime value, produced by pushnamespace and consumed by the
    // runtime-qualified name forms.
    public sealed class Avm2Namespace
    {
        public readonly string Uri;
        public readonly bool IsPrivate;

        public Avm2Namespace(string uri, bool isPrivate = false)
        {
            Uri = uri ?? string.Empty;
            IsPrivate = isPrivate;
        }

        public static readonly Avm2Namespace PublicNamespace = new Avm2Namespace(string.Empty);

        public override string ToString()
        {
            return Uri.Length == 0 ? "public" : Uri;
        }
    }
}
