using System;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Abc;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // Display tree ownership and event dispatch.
    //
    // The runtime owns the Stage and the root, creates the document class instance,
    // and implements the three-phase event model. The player supplies the parts that
    // depend on rendering and input through IAvm2DisplayHost.
    public sealed partial class Avm2Runtime
    {
        public Avm2DisplayObject Stage { get; private set; }

        // The object keyboard events are aimed at. Null means the stage, which is
        // where Flash sends keys when nothing has taken focus.
        public Avm2DisplayObject FocusObject { get; set; }

        // Modifier state captured with the input sample that produced the current
        // event, so a handler sees the keys that were down at that moment.
        public bool ShiftDown { get; set; }
        public bool ControlDown { get; set; }
        public bool AltDown { get; set; }
        public Avm2DisplayObject Root { get; private set; }
        public string DocumentClassName { get; private set; }
        public bool DocumentClassConstructed { get; private set; }

        // Reused across dispatches so a per-frame event costs no allocation for its
        // propagation path.
        private readonly List<Avm2DisplayObject> propagationPath = new List<Avm2DisplayObject>();
        private readonly List<Avm2DisplayObject> dispatchBuffer = new List<Avm2DisplayObject>();
        private readonly List<Avm2Function> handlerBuffer = new List<Avm2Function>();

        public int DispatchedEventCount { get; private set; }

        // ---- stage construction ----------------------------------------------

        public void CreateStage(int rootTotalFrames)
        {
            if (Stage != null)
                return;

            Stage = new Avm2DisplayObject(builtins.StageClass)
            {
                IsStage = true,
                Name = "root"
            };

            // The root is a MovieClip so a document class that extends MovieClip can
            // replace it, and so timeline control from script has somewhere to land.
            Root = new Avm2DisplayObject(builtins.MovieClipClass)
            {
                IsRoot = true,
                Name = "root1",
                TotalFrames = Math.Max(1, rootTotalFrames)
            };

            Stage.AddChild(Root);
            domain.SetGlobal(Avm2QName.Public("stage"), Stage);
        }

        // The SymbolClass tag binds character 0 - the main timeline - to the movie's
        // document class. Instantiating it is how a real AS3 movie starts.
        public bool TryConstructDocumentClass(string className, int rootTotalFrames)
        {
            if (string.IsNullOrEmpty(className) || DocumentClassConstructed)
                return false;

            CreateStage(rootTotalFrames);
            DocumentClassName = className;

            if (!TryResolveClassByName(className, out Avm2Class documentClass))
            {
                Diagnostics.ReportUnsupportedStructure(
                    "document class '" + className + "' was not found among the loaded definitions");
                return false;
            }

            // The document class must be a display object for the stage to hold it.
            if (!documentClass.IsSubclassOf(builtins.DisplayObjectClass))
            {
                Diagnostics.ReportUnsupportedStructure(
                    "document class '" + className + "' does not extend DisplayObject");
                return false;
            }

            try
            {
                object instance = interpreter.Construct(documentClass, Array.Empty<object>());

                if (!(instance is Avm2DisplayObject display))
                    return false;

                display.IsRoot = true;
                display.Name = "root1";
                display.TotalFrames = Math.Max(1, rootTotalFrames);

                // Replaces the placeholder root: the document instance is the root.
                Stage.RemoveChild(Root);
                Root = display;
                Stage.AddChild(display);

                DocumentClassConstructed = true;
                DispatchLifecycleEvent(display, "addedToStage", false);
                DispatchLifecycleEvent(display, "added", true);
                return true;
            }
            catch (Avm2ThrownException thrown)
            {
                FailedScriptCount++;
                Warning?.Invoke(
                    "Document class '" + className + "' threw during construction: " +
                    Avm2Convert.ToString(thrown.Value));
            }
            catch (Avm2AbortException abort)
            {
                FailedScriptCount++;
                Warning?.Invoke("Document class '" + className + "' was stopped: " + abort.Message);
            }
            catch (Avm2UnsupportedException unsupported)
            {
                FailedScriptCount++;
                Diagnostics.ReportUnsupportedOpCode(unsupported.OpCode, "document class '" + className + "'");
            }
            catch (Exception exception)
            {
                FailedScriptCount++;
                Warning?.Invoke(
                    "Document class '" + className + "' failed: " +
                    exception.GetType().Name + ": " + exception.Message);
            }

            return false;
        }

        // SymbolClass writes fully qualified names with dots ("com.example.Main"),
        // while definitions are keyed by package and local name.
        public bool TryResolveClassByName(string className, out Avm2Class type)
        {
            type = null;

            if (string.IsNullOrEmpty(className))
                return false;

            string package = string.Empty;
            string local = className;
            int separator = className.LastIndexOf(':');

            if (separator > 0)
            {
                // Already in "package::Local" form.
                package = className.Substring(0, separator).TrimEnd(':');
                local = className.Substring(separator + 1);
            }
            else
            {
                separator = className.LastIndexOf('.');

                if (separator > 0)
                {
                    package = className.Substring(0, separator);
                    local = className.Substring(separator + 1);
                }
            }

            if (domain.TryGetGlobal(new Avm2QName(package, local), out object value) &&
                value is Avm2Class resolved)
            {
                type = resolved;
                return true;
            }

            // Some tools emit the class under the public namespace regardless of its
            // declared package.
            if (package.Length > 0 &&
                domain.TryGetGlobal(Avm2QName.Public(local), out object fallback) &&
                fallback is Avm2Class fallbackClass)
            {
                type = fallbackClass;
                return true;
            }

            return false;
        }

        // ---- event construction ----------------------------------------------

        public Avm2EventObject CreateEvent(string type, bool bubbles = false, bool cancelable = false)
        {
            return new Avm2EventObject(builtins.EventClass, type, bubbles, cancelable);
        }

        public Avm2MouseEventObject CreateMouseEvent(
            string type,
            double stageX,
            double stageY,
            bool buttonDown,
            double localX,
            double localY
        )
        {
            return new Avm2MouseEventObject(builtins.MouseEventClass, type, true, false)
            {
                StageX = stageX,
                StageY = stageY,
                LocalX = localX,
                LocalY = localY,
                ButtonDown = buttonDown,
                ShiftKey = ShiftDown,
                CtrlKey = ControlDown,
                AltKey = AltDown
            };
        }

        public Avm2KeyboardEventObject CreateKeyboardEvent(string type, int keyCode, int charCode)
        {
            return new Avm2KeyboardEventObject(builtins.KeyboardEventClass, type, true, false)
            {
                KeyCode = keyCode,
                CharCode = charCode,
                ShiftKey = ShiftDown,
                CtrlKey = ControlDown,
                AltKey = AltDown
            };
        }

        // Keys go to the focused object and bubble from there, falling back to the
        // stage when nothing holds focus. A focused object detached from the display
        // tree is treated as having lost focus rather than swallowing input.
        public Avm2DisplayObject ResolveKeyboardTarget()
        {
            if (FocusObject != null && FocusObject.IsOnStage)
                return FocusObject;

            FocusObject = null;
            return Stage;
        }

        // ---- dispatch ---------------------------------------------------------

        // Full three-phase propagation: capture from the stage down to the target's
        // parent, then the target itself, then bubbling back up when the event
        // bubbles. Non-display targets have no tree, so they only see the target
        // phase.
        // A handler is free to dispatch further events, so dispatch is re-entrant.
        // Without a ceiling, two objects that dispatch to each other - or one that
        // re-dispatches the event it just received - would recurse until the CLR
        // stack gave out, which is not an exception any catch block can recover from.
        private const int MaxDispatchDepth = 32;
        private int dispatchDepth;
        private bool dispatchOverflowReported;

        public int AbandonedDispatchCount { get; private set; }

        public bool DispatchEvent(object target, Avm2EventObject e)
        {
            if (e == null || target == null)
                return false;

            if (dispatchDepth >= MaxDispatchDepth)
            {
                AbandonedDispatchCount++;

                if (!dispatchOverflowReported)
                {
                    dispatchOverflowReported = true;
                    Warning?.Invoke(
                        "AVM2 event dispatch nested more than " + MaxDispatchDepth +
                        " levels deep while dispatching '" + e.EventType +
                        "'. The chain was abandoned; a listener is most likely " +
                        "re-dispatching the event it is handling.");
                }

                return false;
            }

            dispatchDepth++;

            try
            {
                return DispatchEventCore(target, e);
            }
            finally
            {
                dispatchDepth--;
            }
        }

        private bool DispatchEventCore(object target, Avm2EventObject e)
        {
            DispatchedEventCount++;
            e.Target = target;
            e.PropagationStopped = false;
            e.ImmediatePropagationStopped = false;

            if (!(target is Avm2DisplayObject display))
            {
                if (target is Avm2EventDispatcher plain)
                {
                    e.EventPhase = Avm2EventObject.AtTarget;
                    InvokeListeners(plain, e, false);
                }

                return !e.DefaultPrevented;
            }

            display.BuildPropagationPath(propagationPath);

            e.EventPhase = Avm2EventObject.CapturingPhase;

            for (int i = 0; i < propagationPath.Count && !e.PropagationStopped; i++)
                InvokeListeners(propagationPath[i], e, true);

            if (!e.PropagationStopped)
            {
                e.EventPhase = Avm2EventObject.AtTarget;

                // At the target both listener sets fire, capture first.
                InvokeListeners(display, e, true);

                if (!e.ImmediatePropagationStopped)
                    InvokeListeners(display, e, false);
            }

            if (e.Bubbles && !e.PropagationStopped)
            {
                e.EventPhase = Avm2EventObject.BubblingPhase;

                for (int i = propagationPath.Count - 1; i >= 0 && !e.PropagationStopped; i--)
                    InvokeListeners(propagationPath[i], e, false);
            }

            return !e.DefaultPrevented;
        }

        private void InvokeListeners(Avm2EventDispatcher node, Avm2EventObject e, bool capture)
        {
            List<Avm2Listener> listeners = node.GetListeners(e.EventType, capture);

            if (listeners == null || listeners.Count == 0)
                return;

            // Snapshotted because a handler may add or remove listeners while the
            // event is still being delivered.
            handlerBuffer.Clear();

            for (int i = 0; i < listeners.Count; i++)
                handlerBuffer.Add(listeners[i].Handler);

            e.CurrentTarget = node;

            for (int i = 0; i < handlerBuffer.Count; i++)
            {
                if (e.ImmediatePropagationStopped)
                    return;

                CallHandlerSafely(handlerBuffer[i], e);
            }
        }

        // One faulty listener must not stop the others, nor escape into the player's
        // update loop.
        private void CallHandlerSafely(Avm2Function handler, Avm2EventObject e)
        {
            if (handler == null)
                return;

            try
            {
                interpreter.CallValue(handler, handler.BoundReceiver, new object[] { e });
            }
            catch (Avm2ThrownException thrown)
            {
                Warning?.Invoke(
                    "Uncaught ActionScript 3 exception in a '" + e.EventType + "' listener: " +
                    Avm2Convert.ToString(thrown.Value));
            }
            catch (Avm2AbortException abort)
            {
                Warning?.Invoke(
                    "AVM2 execution stopped in a '" + e.EventType + "' listener: " + abort.Message);
            }
            catch (Avm2UnsupportedException unsupported)
            {
                Diagnostics.ReportUnsupportedOpCode(
                    unsupported.OpCode, "'" + e.EventType + "' listener");
            }
            catch (Exception exception)
            {
                Warning?.Invoke(
                    "AVM2 internal error in a '" + e.EventType + "' listener: " +
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        public void DispatchLifecycleEvent(Avm2DisplayObject target, string type, bool bubbles)
        {
            if (target == null || Stage == null)
                return;

            DispatchEvent(target, CreateEvent(type, bubbles));
        }

        // ---- broadcast --------------------------------------------------------

        // enterFrame is a broadcast event: every display object receives it directly,
        // with no capture or bubble phase. Only nodes that actually listen are
        // visited, so an idle tree costs one walk and no dispatches.
        public void BroadcastFrameEvent(string type)
        {
            if (Stage == null)
                return;

            dispatchBuffer.Clear();
            CollectListeners(Stage, type, dispatchBuffer);

            for (int i = 0; i < dispatchBuffer.Count; i++)
            {
                Avm2DisplayObject node = dispatchBuffer[i];
                Avm2EventObject e = CreateEvent(type, false);
                e.Target = node;
                e.CurrentTarget = node;
                e.EventPhase = Avm2EventObject.AtTarget;
                DispatchedEventCount++;
                InvokeListeners(node, e, false);
            }
        }

        private void CollectListeners(Avm2DisplayObject node, string type, List<Avm2DisplayObject> into)
        {
            if (node == null || into.Count > 8192)
                return;

            if (node.HasAnyListeners && node.HasListener(type))
                into.Add(node);

            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = 0; i < children.Count; i++)
                CollectListeners(children[i], type, into);
        }

        // Walks the tree so the player can render it without knowing its shape.
        public void CollectDisplayTree(List<Avm2DisplayObject> into)
        {
            into.Clear();

            if (Stage != null)
                CollectAll(Stage, into);
        }

        private static void CollectAll(Avm2DisplayObject node, List<Avm2DisplayObject> into)
        {
            if (node == null || into.Count > 16384)
                return;

            into.Add(node);
            IReadOnlyList<Avm2DisplayObject> children = node.Children;

            for (int i = 0; i < children.Count; i++)
                CollectAll(children[i], into);
        }
    }
}
