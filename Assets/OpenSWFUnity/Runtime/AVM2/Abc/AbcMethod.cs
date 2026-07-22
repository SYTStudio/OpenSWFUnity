using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    public static class AbcMethodFlags
    {
        public const byte NeedArguments = 0x01;
        public const byte NeedActivation = 0x02;
        public const byte NeedRest = 0x04;
        public const byte HasOptional = 0x08;
        public const byte IgnoreRest = 0x10;
        public const byte Native = 0x20;
        public const byte SetsDxns = 0x40;
        public const byte HasParamNames = 0x80;
    }

    // A default value for an optional parameter. The kind selects which constant
    // pool the index refers to, so the pair must be kept together.
    public struct AbcOptionalDetail
    {
        public int ValueIndex;
        public byte ValueKind;
    }

    public sealed class AbcMethodInfo
    {
        public int Index;
        public int[] ParameterTypeIndices;
        public int ReturnTypeIndex;
        public int NameIndex;
        public byte Flags;
        public AbcOptionalDetail[] Optionals;
        public int[] ParameterNameIndices;
        public string Name;

        // The file this method was declared in. Constant pool indices in its body
        // are only meaningful against that file's pool, so the link must travel
        // with the method.
        public AbcFile OwnerFile;

        // Set once the corresponding method_body is read; native and interface
        // methods legitimately never get one.
        public AbcMethodBody Body;

        public int ParameterCount =>
            ParameterTypeIndices != null ? ParameterTypeIndices.Length : 0;

        public bool NeedsActivation => (Flags & AbcMethodFlags.NeedActivation) != 0;
        public bool NeedsRest => (Flags & AbcMethodFlags.NeedRest) != 0;
        public bool NeedsArguments => (Flags & AbcMethodFlags.NeedArguments) != 0;
        public bool IsNative => (Flags & AbcMethodFlags.Native) != 0;
        public bool HasOptional => (Flags & AbcMethodFlags.HasOptional) != 0;

        public override string ToString()
        {
            return "method#" + Index +
                   " '" + (string.IsNullOrEmpty(Name) ? "<anonymous>" : Name) + "'" +
                   " params=" + ParameterCount +
                   (Body != null ? " code=" + Body.CodeLength : " <no body>");
        }
    }

    public sealed class AbcExceptionInfo
    {
        public int From;
        public int To;
        public int Target;
        public int ExceptionTypeIndex;
        public int VariableNameIndex;

        public override string ToString()
        {
            return "try[" + From + "," + To + ") -> " + Target;
        }
    }

    public sealed class AbcMethodBody
    {
        public int MethodIndex;
        public int MaxStack;
        public int LocalCount;
        public int InitScopeDepth;
        public int MaxScopeDepth;

        // Raw bytecode. Decoding into instructions is deliberately deferred to the
        // bytecode layer so parsing an ABC file stays a pure structural pass.
        public byte[] Code;
        public AbcExceptionInfo[] Exceptions;
        public List<AbcTrait> Traits;

        public int CodeLength => Code != null ? Code.Length : 0;

        public override string ToString()
        {
            return "body(method#" + MethodIndex + ") code=" + CodeLength +
                   " maxStack=" + MaxStack + " locals=" + LocalCount +
                   " handlers=" + (Exceptions != null ? Exceptions.Length : 0);
        }
    }
}
