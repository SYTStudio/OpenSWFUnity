using System;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // The flash.display / flash.events / flash.text surface.
    //
    // These classes are the contract every compiled AS3 movie is built against: a
    // document class extends Sprite or MovieClip, adds children, and listens for
    // events. They are implemented natively over Avm2DisplayObject, which is also
    // the record the renderer walks, so a script assignment to `x` moves what is
    // actually drawn.
    public sealed partial class Avm2Builtins
    {
        public const string DisplayPackage = "flash.display";
        public const string EventsPackage = "flash.events";
        public const string TextPackage = "flash.text";
        public const string GeomPackage = "flash.geom";

        public Avm2Class EventDispatcherClass { get; private set; }
        public Avm2Class EventClass { get; private set; }
        public Avm2Class MouseEventClass { get; private set; }
        public Avm2Class KeyboardEventClass { get; private set; }

        public Avm2Class DisplayObjectClass { get; private set; }
        public Avm2Class InteractiveObjectClass { get; private set; }
        public Avm2Class DisplayObjectContainerClass { get; private set; }
        public Avm2Class SpriteClass { get; private set; }
        public Avm2Class MovieClipClass { get; private set; }
        public Avm2Class ShapeClass { get; private set; }
        public Avm2Class BitmapClass { get; private set; }
        public Avm2Class StageClass { get; private set; }
        public Avm2Class TextFieldClass { get; private set; }

        // Supplied by the runtime so property reads that depend on the player - stage
        // size, mouse position, the frame count of a timeline - have somewhere to ask.
        public IAvm2DisplayHost DisplayHost { get; set; }

        private void RegisterFlashClasses()
        {
            DefineEventClasses();
            DefineDisplayClasses();
        }

        // ---- flash.events ------------------------------------------------------

        private void DefineEventClasses()
        {
            EventClass = DefinePackageClass(EventsPackage, "Event", ObjectClass);
            EventClass.NativeConstruct = args => new Avm2EventObject(
                EventClass,
                args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                args.Length > 1 && Avm2Convert.ToBoolean(args[1]),
                args.Length > 2 && Avm2Convert.ToBoolean(args[2]));

            DefineEventConstants(EventClass,
                ("ENTER_FRAME", "enterFrame"),
                ("EXIT_FRAME", "exitFrame"),
                ("FRAME_CONSTRUCTED", "frameConstructed"),
                ("ADDED", "added"),
                ("ADDED_TO_STAGE", "addedToStage"),
                ("REMOVED", "removed"),
                ("REMOVED_FROM_STAGE", "removedFromStage"),
                ("COMPLETE", "complete"),
                ("INIT", "init"),
                ("RESIZE", "resize"),
                ("CHANGE", "change"),
                ("ACTIVATE", "activate"),
                ("DEACTIVATE", "deactivate"));

            DefineEventInstanceMembers(EventClass);

            MouseEventClass = DefinePackageClass(EventsPackage, "MouseEvent", EventClass);
            MouseEventClass.NativeConstruct = args => new Avm2MouseEventObject(
                MouseEventClass,
                args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                args.Length <= 1 || Avm2Convert.ToBoolean(args[1]),
                args.Length > 2 && Avm2Convert.ToBoolean(args[2]));

            DefineEventConstants(MouseEventClass,
                ("CLICK", "click"),
                ("DOUBLE_CLICK", "doubleClick"),
                ("MOUSE_DOWN", "mouseDown"),
                ("MOUSE_UP", "mouseUp"),
                ("MOUSE_MOVE", "mouseMove"),
                ("MOUSE_OVER", "mouseOver"),
                ("MOUSE_OUT", "mouseOut"),
                ("ROLL_OVER", "rollOver"),
                ("ROLL_OUT", "rollOut"),
                ("MOUSE_WHEEL", "mouseWheel"));

            DefineGetter(MouseEventClass, "stageX",
                (r, a) => r is Avm2MouseEventObject m ? m.StageX : 0d);
            DefineGetter(MouseEventClass, "stageY",
                (r, a) => r is Avm2MouseEventObject m ? m.StageY : 0d);
            DefineGetter(MouseEventClass, "localX",
                (r, a) => r is Avm2MouseEventObject m ? m.LocalX : 0d);
            DefineGetter(MouseEventClass, "localY",
                (r, a) => r is Avm2MouseEventObject m ? m.LocalY : 0d);
            DefineGetter(MouseEventClass, "buttonDown",
                (r, a) => r is Avm2MouseEventObject m && m.ButtonDown);
            DefineGetter(MouseEventClass, "shiftKey",
                (r, a) => r is Avm2MouseEventObject m && m.ShiftKey);
            DefineGetter(MouseEventClass, "ctrlKey",
                (r, a) => r is Avm2MouseEventObject m && m.CtrlKey);
            DefineGetter(MouseEventClass, "altKey",
                (r, a) => r is Avm2MouseEventObject m && m.AltKey);

            KeyboardEventClass = DefinePackageClass(EventsPackage, "KeyboardEvent", EventClass);
            KeyboardEventClass.NativeConstruct = args => new Avm2KeyboardEventObject(
                KeyboardEventClass,
                args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                args.Length <= 1 || Avm2Convert.ToBoolean(args[1]),
                args.Length > 2 && Avm2Convert.ToBoolean(args[2]));

            DefineEventConstants(KeyboardEventClass,
                ("KEY_DOWN", "keyDown"),
                ("KEY_UP", "keyUp"));

            DefineGetter(KeyboardEventClass, "keyCode",
                (r, a) => r is Avm2KeyboardEventObject k ? k.KeyCode : 0);
            DefineGetter(KeyboardEventClass, "charCode",
                (r, a) => r is Avm2KeyboardEventObject k ? k.CharCode : 0);
            DefineGetter(KeyboardEventClass, "shiftKey",
                (r, a) => r is Avm2KeyboardEventObject k && k.ShiftKey);
            DefineGetter(KeyboardEventClass, "ctrlKey",
                (r, a) => r is Avm2KeyboardEventObject k && k.CtrlKey);
            DefineGetter(KeyboardEventClass, "altKey",
                (r, a) => r is Avm2KeyboardEventObject k && k.AltKey);

            EventDispatcherClass = DefinePackageClass(EventsPackage, "EventDispatcher", ObjectClass);
            EventDispatcherClass.NativeConstruct = args => new Avm2EventDispatcher(EventDispatcherClass);
            DefineDispatcherMembers(EventDispatcherClass);
        }

        private void DefineEventConstants(Avm2Class type, params (string name, string value)[] constants)
        {
            for (int i = 0; i < constants.Length; i++)
                DefineStaticConstant(type, constants[i].name, constants[i].value);
        }

        private void DefineEventInstanceMembers(Avm2Class type)
        {
            DefineGetter(type, "type", (r, a) => r is Avm2EventObject e ? e.EventType : string.Empty);
            DefineGetter(type, "bubbles", (r, a) => r is Avm2EventObject e && e.Bubbles);
            DefineGetter(type, "cancelable", (r, a) => r is Avm2EventObject e && e.Cancelable);
            DefineGetter(type, "target", (r, a) => r is Avm2EventObject e ? e.Target : null);
            DefineGetter(type, "currentTarget", (r, a) => r is Avm2EventObject e ? e.CurrentTarget : null);
            DefineGetter(type, "eventPhase",
                (r, a) => r is Avm2EventObject e ? e.EventPhase : Avm2EventObject.AtTarget);

            DefineMethod(type, "stopPropagation", (r, a) =>
            {
                if (r is Avm2EventObject e)
                    e.PropagationStopped = true;

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "stopImmediatePropagation", (r, a) =>
            {
                if (r is Avm2EventObject e)
                {
                    e.PropagationStopped = true;
                    e.ImmediatePropagationStopped = true;
                }

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "preventDefault", (r, a) =>
            {
                if (r is Avm2EventObject e && e.Cancelable)
                    e.DefaultPrevented = true;

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "isDefaultPrevented",
                (r, a) => r is Avm2EventObject e && e.DefaultPrevented);

            DefineMethod(type, "toString", (r, a) => r.ToString());
        }

        private void DefineDispatcherMembers(Avm2Class type)
        {
            DefineMethod(type, "addEventListener", (receiver, args) =>
            {
                if (!(receiver is Avm2EventDispatcher dispatcher) || args.Length < 2)
                    return Avm2Undefined.Value;

                dispatcher.AddListener(
                    Avm2Convert.ToString(args[0]),
                    args[1] as Avm2Function,
                    args.Length > 2 && Avm2Convert.ToBoolean(args[2]),
                    args.Length > 3 ? Avm2Convert.ToInt32(args[3]) : 0);

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "removeEventListener", (receiver, args) =>
            {
                if (!(receiver is Avm2EventDispatcher dispatcher) || args.Length < 2)
                    return Avm2Undefined.Value;

                dispatcher.RemoveListener(
                    Avm2Convert.ToString(args[0]),
                    args[1] as Avm2Function,
                    args.Length > 2 && Avm2Convert.ToBoolean(args[2]));

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "hasEventListener", (receiver, args) =>
                receiver is Avm2EventDispatcher dispatcher && args.Length > 0 &&
                dispatcher.HasListener(Avm2Convert.ToString(args[0])));

            // willTrigger also considers ancestors, since a capture listener above
            // this object would still fire for an event aimed at it.
            DefineMethod(type, "willTrigger", (receiver, args) =>
            {
                if (!(receiver is Avm2EventDispatcher dispatcher) || args.Length == 0)
                    return false;

                string eventType = Avm2Convert.ToString(args[0]);

                if (dispatcher.HasListener(eventType))
                    return true;

                Avm2DisplayObject node = (receiver as Avm2DisplayObject)?.Parent;
                int guard = 0;

                while (node != null && guard++ < 1024)
                {
                    if (node.HasListener(eventType))
                        return true;

                    node = node.Parent;
                }

                return false;
            });

            DefineMethod(type, "dispatchEvent", (receiver, args) =>
            {
                if (args.Length == 0 || !(args[0] is Avm2EventObject e))
                    return false;

                return DisplayHost != null && DisplayHost.DispatchEvent(receiver, e);
            });
        }

        // ---- flash.display -----------------------------------------------------

        private void DefineDisplayClasses()
        {
            DisplayObjectClass = DefinePackageClass(DisplayPackage, "DisplayObject", EventDispatcherClass);
            DefineDisplayObjectMembers(DisplayObjectClass);

            InteractiveObjectClass =
                DefinePackageClass(DisplayPackage, "InteractiveObject", DisplayObjectClass);
            DefineGetter(InteractiveObjectClass, "mouseEnabled",
                (r, a) => true, (r, a) => Avm2Undefined.Value);

            DisplayObjectContainerClass =
                DefinePackageClass(DisplayPackage, "DisplayObjectContainer", InteractiveObjectClass);
            DefineContainerMembers(DisplayObjectContainerClass);

            SpriteClass = DefinePackageClass(DisplayPackage, "Sprite", DisplayObjectContainerClass);
            SpriteClass.NativeConstruct = args => new Avm2DisplayObject(SpriteClass);

            MovieClipClass = DefinePackageClass(DisplayPackage, "MovieClip", SpriteClass);
            MovieClipClass.NativeConstruct = args => new Avm2DisplayObject(MovieClipClass);
            DefineMovieClipMembers(MovieClipClass);

            ShapeClass = DefinePackageClass(DisplayPackage, "Shape", DisplayObjectClass);
            ShapeClass.NativeConstruct = args => new Avm2DisplayObject(ShapeClass);

            BitmapClass = DefinePackageClass(DisplayPackage, "Bitmap", DisplayObjectClass);
            BitmapClass.NativeConstruct = args => new Avm2DisplayObject(BitmapClass);

            StageClass = DefinePackageClass(DisplayPackage, "Stage", DisplayObjectContainerClass);
            DefineStageMembers(StageClass);

            TextFieldClass = DefinePackageClass(TextPackage, "TextField", InteractiveObjectClass);
            TextFieldClass.NativeConstruct = args => new Avm2DisplayObject(TextFieldClass);
            DefineTextFieldMembers(TextFieldClass);
        }

        private static Avm2DisplayObject AsDisplay(object receiver)
        {
            return receiver as Avm2DisplayObject;
        }

        private void DefineDisplayObjectMembers(Avm2Class type)
        {
            DefineGetter(type, "x",
                (r, a) => AsDisplay(r)?.X ?? 0d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.X = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "y",
                (r, a) => AsDisplay(r)?.Y ?? 0d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.Y = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "scaleX",
                (r, a) => AsDisplay(r)?.ScaleX ?? 1d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.ScaleX = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "scaleY",
                (r, a) => AsDisplay(r)?.ScaleY ?? 1d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.ScaleY = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "rotation",
                (r, a) => AsDisplay(r)?.Rotation ?? 0d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.Rotation = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "alpha",
                (r, a) => AsDisplay(r)?.Alpha ?? 1d,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.Alpha = Avm2Convert.ToNumber(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "visible",
                (r, a) => AsDisplay(r)?.Visible ?? true,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.Visible = Avm2Convert.ToBoolean(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "name",
                (r, a) => AsDisplay(r)?.Name ?? string.Empty,
                (r, a) => { Avm2DisplayObject d = AsDisplay(r); if (d != null && a.Length > 0) d.Name = Avm2Convert.ToString(a[0]); return Avm2Undefined.Value; });

            DefineGetter(type, "parent", (r, a) => (object)AsDisplay(r)?.Parent);
            DefineGetter(type, "stage", (r, a) => (object)AsDisplay(r)?.FindStage());
            DefineGetter(type, "root", (r, a) => (object)AsDisplay(r)?.FindRoot());

            // width and height are the object's drawn extent after its own scale, so
            // they come from the host, which owns character bounds.
            DefineGetter(type, "width",
                (r, a) => DisplayHost != null && AsDisplay(r) != null
                    ? DisplayHost.GetWidth(AsDisplay(r))
                    : 0d,
                (r, a) =>
                {
                    Avm2DisplayObject d = AsDisplay(r);

                    if (d != null && a.Length > 0)
                        DisplayHost?.SetWidth(d, Avm2Convert.ToNumber(a[0]));

                    return Avm2Undefined.Value;
                });

            DefineGetter(type, "height",
                (r, a) => DisplayHost != null && AsDisplay(r) != null
                    ? DisplayHost.GetHeight(AsDisplay(r))
                    : 0d,
                (r, a) =>
                {
                    Avm2DisplayObject d = AsDisplay(r);

                    if (d != null && a.Length > 0)
                        DisplayHost?.SetHeight(d, Avm2Convert.ToNumber(a[0]));

                    return Avm2Undefined.Value;
                });

            DefineGetter(type, "mouseX", (r, a) => DisplayHost?.GetMouseX(AsDisplay(r)) ?? 0d);
            DefineGetter(type, "mouseY", (r, a) => DisplayHost?.GetMouseY(AsDisplay(r)) ?? 0d);
        }

        private void DefineContainerMembers(Avm2Class type)
        {
            DefineGetter(type, "numChildren", (r, a) => AsDisplay(r)?.NumChildren ?? 0);

            DefineMethod(type, "addChild", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);
                Avm2DisplayObject child = args.Length > 0 ? AsDisplay(args[0]) : null;

                if (parent == null || child == null)
                    return null;

                // A container cannot contain one of its own ancestors; AS3 raises an
                // ArgumentError rather than building a cycle.
                if (child.Contains(parent))
                    throw new Avm2ThrownException(MakeArgumentError(
                        "An object cannot be added as a child of itself or its own descendant."));

                bool wasOnStage = child.IsOnStage;
                parent.AddChild(child);
                DisplayHost?.NotifyChildAdded(child, wasOnStage);
                return child;
            });

            DefineMethod(type, "addChildAt", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);
                Avm2DisplayObject child = args.Length > 0 ? AsDisplay(args[0]) : null;

                if (parent == null || child == null)
                    return null;

                if (child.Contains(parent))
                    throw new Avm2ThrownException(MakeArgumentError(
                        "An object cannot be added as a child of itself or its own descendant."));

                bool wasOnStage = child.IsOnStage;
                parent.AddChild(child, args.Length > 1 ? Avm2Convert.ToInt32(args[1]) : -1);
                DisplayHost?.NotifyChildAdded(child, wasOnStage);
                return child;
            });

            DefineMethod(type, "removeChild", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);
                Avm2DisplayObject child = args.Length > 0 ? AsDisplay(args[0]) : null;

                if (parent == null || child == null)
                    return null;

                bool wasOnStage = child.IsOnStage;

                if (!parent.RemoveChild(child))
                    return null;

                DisplayHost?.NotifyChildRemoved(child, wasOnStage);
                return child;
            });

            DefineMethod(type, "removeChildAt", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);

                if (parent == null || args.Length == 0)
                    return null;

                int index = Avm2Convert.ToInt32(args[0]);
                Avm2DisplayObject child = parent.GetChildAt(index);

                if (child == null)
                    return null;

                bool wasOnStage = child.IsOnStage;
                parent.RemoveChildAt(index);
                DisplayHost?.NotifyChildRemoved(child, wasOnStage);
                return child;
            });

            DefineMethod(type, "getChildAt", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);
                int index = args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : -1;
                Avm2DisplayObject child = parent?.GetChildAt(index);

                if (child == null)
                {
                    throw new Avm2ThrownException(MakeRangeError(
                        "The supplied index is out of bounds."));
                }

                return child;
            });

            DefineMethod(type, "getChildByName", (receiver, args) =>
                (object)AsDisplay(receiver)?.GetChildByName(
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty));

            DefineMethod(type, "getChildIndex", (receiver, args) =>
                AsDisplay(receiver)?.GetChildIndex(args.Length > 0 ? AsDisplay(args[0]) : null) ?? -1);

            DefineMethod(type, "contains", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);
                Avm2DisplayObject candidate = args.Length > 0 ? AsDisplay(args[0]) : null;
                return parent != null && candidate != null && parent.Contains(candidate);
            });

            DefineMethod(type, "removeChildren", (receiver, args) =>
            {
                Avm2DisplayObject parent = AsDisplay(receiver);

                if (parent == null)
                    return Avm2Undefined.Value;

                while (parent.NumChildren > 0)
                {
                    Avm2DisplayObject child = parent.GetChildAt(parent.NumChildren - 1);
                    bool wasOnStage = child.IsOnStage;
                    parent.RemoveChildAt(parent.NumChildren - 1);
                    DisplayHost?.NotifyChildRemoved(child, wasOnStage);
                }

                return Avm2Undefined.Value;
            });
        }

        private void DefineMovieClipMembers(Avm2Class type)
        {
            DefineGetter(type, "currentFrame", (r, a) => AsDisplay(r)?.CurrentFrame ?? 1);
            DefineGetter(type, "totalFrames", (r, a) => AsDisplay(r)?.TotalFrames ?? 1);
            DefineGetter(type, "framesLoaded", (r, a) => AsDisplay(r)?.TotalFrames ?? 1);

            DefineMethod(type, "play", (r, a) =>
            {
                Avm2DisplayObject d = AsDisplay(r);

                if (d != null)
                {
                    d.IsPlaying = true;
                    DisplayHost?.NotifyPlayStateChanged(d);
                }

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "stop", (r, a) =>
            {
                Avm2DisplayObject d = AsDisplay(r);

                if (d != null)
                {
                    d.IsPlaying = false;
                    DisplayHost?.NotifyPlayStateChanged(d);
                }

                return Avm2Undefined.Value;
            });

            DefineMethod(type, "gotoAndPlay", (r, a) => Goto(r, a, true));
            DefineMethod(type, "gotoAndStop", (r, a) => Goto(r, a, false));

            DefineMethod(type, "nextFrame", (r, a) => Step(r, 1));
            DefineMethod(type, "prevFrame", (r, a) => Step(r, -1));
        }

        private object Goto(object receiver, object[] args, bool play)
        {
            Avm2DisplayObject clip = AsDisplay(receiver);

            if (clip == null || args.Length == 0)
                return Avm2Undefined.Value;

            // A frame label resolves through the host, which owns the timeline; a
            // number is used directly.
            int frame = args[0] is string label
                ? (DisplayHost?.ResolveFrameLabel(clip, label) ?? 1)
                : Avm2Convert.ToInt32(args[0]);

            clip.CurrentFrame = Math.Max(1, Math.Min(Math.Max(1, clip.TotalFrames), frame));
            clip.IsPlaying = play;
            DisplayHost?.NotifyFrameChanged(clip);
            return Avm2Undefined.Value;
        }

        private object Step(object receiver, int direction)
        {
            Avm2DisplayObject clip = AsDisplay(receiver);

            if (clip == null)
                return Avm2Undefined.Value;

            clip.CurrentFrame = Math.Max(1,
                Math.Min(Math.Max(1, clip.TotalFrames), clip.CurrentFrame + direction));
            clip.IsPlaying = false;
            DisplayHost?.NotifyFrameChanged(clip);
            return Avm2Undefined.Value;
        }

        private void DefineStageMembers(Avm2Class type)
        {
            DefineGetter(type, "stageWidth", (r, a) => DisplayHost?.StageWidth ?? 0d);
            DefineGetter(type, "stageHeight", (r, a) => DisplayHost?.StageHeight ?? 0d);
            DefineGetter(type, "frameRate", (r, a) => DisplayHost?.FrameRate ?? 30d);
            DefineGetter(type, "scaleMode", (r, a) => "showAll", (r, a) => Avm2Undefined.Value);
            DefineGetter(type, "align", (r, a) => string.Empty, (r, a) => Avm2Undefined.Value);
            DefineGetter(type, "quality", (r, a) => DisplayHost?.Quality ?? "HIGH",
                (r, a) => Avm2Undefined.Value);

            // Assigning focus is how content aims the keyboard at a specific object;
            // reading it back reports whatever currently holds it.
            DefineGetter(type, "focus",
                (r, a) => DisplayHost?.GetFocus(),
                (r, a) =>
                {
                    DisplayHost?.SetFocus(a.Length > 0 ? a[0] as Avm2DisplayObject : null);
                    return Avm2Undefined.Value;
                });
        }

        // TextField's text is kept as a dynamic property so it survives without a
        // dedicated backing field; rendering AS3-authored text is not implemented, and
        // the host reports that once when a field is actually placed.
        private void DefineTextFieldMembers(Avm2Class type)
        {
            Avm2QName textKey = Avm2QName.Public("__text");

            DefineGetter(type, "text",
                (r, a) => r is Avm2Object o && o.TryGetDynamic(textKey, out object v)
                    ? v
                    : string.Empty,
                (r, a) =>
                {
                    if (r is Avm2Object o && a.Length > 0)
                    {
                        o.SetDynamic(textKey, Avm2Convert.ToString(a[0]));
                        DisplayHost?.NotifyTextChanged(AsDisplay(r));
                    }

                    return Avm2Undefined.Value;
                });

            DefineGetter(type, "length",
                (r, a) => r is Avm2Object o && o.TryGetDynamic(textKey, out object v)
                    ? Avm2Convert.ToString(v).Length
                    : 0);

            DefineMethod(type, "appendText", (r, a) =>
            {
                if (r is Avm2Object o && a.Length > 0)
                {
                    o.TryGetDynamic(textKey, out object existing);
                    o.SetDynamic(textKey,
                        Avm2Convert.ToString(existing) + Avm2Convert.ToString(a[0]));
                    DisplayHost?.NotifyTextChanged(AsDisplay(r));
                }

                return Avm2Undefined.Value;
            });
        }

        private object MakeArgumentError(string message)
        {
            return MakeNamedError("ArgumentError", message);
        }

        private object MakeRangeError(string message)
        {
            return MakeNamedError("RangeError", message);
        }

        private object MakeNamedError(string className, string message)
        {
            if (domain.TryGetGlobal(Avm2QName.Public(className), out object type) &&
                type is Avm2Class errorClass && errorClass.NativeConstruct != null)
            {
                return errorClass.NativeConstruct(new object[] { message });
            }

            return message;
        }
    }

    // What the display classes need from the player. Implemented by the runtime,
    // which forwards to SwfPlayer; kept as an interface so the AVM2 assembly does not
    // depend on the player directly.
    public interface IAvm2DisplayHost
    {
        double StageWidth { get; }
        double StageHeight { get; }
        double FrameRate { get; }
        string Quality { get; }

        bool DispatchEvent(object target, Avm2EventObject e);

        double GetWidth(Avm2DisplayObject target);
        double GetHeight(Avm2DisplayObject target);
        void SetWidth(Avm2DisplayObject target, double value);
        void SetHeight(Avm2DisplayObject target, double value);
        double GetMouseX(Avm2DisplayObject target);
        double GetMouseY(Avm2DisplayObject target);

        int ResolveFrameLabel(Avm2DisplayObject target, string label);

        object GetFocus();
        void SetFocus(Avm2DisplayObject target);

        void NotifyChildAdded(Avm2DisplayObject child, bool wasOnStage);
        void NotifyChildRemoved(Avm2DisplayObject child, bool wasOnStage);
        void NotifyFrameChanged(Avm2DisplayObject clip);
        void NotifyPlayStateChanged(Avm2DisplayObject clip);
        void NotifyTextChanged(Avm2DisplayObject field);
    }
}
