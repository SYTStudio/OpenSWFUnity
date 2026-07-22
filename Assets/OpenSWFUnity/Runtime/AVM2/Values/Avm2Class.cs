using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Abc;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // How a resolved trait should be reached at runtime.
    public enum Avm2BindingKind
    {
        Slot,
        Constant,
        Method,
        Getter,
        Setter,
        GetterSetter,
        Class
    }

    // A class member after resolution: what it is and where its storage or code
    // lives. Building these once per class turns every later property access into a
    // dictionary hit instead of a walk over the ABC trait list.
    public sealed class Avm2Binding
    {
        public Avm2QName Name;
        public Avm2BindingKind Kind;
        public int SlotId;
        public AbcMethodInfo Method;
        public AbcMethodInfo Getter;
        public AbcMethodInfo Setter;
        public Avm2Class DeclaringClass;
        public object ConstantValue;
        public bool IsStatic;

        // Set when the member is provided by the host rather than by bytecode.
        public Avm2Function NativeFunction;
        public Avm2NativeCall NativeGetter;
        public Avm2NativeCall NativeSetter;

        public bool IsAccessor =>
            Kind == Avm2BindingKind.Getter ||
            Kind == Avm2BindingKind.Setter ||
            Kind == Avm2BindingKind.GetterSetter;

        public override string ToString()
        {
            return Kind + " " + Name;
        }
    }

    // A class: its instance shape, its static side, and its link to a supertype.
    //
    // A class is also an object - static members live on it - so it derives from
    // Avm2Object and its own Slots array holds the static slots.
    public sealed class Avm2Class : Avm2Object
    {
        public Avm2QName Name;
        public Avm2Class Super;
        public AbcInstanceInfo Instance;
        public AbcClassInfo Static;
        public AbcMethodInfo Constructor;
        public AbcMethodInfo StaticInitialiser;

        public bool IsDynamic;
        public bool IsInterface;
        public bool IsSealed;

        // Instance members, flattened over the inheritance chain so a lookup never
        // walks supertypes at call time.
        public readonly Dictionary<Avm2QName, Avm2Binding> InstanceBindings =
            new Dictionary<Avm2QName, Avm2Binding>();

        public readonly Dictionary<Avm2QName, Avm2Binding> StaticBindings =
            new Dictionary<Avm2QName, Avm2Binding>();

        public int InstanceSlotCount;
        public int StaticSlotCount;

        // Builds an instance of a host-provided class. Native classes have no
        // iinit bytecode, so construction runs through this instead.
        public System.Func<object[], object> NativeConstruct;

        // Set for classes the host provides rather than the ABC file - Object, Array,
        // Math and friends. Their behaviour comes from native callbacks instead of
        // bytecode.
        public bool IsNative;

        // Interfaces this class claims, by resolved name, used by is/as.
        public readonly List<Avm2QName> Interfaces = new List<Avm2QName>();

        // The scope chain captured where the class was created. Methods of the class
        // are entered with this as their outer scope, which is how AS3 code inside a
        // method can still see the class and its package.
        public object[] CapturedScope;

        public bool StaticInitialiserRun;

        public Avm2Class(Avm2QName name)
        {
            Name = name;
        }

        public bool TryFindInstanceBinding(Avm2QName name, out Avm2Binding binding)
        {
            return InstanceBindings.TryGetValue(name, out binding);
        }

        public bool TryFindStaticBinding(Avm2QName name, out Avm2Binding binding)
        {
            return StaticBindings.TryGetValue(name, out binding);
        }

        public bool IsSubclassOf(Avm2Class other)
        {
            if (other == null)
                return false;

            Avm2Class current = this;
            int guard = 0;

            while (current != null && guard++ < 256)
            {
                if (ReferenceEquals(current, other))
                    return true;

                for (int i = 0; i < current.Interfaces.Count; i++)
                {
                    if (current.Interfaces[i].Equals(other.Name))
                        return true;
                }

                current = current.Super;
            }

            return false;
        }

        public override string ToString()
        {
            return "[class " + Name.Local + "]";
        }
    }
}
