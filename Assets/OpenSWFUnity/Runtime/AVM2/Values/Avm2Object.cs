using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // Base AVM2 instance.
    //
    // AS3 splits storage in two: trait slots, which are fixed by the class and
    // addressed by index, and dynamic properties, which only exist on non-sealed
    // classes. Slots are an array because slot ids are dense and assigned at class
    // build time; the dictionary is allocated lazily so a sealed instance costs
    // nothing for storage it can never use.
    public class Avm2Object
    {
        private Dictionary<Avm2QName, object> dynamicProperties;

        public Avm2Class Class { get; internal set; }
        public object[] Slots { get; internal set; }

        public Avm2Object()
        {
        }

        public Avm2Object(Avm2Class type)
        {
            Class = type;

            int slotCount = type != null ? type.InstanceSlotCount : 0;
            Slots = slotCount > 0 ? new object[slotCount] : System.Array.Empty<object>();

            for (int i = 0; i < Slots.Length; i++)
                Slots[i] = Avm2Undefined.Value;
        }

        // Whether this instance may carry properties beyond its class's traits.
        // Named apart from Avm2Class.IsDynamic, which states the same thing about a
        // class's instances rather than about the class object itself.
        public bool AllowsDynamicProperties => Class == null || Class.IsDynamic;

        public bool TryGetDynamic(Avm2QName name, out object value)
        {
            if (dynamicProperties != null)
                return dynamicProperties.TryGetValue(name, out value);

            value = null;
            return false;
        }

        public void SetDynamic(Avm2QName name, object value)
        {
            dynamicProperties ??= new Dictionary<Avm2QName, object>();
            dynamicProperties[name] = value;
        }

        public bool DeleteDynamic(Avm2QName name)
        {
            return dynamicProperties != null && dynamicProperties.Remove(name);
        }

        public bool HasDynamic(Avm2QName name)
        {
            return dynamicProperties != null && dynamicProperties.ContainsKey(name);
        }

        public int DynamicCount => dynamicProperties != null ? dynamicProperties.Count : 0;

        // Enumeration order for for-in. Dynamic properties only, which matches AS3:
        // trait members are not enumerable.
        public IEnumerable<Avm2QName> DynamicNames =>
            dynamicProperties != null
                ? (IEnumerable<Avm2QName>)dynamicProperties.Keys
                : System.Array.Empty<Avm2QName>();

        public object GetSlot(int slotId)
        {
            int index = slotId - 1;
            return Slots != null && index >= 0 && index < Slots.Length
                ? Slots[index]
                : Avm2Undefined.Value;
        }

        public bool SetSlot(int slotId, object value)
        {
            int index = slotId - 1;

            if (Slots == null || index < 0 || index >= Slots.Length)
                return false;

            Slots[index] = value;
            return true;
        }

        public override string ToString()
        {
            return Class != null ? "[object " + Class.Name.Local + "]" : "[object Object]";
        }
    }
}
