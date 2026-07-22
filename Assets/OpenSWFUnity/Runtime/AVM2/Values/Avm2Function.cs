using System;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Abc;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // Signature for a member the host implements in C# rather than in bytecode.
    public delegate object Avm2NativeCall(object receiver, object[] arguments);

    // A callable value.
    //
    // Covers three cases behind one type: a bytecode method (Method + captured
    // scope), a host builtin (Native), and a bound method extracted from an object.
    // Keeping them unified means the interpreter's call path has exactly one shape.
    public sealed class Avm2Function : Avm2Object
    {
        public AbcMethodInfo Method;
        public Avm2NativeCall Native;
        public string NativeName;

        // Scope chain in force where the function was created. A method entered
        // through this closure sees these scopes beneath its own, which is what makes
        // package-level and class-level names visible inside a method body.
        public object[] CapturedScope;

        // Receiver bound at extraction time, so `var f = obj.method; f()` keeps obj.
        public object BoundReceiver;
        public bool HasBoundReceiver;

        // The class this method was declared on, carried so a `super` call inside it
        // resolves against the declaring type rather than the receiver's type.
        public Avm2Class DeclaringClass;

        public Avm2Function()
        {
        }

        public static Avm2Function FromMethod(
            AbcMethodInfo method,
            object[] capturedScope,
            Avm2Class declaringClass = null
        )
        {
            return new Avm2Function
            {
                Method = method,
                CapturedScope = capturedScope,
                DeclaringClass = declaringClass
            };
        }

        public static Avm2Function FromNative(string name, Avm2NativeCall call)
        {
            return new Avm2Function
            {
                NativeName = name,
                Native = call
            };
        }

        public Avm2Function Bind(object receiver)
        {
            return new Avm2Function
            {
                Method = Method,
                Native = Native,
                NativeName = NativeName,
                CapturedScope = CapturedScope,
                DeclaringClass = DeclaringClass,
                BoundReceiver = receiver,
                HasBoundReceiver = true
            };
        }

        public bool IsNative => Native != null;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(NativeName))
                    return NativeName;

                if (Method != null && !string.IsNullOrEmpty(Method.Name))
                    return Method.Name;

                return "<anonymous>";
            }
        }

        public override string ToString()
        {
            return "[function " + DisplayName + "]";
        }
    }

    // AS3 Array. Dense storage plus the dynamic-property behaviour arrays inherit,
    // so `a[0]` and `a.someName` both work through the normal property path.
    public sealed class Avm2Array : Avm2Object
    {
        public readonly List<object> Items = new List<object>();

        public Avm2Array()
        {
        }

        public Avm2Array(int capacity)
        {
            if (capacity > 0)
                Items.Capacity = Math.Min(capacity, 1 << 20);
        }

        public int Length => Items.Count;

        public object GetIndex(int index)
        {
            return index >= 0 && index < Items.Count ? Items[index] : Avm2Undefined.Value;
        }

        // Writing past the end grows the array with undefined, matching AS3 rather
        // than throwing.
        public void SetIndex(int index, object value)
        {
            if (index < 0)
                return;

            while (Items.Count <= index)
                Items.Add(Avm2Undefined.Value);

            Items[index] = value;
        }

        public void SetLength(int length)
        {
            if (length < 0)
                return;

            while (Items.Count > length)
                Items.RemoveAt(Items.Count - 1);

            while (Items.Count < length)
                Items.Add(Avm2Undefined.Value);
        }

        public override string ToString()
        {
            return "[object Array]";
        }
    }
}
