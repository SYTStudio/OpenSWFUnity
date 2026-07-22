using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // The host-provided half of the AS3 environment.
    //
    // Everything here is a real implementation, not a stub: these are the classes a
    // compiled AS3 program assumes exist before any of its own code runs. The set is
    // deliberately limited to the language's own core - the flash.* display, event
    // and media APIs are not part of it.
    public sealed partial class Avm2Builtins
    {
        private readonly Avm2Domain domain;
        private readonly Func<object, object[], object> callFunction;

        public Avm2Class ObjectClass { get; private set; }
        public Avm2Class ClassClass { get; private set; }
        public Avm2Class FunctionClass { get; private set; }
        public Avm2Class ArrayClass { get; private set; }
        public Avm2Class StringClass { get; private set; }
        public Avm2Class NumberClass { get; private set; }
        public Avm2Class IntClass { get; private set; }
        public Avm2Class UIntClass { get; private set; }
        public Avm2Class BooleanClass { get; private set; }
        public Avm2Class ErrorClass { get; private set; }

        public Action<string> Trace { get; set; }

        public Avm2Builtins(Avm2Domain domain, Func<object, object[], object> callFunction)
        {
            this.domain = domain;
            this.callFunction = callFunction;
        }

        public void RegisterAll()
        {
            ObjectClass = DefineClass("Object", null, dynamic: true);
            ObjectClass.NativeConstruct = args => new Avm2Object(ObjectClass);

            ClassClass = DefineClass("Class", ObjectClass);
            FunctionClass = DefineClass("Function", ObjectClass);

            DefineArrayClass();
            DefineStringClass();
            DefineNumericClasses();
            DefineMathClass();
            DefineErrorClasses();
            DefineGlobalFunctions();
            RegisterFlashClasses();
        }

        // ---- class construction helpers ---------------------------------------

        private Avm2Class DefineClass(string localName, Avm2Class super, bool dynamic = false)
        {
            return DefinePackageClass(string.Empty, localName, super, dynamic);
        }

        // Flash's own classes live in packages, so their namespace is the package
        // path rather than the empty public namespace.
        private Avm2Class DefinePackageClass(
            string package,
            string localName,
            Avm2Class super,
            bool dynamic = false
        )
        {
            Avm2QName name = new Avm2QName(package, localName);
            Avm2Class type = new Avm2Class(name)
            {
                Super = super,
                IsNative = true,
                IsDynamic = dynamic,
                Class = ClassClass
            };

            // Inherit the supertype's members so a lookup never walks the chain.
            if (super != null)
            {
                foreach (KeyValuePair<Avm2QName, Avm2Binding> entry in super.InstanceBindings)
                    type.InstanceBindings[entry.Key] = entry.Value;
            }

            domain.SetGlobal(name, type);
            return type;
        }

        private void DefineMethod(Avm2Class type, string name, Avm2NativeCall call, bool isStatic = false)
        {
            Avm2QName qname = Avm2QName.Public(name);
            Avm2Binding binding = new Avm2Binding
            {
                Name = qname,
                Kind = Avm2BindingKind.Method,
                DeclaringClass = type,
                IsStatic = isStatic,
                NativeFunction = Avm2Function.FromNative(name, call)
            };

            if (isStatic)
                type.StaticBindings[qname] = binding;
            else
                type.InstanceBindings[qname] = binding;
        }

        private void DefineGetter(Avm2Class type, string name, Avm2NativeCall getter, Avm2NativeCall setter = null)
        {
            Avm2QName qname = Avm2QName.Public(name);
            type.InstanceBindings[qname] = new Avm2Binding
            {
                Name = qname,
                Kind = setter != null ? Avm2BindingKind.GetterSetter : Avm2BindingKind.Getter,
                DeclaringClass = type,
                NativeGetter = getter,
                NativeSetter = setter
            };
        }

        // Members are always addressed by their public name, whatever package the
        // declaring class lives in.
        private void DefineStaticConstant(Avm2Class type, string name, object value)
        {
            Avm2QName qname = Avm2QName.Public(name);
            type.StaticBindings[qname] = new Avm2Binding
            {
                Name = qname,
                Kind = Avm2BindingKind.Constant,
                DeclaringClass = type,
                IsStatic = true,
                ConstantValue = value
            };
        }

        // ---- Array ------------------------------------------------------------

        private void DefineArrayClass()
        {
            ArrayClass = DefineClass("Array", ObjectClass, dynamic: true);

            // new Array(5) presizes; new Array(1,2) fills.
            ArrayClass.NativeConstruct = args =>
            {
                Avm2Array array = new Avm2Array { Class = ArrayClass };

                if (args != null && args.Length == 1 && Avm2Convert.IsNumeric(args[0]))
                {
                    double requested = Avm2Convert.ToNumber(args[0]);

                    if (requested >= 0 && requested < 1 << 24 &&
                        Math.Abs(requested - Math.Floor(requested)) < double.Epsilon)
                    {
                        array.SetLength((int)requested);
                        return array;
                    }
                }

                if (args != null)
                    array.Items.AddRange(args);

                return array;
            };

            DefineGetter(ArrayClass, "length",
                (receiver, args) => receiver is Avm2Array a ? a.Length : 0,
                (receiver, args) =>
                {
                    if (receiver is Avm2Array a && args.Length > 0)
                        a.SetLength(Avm2Convert.ToInt32(args[0]));

                    return Avm2Undefined.Value;
                });

            DefineMethod(ArrayClass, "push", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a))
                    return 0;

                for (int i = 0; i < args.Length; i++)
                    a.Items.Add(args[i]);

                return a.Length;
            });

            DefineMethod(ArrayClass, "pop", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a) || a.Length == 0)
                    return Avm2Undefined.Value;

                object value = a.Items[a.Length - 1];
                a.Items.RemoveAt(a.Length - 1);
                return value;
            });

            DefineMethod(ArrayClass, "shift", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a) || a.Length == 0)
                    return Avm2Undefined.Value;

                object value = a.Items[0];
                a.Items.RemoveAt(0);
                return value;
            });

            DefineMethod(ArrayClass, "unshift", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a))
                    return 0;

                for (int i = 0; i < args.Length; i++)
                    a.Items.Insert(i, args[i]);

                return a.Length;
            });

            DefineMethod(ArrayClass, "join", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a))
                    return string.Empty;

                string separator = args.Length > 0 && !Avm2Convert.IsNullOrUndefined(args[0])
                    ? Avm2Convert.ToString(args[0])
                    : ",";
                StringBuilder builder = new StringBuilder();

                for (int i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0)
                        builder.Append(separator);

                    if (!Avm2Convert.IsNullOrUndefined(a.Items[i]))
                        builder.Append(Avm2Convert.ToString(a.Items[i]));
                }

                return builder.ToString();
            });

            DefineMethod(ArrayClass, "indexOf", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a) || args.Length == 0)
                    return -1;

                for (int i = 0; i < a.Items.Count; i++)
                {
                    if (Avm2Convert.StrictEquals(a.Items[i], args[0]))
                        return i;
                }

                return -1;
            });

            DefineMethod(ArrayClass, "slice", (receiver, args) =>
            {
                Avm2Array result = new Avm2Array { Class = ArrayClass };

                if (!(receiver is Avm2Array a))
                    return result;

                int start = NormaliseIndex(args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0, a.Length);
                int end = NormaliseIndex(args.Length > 1 ? Avm2Convert.ToInt32(args[1]) : a.Length, a.Length);

                for (int i = start; i < end; i++)
                    result.Items.Add(a.Items[i]);

                return result;
            });

            DefineMethod(ArrayClass, "splice", (receiver, args) =>
            {
                Avm2Array removed = new Avm2Array { Class = ArrayClass };

                if (!(receiver is Avm2Array a) || args.Length == 0)
                    return removed;

                int start = NormaliseIndex(Avm2Convert.ToInt32(args[0]), a.Length);
                int count = args.Length > 1
                    ? Math.Max(0, Math.Min(a.Length - start, Avm2Convert.ToInt32(args[1])))
                    : a.Length - start;

                for (int i = 0; i < count; i++)
                {
                    removed.Items.Add(a.Items[start]);
                    a.Items.RemoveAt(start);
                }

                for (int i = 2; i < args.Length; i++)
                    a.Items.Insert(start + i - 2, args[i]);

                return removed;
            });

            DefineMethod(ArrayClass, "concat", (receiver, args) =>
            {
                Avm2Array result = new Avm2Array { Class = ArrayClass };

                if (receiver is Avm2Array a)
                    result.Items.AddRange(a.Items);

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] is Avm2Array nested)
                        result.Items.AddRange(nested.Items);
                    else
                        result.Items.Add(args[i]);
                }

                return result;
            });

            DefineMethod(ArrayClass, "reverse", (receiver, args) =>
            {
                if (receiver is Avm2Array a)
                    a.Items.Reverse();

                return receiver;
            });

            DefineMethod(ArrayClass, "sort", (receiver, args) =>
            {
                if (!(receiver is Avm2Array a))
                    return receiver;

                Avm2Function comparator = args.Length > 0 ? args[0] as Avm2Function : null;

                // A comparator is user code and may throw or be inconsistent, which
                // makes List.Sort raise. AS3 leaves the array untouched in that case.
                try
                {
                    if (comparator != null && callFunction != null)
                    {
                        a.Items.Sort((left, right) => Avm2Convert.ToInt32(
                            callFunction(comparator, new[] { left, right })));
                    }
                    else
                    {
                        a.Items.Sort((left, right) => string.CompareOrdinal(
                            Avm2Convert.ToString(left), Avm2Convert.ToString(right)));
                    }
                }
                catch (InvalidOperationException)
                {
                    return receiver;
                }

                return receiver;
            });

            DefineMethod(ArrayClass, "toString",
                (receiver, args) => Avm2Convert.ToString(receiver));
        }

        private static int NormaliseIndex(int index, int length)
        {
            if (index < 0)
                index = Math.Max(0, length + index);

            return Math.Max(0, Math.Min(length, index));
        }

        // ---- String -----------------------------------------------------------

        private void DefineStringClass()
        {
            StringClass = DefineClass("String", ObjectClass);
            StringClass.NativeConstruct = args =>
                args != null && args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty;

            DefineMethod(StringClass, "fromCharCode", (receiver, args) =>
            {
                StringBuilder builder = new StringBuilder();

                for (int i = 0; i < args.Length; i++)
                    builder.Append((char)(ushort)Avm2Convert.ToInt32(args[i]));

                return builder.ToString();
            }, isStatic: true);
        }

        // Strings are C# strings rather than AVM2 objects, so their members are
        // served here instead of through a bindings table.
        public bool TryGetStringMember(string text, Avm2QName name, out object value)
        {
            switch (name.Local)
            {
                case "length":
                    value = text.Length;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        public bool TryCallStringMethod(string text, Avm2QName name, object[] args, out object result)
        {
            result = null;

            switch (name.Local)
            {
                case "charAt":
                {
                    int index = args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0;
                    result = index >= 0 && index < text.Length
                        ? text[index].ToString()
                        : string.Empty;
                    return true;
                }
                case "charCodeAt":
                {
                    int index = args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0;
                    result = index >= 0 && index < text.Length ? (double)text[index] : double.NaN;
                    return true;
                }
                case "indexOf":
                {
                    string needle = args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty;
                    int start = args.Length > 1 ? Math.Max(0, Avm2Convert.ToInt32(args[1])) : 0;
                    result = start <= text.Length
                        ? text.IndexOf(needle, start, StringComparison.Ordinal)
                        : -1;
                    return true;
                }
                case "lastIndexOf":
                {
                    string needle = args.Length > 0 ? Avm2Convert.ToString(args[0]) : string.Empty;
                    result = text.LastIndexOf(needle, StringComparison.Ordinal);
                    return true;
                }
                case "substring":
                {
                    int start = Math.Max(0, Math.Min(text.Length,
                        args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0));
                    int end = Math.Max(0, Math.Min(text.Length,
                        args.Length > 1 ? Avm2Convert.ToInt32(args[1]) : text.Length));

                    if (start > end)
                    {
                        int swap = start;
                        start = end;
                        end = swap;
                    }

                    result = text.Substring(start, end - start);
                    return true;
                }
                case "substr":
                {
                    int start = NormaliseIndex(args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0, text.Length);
                    int count = args.Length > 1
                        ? Math.Max(0, Math.Min(text.Length - start, Avm2Convert.ToInt32(args[1])))
                        : text.Length - start;
                    result = text.Substring(start, count);
                    return true;
                }
                case "slice":
                {
                    int start = NormaliseIndex(args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0, text.Length);
                    int end = NormaliseIndex(args.Length > 1 ? Avm2Convert.ToInt32(args[1]) : text.Length, text.Length);
                    result = text.Substring(start, Math.Max(0, end - start));
                    return true;
                }
                case "toLowerCase":
                    result = text.ToLowerInvariant();
                    return true;
                case "toUpperCase":
                    result = text.ToUpperInvariant();
                    return true;
                case "split":
                {
                    Avm2Array parts = new Avm2Array { Class = ArrayClass };
                    string separator = args.Length > 0 ? Avm2Convert.ToString(args[0]) : ",";

                    if (separator.Length == 0)
                    {
                        for (int i = 0; i < text.Length; i++)
                            parts.Items.Add(text[i].ToString());
                    }
                    else
                    {
                        string[] split = text.Split(new[] { separator }, StringSplitOptions.None);

                        for (int i = 0; i < split.Length; i++)
                            parts.Items.Add(split[i]);
                    }

                    result = parts;
                    return true;
                }
                case "concat":
                {
                    StringBuilder builder = new StringBuilder(text);

                    for (int i = 0; i < args.Length; i++)
                        builder.Append(Avm2Convert.ToString(args[i]));

                    result = builder.ToString();
                    return true;
                }
                case "toString":
                case "valueOf":
                    result = text;
                    return true;
                default:
                    return false;
            }
        }

        // ---- numbers ----------------------------------------------------------

        private void DefineNumericClasses()
        {
            NumberClass = DefineClass("Number", ObjectClass);
            NumberClass.NativeConstruct = args =>
                args != null && args.Length > 0 ? Avm2Convert.ToNumber(args[0]) : 0d;
            DefineStaticConstant(NumberClass, "MAX_VALUE", double.MaxValue);
            DefineStaticConstant(NumberClass, "MIN_VALUE", double.Epsilon);
            DefineStaticConstant(NumberClass, "NaN", double.NaN);
            DefineStaticConstant(NumberClass, "POSITIVE_INFINITY", double.PositiveInfinity);
            DefineStaticConstant(NumberClass, "NEGATIVE_INFINITY", double.NegativeInfinity);

            IntClass = DefineClass("int", ObjectClass);
            IntClass.NativeConstruct = args =>
                args != null && args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 0;
            DefineStaticConstant(IntClass, "MAX_VALUE", int.MaxValue);
            DefineStaticConstant(IntClass, "MIN_VALUE", int.MinValue);

            UIntClass = DefineClass("uint", ObjectClass);
            UIntClass.NativeConstruct = args =>
                args != null && args.Length > 0 ? Avm2Convert.ToUint32(args[0]) : 0u;
            DefineStaticConstant(UIntClass, "MAX_VALUE", uint.MaxValue);
            DefineStaticConstant(UIntClass, "MIN_VALUE", 0u);

            BooleanClass = DefineClass("Boolean", ObjectClass);
            BooleanClass.NativeConstruct = args =>
                args != null && args.Length > 0 && Avm2Convert.ToBoolean(args[0]);
        }

        public bool TryCallNumberMethod(object receiver, Avm2QName name, object[] args, out object result)
        {
            result = null;
            double number = Avm2Convert.ToNumber(receiver);

            switch (name.Local)
            {
                case "toString":
                {
                    int radix = args.Length > 0 ? Avm2Convert.ToInt32(args[0]) : 10;

                    if (radix == 10)
                    {
                        result = Avm2Convert.ToString(receiver);
                        return true;
                    }

                    if (radix < 2 || radix > 36)
                        return false;

                    result = ToRadixString((long)number, radix);
                    return true;
                }
                case "toFixed":
                {
                    int digits = args.Length > 0 ? Math.Max(0, Math.Min(20, Avm2Convert.ToInt32(args[0]))) : 0;
                    result = number.ToString("F" + digits, CultureInfo.InvariantCulture);
                    return true;
                }
                case "valueOf":
                    result = receiver;
                    return true;
                default:
                    return false;
            }
        }

        private static string ToRadixString(long value, int radix)
        {
            if (value == 0)
                return "0";

            bool negative = value < 0;
            ulong magnitude = negative ? (ulong)(-value) : (ulong)value;
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            StringBuilder builder = new StringBuilder();

            while (magnitude > 0)
            {
                builder.Insert(0, digits[(int)(magnitude % (ulong)radix)]);
                magnitude /= (ulong)radix;
            }

            if (negative)
                builder.Insert(0, '-');

            return builder.ToString();
        }

        // ---- Math -------------------------------------------------------------

        private void DefineMathClass()
        {
            Avm2Class math = DefineClass("Math", ObjectClass);

            DefineStaticConstant(math, "PI", Math.PI);
            DefineStaticConstant(math, "E", Math.E);
            DefineStaticConstant(math, "LN2", Math.Log(2d));
            DefineStaticConstant(math, "LN10", Math.Log(10d));
            DefineStaticConstant(math, "LOG2E", 1d / Math.Log(2d));
            DefineStaticConstant(math, "LOG10E", 1d / Math.Log(10d));
            DefineStaticConstant(math, "SQRT2", Math.Sqrt(2d));
            DefineStaticConstant(math, "SQRT1_2", Math.Sqrt(0.5d));

            DefineMethod(math, "abs", (r, a) => Math.Abs(Arg(a, 0)), true);
            DefineMethod(math, "floor", (r, a) => Math.Floor(Arg(a, 0)), true);
            DefineMethod(math, "ceil", (r, a) => Math.Ceiling(Arg(a, 0)), true);
            // Flash rounds halfway cases upward, unlike .NET's banker's rounding.
            DefineMethod(math, "round", (r, a) => Math.Floor(Arg(a, 0) + 0.5d), true);
            DefineMethod(math, "sqrt", (r, a) => Math.Sqrt(Arg(a, 0)), true);
            DefineMethod(math, "pow", (r, a) => Math.Pow(Arg(a, 0), Arg(a, 1)), true);
            DefineMethod(math, "exp", (r, a) => Math.Exp(Arg(a, 0)), true);
            DefineMethod(math, "log", (r, a) => Math.Log(Arg(a, 0)), true);
            DefineMethod(math, "sin", (r, a) => Math.Sin(Arg(a, 0)), true);
            DefineMethod(math, "cos", (r, a) => Math.Cos(Arg(a, 0)), true);
            DefineMethod(math, "tan", (r, a) => Math.Tan(Arg(a, 0)), true);
            DefineMethod(math, "asin", (r, a) => Math.Asin(Arg(a, 0)), true);
            DefineMethod(math, "acos", (r, a) => Math.Acos(Arg(a, 0)), true);
            DefineMethod(math, "atan", (r, a) => Math.Atan(Arg(a, 0)), true);
            DefineMethod(math, "atan2", (r, a) => Math.Atan2(Arg(a, 0), Arg(a, 1)), true);
            DefineMethod(math, "random", (r, a) => random.NextDouble(), true);

            DefineMethod(math, "min", (r, a) => Aggregate(a, true), true);
            DefineMethod(math, "max", (r, a) => Aggregate(a, false), true);
        }

        private readonly Random random = new Random();

        private static double Arg(object[] args, int index)
        {
            return index < args.Length ? Avm2Convert.ToNumber(args[index]) : double.NaN;
        }

        private static double Aggregate(object[] args, bool minimum)
        {
            if (args.Length == 0)
                return minimum ? double.PositiveInfinity : double.NegativeInfinity;

            double result = Avm2Convert.ToNumber(args[0]);

            for (int i = 1; i < args.Length; i++)
            {
                double value = Avm2Convert.ToNumber(args[i]);

                if (double.IsNaN(value) || double.IsNaN(result))
                    return double.NaN;

                result = minimum ? Math.Min(result, value) : Math.Max(result, value);
            }

            return result;
        }

        // ---- Error ------------------------------------------------------------

        private void DefineErrorClasses()
        {
            ErrorClass = DefineClass("Error", ObjectClass, dynamic: true);
            ErrorClass.NativeConstruct = args => MakeError(ErrorClass, args);

            DefineGetter(ErrorClass, "message",
                (receiver, args) =>
                    receiver is Avm2Object o && o.TryGetDynamic(Avm2QName.Public("__message"), out object m)
                        ? m
                        : string.Empty,
                (receiver, args) =>
                {
                    if (receiver is Avm2Object o && args.Length > 0)
                        o.SetDynamic(Avm2QName.Public("__message"), args[0]);

                    return Avm2Undefined.Value;
                });

            DefineMethod(ErrorClass, "toString", (receiver, args) =>
            {
                if (receiver is Avm2Object o &&
                    o.TryGetDynamic(Avm2QName.Public("__message"), out object m))
                {
                    return "Error: " + Avm2Convert.ToString(m);
                }

                return "Error";
            });

            // The standard subclasses a program may throw or catch by name.
            string[] subclasses =
            {
                "ArgumentError", "TypeError", "RangeError", "ReferenceError",
                "SecurityError", "VerifyError", "EvalError", "URIError"
            };

            for (int i = 0; i < subclasses.Length; i++)
            {
                Avm2Class subclass = DefineClass(subclasses[i], ErrorClass, dynamic: true);
                Avm2Class captured = subclass;
                subclass.NativeConstruct = args => MakeError(captured, args);
            }
        }

        private static Avm2Object MakeError(Avm2Class type, object[] args)
        {
            Avm2Object error = new Avm2Object(type);

            if (args != null && args.Length > 0)
                error.SetDynamic(Avm2QName.Public("__message"), Avm2Convert.ToString(args[0]));

            return error;
        }

        // ---- global functions -------------------------------------------------

        private void DefineGlobalFunctions()
        {
            RegisterGlobalFunction("trace", (receiver, args) =>
            {
                StringBuilder builder = new StringBuilder();

                for (int i = 0; i < args.Length; i++)
                {
                    if (i > 0)
                        builder.Append(' ');

                    builder.Append(Avm2Convert.ToString(args[i]));
                }

                Trace?.Invoke(builder.ToString());
                return Avm2Undefined.Value;
            });

            RegisterGlobalFunction("parseInt", (receiver, args) =>
            {
                string text = args.Length > 0 ? Avm2Convert.ToString(args[0]).Trim() : string.Empty;
                int radix = args.Length > 1 ? Avm2Convert.ToInt32(args[1]) : 0;
                return ParseInteger(text, radix);
            });

            RegisterGlobalFunction("parseFloat", (receiver, args) =>
            {
                string text = args.Length > 0 ? Avm2Convert.ToString(args[0]).Trim() : string.Empty;

                for (int length = text.Length; length > 0; length--)
                {
                    if (double.TryParse(text.Substring(0, length), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double parsed))
                    {
                        return parsed;
                    }
                }

                return double.NaN;
            });

            RegisterGlobalFunction("isNaN",
                (receiver, args) => double.IsNaN(Arg(args, 0)));

            RegisterGlobalFunction("isFinite", (receiver, args) =>
            {
                double value = Arg(args, 0);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            });

            domain.SetGlobal(Avm2QName.Public("undefined"), Avm2Undefined.Value);
            domain.SetGlobal(Avm2QName.Public("NaN"), double.NaN);
            domain.SetGlobal(Avm2QName.Public("Infinity"), double.PositiveInfinity);
        }

        private void RegisterGlobalFunction(string name, Avm2NativeCall call)
        {
            domain.SetGlobal(Avm2QName.Public(name), Avm2Function.FromNative(name, call));
        }

        private static object ParseInteger(string text, int radix)
        {
            bool negative = text.StartsWith("-", StringComparison.Ordinal);

            if (negative || text.StartsWith("+", StringComparison.Ordinal))
                text = text.Substring(1);

            if (radix == 0)
                radix = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10;

            if (radix == 16 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            if (radix < 2 || radix > 36)
                return double.NaN;

            long result = 0;
            int consumed = 0;

            for (int i = 0; i < text.Length; i++)
            {
                int digit = DigitValue(text[i]);

                if (digit < 0 || digit >= radix)
                    break;

                result = result * radix + digit;
                consumed++;
            }

            if (consumed == 0)
                return double.NaN;

            return negative ? -result : result;
        }

        private static int DigitValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'z') return value - 'a' + 10;
            if (value >= 'A' && value <= 'Z') return value - 'A' + 10;
            return -1;
        }

        // The class that describes a runtime value, used by is/as/instanceof.
        public Avm2Class GetClassOf(object value)
        {
            if (value == null || Avm2Convert.IsUndefined(value))
                return null;

            if (value is string) return StringClass;
            if (value is int) return IntClass;
            if (value is uint) return UIntClass;
            if (value is double) return NumberClass;
            if (value is bool) return BooleanClass;
            if (value is Avm2Array) return ArrayClass;
            if (value is Avm2Class) return ClassClass;
            if (value is Avm2Function) return FunctionClass;
            if (value is Avm2Object instance) return instance.Class ?? ObjectClass;

            return ObjectClass;
        }
    }
}
