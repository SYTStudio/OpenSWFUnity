using System;
using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // An AS3 Event instance.
    //
    // The mutable phase fields (target, currentTarget, eventPhase) are written by the
    // dispatcher as the event travels, which is why a single event object is reused
    // across the whole propagation path rather than copied per node.
    public class Avm2EventObject : Avm2Object
    {
        public const int CapturingPhase = 1;
        public const int AtTarget = 2;
        public const int BubblingPhase = 3;

        public string EventType = string.Empty;
        public bool Bubbles;
        public bool Cancelable;

        public object Target;
        public object CurrentTarget;
        public int EventPhase = AtTarget;

        public bool PropagationStopped;
        public bool ImmediatePropagationStopped;
        public bool DefaultPrevented;

        public Avm2EventObject()
        {
        }

        public Avm2EventObject(Avm2Class type, string eventType, bool bubbles, bool cancelable)
            : base(type)
        {
            EventType = eventType ?? string.Empty;
            Bubbles = bubbles;
            Cancelable = cancelable;
        }

        public override string ToString()
        {
            return "[Event type=" + EventType + "]";
        }
    }

    // MouseEvent adds pointer position and button state.
    public sealed class Avm2MouseEventObject : Avm2EventObject
    {
        public double StageX;
        public double StageY;
        public double LocalX;
        public double LocalY;
        public bool ButtonDown;
        public bool ShiftKey;
        public bool CtrlKey;
        public bool AltKey;

        public Avm2MouseEventObject(Avm2Class type, string eventType, bool bubbles, bool cancelable)
            : base(type, eventType, bubbles, cancelable)
        {
        }
    }

    public sealed class Avm2KeyboardEventObject : Avm2EventObject
    {
        public int KeyCode;
        public int CharCode;
        public bool ShiftKey;
        public bool CtrlKey;
        public bool AltKey;

        public Avm2KeyboardEventObject(Avm2Class type, string eventType, bool bubbles, bool cancelable)
            : base(type, eventType, bubbles, cancelable)
        {
        }
    }

    internal struct Avm2Listener
    {
        public Avm2Function Handler;
        public int Priority;
    }

    // The AS3 EventDispatcher.
    //
    // Capture and bubble listeners are kept in separate tables because the two phases
    // consult different sets: a capture listener never fires in the bubble phase and
    // vice versa. Both are lazily allocated, so an object that never listens for
    // anything carries no per-instance cost.
    public class Avm2EventDispatcher : Avm2Object
    {
        private Dictionary<string, List<Avm2Listener>> bubbleListeners;
        private Dictionary<string, List<Avm2Listener>> captureListeners;

        public Avm2EventDispatcher()
        {
        }

        public Avm2EventDispatcher(Avm2Class type) : base(type)
        {
        }

        public bool AddListener(string type, Avm2Function handler, bool useCapture, int priority)
        {
            if (string.IsNullOrEmpty(type) || handler == null)
                return false;

            Dictionary<string, List<Avm2Listener>> table = useCapture
                ? (captureListeners ??= new Dictionary<string, List<Avm2Listener>>(StringComparer.Ordinal))
                : (bubbleListeners ??= new Dictionary<string, List<Avm2Listener>>(StringComparer.Ordinal));

            if (!table.TryGetValue(type, out List<Avm2Listener> list))
            {
                list = new List<Avm2Listener>();
                table[type] = list;
            }

            // Registering the same function twice for the same phase is a no-op in
            // AS3, not a second subscription.
            for (int i = 0; i < list.Count; i++)
            {
                if (SameHandler(list[i].Handler, handler))
                    return false;
            }

            list.Add(new Avm2Listener { Handler = handler, Priority = priority });

            // Higher priority runs first; equal priorities keep insertion order, which
            // List.Sort does not guarantee, so insert-in-place instead of sorting.
            for (int i = list.Count - 1; i > 0; i--)
            {
                if (list[i].Priority <= list[i - 1].Priority)
                    break;

                Avm2Listener swap = list[i];
                list[i] = list[i - 1];
                list[i - 1] = swap;
            }

            return true;
        }

        public bool RemoveListener(string type, Avm2Function handler, bool useCapture)
        {
            Dictionary<string, List<Avm2Listener>> table =
                useCapture ? captureListeners : bubbleListeners;

            if (table == null || string.IsNullOrEmpty(type) ||
                !table.TryGetValue(type, out List<Avm2Listener> list))
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (!SameHandler(list[i].Handler, handler))
                    continue;

                list.RemoveAt(i);

                if (list.Count == 0)
                    table.Remove(type);

                return true;
            }

            return false;
        }

        // Two Avm2Function values denote the same listener when they wrap the same
        // method, even if they are distinct objects - which they are whenever a
        // bound method is re-extracted from an object.
        private static bool SameHandler(Avm2Function left, Avm2Function right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return false;

            if (left.Method != null && ReferenceEquals(left.Method, right.Method))
                return ReferenceEquals(left.BoundReceiver, right.BoundReceiver);

            return left.Native != null && ReferenceEquals(left.Native, right.Native);
        }

        public bool HasListener(string type)
        {
            return (bubbleListeners != null && bubbleListeners.ContainsKey(type)) ||
                   (captureListeners != null && captureListeners.ContainsKey(type));
        }

        internal List<Avm2Listener> GetListeners(string type, bool capture)
        {
            Dictionary<string, List<Avm2Listener>> table =
                capture ? captureListeners : bubbleListeners;

            if (table == null || !table.TryGetValue(type, out List<Avm2Listener> list))
                return null;

            return list;
        }

        public bool HasAnyListeners => bubbleListeners != null || captureListeners != null;
    }
}
