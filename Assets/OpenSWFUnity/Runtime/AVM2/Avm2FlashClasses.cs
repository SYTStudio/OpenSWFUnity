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
        public const string SystemPackage = "flash.system";
        public const string NetPackage = "flash.net";
        public const string FiltersPackage = "flash.filters";
        public const string UiPackage = "flash.ui";

        public Avm2Class EventDispatcherClass { get; private set; }
        public Avm2Class EventClass { get; private set; }
        public Avm2Class MouseEventClass { get; private set; }
        public Avm2Class KeyboardEventClass { get; private set; }

        public Avm2Class DisplayObjectClass { get; private set; }
        public Avm2Class InteractiveObjectClass { get; private set; }
        public Avm2Class DisplayObjectContainerClass { get; private set; }
        public Avm2Class GraphicsClass { get; private set; }
        public Avm2Class SpriteClass { get; private set; }
        public Avm2Class MovieClipClass { get; private set; }
        public Avm2Class ShapeClass { get; private set; }
        public Avm2Class BitmapClass { get; private set; }
        public Avm2Class BitmapDataClass { get; private set; }
        public Avm2Class StageClass { get; private set; }
        public Avm2Class LoaderClass { get; private set; }
        public Avm2Class LoaderInfoClass { get; private set; }
        public Avm2Class TextFieldClass { get; private set; }

        // Supplied by the runtime so property reads that depend on the player - stage
        // size, mouse position, the frame count of a timeline - have somewhere to ask.
        public IAvm2DisplayHost DisplayHost { get; set; }

        private void RegisterFlashClasses()
        {
            DefineUtilityClasses();
            DefineSystemClasses();
            DefineEventClasses();
            DefineUiClasses();
            DefineNetworkClasses();
            DefineGeometryClasses();
            DefineFilterClasses();
            DefineDisplayClasses();
        }

        private void DefineFilterClasses()
        {
            Avm2Class bitmapFilter =
                DefinePackageClass(FiltersPackage, "BitmapFilter", ObjectClass, dynamic: true);
            bitmapFilter.NativeConstruct = args => new Avm2Object(bitmapFilter);

            string[] names =
            {
                "BlurFilter", "DropShadowFilter", "GlowFilter", "ColorMatrixFilter",
                "BevelFilter", "GradientBevelFilter", "GradientGlowFilter",
                "ConvolutionFilter", "DisplacementMapFilter", "ShaderFilter"
            };

            for (int i = 0; i < names.Length; i++)
            {
                Avm2Class filter =
                    DefinePackageClass(FiltersPackage, names[i], bitmapFilter, dynamic: true);
                filter.NativeConstruct = args =>
                {
                    Avm2Object value = new Avm2Object(filter);
                    for (int argument = 0; argument < args.Length; argument++)
                        value.SetDynamic(
                            Avm2QName.Public("__arg" + argument), args[argument]);
                    return value;
                };
            }
        }

        private void DefineUiClasses()
        {
            Avm2Class contextMenu =
                DefinePackageClass(UiPackage, "ContextMenu", EventDispatcherClass, dynamic: true);
            contextMenu.NativeConstruct = args =>
            {
                Avm2EventDispatcher menu = new Avm2EventDispatcher(contextMenu);
                menu.SetDynamic(
                    Avm2QName.Public("__customItems"),
                    new Avm2Array { Class = ArrayClass });
                return menu;
            };
            DefineDispatcherMembers(contextMenu);
            DefineGetter(contextMenu, "customItems",
                (receiver, args) =>
                {
                    if (!(receiver is Avm2Object menu))
                        return null;

                    Avm2QName key = Avm2QName.Public("__customItems");
                    if (menu.TryGetDynamic(key, out object items))
                        return items;

                    Avm2Array created = new Avm2Array { Class = ArrayClass };
                    menu.SetDynamic(key, created);
                    return created;
                },
                (receiver, args) =>
                {
                    if (receiver is Avm2Object menu)
                        menu.SetDynamic(
                            Avm2QName.Public("__customItems"),
                            args.Length > 0 ? args[0] : new Avm2Array { Class = ArrayClass });
                    return Avm2Undefined.Value;
                });
            DefineMethod(contextMenu, "hideBuiltInItems",
                (receiver, args) => Avm2Undefined.Value);
            DefineMethod(contextMenu, "clone", (receiver, args) =>
            {
                Avm2EventDispatcher clone = new Avm2EventDispatcher(contextMenu);
                Avm2Array copied = new Avm2Array { Class = ArrayClass };
                if (receiver is Avm2Object source &&
                    source.TryGetDynamic(
                        Avm2QName.Public("__customItems"), out object sourceItems) &&
                    sourceItems is Avm2Array array)
                {
                    copied.Items.AddRange(array.Items);
                }
                clone.SetDynamic(Avm2QName.Public("__customItems"), copied);
                return clone;
            });

            Avm2Class contextMenuItem =
                DefinePackageClass(
                    UiPackage, "ContextMenuItem", EventDispatcherClass, dynamic: true);
            contextMenuItem.NativeConstruct = args =>
            {
                Avm2EventDispatcher item = new Avm2EventDispatcher(contextMenuItem);
                item.SetDynamic(Avm2QName.Public("__caption"),
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty);
                item.SetDynamic(Avm2QName.Public("__separatorBefore"),
                    args.Length > 1 && Avm2Convert.ToBoolean(args[1]));
                item.SetDynamic(Avm2QName.Public("__enabled"),
                    args.Length <= 2 || Avm2Convert.ToBoolean(args[2]));
                item.SetDynamic(Avm2QName.Public("__visible"),
                    args.Length <= 3 || Avm2Convert.ToBoolean(args[3]));
                return item;
            };
            DefineDispatcherMembers(contextMenuItem);
            DefineDynamicMember(contextMenuItem, "caption", string.Empty);
            DefineDynamicMember(contextMenuItem, "separatorBefore", false);
            DefineDynamicMember(contextMenuItem, "enabled", true);
            DefineDynamicMember(contextMenuItem, "visible", true);
            DefineMethod(contextMenuItem, "clone", (receiver, args) =>
                contextMenuItem.NativeConstruct(new object[]
                {
                    ReadDynamic(receiver, "__caption", string.Empty),
                    ReadDynamic(receiver, "__separatorBefore", false),
                    ReadDynamic(receiver, "__enabled", true),
                    ReadDynamic(receiver, "__visible", true)
                }));

            Avm2Class keyboard = DefinePackageClass(UiPackage, "Keyboard", ObjectClass);
            DefineStaticConstant(keyboard, "BACKSPACE", 8u);
            DefineStaticConstant(keyboard, "TAB", 9u);
            DefineStaticConstant(keyboard, "ENTER", 13u);
            DefineStaticConstant(keyboard, "SHIFT", 16u);
            DefineStaticConstant(keyboard, "CONTROL", 17u);
            DefineStaticConstant(keyboard, "ESCAPE", 27u);
            DefineStaticConstant(keyboard, "SPACE", 32u);
            DefineStaticConstant(keyboard, "PAGE_UP", 33u);
            DefineStaticConstant(keyboard, "PAGE_DOWN", 34u);
            DefineStaticConstant(keyboard, "END", 35u);
            DefineStaticConstant(keyboard, "HOME", 36u);
            DefineStaticConstant(keyboard, "LEFT", 37u);
            DefineStaticConstant(keyboard, "UP", 38u);
            DefineStaticConstant(keyboard, "RIGHT", 39u);
            DefineStaticConstant(keyboard, "DOWN", 40u);
            DefineStaticConstant(keyboard, "INSERT", 45u);
            DefineStaticConstant(keyboard, "DELETE", 46u);
            for (int key = 0; key <= 9; key++)
                DefineStaticConstant(keyboard, "NUMBER_" + key, (uint)(48 + key));
            for (int key = 0; key < 26; key++)
                DefineStaticConstant(
                    keyboard, ((char)('A' + key)).ToString(), (uint)(65 + key));

            Avm2Class mouse = DefinePackageClass(UiPackage, "Mouse", ObjectClass);
            DefineMethod(mouse, "hide",
                (receiver, args) => Avm2Undefined.Value, isStatic: true);
            DefineMethod(mouse, "show",
                (receiver, args) => Avm2Undefined.Value, isStatic: true);
            DefineGetter(mouse, "cursor",
                (receiver, args) => "auto",
                (receiver, args) => Avm2Undefined.Value,
                isStatic: true);
            DefineGetter(mouse, "supportsCursor", (receiver, args) => false, isStatic: true);
            DefineGetter(
                mouse, "supportsNativeCursor", (receiver, args) => false, isStatic: true);

            Avm2Class contextMenuEvent =
                DefinePackageClass(
                    EventsPackage, "ContextMenuEvent", EventClass, dynamic: true);
            contextMenuEvent.NativeConstruct = args => new Avm2EventObject(
                contextMenuEvent,
                args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                args.Length <= 1 || Avm2Convert.ToBoolean(args[1]),
                args.Length > 2 && Avm2Convert.ToBoolean(args[2]));
            DefineEventConstants(contextMenuEvent,
                ("MENU_ITEM_SELECT", "menuItemSelect"),
                ("MENU_SELECT", "menuSelect"));
            DefineEventInstanceMembers(contextMenuEvent);
        }

        private static object ReadDynamic(object receiver, string key, object fallback)
        {
            return receiver is Avm2Object obj &&
                   obj.TryGetDynamic(Avm2QName.Public(key), out object value)
                ? value
                : fallback;
        }

        private void DefineNetworkClasses()
        {
            Avm2Class variables =
                DefinePackageClass(NetPackage, "URLVariables", ObjectClass, dynamic: true);
            variables.NativeConstruct = args =>
            {
                Avm2Object value = new Avm2Object(variables);
                if (args.Length > 0)
                {
                    string[] pairs = Avm2Convert.ToString(args[0]).Split('&');
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        int separator = pairs[i].IndexOf('=');
                        string key = separator >= 0 ? pairs[i].Substring(0, separator) : pairs[i];
                        string text = separator >= 0 ? pairs[i].Substring(separator + 1) : string.Empty;
                        if (key.Length > 0)
                            value.SetDynamic(Avm2QName.Public(Uri.UnescapeDataString(key)),
                                Uri.UnescapeDataString(text.Replace("+", " ")));
                    }
                }
                return value;
            };

            Avm2Class request =
                DefinePackageClass(NetPackage, "URLRequest", ObjectClass, dynamic: true);
            request.NativeConstruct = args =>
            {
                Avm2Object value = new Avm2Object(request);
                value.SetDynamic(Avm2QName.Public("url"),
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty);
                return value;
            };
            DefineDynamicMember(request, "url", string.Empty);
            DefineDynamicMember(request, "method", "GET");
            DefineDynamicMember(request, "data", null);
            DefineDynamicMember(request, "contentType", "application/x-www-form-urlencoded");
            DefineDynamicMember(request, "requestHeaders", null);

            Avm2Class requestMethod =
                DefinePackageClass(NetPackage, "URLRequestMethod", ObjectClass);
            DefineStaticConstant(requestMethod, "GET", "GET");
            DefineStaticConstant(requestMethod, "POST", "POST");

            Avm2Class loader =
                DefinePackageClass(NetPackage, "URLLoader", EventDispatcherClass, dynamic: true);
            loader.NativeConstruct = args => new Avm2EventDispatcher(loader);
            DefineDispatcherMembers(loader);
            DefineDynamicMember(loader, "data", string.Empty);
            DefineDynamicMember(loader, "dataFormat", "text");
            DefineGetter(loader, "bytesLoaded", (r, a) => 0u);
            DefineGetter(loader, "bytesTotal", (r, a) => 0u);
            DefineMethod(loader, "load", (r, a) => Avm2Undefined.Value);
            DefineMethod(loader, "close", (r, a) => Avm2Undefined.Value);

            Avm2Class dataFormat =
                DefinePackageClass(NetPackage, "URLLoaderDataFormat", ObjectClass);
            DefineStaticConstant(dataFormat, "BINARY", "binary");
            DefineStaticConstant(dataFormat, "TEXT", "text");
            DefineStaticConstant(dataFormat, "VARIABLES", "variables");

            domain.SetGlobal(new Avm2QName(NetPackage, "sendToURL"),
                Avm2Function.FromNative("sendToURL",
                    (receiver, args) => Avm2Undefined.Value));
            domain.SetGlobal(new Avm2QName(NetPackage, "navigateToURL"),
                Avm2Function.FromNative("navigateToURL",
                    (receiver, args) => Avm2Undefined.Value));
        }

        private void DefineDynamicMember(Avm2Class type, string name, object fallback)
        {
            Avm2QName key = Avm2QName.Public("__" + name);
            DefineGetter(type, name,
                (receiver, args) =>
                    receiver is Avm2Object obj && obj.TryGetDynamic(key, out object value)
                        ? value
                        : fallback,
                (receiver, args) =>
                {
                    if (receiver is Avm2Object obj)
                        obj.SetDynamic(key, args.Length > 0 ? args[0] : fallback);
                    return Avm2Undefined.Value;
                });
        }

        private void DefineSystemClasses()
        {
            Avm2Class security =
                DefinePackageClass(SystemPackage, "Security", ObjectClass);
            DefineMethod(security, "allowDomain",
                (receiver, args) => Avm2Undefined.Value, isStatic: true);
            DefineMethod(security, "allowInsecureDomain",
                (receiver, args) => Avm2Undefined.Value, isStatic: true);
            DefineStaticConstant(security, "REMOTE", "remote");
            DefineStaticConstant(security, "LOCAL_TRUSTED", "localTrusted");
            DefineStaticConstant(security, "LOCAL_WITH_FILE", "localWithFile");
            DefineStaticConstant(security, "LOCAL_WITH_NETWORK", "localWithNetwork");
            DefineStaticConstant(security, "sandboxType", "localTrusted");

            Avm2Class capabilities =
                DefinePackageClass(SystemPackage, "Capabilities", ObjectClass);
            DefineStaticConstant(capabilities, "version", "WIN 32,0,0,0");
            DefineStaticConstant(capabilities, "playerType", "StandAlone");
            DefineStaticConstant(capabilities, "os", "Windows");
            DefineStaticConstant(capabilities, "language", "en");
            DefineStaticConstant(capabilities, "isDebugger", false);
            DefineStaticConstant(capabilities, "screenResolutionX", 1920d);
            DefineStaticConstant(capabilities, "screenResolutionY", 1080d);
            DefineStaticConstant(capabilities, "screenDPI", 96d);

            Avm2Class applicationDomain =
                DefinePackageClass(SystemPackage, "ApplicationDomain", ObjectClass, dynamic: true);
            applicationDomain.NativeConstruct = args => new Avm2Object(applicationDomain);
            Avm2Object currentDomain = new Avm2Object(applicationDomain);
            applicationDomain.SetDynamic(Avm2QName.Public("currentDomain"), currentDomain);
            DefineGetter(applicationDomain, "parentDomain", (r, a) => null);
            DefineMethod(applicationDomain, "hasDefinition", (r, args) =>
            {
                if (args.Length == 0)
                    return false;
                Avm2QName name = QualifiedName(Avm2Convert.ToString(args[0]));
                return domain.HasDefinition(name);
            });
            DefineMethod(applicationDomain, "getDefinition", (r, args) =>
            {
                if (args.Length == 0)
                    return Avm2Undefined.Value;
                Avm2QName name = QualifiedName(Avm2Convert.ToString(args[0]));
                return domain.TryGetGlobal(name, out object value)
                    ? value
                    : Avm2Undefined.Value;
            });

            Avm2Class securityDomain =
                DefinePackageClass(SystemPackage, "SecurityDomain", ObjectClass, dynamic: true);
            securityDomain.NativeConstruct = args => new Avm2Object(securityDomain);
            securityDomain.SetDynamic(
                Avm2QName.Public("currentDomain"), new Avm2Object(securityDomain));

            Avm2Class loaderContext =
                DefinePackageClass(SystemPackage, "LoaderContext", ObjectClass, dynamic: true);
            loaderContext.NativeConstruct = args =>
            {
                Avm2Object context = new Avm2Object(loaderContext);
                context.SetDynamic(Avm2QName.Public("__checkPolicyFile"),
                    args.Length > 0 && Avm2Convert.ToBoolean(args[0]));
                context.SetDynamic(Avm2QName.Public("__applicationDomain"),
                    args.Length > 1 ? args[1] : null);
                context.SetDynamic(Avm2QName.Public("__securityDomain"),
                    args.Length > 2 ? args[2] : null);
                return context;
            };
            DefineDynamicMember(loaderContext, "checkPolicyFile", false);
            DefineDynamicMember(loaderContext, "applicationDomain", null);
            DefineDynamicMember(loaderContext, "securityDomain", null);
            DefineDynamicMember(loaderContext, "allowCodeImport", false);
        }

        private static Avm2QName QualifiedName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Avm2QName.Public(string.Empty);

            int separator = text.LastIndexOf("::", StringComparison.Ordinal);
            if (separator >= 0)
                return new Avm2QName(
                    text.Substring(0, separator),
                    text.Substring(separator + 2));

            separator = text.LastIndexOf('.');
            return separator >= 0
                ? new Avm2QName(text.Substring(0, separator), text.Substring(separator + 1))
                : Avm2QName.Public(text);
        }

        private void DefineGeometryClasses()
        {
            Avm2Class point = DefinePackageClass(GeomPackage, "Point", ObjectClass, dynamic: true);
            point.NativeConstruct = args =>
            {
                Avm2Object value = new Avm2Object(point);
                value.SetDynamic(Avm2QName.Public("x"),
                    args.Length > 0 ? Avm2Convert.ToNumber(args[0]) : 0d);
                value.SetDynamic(Avm2QName.Public("y"),
                    args.Length > 1 ? Avm2Convert.ToNumber(args[1]) : 0d);
                return value;
            };
            DefineGeometryNumber(point, "x");
            DefineGeometryNumber(point, "y");

            Avm2Class rectangle =
                DefinePackageClass(GeomPackage, "Rectangle", ObjectClass, dynamic: true);
            rectangle.NativeConstruct = args =>
            {
                Avm2Object value = new Avm2Object(rectangle);
                string[] names = { "x", "y", "width", "height" };
                for (int i = 0; i < names.Length; i++)
                    value.SetDynamic(Avm2QName.Public(names[i]),
                        args.Length > i ? Avm2Convert.ToNumber(args[i]) : 0d);
                return value;
            };
            DefineGeometryNumber(rectangle, "x");
            DefineGeometryNumber(rectangle, "y");
            DefineGeometryNumber(rectangle, "width");
            DefineGeometryNumber(rectangle, "height");

            Avm2Class matrix =
                DefinePackageClass(GeomPackage, "Matrix", ObjectClass, dynamic: true);
            matrix.NativeConstruct = args =>
            {
                Avm2Object value = new Avm2Object(matrix);
                string[] names = { "a", "b", "c", "d", "tx", "ty" };
                double[] defaults = { 1d, 0d, 0d, 1d, 0d, 0d };
                for (int i = 0; i < names.Length; i++)
                    value.SetDynamic(Avm2QName.Public(names[i]),
                        args.Length > i ? Avm2Convert.ToNumber(args[i]) : defaults[i]);
                return value;
            };
            DefineGeometryNumber(matrix, "a", 1d);
            DefineGeometryNumber(matrix, "b");
            DefineGeometryNumber(matrix, "c");
            DefineGeometryNumber(matrix, "d", 1d);
            DefineGeometryNumber(matrix, "tx");
            DefineGeometryNumber(matrix, "ty");
            DefineMethod(matrix, "identity", (receiver, args) =>
            {
                SetGeometryNumber(receiver, "a", 1d);
                SetGeometryNumber(receiver, "b", 0d);
                SetGeometryNumber(receiver, "c", 0d);
                SetGeometryNumber(receiver, "d", 1d);
                SetGeometryNumber(receiver, "tx", 0d);
                SetGeometryNumber(receiver, "ty", 0d);
                return Avm2Undefined.Value;
            });
            DefineMethod(matrix, "translate", (receiver, args) =>
            {
                SetGeometryNumber(receiver, "tx", GetGeometryNumber(receiver, "tx") +
                    (args.Length > 0 ? Avm2Convert.ToNumber(args[0]) : 0d));
                SetGeometryNumber(receiver, "ty", GetGeometryNumber(receiver, "ty") +
                    (args.Length > 1 ? Avm2Convert.ToNumber(args[1]) : 0d));
                return Avm2Undefined.Value;
            });
            DefineMethod(matrix, "scale", (receiver, args) =>
            {
                double sx = args.Length > 0 ? Avm2Convert.ToNumber(args[0]) : 1d;
                double sy = args.Length > 1 ? Avm2Convert.ToNumber(args[1]) : 1d;
                SetGeometryNumber(receiver, "a", GetGeometryNumber(receiver, "a", 1d) * sx);
                SetGeometryNumber(receiver, "b", GetGeometryNumber(receiver, "b") * sy);
                SetGeometryNumber(receiver, "c", GetGeometryNumber(receiver, "c") * sx);
                SetGeometryNumber(receiver, "d", GetGeometryNumber(receiver, "d", 1d) * sy);
                SetGeometryNumber(receiver, "tx", GetGeometryNumber(receiver, "tx") * sx);
                SetGeometryNumber(receiver, "ty", GetGeometryNumber(receiver, "ty") * sy);
                return Avm2Undefined.Value;
            });
        }

        private void DefineGeometryNumber(Avm2Class type, string name, double fallback = 0d)
        {
            DefineGetter(type, name,
                (receiver, args) => GetGeometryNumber(receiver, name, fallback),
                (receiver, args) =>
                {
                    SetGeometryNumber(receiver, name,
                        args.Length > 0 ? Avm2Convert.ToNumber(args[0]) : fallback);
                    return Avm2Undefined.Value;
                });
        }

        private static double GetGeometryNumber(object receiver, string name, double fallback = 0d)
        {
            if (receiver is Avm2Object obj &&
                obj.TryGetDynamic(Avm2QName.Public(name), out object value))
            {
                return Avm2Convert.ToNumber(value);
            }

            return fallback;
        }

        private static void SetGeometryNumber(object receiver, string name, double value)
        {
            if (receiver is Avm2Object obj)
                obj.SetDynamic(Avm2QName.Public(name), value);
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

            Avm2Class progressEvent =
                DefinePackageClass(EventsPackage, "ProgressEvent", EventClass, dynamic: true);
            progressEvent.NativeConstruct = args =>
            {
                Avm2EventObject e = new Avm2EventObject(
                    progressEvent,
                    args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                    args.Length > 1 && Avm2Convert.ToBoolean(args[1]),
                    args.Length > 2 && Avm2Convert.ToBoolean(args[2]));
                e.SetDynamic(Avm2QName.Public("__bytesLoaded"),
                    args.Length > 3 ? Avm2Convert.ToNumber(args[3]) : 0d);
                e.SetDynamic(Avm2QName.Public("__bytesTotal"),
                    args.Length > 4 ? Avm2Convert.ToNumber(args[4]) : 0d);
                return e;
            };
            DefineEventConstants(progressEvent,
                ("PROGRESS", "progress"),
                ("SOCKET_DATA", "socketData"));
            DefineEventInstanceMembers(progressEvent);
            DefineGetter(progressEvent, "bytesLoaded", (r, a) =>
                r is Avm2Object o &&
                o.TryGetDynamic(Avm2QName.Public("__bytesLoaded"), out object loaded)
                    ? loaded
                    : 0d);
            DefineGetter(progressEvent, "bytesTotal", (r, a) =>
                r is Avm2Object o &&
                o.TryGetDynamic(Avm2QName.Public("__bytesTotal"), out object total)
                    ? total
                    : 0d);

            DefineSimpleEventSubclass("IOErrorEvent", ("IO_ERROR", "ioError"));
            DefineSimpleEventSubclass("SecurityErrorEvent", ("SECURITY_ERROR", "securityError"));
            DefineSimpleEventSubclass("HTTPStatusEvent", ("HTTP_STATUS", "httpStatus"));

            EventDispatcherClass = DefinePackageClass(EventsPackage, "EventDispatcher", ObjectClass);
            EventDispatcherClass.NativeConstruct = args => new Avm2EventDispatcher(EventDispatcherClass);
            DefineDispatcherMembers(EventDispatcherClass);
        }

        private void DefineSimpleEventSubclass(
            string className,
            params (string name, string value)[] constants
        )
        {
            Avm2Class type = DefinePackageClass(EventsPackage, className, EventClass, dynamic: true);
            type.NativeConstruct = args => new Avm2EventObject(
                type,
                args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty,
                args.Length > 1 && Avm2Convert.ToBoolean(args[1]),
                args.Length > 2 && Avm2Convert.ToBoolean(args[2]));
            DefineEventConstants(type, constants);
            DefineEventInstanceMembers(type);
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
            GraphicsClass =
                DefinePackageClass(DisplayPackage, "Graphics", ObjectClass, dynamic: true);
            GraphicsClass.NativeConstruct = args => new Avm2Object(GraphicsClass);
            string[] graphicsMethods =
            {
                "clear", "beginFill", "beginGradientFill", "beginBitmapFill",
                "endFill", "lineStyle", "moveTo", "lineTo", "curveTo",
                "cubicCurveTo", "drawRect", "drawRoundRect", "drawCircle",
                "drawEllipse", "drawPath", "drawTriangles"
            };
            for (int i = 0; i < graphicsMethods.Length; i++)
                DefineMethod(GraphicsClass, graphicsMethods[i],
                    (receiver, args) => Avm2Undefined.Value);

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
            DefineGetter(SpriteClass, "graphics", (r, a) =>
            {
                if (!(r is Avm2Object obj))
                    return null;
                Avm2QName key = Avm2QName.Public("__graphics");
                if (obj.TryGetDynamic(key, out object existing))
                    return existing;
                Avm2Object graphics = new Avm2Object(GraphicsClass);
                obj.SetDynamic(key, graphics);
                return graphics;
            });

            MovieClipClass = DefinePackageClass(DisplayPackage, "MovieClip", SpriteClass);
            MovieClipClass.NativeConstruct = args => new Avm2DisplayObject(MovieClipClass);
            DefineMovieClipMembers(MovieClipClass);

            ShapeClass = DefinePackageClass(DisplayPackage, "Shape", DisplayObjectClass);
            ShapeClass.NativeConstruct = args => new Avm2DisplayObject(ShapeClass);

            BitmapDataClass =
                DefinePackageClass(DisplayPackage, "BitmapData", ObjectClass, dynamic: true);
            BitmapDataClass.NativeConstruct = args => new Avm2BitmapData(
                BitmapDataClass,
                args.Length > 0 ? Math.Max(1, Avm2Convert.ToInt32(args[0])) : 1,
                args.Length > 1 ? Math.Max(1, Avm2Convert.ToInt32(args[1])) : 1,
                args.Length <= 2 || Avm2Convert.ToBoolean(args[2]),
                args.Length > 3 ? Avm2Convert.ToUint32(args[3]) : 0xFFFFFFFFu);
            DefineGetter(BitmapDataClass, "width",
                (r, a) => r is Avm2BitmapData bitmapData ? bitmapData.Width : 0);
            DefineGetter(BitmapDataClass, "height",
                (r, a) => r is Avm2BitmapData bitmapData ? bitmapData.Height : 0);
            DefineGetter(BitmapDataClass, "transparent",
                (r, a) => !(r is Avm2BitmapData bitmapData) || bitmapData.Transparent);
            DefineMethod(BitmapDataClass, "getPixel",
                (r, a) => r is Avm2BitmapData bitmapData && a.Length > 1
                    ? bitmapData.GetPixel32(
                        Avm2Convert.ToInt32(a[0]), Avm2Convert.ToInt32(a[1])) & 0x00FFFFFFu
                    : 0u);
            DefineMethod(BitmapDataClass, "getPixel32",
                (r, a) => r is Avm2BitmapData bitmapData && a.Length > 1
                    ? bitmapData.GetPixel32(
                        Avm2Convert.ToInt32(a[0]), Avm2Convert.ToInt32(a[1]))
                    : 0u);
            DefineMethod(BitmapDataClass, "setPixel", (r, a) =>
            {
                if (r is Avm2BitmapData bitmapData && a.Length > 2)
                    bitmapData.SetPixel(
                        Avm2Convert.ToInt32(a[0]),
                        Avm2Convert.ToInt32(a[1]),
                        Avm2Convert.ToUint32(a[2]));
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "setPixel32", (r, a) =>
            {
                if (r is Avm2BitmapData bitmapData && a.Length > 2)
                    bitmapData.SetPixel32(
                        Avm2Convert.ToInt32(a[0]),
                        Avm2Convert.ToInt32(a[1]),
                        Avm2Convert.ToUint32(a[2]));
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "fillRect", (r, a) =>
            {
                if (r is Avm2BitmapData bitmapData && a.Length > 1)
                    bitmapData.FillRectangle(
                        GetRectangle(a[0], bitmapData.Width, bitmapData.Height),
                        Avm2Convert.ToUint32(a[1]));
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "getVector", (r, a) =>
            {
                Avm2Array result = new Avm2Array { Class = ArrayClass };
                if (r is Avm2BitmapData bitmapData)
                    bitmapData.CopyToVector(
                        GetRectangle(a.Length > 0 ? a[0] : null,
                            bitmapData.Width, bitmapData.Height),
                        result);
                return result;
            });
            DefineMethod(BitmapDataClass, "setVector", (r, a) =>
            {
                if (r is Avm2BitmapData bitmapData && a.Length > 1 &&
                    a[1] is Avm2Array pixels)
                {
                    bitmapData.CopyFromVector(
                        GetRectangle(a[0], bitmapData.Width, bitmapData.Height),
                        pixels);
                }
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "copyPixels", (r, a) =>
            {
                if (r is Avm2BitmapData destination && a.Length > 2 &&
                    a[0] is Avm2BitmapData source)
                {
                    destination.CopyPixels(
                        source,
                        GetRectangle(a[1], source.Width, source.Height),
                        GetPoint(a[2]));
                }
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "draw", (r, a) =>
            {
                if (r is Avm2BitmapData destination && a.Length > 0)
                {
                    Avm2BitmapData source = a[0] as Avm2BitmapData;
                    if (source == null && a[0] is Avm2Object sourceObject &&
                        sourceObject.TryGetDynamic(
                            Avm2QName.Public("__bitmapData"), out object bitmapDataValue))
                    {
                        source = bitmapDataValue as Avm2BitmapData;
                    }

                    if (source != null)
                        destination.CopyPixels(
                            source,
                            new Avm2PixelRect(0, 0, source.Width, source.Height),
                            new Avm2PixelPoint(0, 0));
                }
                return Avm2Undefined.Value;
            });
            DefineMethod(BitmapDataClass, "clone", (r, a) =>
                r is Avm2BitmapData bitmapData ? bitmapData.Clone(BitmapDataClass) : null);
            string[] bitmapDataMethods =
            {
                "applyFilter", "colorTransform", "copyChannel",
                "dispose", "drawWithQuality", "floodFill", "generateFilterRect",
                "getColorBoundsRect", "getPixels", "histogram", "hitTest", "lock",
                "merge", "noise", "paletteMap", "perlinNoise", "pixelDissolve",
                "scroll", "setPixels", "threshold", "unlock"
            };
            for (int i = 0; i < bitmapDataMethods.Length; i++)
                DefineMethod(BitmapDataClass, bitmapDataMethods[i],
                    (r, a) => Avm2Undefined.Value);

            Avm2Class bitmapDataChannel =
                DefinePackageClass(DisplayPackage, "BitmapDataChannel", ObjectClass);
            DefineStaticConstant(bitmapDataChannel, "RED", 1u);
            DefineStaticConstant(bitmapDataChannel, "GREEN", 2u);
            DefineStaticConstant(bitmapDataChannel, "BLUE", 4u);
            DefineStaticConstant(bitmapDataChannel, "ALPHA", 8u);

            BitmapClass = DefinePackageClass(DisplayPackage, "Bitmap", DisplayObjectClass);
            BitmapClass.NativeConstruct = args =>
            {
                Avm2DisplayObject bitmap = new Avm2DisplayObject(BitmapClass);
                if (args.Length > 0)
                    bitmap.SetDynamic(Avm2QName.Public("__bitmapData"), args[0]);
                return bitmap;
            };
            DefineGetter(BitmapClass, "bitmapData",
                (r, a) => r is Avm2Object o &&
                          o.TryGetDynamic(Avm2QName.Public("__bitmapData"), out object data)
                    ? data
                    : null,
                (r, a) =>
                {
                    if (r is Avm2Object o)
                        o.SetDynamic(Avm2QName.Public("__bitmapData"),
                            a.Length > 0 ? a[0] : null);
                    return Avm2Undefined.Value;
                });
            DefineGetter(BitmapClass, "smoothing",
                (r, a) => false, (r, a) => Avm2Undefined.Value);

            StageClass = DefinePackageClass(DisplayPackage, "Stage", DisplayObjectContainerClass);
            DefineStageMembers(StageClass);
            DefineStageConstantClasses();

            LoaderInfoClass =
                DefinePackageClass(DisplayPackage, "LoaderInfo", EventDispatcherClass, dynamic: true);
            LoaderInfoClass.NativeConstruct = args => new Avm2EventDispatcher(LoaderInfoClass);
            DefineDispatcherMembers(LoaderInfoClass);
            DefineGetter(LoaderInfoClass, "bytesLoaded", (r, a) => 1u);
            DefineGetter(LoaderInfoClass, "bytesTotal", (r, a) => 1u);
            DefineGetter(LoaderInfoClass, "url", (r, a) => string.Empty);
            DefineGetter(LoaderInfoClass, "loaderURL", (r, a) => string.Empty);
            DefineGetter(LoaderInfoClass, "content", (r, a) =>
                r is Avm2Object o &&
                o.TryGetDynamic(Avm2QName.Public("__content"), out object content)
                    ? content
                    : null);

            LoaderClass =
                DefinePackageClass(DisplayPackage, "Loader", DisplayObjectContainerClass, dynamic: true);
            LoaderClass.NativeConstruct = args => new Avm2DisplayObject(LoaderClass);
            DefineContainerMembers(LoaderClass);
            DefineGetter(LoaderClass, "contentLoaderInfo", (r, a) =>
            {
                if (!(r is Avm2Object obj))
                    return null;
                Avm2QName key = Avm2QName.Public("__contentLoaderInfo");
                if (obj.TryGetDynamic(key, out object existing))
                    return existing;
                Avm2EventDispatcher info = new Avm2EventDispatcher(LoaderInfoClass);
                obj.SetDynamic(key, info);
                return info;
            });
            DefineGetter(LoaderClass, "content", (r, a) =>
                r is Avm2DisplayObject loader && loader.NumChildren > 0
                    ? loader.GetChildAt(0)
                    : null);
            DefineMethod(LoaderClass, "load", (r, a) => Avm2Undefined.Value);
            DefineMethod(LoaderClass, "loadBytes", (r, a) => Avm2Undefined.Value);
            DefineMethod(LoaderClass, "close", (r, a) => Avm2Undefined.Value);
            DefineMethod(LoaderClass, "unload", (r, a) =>
            {
                if (r is Avm2DisplayObject loader)
                    while (loader.NumChildren > 0)
                        loader.RemoveChildAt(loader.NumChildren - 1);
                return Avm2Undefined.Value;
            });

            TextFieldClass = DefinePackageClass(TextPackage, "TextField", InteractiveObjectClass);
            TextFieldClass.NativeConstruct = args => new Avm2DisplayObject(TextFieldClass);
            DefineTextFieldMembers(TextFieldClass);
        }

        private static Avm2PixelRect GetRectangle(object value, int fallbackWidth, int fallbackHeight)
        {
            if (!(value is Avm2Object rectangle))
                return new Avm2PixelRect(0, 0, fallbackWidth, fallbackHeight);

            return new Avm2PixelRect(
                (int)GetGeometryNumber(rectangle, "x"),
                (int)GetGeometryNumber(rectangle, "y"),
                Math.Max(0, (int)GetGeometryNumber(rectangle, "width", fallbackWidth)),
                Math.Max(0, (int)GetGeometryNumber(rectangle, "height", fallbackHeight)));
        }

        private static Avm2PixelPoint GetPoint(object value)
        {
            if (!(value is Avm2Object point))
                return new Avm2PixelPoint(0, 0);

            return new Avm2PixelPoint(
                (int)GetGeometryNumber(point, "x"),
                (int)GetGeometryNumber(point, "y"));
        }

        private void DefineStageConstantClasses()
        {
            Avm2Class scaleMode =
                DefinePackageClass(DisplayPackage, "StageScaleMode", ObjectClass);
            DefineStaticConstant(scaleMode, "EXACT_FIT", "exactFit");
            DefineStaticConstant(scaleMode, "NO_BORDER", "noBorder");
            DefineStaticConstant(scaleMode, "NO_SCALE", "noScale");
            DefineStaticConstant(scaleMode, "SHOW_ALL", "showAll");

            Avm2Class align = DefinePackageClass(DisplayPackage, "StageAlign", ObjectClass);
            DefineStaticConstant(align, "TOP", "T");
            DefineStaticConstant(align, "BOTTOM", "B");
            DefineStaticConstant(align, "LEFT", "L");
            DefineStaticConstant(align, "RIGHT", "R");
            DefineStaticConstant(align, "TOP_LEFT", "TL");
            DefineStaticConstant(align, "TOP_RIGHT", "TR");
            DefineStaticConstant(align, "BOTTOM_LEFT", "BL");
            DefineStaticConstant(align, "BOTTOM_RIGHT", "BR");

            Avm2Class quality =
                DefinePackageClass(DisplayPackage, "StageQuality", ObjectClass);
            DefineStaticConstant(quality, "LOW", "LOW");
            DefineStaticConstant(quality, "MEDIUM", "MEDIUM");
            DefineStaticConstant(quality, "HIGH", "HIGH");
            DefineStaticConstant(quality, "BEST", "BEST");
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
            DefineGetter(type, "loaderInfo", (r, a) =>
            {
                if (!(r is Avm2Object obj))
                    return null;

                Avm2QName key = Avm2QName.Public("__loaderInfo");

                if (obj.TryGetDynamic(key, out object existing))
                    return existing;

                Avm2EventDispatcher info = new Avm2EventDispatcher(LoaderInfoClass);
                info.SetDynamic(Avm2QName.Public("__content"), r);
                obj.SetDynamic(key, info);
                return info;
            });

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
            Avm2QName textColorKey = Avm2QName.Public("__textColor");

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

            DefineGetter(type, "htmlText",
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

            DefineGetter(type, "textColor",
                (r, a) => r is Avm2Object o &&
                          o.TryGetDynamic(textColorKey, out object color)
                    ? color
                    : 0u,
                (r, a) =>
                {
                    if (r is Avm2Object o && a.Length > 0)
                        o.SetDynamic(textColorKey, Avm2Convert.ToUint32(a[0]));
                    return Avm2Undefined.Value;
                });

            DefineDynamicMember(type, "autoSize", "none");
            DefineDynamicMember(type, "selectable", true);
            DefineDynamicMember(type, "multiline", false);
            DefineDynamicMember(type, "wordWrap", false);
            DefineDynamicMember(type, "embedFonts", false);

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

    public sealed class Avm2BitmapData : Avm2Object
    {
        public int Width { get; }
        public int Height { get; }
        public bool Transparent { get; }
        public uint FillColor { get; private set; }
        public int Version { get; private set; }

        private uint[] pixels;

        public Avm2BitmapData(
            Avm2Class type,
            int width,
            int height,
            bool transparent,
            uint fillColor
        ) : base(type)
        {
            Width = width;
            Height = height;
            Transparent = transparent;
            FillColor = NormaliseAlpha(fillColor);
        }

        public uint GetPixel32(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return 0u;

            return pixels != null ? pixels[y * Width + x] : FillColor;
        }

        public void SetPixel(int x, int y, uint rgb)
        {
            uint old = GetPixel32(x, y);
            SetPixel32(x, y, (old & 0xFF000000u) | (rgb & 0x00FFFFFFu));
        }

        public void SetPixel32(int x, int y, uint argb)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return;

            EnsurePixels();
            pixels[y * Width + x] = NormaliseAlpha(argb);
            Version++;
        }

        public void FillRectangle(Avm2PixelRect rectangle, uint argb)
        {
            int x0 = Math.Max(0, rectangle.X);
            int y0 = Math.Max(0, rectangle.Y);
            int x1 = Math.Min(Width, rectangle.X + rectangle.Width);
            int y1 = Math.Min(Height, rectangle.Y + rectangle.Height);
            uint color = NormaliseAlpha(argb);

            if (x0 == 0 && y0 == 0 && x1 == Width && y1 == Height)
            {
                pixels = null;
                FillColor = color;
                Version++;
                return;
            }

            if (x0 >= x1 || y0 >= y1)
                return;

            EnsurePixels();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    pixels[y * Width + x] = color;
            Version++;
        }

        public void CopyToVector(Avm2PixelRect rectangle, Avm2Array destination)
        {
            int x0 = Math.Max(0, rectangle.X);
            int y0 = Math.Max(0, rectangle.Y);
            int x1 = Math.Min(Width, rectangle.X + rectangle.Width);
            int y1 = Math.Min(Height, rectangle.Y + rectangle.Height);

            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    destination.Items.Add(GetPixel32(x, y));
        }

        public void CopyFromVector(Avm2PixelRect rectangle, Avm2Array source)
        {
            int x0 = Math.Max(0, rectangle.X);
            int y0 = Math.Max(0, rectangle.Y);
            int x1 = Math.Min(Width, rectangle.X + rectangle.Width);
            int y1 = Math.Min(Height, rectangle.Y + rectangle.Height);
            int sourceIndex = 0;
            EnsurePixels();

            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (sourceIndex >= source.Length)
                        break;
                    pixels[y * Width + x] =
                        NormaliseAlpha(Avm2Convert.ToUint32(source.Items[sourceIndex++]));
                }
            }

            Version++;
        }

        public void CopyPixels(
            Avm2BitmapData source,
            Avm2PixelRect sourceRectangle,
            Avm2PixelPoint destinationPoint)
        {
            if (source == null)
                return;

            EnsurePixels();
            for (int sy = 0; sy < sourceRectangle.Height; sy++)
            {
                int sourceY = sourceRectangle.Y + sy;
                int destinationY = destinationPoint.Y + sy;
                if ((uint)sourceY >= (uint)source.Height ||
                    (uint)destinationY >= (uint)Height)
                    continue;

                for (int sx = 0; sx < sourceRectangle.Width; sx++)
                {
                    int sourceX = sourceRectangle.X + sx;
                    int destinationX = destinationPoint.X + sx;
                    if ((uint)sourceX >= (uint)source.Width ||
                        (uint)destinationX >= (uint)Width)
                        continue;

                    pixels[destinationY * Width + destinationX] =
                        source.GetPixel32(sourceX, sourceY);
                }
            }

            Version++;
        }

        public Avm2BitmapData Clone(Avm2Class bitmapDataClass)
        {
            Avm2BitmapData clone = new Avm2BitmapData(
                bitmapDataClass, Width, Height, Transparent, FillColor);
            if (pixels != null)
                clone.pixels = (uint[])pixels.Clone();
            return clone;
        }

        public void CopyPixelsTo(uint[] destination)
        {
            if (destination == null || destination.Length < Width * Height)
                return;

            if (pixels != null)
            {
                Array.Copy(pixels, destination, Width * Height);
                return;
            }

            for (int i = 0; i < Width * Height; i++)
                destination[i] = FillColor;
        }

        private void EnsurePixels()
        {
            if (pixels != null)
                return;

            pixels = new uint[Width * Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = FillColor;
        }

        private uint NormaliseAlpha(uint argb)
        {
            return Transparent ? argb : argb | 0xFF000000u;
        }
    }

    public readonly struct Avm2PixelRect
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;

        public Avm2PixelRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    public readonly struct Avm2PixelPoint
    {
        public readonly int X;
        public readonly int Y;

        public Avm2PixelPoint(int x, int y)
        {
            X = x;
            Y = y;
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
        void NotifyInstanceConstructed(Avm2Class type, Avm2Object instance);
        object ResolveTimelineChild(Avm2DisplayObject parent, string name);
    }
}
