using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace OpenSWFUnity.Runtime.AVM1
{
    public sealed class Avm1Runtime
    {
        // A real game's initialisation script legitimately runs far more than a
        // demo's: 200k was low enough that Isaac-sized content tripped the guard
        // during startup and silently stopped half-configured. Still bounded, so
        // a genuinely runaway script cannot hang the Editor.
        private const int DefaultInstructionBudget = 5000000;
        private static readonly object Undefined = new object();

        private readonly StringComparer nameComparer;
        private readonly Dictionary<string, object> globals;
        private readonly Dictionary<string, object> registeredClasses;
        private readonly HashSet<int> pressedKeys = new HashSet<int>();
        private readonly System.Random randomSource = new System.Random();

        public Avm1Object RootObject { get; }
        public Func<string, IReadOnlyList<object>, object> ExternalFunction { get; set; }
        public Func<object, string, IReadOnlyList<object>, object> ExternalMethod { get; set; }
        public Action<string> Trace { get; set; }
        public Action<string> Warning { get; set; }
        public int DefinedFunctionCount { get; private set; }
        public bool VerboseLogging { get; set; }

        public Avm1Runtime(bool caseSensitive = true)
        {
            nameComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            globals = new Dictionary<string, object>(nameComparer);
            registeredClasses = new Dictionary<string, object>(nameComparer);
            RootObject = new Avm1Object(nameComparer);
            globals["_root"] = RootObject;
            globals["_level0"] = RootObject;
            globals["this"] = RootObject;
            globals["true"] = true;
            globals["false"] = false;
            globals["null"] = null;
            globals["undefined"] = Undefined;

            RootObject.Set("_root", RootObject);
            RootObject.Set("_global", RootObject);
            RootObject.Set("Stage", BuildStageObject());
            globals["Stage"] = RootObject.Get("Stage");
            globals["Math"] = BuildMathObject();
            globals["_global"] = RootObject;
            RegisterCoreBuiltIns();
        }

        public Avm1Object CreateObject()
        {
            return new Avm1Object(nameComparer);
        }

        public bool ApplyRegisteredClass(string linkageName, Avm1Object instance)
        {
            if (string.IsNullOrEmpty(linkageName) || instance == null ||
                !registeredClasses.TryGetValue(linkageName, out object constructor) ||
                !IsCallable(constructor))
            {
                return false;
            }

            if (constructor is Avm1Object constructorObject)
            {
                instance.Prototype = constructorObject.Get("prototype") as Avm1Object;
                instance.Set("__constructor", constructorObject);
            }

            CallValue(constructor, EmptyArguments, instance);
            return true;
        }

        public void Broadcast(string broadcasterName, string messageName, params object[] arguments)
        {
            if (!(ResolveVariable(null, broadcasterName) is Avm1Object broadcaster))
                return;

            List<object> values = new List<object> { messageName };

            if (arguments != null)
                values.AddRange(arguments);

            BroadcastMessage(broadcaster, values);
        }

        public void SetKeyState(int keyCode, int asciiCode, bool pressed)
        {
            if (pressed)
                pressedKeys.Add(keyCode);
            else
                pressedKeys.Remove(keyCode);

            if (ResolveVariable(null, "Key") is Avm1Object keyObject)
            {
                keyObject.Set("__lastCode", keyCode);
                keyObject.Set("__lastAscii", asciiCode);
            }
        }

        public void RegisterFunctions(byte[] actionBytes)
        {
            if (actionBytes == null || actionBytes.Length == 0)
                return;

            ScanFunctionDefinitions(actionBytes, 0, actionBytes.Length, new List<string>());
        }

        public object Execute(byte[] actionBytes)
        {
            return Execute(actionBytes, RootObject);
        }

        public object Execute(byte[] actionBytes, Avm1Object thisObject)
        {
            if (actionBytes == null || actionBytes.Length == 0)
                return null;

            Dictionary<string, object> locals =
                new Dictionary<string, object>(nameComparer)
                {
                    ["this"] = thisObject ?? RootObject
                };
            ExecutionContext context = new ExecutionContext(locals, new List<string>(), false);
            context.Target = thisObject ?? RootObject;
            context.OriginalTarget = context.Target;
            int budget = DefaultInstructionBudget;

            try
            {
                ExecuteBlock(actionBytes, 0, actionBytes.Length, context, ref budget);
            }
            catch (Avm1ThrownException thrown)
            {
                ReportWarning("Uncaught AVM1 exception: " + ToAvm1String(thrown.Value));
            }

            return context.ReturnValue;
        }

        public bool TryCallFunction(string functionName, IReadOnlyList<object> arguments, out object result)
        {
            result = null;

            if (string.IsNullOrEmpty(functionName))
                return false;

            object function = ResolveVariable(null, functionName);

            if (!(function is Avm1Function) && !(function is Avm1NativeFunction))
                return false;

            result = CallValue(function, arguments ?? EmptyArguments, RootObject);
            return true;
        }

        public bool TryCallMethod(
            Avm1Object receiver,
            string methodName,
            IReadOnlyList<object> arguments,
            out object result
        )
        {
            result = null;

            if (receiver == null || string.IsNullOrEmpty(methodName))
                return false;

            object method = GetMember(receiver, methodName);

            if (!IsCallable(method))
                return false;

            result = CallValue(method, arguments ?? EmptyArguments, receiver);
            return true;
        }

        public object GetVariable(string name)
        {
            return NormalizePublicValue(ResolveVariable(null, name));
        }

        public void SetVariable(string name, object value)
        {
            SetVariable(null, name, value);
        }

        private void ExecuteBlock(
            byte[] code,
            int start,
            int end,
            ExecutionContext context,
            ref int instructionBudget
        )
        {
            int p = Math.Max(0, start);
            end = Math.Min(code.Length, end);

            while (p < end && !context.Returned)
            {
                if (--instructionBudget < 0)
                {
                    ReportWarning("AVM1 instruction budget reached; script execution was stopped safely.");
                    return;
                }

                int actionStart = p;
                byte opcode = code[p++];

                if (opcode == 0)
                    return;

                int payloadLength = 0;

                if (opcode >= 0x80)
                {
                    if (p + 2 > end)
                        return;

                    payloadLength = ReadUInt16(code, p);
                    p += 2;
                }

                int payloadStart = p;
                int payloadEnd = Math.Min(end, payloadStart + payloadLength);

                try
                {
                    switch (opcode)
                    {
                        case 0x04: InvokeExternal("nextFrame", EmptyArguments, context.Target); break;
                        case 0x05: InvokeExternal("prevFrame", EmptyArguments, context.Target); break;
                        case 0x06: InvokeExternal("play", EmptyArguments, context.Target); break;
                        case 0x07: InvokeExternal("stop", EmptyArguments, context.Target); break;
                        case 0x08: InvokeExternal("toggleHighQuality", EmptyArguments, context.Target); break;
                        case 0x09: InvokeExternal("stopSounds", EmptyArguments, context.Target); break;
                        case 0x0A: BinaryNumber(context, (a, b) => a + b); break;
                        case 0x0B: BinaryNumber(context, (a, b) => a - b); break;
                        case 0x0C: BinaryNumber(context, (a, b) => a * b); break;
                        case 0x0D: BinaryNumber(context, (a, b) => b == 0d ? double.NaN : a / b); break;
                        case 0x0E: BinaryCompare(context, (a, b) => ToNumber(a) == ToNumber(b)); break;
                        case 0x0F: BinaryCompare(context, (a, b) => ToNumber(a) < ToNumber(b)); break;
                        case 0x10: BinaryCompare(context, (a, b) => ToBoolean(a) && ToBoolean(b)); break;
                        case 0x11: BinaryCompare(context, (a, b) => ToBoolean(a) || ToBoolean(b)); break;
                        case 0x12: Push(context, !ToBoolean(Pop(context))); break;
                        case 0x13: BinaryCompare(context, (a, b) => ToAvm1String(a) == ToAvm1String(b)); break;
                        case 0x14: Push(context, ToAvm1String(Pop(context)).Length); break;
                        case 0x15:
                        {
                            int count = (int)ToNumber(Pop(context));
                            int index = Math.Max(0, (int)ToNumber(Pop(context)) - 1);
                            string text = ToAvm1String(Pop(context));
                            Push(context, index < text.Length
                                ? text.Substring(index, Math.Min(Math.Max(0, count), text.Length - index))
                                : string.Empty);
                            break;
                        }
                        case 0x17: Pop(context); break;
                        case 0x18: Push(context, (int)ToNumber(Pop(context))); break;

                        case 0x1C: // GetVariable
                            Push(context, ResolveVariable(context, ToAvm1String(Pop(context))));
                            break;

                        case 0x1D: // SetVariable
                        {
                            object value = Pop(context);
                            string name = ToAvm1String(Pop(context));
                            SetVariable(context, name, value);
                            break;
                        }

                        case 0x20: // SetTarget2
                            context.Target = ResolveTarget(context, Pop(context));
                            break;

                        case 0x21:
                        case 0x47:
                        {
                            object right = Pop(context);
                            object left = Pop(context);
                            Push(context, AddValues(left, right));
                            break;
                        }

                        case 0x22: // GetProperty
                        {
                            int propertyIndex = (int)ToNumber(Pop(context));
                            object target = ResolveTarget(context, Pop(context));
                            Push(context, GetMember(target, PropertyName(propertyIndex)));
                            break;
                        }

                        case 0x23: // SetProperty
                        {
                            object value = Pop(context);
                            int propertyIndex = (int)ToNumber(Pop(context));
                            object target = ResolveTarget(context, Pop(context));
                            SetMember(target, PropertyName(propertyIndex), value);
                            break;
                        }

                        case 0x24: // CloneSprite
                        {
                            int depth = (int)ToNumber(Pop(context));
                            string name = ToAvm1String(Pop(context));
                            object target = ResolveTarget(context, Pop(context));
                            InvokeExternal(
                                "duplicateMovieClip",
                                new object[] { name, depth },
                                target
                            );
                            break;
                        }

                        case 0x25: // RemoveSprite
                            InvokeExternal("removeMovieClip", EmptyArguments, ResolveTarget(context, Pop(context)));
                            break;

                        case 0x26: // Trace
                        {
                            string message = ToAvm1String(Pop(context));
                            Trace?.Invoke(message);

                            if (Trace == null)
                                Debug.Log("[AVM1] " + message);
                            break;
                        }

                        case 0x27: InvokeExternal("startDrag", EmptyArguments, ResolveTarget(context, Pop(context))); break;
                        case 0x28: InvokeExternal("stopDrag", EmptyArguments, context.Target); break;
                        case 0x29: BinaryCompare(context, (a, b) => string.Compare(ToAvm1String(a), ToAvm1String(b), StringComparison.Ordinal) < 0); break;

                        case 0x2A: // Throw
                            throw new Avm1ThrownException(Pop(context));

                        case 0x2B: // CastOp
                        {
                            object constructor = Pop(context);
                            object value = Pop(context);
                            Push(context, IsInstanceOf(value, constructor) ? value : null);
                            break;
                        }

                        case 0x2C: // ImplementsOp
                        {
                            object constructor = Pop(context);
                            int count = ClampArgumentCount(ToNumber(Pop(context)));
                            List<object> interfaces = new List<object>(count);

                            for (int i = 0; i < count; i++)
                                interfaces.Add(Pop(context));

                            if (constructor is Avm1Object constructorObject)
                                constructorObject.Set("__interfaces", interfaces);
                            break;
                        }

                        case 0x30:
                        {
                            object maximum = Pop(context);
                            Push(context, InvokeExternal("random", new object[] { maximum }, context.Target));
                            break;
                        }
                        case 0x31: Push(context, Encoding.UTF8.GetByteCount(ToAvm1String(Pop(context)))); break;
                        case 0x32:
                        {
                            string text = ToAvm1String(Pop(context));
                            Push(context, text.Length > 0 ? (int)text[0] : 0);
                            break;
                        }
                        case 0x33: Push(context, ((char)(int)ToNumber(Pop(context))).ToString()); break;
                        case 0x34: Push(context, InvokeExternal("getTimer", EmptyArguments, context.Target)); break;
                        case 0x35:
                        {
                            int count = (int)ToNumber(Pop(context));
                            int index = Math.Max(0, (int)ToNumber(Pop(context)) - 1);
                            string text = ToAvm1String(Pop(context));
                            Push(context, index < text.Length
                                ? text.Substring(index, Math.Min(Math.Max(0, count), text.Length - index))
                                : string.Empty);
                            break;
                        }
                        case 0x36:
                        {
                            string text = ToAvm1String(Pop(context));
                            Push(context, text.Length > 0 ? (int)text[0] : 0);
                            break;
                        }
                        case 0x37: Push(context, ((char)(int)ToNumber(Pop(context))).ToString()); break;

                        case 0x3A: // Delete
                        {
                            string memberName = ToAvm1String(Pop(context));
                            object target = Pop(context);
                            Push(context, target is Avm1Object objectTarget && objectTarget.Remove(memberName));
                            break;
                        }

                        case 0x3B: // Delete2
                            Push(context, DeleteVariable(context, ToAvm1String(Pop(context))));
                            break;

                        case 0x3C: // DefineLocal
                        {
                            object value = Pop(context);
                            string name = ToAvm1String(Pop(context));
                            DefineLocal(context, name, value);
                            break;
                        }

                        case 0x3D: // CallFunction
                        {
                            string name = ToAvm1String(Pop(context));
                            List<object> arguments = PopArguments(context);
                            object function = ResolveVariable(context, name);
                            object result = IsCallable(function)
                                ? CallValue(function, arguments, context.Target ?? RootObject)
                                : InvokeExternal(name, arguments, context.Target ?? RootObject);
                            Push(context, result);
                            break;
                        }

                        case 0x3E: // Return
                            context.ReturnValue = Pop(context);
                            context.Returned = true;
                            break;

                        case 0x3F: BinaryNumber(context, (a, b) => b == 0d ? double.NaN : a % b); break;

                        case 0x40: // NewObject
                        {
                            string className = ToAvm1String(Pop(context));
                            List<object> arguments = PopArguments(context);
                            object constructor = ResolveVariable(context, className);
                            object created = IsCallable(constructor)
                                ? ConstructValue(constructor, arguments)
                                : InvokeExternal("new:" + className, arguments, RootObject);

                            if (created == null || ReferenceEquals(created, Undefined))
                            {
                                Avm1Object genericObject = CreateObject();
                                genericObject.Set("__class", className);
                                created = genericObject;
                            }

                            Push(context, created);
                            break;
                        }

                        case 0x41: // DefineLocal2
                            DefineLocal(context, ToAvm1String(Pop(context)), Undefined);
                            break;

                        case 0x42: // InitArray
                        {
                            int count = ClampArgumentCount(ToNumber(Pop(context)));
                            List<object> array = new List<object>(count);

                            for (int i = 0; i < count; i++)
                                array.Add(Pop(context));

                            Push(context, array);
                            break;
                        }

                        case 0x43: // InitObject
                        {
                            int count = ClampArgumentCount(ToNumber(Pop(context)));
                            Avm1Object value = CreateObject();

                            for (int i = 0; i < count; i++)
                            {
                                object memberValue = Pop(context);
                                string memberName = ToAvm1String(Pop(context));
                                value.Set(memberName, memberValue);
                            }

                            Push(context, value);
                            break;
                        }

                        case 0x44: Push(context, TypeOf(Pop(context))); break;
                        case 0x45:
                        {
                            object target = Pop(context);
                            Push(context, target is Avm1Object objectTarget
                                ? ToAvm1String(objectTarget.Get("_target"))
                                : string.Empty);
                            break;
                        }
                        case 0x46: // Enumerate
                        {
                            string variableName = ToAvm1String(Pop(context));
                            PushEnumeration(context, ResolveVariable(context, variableName));
                            break;
                        }
                        case 0x48: BinaryCompare(context, (a, b) => CompareValues(a, b) < 0); break;
                        case 0x49: BinaryCompare(context, ValuesEqual); break;
                        case 0x4A: Push(context, ToNumber(Pop(context))); break;
                        case 0x4B: Push(context, ToAvm1String(Pop(context))); break;

                        case 0x4C:
                            Push(context, Peek(context));
                            break;

                        case 0x4D:
                        {
                            object a = Pop(context);
                            object b = Pop(context);
                            Push(context, a);
                            Push(context, b);
                            break;
                        }

                        case 0x4E: // GetMember
                        {
                            string memberName = ToAvm1String(Pop(context));
                            object target = Pop(context);
                            Push(context, GetMember(target, memberName));
                            break;
                        }

                        case 0x4F: // SetMember
                        {
                            object value = Pop(context);
                            string memberName = ToAvm1String(Pop(context));
                            object target = Pop(context);
                            SetMember(target, memberName, value);
                            break;
                        }

                        case 0x50: Push(context, ToNumber(Pop(context)) + 1d); break;
                        case 0x51: Push(context, ToNumber(Pop(context)) - 1d); break;

                        case 0x52: // CallMethod
                        {
                            string methodName = ToAvm1String(Pop(context));
                            object target = Pop(context);
                            List<object> arguments = PopArguments(context);
                            object method;
                            object callReceiver;

                            if (target is Avm1SuperReference superReference)
                            {
                                method = string.IsNullOrEmpty(methodName)
                                    ? superReference.SuperClass
                                    : GetMember(superReference, methodName);
                                callReceiver = superReference.Receiver;
                            }
                            else
                            {
                                method = string.IsNullOrEmpty(methodName) && IsCallable(target)
                                    ? target
                                    : GetMember(target, methodName);
                                callReceiver = string.IsNullOrEmpty(methodName) && IsCallable(target)
                                    ? context.Target ?? RootObject
                                    : target;
                            }
                            object result = IsCallable(method)
                                ? CallValue(method, arguments, callReceiver)
                                : InvokeExternal(methodName, arguments, callReceiver);
                            Push(context, result);
                            break;
                        }

                        case 0x53: // NewMethod
                        {
                            string methodName = ToAvm1String(Pop(context));
                            object target = Pop(context);
                            List<object> arguments = PopArguments(context);
                            object method = string.IsNullOrEmpty(methodName) && IsCallable(target)
                                ? target
                                : GetMember(target, methodName);
                            Push(context, IsCallable(method)
                                ? ConstructValue(method, arguments)
                                : InvokeExternal("new:" + methodName, arguments, target));
                            break;
                        }

                        case 0x54: // InstanceOf
                        {
                            object constructor = Pop(context);
                            object value = Pop(context);
                            Push(context, IsInstanceOf(value, constructor));
                            break;
                        }

                        case 0x55: // Enumerate2
                            PushEnumeration(context, Pop(context));
                            break;

                        case 0x60: BinaryInteger(context, (a, b) => a & b); break;
                        case 0x61: BinaryInteger(context, (a, b) => a | b); break;
                        case 0x62: BinaryInteger(context, (a, b) => a ^ b); break;
                        case 0x63: BinaryInteger(context, (a, b) => a << (b & 31)); break;
                        case 0x64: BinaryInteger(context, (a, b) => a >> (b & 31)); break;
                        case 0x65: BinaryInteger(context, (a, b) => (int)((uint)a >> (b & 31))); break;
                        case 0x66: BinaryCompare(context, (a, b) => a != null && b != null && a.GetType() == b.GetType() && ValuesEqual(a, b)); break;
                        case 0x67: BinaryCompare(context, (a, b) => CompareValues(a, b) > 0); break;
                        case 0x68: BinaryCompare(context, (a, b) => string.Compare(ToAvm1String(a), ToAvm1String(b), StringComparison.Ordinal) > 0); break;

                        case 0x69: // Extends
                        {
                            object superClass = Pop(context);
                            object subClass = Pop(context);
                            ApplyInheritance(subClass, superClass);
                            break;
                        }

                        case 0x81: // GotoFrame
                            if (payloadStart + 2 <= payloadEnd)
                                InvokeExternal("gotoFrame", new object[] { ReadUInt16(code, payloadStart) + 1 }, context.Target);
                            break;

                        case 0x83: // GetURL
                        {
                            int urlCursor = payloadStart;
                            string url = ReadString(code, ref urlCursor, payloadEnd);
                            string target = ReadString(code, ref urlCursor, payloadEnd);
                            InvokeExternal("getURL", new object[] { url, target, 0 }, context.Target);
                            break;
                        }

                        case 0x87: // StoreRegister
                            if (payloadStart < payloadEnd)
                                context.Registers[code[payloadStart]] = Peek(context);
                            break;

                        case 0x88: // ConstantPool
                            ReadConstantPool(code, payloadStart, payloadEnd, context.ConstantPool);
                            break;

                        case 0x89: // StrictMode
                        case 0x8A: // WaitForFrame (the complete SWF is already loaded)
                            break;

                        case 0x8B: // SetTarget
                        {
                            int targetCursor = payloadStart;
                            string targetName = ReadString(code, ref targetCursor, payloadEnd);
                            context.Target = string.IsNullOrEmpty(targetName)
                                ? context.OriginalTarget ?? RootObject
                                : ResolveTarget(context, targetName);
                            break;
                        }

                        case 0x8C: // GoToLabel
                        {
                            int labelCursor = payloadStart;
                            string label = ReadString(code, ref labelCursor, payloadEnd);
                            InvokeExternal("gotoLabel", new object[] { label }, context.Target);
                            break;
                        }

                        case 0x8E: // DefineFunction2
                        case 0x9B: // DefineFunction
                        {
                            Avm1Function function = ParseFunction(
                                code,
                                opcode,
                                payloadStart,
                                payloadEnd,
                                end,
                                context.ConstantPool,
                                out int bodyEnd
                            );

                            if (function != null)
                            {
                                RegisterFunction(context, function);
                                p = bodyEnd;
                                continue;
                            }
                            break;
                        }

                        case 0x8D: // WaitForFrame2
                            Pop(context);
                            break;

                        case 0x8F: // Try
                        {
                            ExecuteTryAction(
                                code,
                                payloadStart,
                                payloadEnd,
                                context,
                                ref instructionBudget
                            );
                            p = payloadEnd;
                            continue;
                        }

                        case 0x94: // With
                        {
                            if (payloadStart + 2 > payloadEnd)
                                break;

                            int bodyStart = payloadStart + 2;
                            int bodyEnd = Math.Min(payloadEnd, bodyStart + ReadUInt16(code, payloadStart));
                            object scopeValue = Pop(context);

                            if (scopeValue is Avm1Object scopeObject)
                            {
                                context.ScopeObjects.Add(scopeObject);

                                try
                                {
                                    ExecuteBlock(code, bodyStart, bodyEnd, context, ref instructionBudget);
                                }
                                finally
                                {
                                    context.ScopeObjects.RemoveAt(context.ScopeObjects.Count - 1);
                                }
                            }

                            p = bodyEnd;
                            continue;
                        }

                        case 0x96:
                            ReadPushValues(code, payloadStart, payloadEnd, context);
                            break;

                        case 0x99: // Jump
                            if (payloadStart + 2 <= payloadEnd)
                            {
                                int target = payloadEnd + ReadInt16(code, payloadStart);
                                p = Clamp(target, start, end);
                                continue;
                            }
                            break;

                        case 0x9A: // GetURL2
                        {
                            byte flags = payloadStart < payloadEnd ? code[payloadStart] : (byte)0;
                            string target = ToAvm1String(Pop(context));
                            string url = ToAvm1String(Pop(context));
                            InvokeExternal("getURL", new object[] { url, target, flags }, context.Target);
                            break;
                        }

                        case 0x9D: // If
                            if (ToBoolean(Pop(context)) && payloadStart + 2 <= payloadEnd)
                            {
                                int target = payloadEnd + ReadInt16(code, payloadStart);
                                p = Clamp(target, start, end);
                                continue;
                            }
                            break;

                        case 0x9E: // Call
                            InvokeExternal("call", new object[] { Pop(context) }, context.Target);
                            break;

                        case 0x9F: // GotoFrame2
                        {
                            byte flags = payloadStart < payloadEnd ? code[payloadStart] : (byte)0;
                            object frame = Pop(context);

                            if ((flags & 0x02) != 0 && payloadStart + 3 <= payloadEnd)
                                frame = ToNumber(frame) + ReadUInt16(code, payloadStart + 1);

                            string method = (flags & 0x01) != 0 ? "gotoAndPlay" : "gotoAndStop";
                            InvokeExternal(method, new object[] { frame }, context.Target);
                            break;
                        }

                        default:
                            if (VerboseLogging)
                                Debug.Log("AVM1 opcode 0x" + opcode.ToString("X2") + " skipped at " + actionStart);
                            break;
                    }
                }
                catch (Avm1ThrownException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ReportWarning(
                        "AVM1 opcode 0x" + opcode.ToString("X2") +
                        " failed at " + actionStart + ": " + exception.Message
                    );
                }

                p = payloadEnd;
            }
        }

        private void ExecuteTryAction(
            byte[] code,
            int payloadStart,
            int payloadEnd,
            ExecutionContext context,
            ref int instructionBudget
        )
        {
            int cursor = payloadStart;

            if (cursor + 7 > payloadEnd)
                return;

            byte flags = code[cursor++];
            int trySize = ReadUInt16(code, cursor);
            cursor += 2;
            int catchSize = ReadUInt16(code, cursor);
            cursor += 2;
            int finallySize = ReadUInt16(code, cursor);
            cursor += 2;
            bool catchInRegister = (flags & 0x04) != 0;
            byte catchRegister = 0;
            string catchName = string.Empty;

            if (catchInRegister)
            {
                if (cursor >= payloadEnd)
                    return;

                catchRegister = code[cursor++];
            }
            else
            {
                catchName = ReadString(code, ref cursor, payloadEnd);
            }

            int tryStart = cursor;
            int tryEnd = Math.Min(payloadEnd, tryStart + trySize);
            int catchStart = tryEnd;
            int catchEnd = Math.Min(payloadEnd, catchStart + catchSize);
            int finallyStart = catchEnd;
            int finallyEnd = Math.Min(payloadEnd, finallyStart + finallySize);
            Avm1ThrownException pending = null;

            try
            {
                ExecuteBlock(code, tryStart, tryEnd, context, ref instructionBudget);
            }
            catch (Avm1ThrownException thrown)
            {
                pending = thrown;

                if ((flags & 0x01) != 0)
                {
                    pending = null;

                    if (catchInRegister)
                    {
                        object previous = context.Registers[catchRegister];
                        context.Registers[catchRegister] = thrown.Value;

                        try
                        {
                            ExecuteBlock(code, catchStart, catchEnd, context, ref instructionBudget);
                        }
                        catch (Avm1ThrownException catchThrown)
                        {
                            pending = catchThrown;
                        }
                        finally
                        {
                            context.Registers[catchRegister] = previous;
                        }
                    }
                    else
                    {
                        Dictionary<string, object> locals = EnsureLocals(context);
                        bool hadPrevious = locals.TryGetValue(catchName, out object previous);
                        locals[catchName] = thrown.Value;

                        try
                        {
                            ExecuteBlock(code, catchStart, catchEnd, context, ref instructionBudget);
                        }
                        catch (Avm1ThrownException catchThrown)
                        {
                            pending = catchThrown;
                        }
                        finally
                        {
                            if (hadPrevious)
                                locals[catchName] = previous;
                            else
                                locals.Remove(catchName);
                        }
                    }
                }
            }
            finally
            {
                if ((flags & 0x02) != 0)
                {
                    bool returnedBeforeFinally = context.Returned;
                    object returnValueBeforeFinally = context.ReturnValue;
                    context.Returned = false;

                    ExecuteBlock(
                        code,
                        finallyStart,
                        finallyEnd,
                        context,
                        ref instructionBudget
                    );

                    if (!context.Returned && returnedBeforeFinally)
                    {
                        context.Returned = true;
                        context.ReturnValue = returnValueBeforeFinally;
                    }
                }
            }

            if (pending != null && !context.Returned)
                throw pending;
        }

        private void ScanFunctionDefinitions(
            byte[] code,
            int start,
            int end,
            List<string> constantPool
        )
        {
            int p = start;

            while (p < end)
            {
                byte opcode = code[p++];

                if (opcode == 0)
                    return;

                int length = 0;

                if (opcode >= 0x80)
                {
                    if (p + 2 > end)
                        return;

                    length = ReadUInt16(code, p);
                    p += 2;
                }

                int payloadStart = p;
                int payloadEnd = Math.Min(end, p + length);

                if (opcode == 0x88)
                {
                    ReadConstantPool(code, payloadStart, payloadEnd, constantPool);
                }
                else if (opcode == 0x8E || opcode == 0x9B)
                {
                    Avm1Function function = ParseFunction(
                        code,
                        opcode,
                        payloadStart,
                        payloadEnd,
                        end,
                        constantPool,
                        out int bodyEnd
                    );

                    if (function != null)
                    {
                        if (!string.IsNullOrEmpty(function.Name))
                        {
                            globals[function.Name] = function;
                            DefinedFunctionCount++;
                        }

                        p = bodyEnd;
                        continue;
                    }
                }

                p = payloadEnd;
            }
        }

        private Avm1Function ParseFunction(
            byte[] code,
            byte opcode,
            int payloadStart,
            int payloadEnd,
            int blockEnd,
            List<string> constantPool,
            out int bodyEnd
        )
        {
            bodyEnd = payloadEnd;
            int cursor = payloadStart;
            string functionName = ReadString(code, ref cursor, payloadEnd);

            if (cursor + 2 > payloadEnd)
                return null;

            int parameterCount = ReadUInt16(code, cursor);
            cursor += 2;
            byte registerCount = 0;
            ushort flags = 0;
            Avm1Parameter[] parameters = new Avm1Parameter[parameterCount];

            if (opcode == 0x8E)
            {
                if (cursor + 3 > payloadEnd)
                    return null;

                registerCount = code[cursor++];
                flags = (ushort)ReadUInt16(code, cursor);
                cursor += 2;

                for (int i = 0; i < parameterCount; i++)
                {
                    if (cursor >= payloadEnd)
                        return null;

                    byte register = code[cursor++];
                    parameters[i] = new Avm1Parameter(
                        ReadString(code, ref cursor, payloadEnd),
                        register
                    );
                }
            }
            else
            {
                for (int i = 0; i < parameterCount; i++)
                    parameters[i] = new Avm1Parameter(ReadString(code, ref cursor, payloadEnd), 0);
            }

            if (cursor + 2 > payloadEnd)
                return null;

            int codeSize = ReadUInt16(code, cursor);
            cursor += 2;

            int bodyStart = payloadEnd - cursor >= codeSize ? cursor : payloadEnd;
            bodyEnd = Math.Min(blockEnd, bodyStart + codeSize);
            byte[] body = new byte[Math.Max(0, bodyEnd - bodyStart)];
            Array.Copy(code, bodyStart, body, 0, body.Length);

            return new Avm1Function(
                functionName,
                parameters,
                registerCount,
                flags,
                body,
                new List<string>(constantPool),
                nameComparer
            );
        }

        private void RegisterFunction(ExecutionContext context, Avm1Function function)
        {
            DefinedFunctionCount++;

            function.CapturedLocals = context?.Locals;
            function.CapturedOuterLocals = context != null
                ? new List<Dictionary<string, object>>(context.OuterLocals)
                : new List<Dictionary<string, object>>();
            function.CapturedScopes = context != null
                ? new List<Avm1Object>(context.ScopeObjects)
                : new List<Avm1Object>();
            function.DefiningTarget = context?.Target;

            if (string.IsNullOrEmpty(function.Name))
                Push(context, function);
            else
                SetVariable(context, function.Name, function);
        }

        private object CallValue(object callable, IReadOnlyList<object> arguments, object thisObject)
        {
            if (callable is Avm1NativeFunction native)
                return native.Invoke(arguments ?? EmptyArguments);

            if (!(callable is Avm1Function function))
                return Undefined;

            Dictionary<string, object> locals =
                new Dictionary<string, object>(nameComparer);
            ExecutionContext context = new ExecutionContext(
                locals,
                new List<string>(function.ConstantPool),
                true
            );
            object resolvedThis = thisObject ?? RootObject;
            context.Target = resolvedThis;
            context.OriginalTarget = resolvedThis;
            context.OuterLocals.AddRange(function.CapturedOuterLocals);

            if (function.CapturedLocals != null)
                context.OuterLocals.Insert(0, function.CapturedLocals);

            context.ScopeObjects.AddRange(function.CapturedScopes);
            List<object> argumentArray = new List<object>(arguments ?? EmptyArguments);
            object superClass = ResolveSuperClass(resolvedThis, function);

            if ((function.Flags & 0x0002) == 0)
                locals["this"] = resolvedThis;

            if ((function.Flags & 0x0008) == 0)
                locals["arguments"] = argumentArray;

            if ((function.Flags & 0x0020) == 0)
                locals["super"] = superClass;

            byte preloadRegister = 1;
            PreloadRegister(function, 0x0001, resolvedThis, context, ref preloadRegister);
            PreloadRegister(function, 0x0004, argumentArray, context, ref preloadRegister);
            PreloadRegister(function, 0x0010, superClass, context, ref preloadRegister);
            PreloadRegister(function, 0x0040, RootObject, context, ref preloadRegister);
            PreloadRegister(function, 0x0080, GetMember(resolvedThis, "_parent"), context, ref preloadRegister);
            PreloadRegister(function, 0x0100, RootObject, context, ref preloadRegister);

            for (int i = 0; i < function.Parameters.Length; i++)
            {
                object argument = i < arguments.Count ? arguments[i] : Undefined;
                Avm1Parameter parameter = function.Parameters[i];
                locals[parameter.Name] = argument;

                if (parameter.Register > 0)
                    context.Registers[parameter.Register] = argument;
            }

            int budget = DefaultInstructionBudget;
            ExecuteBlock(function.Code, 0, function.Code.Length, context, ref budget);
            return context.ReturnValue;
        }

        private static object ResolveSuperClass(object thisObject, Avm1Function function)
        {
            if (thisObject is Avm1Object instance &&
                instance.Get("__constructor") is Avm1Object constructor &&
                constructor.Get("__super") is object superClass)
            {
                return new Avm1SuperReference(instance, superClass);
            }

            if (function?.DefiningTarget is Avm1Object definingObject &&
                definingObject.Get("__constructor") is Avm1Object definingConstructor &&
                definingConstructor.Get("__super") is object definingSuper)
            {
                return new Avm1SuperReference(thisObject, definingSuper);
            }

            return Undefined;
        }

        private object ConstructValue(object constructor, IReadOnlyList<object> arguments)
        {
            if (!IsCallable(constructor))
                return Undefined;

            Avm1Object instance = constructor is Avm1Object constructorObjectForComparer
                ? new Avm1Object(constructorObjectForComparer.NameComparer)
                : CreateObject();

            if (constructor is Avm1Object constructorObject)
            {
                instance.Prototype = constructorObject.Get("prototype") as Avm1Object;
                instance.Set("__constructor", constructorObject);
            }

            object result = CallValue(constructor, arguments ?? EmptyArguments, instance);
            return result is Avm1Object ? result : instance;
        }

        private static bool IsInstanceOf(object value, object constructor)
        {
            if (!(value is Avm1Object instance) || !(constructor is Avm1Object classObject))
                return false;

            Avm1Object expectedPrototype = classObject.Get("prototype") as Avm1Object;

            if (expectedPrototype == null)
                return false;

            Avm1Object current = instance.Prototype;
            int guard = 0;

            while (current != null && guard++ < 256)
            {
                if (ReferenceEquals(current, expectedPrototype))
                    return true;

                current = current.Prototype;
            }

            object instanceConstructor = instance.Get("__constructor");
            guard = 0;

            while (instanceConstructor is Avm1Object actualClass && guard++ < 256)
            {
                if (actualClass.Get("__interfaces") is IList<object> interfaces)
                {
                    for (int i = 0; i < interfaces.Count; i++)
                        if (ReferenceEquals(interfaces[i], constructor)) return true;
                }

                instanceConstructor = actualClass.Get("__super");
            }

            return false;
        }

        private static void ApplyInheritance(object subClass, object superClass)
        {
            if (!(subClass is Avm1Object child) || !(superClass is Avm1Object parent))
                return;

            Avm1Object parentPrototype = parent.Get("prototype") as Avm1Object;
            Avm1Object previousPrototype = child.Get("prototype") as Avm1Object;
            Avm1Object childPrototype = new Avm1Object(child.NameComparer)
            {
                Prototype = parentPrototype
            };
            previousPrototype?.CopyMembersTo(childPrototype);
            childPrototype.Set("constructor", child);
            child.Set("prototype", childPrototype);
            child.Set("__super", parent);
        }

        private static void PushEnumeration(ExecutionContext context, object value)
        {
            Push(context, null);

            if (!(value is Avm1Object objectValue))
                return;

            List<string> names = objectValue.GetEnumerableMemberNames();

            for (int i = names.Count - 1; i >= 0; i--)
                Push(context, names[i]);
        }

        private static void PreloadRegister(
            Avm1Function function,
            ushort flag,
            object value,
            ExecutionContext context,
            ref byte register
        )
        {
            if ((function.Flags & flag) == 0 || register == 0)
                return;

            context.Registers[register] = value;
            register++;
        }

        private static void ReadConstantPool(
            byte[] code,
            int start,
            int end,
            List<string> destination
        )
        {
            destination.Clear();

            if (start + 2 > end)
                return;

            int cursor = start;
            int count = ReadUInt16(code, cursor);
            cursor += 2;

            for (int i = 0; i < count && cursor < end; i++)
                destination.Add(ReadString(code, ref cursor, end));
        }

        private static void ReadPushValues(
            byte[] code,
            int start,
            int end,
            ExecutionContext context
        )
        {
            int p = start;

            while (p < end)
            {
                byte type = code[p++];

                switch (type)
                {
                    case 0:
                        Push(context, ReadString(code, ref p, end));
                        break;
                    case 1:
                        Push(context, p + 4 <= end ? BitConverter.ToSingle(code, p) : 0f);
                        p = Math.Min(end, p + 4);
                        break;
                    case 2:
                        Push(context, null);
                        break;
                    case 3:
                        Push(context, Undefined);
                        break;
                    case 4:
                        Push(context, p < end ? context.Registers[code[p++]] : Undefined);
                        break;
                    case 5:
                        Push(context, p < end && code[p++] != 0);
                        break;
                    case 6:
                        Push(context, ReadSwfDouble(code, ref p, end));
                        break;
                    case 7:
                        Push(context, p + 4 <= end ? BitConverter.ToInt32(code, p) : 0);
                        p = Math.Min(end, p + 4);
                        break;
                    case 8:
                    {
                        int index = p < end ? code[p++] : -1;
                        Push(context, ConstantAt(context.ConstantPool, index));
                        break;
                    }
                    case 9:
                    {
                        int index = p + 2 <= end ? ReadUInt16(code, p) : -1;
                        p = Math.Min(end, p + 2);
                        Push(context, ConstantAt(context.ConstantPool, index));
                        break;
                    }
                    default:
                        p = end;
                        break;
                }
            }
        }

        private Avm1Object BuildMathObject()
        {
            Avm1Object math = CreateObject();
            math.Set("min", new Avm1NativeFunction(args => AggregateNumbers(args, true)));
            math.Set("max", new Avm1NativeFunction(args => AggregateNumbers(args, false)));
            math.Set("abs", new Avm1NativeFunction(args => Math.Abs(NumberAt(args, 0))));
            math.Set("floor", new Avm1NativeFunction(args => Math.Floor(NumberAt(args, 0))));
            math.Set("ceil", new Avm1NativeFunction(args => Math.Ceiling(NumberAt(args, 0))));
            math.Set("round", new Avm1NativeFunction(args => Math.Floor(NumberAt(args, 0) + 0.5d)));
            math.Set("sin", new Avm1NativeFunction(args => Math.Sin(NumberAt(args, 0))));
            math.Set("cos", new Avm1NativeFunction(args => Math.Cos(NumberAt(args, 0))));
            math.Set("tan", new Avm1NativeFunction(args => Math.Tan(NumberAt(args, 0))));
            math.Set("asin", new Avm1NativeFunction(args => Math.Asin(NumberAt(args, 0))));
            math.Set("acos", new Avm1NativeFunction(args => Math.Acos(NumberAt(args, 0))));
            math.Set("atan", new Avm1NativeFunction(args => Math.Atan(NumberAt(args, 0))));
            math.Set("atan2", new Avm1NativeFunction(args => Math.Atan2(NumberAt(args, 0), NumberAt(args, 1))));
            math.Set("sqrt", new Avm1NativeFunction(args => Math.Sqrt(NumberAt(args, 0))));
            math.Set("pow", new Avm1NativeFunction(args => Math.Pow(NumberAt(args, 0), NumberAt(args, 1))));
            math.Set("exp", new Avm1NativeFunction(args => Math.Exp(NumberAt(args, 0))));
            math.Set("log", new Avm1NativeFunction(args => Math.Log(NumberAt(args, 0))));
            math.Set("random", new Avm1NativeFunction(args => randomSource.NextDouble()));
            math.Set("PI", Math.PI);
            math.Set("E", Math.E);
            math.Set("LN2", Math.Log(2d));
            math.Set("LN10", Math.Log(10d));
            math.Set("LOG2E", Math.Log(Math.E, 2d));
            math.Set("LOG10E", Math.Log10(Math.E));
            math.Set("SQRT1_2", Math.Sqrt(0.5d));
            math.Set("SQRT2", Math.Sqrt(2d));
            return math;
        }

        private static double AggregateNumbers(IReadOnlyList<object> values, bool minimum)
        {
            if (values == null || values.Count == 0)
                return minimum ? double.PositiveInfinity : double.NegativeInfinity;

            double result = NumberAt(values, 0);

            for (int i = 1; i < values.Count; i++)
            {
                double value = NumberAt(values, i);
                result = minimum ? Math.Min(result, value) : Math.Max(result, value);
            }

            return result;
        }

        private void RegisterCoreBuiltIns()
        {
            globals["parseInt"] = new Avm1NativeFunction(args => ParseInteger(args));
            globals["parseFloat"] = new Avm1NativeFunction(args => ParseFloatingPoint(args));
            globals["isNaN"] = new Avm1NativeFunction(args =>
                double.IsNaN(NumberAt(args, 0)));
            globals["isFinite"] = new Avm1NativeFunction(args =>
            {
                double value = NumberAt(args, 0);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            });
            globals["Number"] = new Avm1NativeFunction(args => NumberAt(args, 0));
            globals["String"] = new Avm1NativeFunction(args =>
                args != null && args.Count > 0 ? ToAvm1String(args[0]) : string.Empty);
            globals["Boolean"] = new Avm1NativeFunction(args =>
                args != null && args.Count > 0 && ToBoolean(args[0]));
            Avm1NativeFunction objectConstructor = new Avm1NativeFunction(args =>
                args != null && args.Count > 0 && args[0] is Avm1Object objectValue
                    ? objectValue
                    : CreateObject());
            objectConstructor.Set("registerClass", new Avm1NativeFunction(args =>
            {
                if (args.Count < 2 || string.IsNullOrEmpty(ToAvm1String(args[0])) ||
                    !IsCallable(args[1]))
                {
                    return false;
                }

                registeredClasses[ToAvm1String(args[0])] = args[1];
                return true;
            }));
            globals["Object"] = objectConstructor;
            globals["Array"] = new Avm1NativeFunction(args =>
                new List<object>(args ?? EmptyArguments));
            globals["MovieClip"] = new Avm1NativeFunction(args => null);
            globals["Button"] = new Avm1NativeFunction(args => null);
            globals["TextField"] = new Avm1NativeFunction(args => null);
            globals["ASSetPropFlags"] = new Avm1NativeFunction(args => null);
            Avm1Object asBroadcaster = CreateObject();
            asBroadcaster.Set("initialize", new Avm1NativeFunction(args =>
                args.Count > 0 && args[0] is Avm1Object));
            globals["AsBroadcaster"] = asBroadcaster;
            globals["Mouse"] = CreateObject();
            Avm1Object key = CreateObject();
            key.Set("getCode", new Avm1NativeFunction(args => ToNumber(key.Get("__lastCode"))));
            key.Set("getAscii", new Avm1NativeFunction(args => ToNumber(key.Get("__lastAscii"))));
            key.Set("isDown", new Avm1NativeFunction(args =>
                args.Count > 0 && pressedKeys.Contains((int)ToNumber(args[0]))));
            key.Set("isToggled", new Avm1NativeFunction(args => false));
            globals["Key"] = key;

            foreach (KeyValuePair<string, object> builtIn in globals)
            {
                if (!RootObject.TryGetOwn(builtIn.Key, out _))
                    RootObject.Set(builtIn.Key, builtIn.Value);
            }
        }

        private static object ParseInteger(IReadOnlyList<object> arguments)
        {
            string text = arguments != null && arguments.Count > 0
                ? ToAvm1String(arguments[0]).TrimStart()
                : string.Empty;
            int radix = arguments != null && arguments.Count > 1
                ? (int)ToNumber(arguments[1])
                : 0;
            bool negative = text.StartsWith("-", StringComparison.Ordinal);

            if (negative || text.StartsWith("+", StringComparison.Ordinal))
                text = text.Substring(1);

            if (radix == 0)
            {
                radix = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10;
            }

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

            return consumed == 0 ? double.NaN : (negative ? -result : result);
        }

        private static object ParseFloatingPoint(IReadOnlyList<object> arguments)
        {
            string text = arguments != null && arguments.Count > 0
                ? ToAvm1String(arguments[0]).TrimStart()
                : string.Empty;

            for (int length = text.Length; length > 0; length--)
            {
                if (double.TryParse(
                    text.Substring(0, length),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed
                ))
                {
                    return parsed;
                }
            }

            return double.NaN;
        }

        private static int DigitValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'z') return value - 'a' + 10;
            if (value >= 'A' && value <= 'Z') return value - 'A' + 10;
            return -1;
        }

        private Avm1Object BuildStageObject()
        {
            Avm1Object stage = CreateObject();
            stage.Set("scaleMode", "showAll");
            stage.Set("align", string.Empty);
            return stage;
        }

        private object InvokeExternal(
            string name,
            IReadOnlyList<object> arguments,
            object receiver
        )
        {
            if (ExternalMethod != null)
            {
                object methodResult = ExternalMethod(receiver, name, arguments ?? EmptyArguments);
                return methodResult ?? Undefined;
            }

            if (ExternalFunction != null)
            {
                object functionResult = ExternalFunction(name, arguments ?? EmptyArguments);
                return functionResult ?? Undefined;
            }

            return Undefined;
        }

        private void ReportWarning(string message)
        {
            if (Warning != null)
                Warning(message);
            else
                Debug.LogWarning(message);
        }

        private object ResolveVariable(ExecutionContext context, string name)
        {
            if (string.IsNullOrEmpty(name))
                return Undefined;

            int targetSeparator = name.LastIndexOf(':');

            if (targetSeparator >= 0)
            {
                object variableTarget = ResolveTarget(context, name.Substring(0, targetSeparator));
                return GetMember(variableTarget, name.Substring(targetSeparator + 1));
            }

            if (context != null && context.Locals != null && context.Locals.TryGetValue(name, out object local))
                return local;

            if (context != null)
            {
                for (int i = 0; i < context.OuterLocals.Count; i++)
                {
                    Dictionary<string, object> outer = context.OuterLocals[i];

                    if (outer != null && outer.TryGetValue(name, out object captured))
                        return captured;
                }

                for (int i = context.ScopeObjects.Count - 1; i >= 0; i--)
                {
                    if (context.ScopeObjects[i] != null &&
                        context.ScopeObjects[i].TryGet(name, out object scoped))
                    {
                        return scoped;
                    }
                }
            }

            if (context != null && context.Target is Avm1Object targetObject &&
                targetObject.TryGet(name, out object targetMember))
            {
                return targetMember;
            }

            if (globals.TryGetValue(name, out object global))
                return global;

            if (name.IndexOf('.') >= 0)
            {
                string[] parts = name.Split('.');
                object current = ResolveVariable(context, parts[0]);

                for (int i = 1; i < parts.Length; i++)
                    current = GetMember(current, parts[i]);

                return current;
            }

            object rootMember = RootObject.Get(name);
            return rootMember ?? Undefined;
        }

        private void SetVariable(ExecutionContext context, string name, object value)
        {
            if (string.IsNullOrEmpty(name))
                return;

            int colonSeparator = name.LastIndexOf(':');

            if (colonSeparator >= 0)
            {
                object pathTarget = ResolveTarget(context, name.Substring(0, colonSeparator));
                SetMember(pathTarget, name.Substring(colonSeparator + 1), value);
                return;
            }

            int separator = name.LastIndexOf('.');

            if (separator > 0)
            {
                object target = ResolveVariable(context, name.Substring(0, separator));
                SetMember(target, name.Substring(separator + 1), value);
                return;
            }

            if (context != null && context.Locals != null && context.Locals.ContainsKey(name))
            {
                context.Locals[name] = value;
                return;
            }

            if (context != null)
            {
                for (int i = 0; i < context.OuterLocals.Count; i++)
                {
                    Dictionary<string, object> outer = context.OuterLocals[i];

                    if (outer != null && outer.ContainsKey(name))
                    {
                        outer[name] = value;
                        return;
                    }
                }

                for (int i = context.ScopeObjects.Count - 1; i >= 0; i--)
                {
                    Avm1Object scope = context.ScopeObjects[i];

                    if (scope != null && scope.TryGet(name, out _))
                    {
                        scope.Set(name, value);
                        return;
                    }
                }
            }

            if (context != null && context.Target is Avm1Object targetObject &&
                targetObject != RootObject)
            {
                targetObject.Set(name, value);
                return;
            }

            globals[name] = value;
            RootObject.Set(name, value);
        }

        private void DefineLocal(ExecutionContext context, string name, object value)
        {
            if (string.IsNullOrEmpty(name))
                return;

            // ActionDefineLocal inside a function creates a real function-local variable.
            // A DoAction/DoInitAction block, however, runs on a timeline object. Variables
            // declared there must survive the end of that action block as MovieClip members.
            if (context != null && context.IsFunction)
            {
                EnsureLocals(context)[name] = value;
                return;
            }

            SetVariable(context, name, value);
        }

        private bool DeleteVariable(ExecutionContext context, string name)
        {
            if (context != null && context.Locals != null && context.Locals.Remove(name))
                return true;

            if (context != null)
            {
                for (int i = 0; i < context.OuterLocals.Count; i++)
                {
                    Dictionary<string, object> outer = context.OuterLocals[i];

                    if (outer != null && outer.Remove(name))
                        return true;
                }

                for (int i = context.ScopeObjects.Count - 1; i >= 0; i--)
                {
                    Avm1Object scope = context.ScopeObjects[i];

                    if (scope != null && scope.Remove(name))
                        return true;
                }
            }

            if (context != null && context.Target is Avm1Object targetObject &&
                targetObject != RootObject && targetObject.Remove(name))
            {
                return true;
            }

            bool removedRoot = RootObject.Remove(name);
            bool removedGlobal = globals.Remove(name);
            return removedRoot || removedGlobal;
        }

        private object ResolveTarget(ExecutionContext context, object target)
        {
            if (target is string path)
            {
                if (string.IsNullOrEmpty(path))
                    return context?.Target ?? RootObject;

                if (path == "/" || string.Equals(path, "_root", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "_level0", StringComparison.OrdinalIgnoreCase))
                {
                    return RootObject;
                }

                if (path.IndexOf('/') >= 0)
                {
                    Avm1Object current = path.StartsWith("/", StringComparison.Ordinal)
                        ? RootObject
                        : context?.Target as Avm1Object ?? RootObject;
                    string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                    for (int i = 0; i < parts.Length && current != null; i++)
                    {
                        string part = parts[i];

                        if (part == ".")
                            continue;

                        if (part == "..")
                        {
                            current = current.Get("_parent") as Avm1Object ?? RootObject;
                            continue;
                        }

                        if (string.Equals(part, "_root", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(part, "_level0", StringComparison.OrdinalIgnoreCase))
                        {
                            current = RootObject;
                            continue;
                        }

                        current = GetMember(current, part) as Avm1Object;
                    }

                    return current ?? Undefined;
                }

                return ResolveVariable(context, path);
            }

            return target;
        }

        private object GetMember(object target, string name)
        {
            if (ReferenceEquals(target, Undefined) || target == null)
                return Undefined;

            if (target is Avm1SuperReference superReference)
            {
                if (superReference.SuperClass is Avm1Object superClass &&
                    superClass.Get("prototype") is Avm1Object superPrototype &&
                    superPrototype.TryGet(name, out object superMember))
                {
                    return superMember;
                }

                return Undefined;
            }

            if (target is Avm1Object avmObject)
            {
                if (avmObject.Get("__getters") is Avm1Object getterMap &&
                    getterMap.Get(name) is object getter && IsCallable(getter))
                {
                    return CallValue(getter, EmptyArguments, avmObject);
                }

                if (avmObject.TryGet(name, out object value))
                    return value;

                switch ((name ?? string.Empty).ToLowerInvariant())
                {
                    case "hasownproperty":
                        return new Avm1NativeFunction(args =>
                            args.Count > 0 && avmObject.TryGetOwn(ToAvm1String(args[0]), out _));
                    case "isprototypeof":
                        return new Avm1NativeFunction(args =>
                        {
                            Avm1Object current = args.Count > 0
                                ? (args[0] as Avm1Object)?.Prototype
                                : null;
                            int guard = 0;

                            while (current != null && guard++ < 256)
                            {
                                if (ReferenceEquals(current, avmObject))
                                    return true;

                                current = current.Prototype;
                            }

                            return false;
                        });
                    case "tostring":
                        return new Avm1NativeFunction(args => "[object Object]");
                    case "valueof":
                        return new Avm1NativeFunction(args => avmObject);
                    case "addproperty":
                        return new Avm1NativeFunction(args =>
                        {
                            if (args.Count < 3)
                                return false;

                            string propertyName = ToAvm1String(args[0]);
                            StorePropertyAccessor(avmObject, "__getters", propertyName, args[1]);
                            StorePropertyAccessor(avmObject, "__setters", propertyName, args[2]);
                            return true;
                        });
                    case "__definegetter__":
                        return new Avm1NativeFunction(args =>
                        {
                            if (args.Count < 2) return false;
                            StorePropertyAccessor(avmObject, "__getters", ToAvm1String(args[0]), args[1]);
                            return true;
                        });
                    case "__definesetter__":
                        return new Avm1NativeFunction(args =>
                        {
                            if (args.Count < 2) return false;
                            StorePropertyAccessor(avmObject, "__setters", ToAvm1String(args[0]), args[1]);
                            return true;
                        });
                    case "addeventlistener":
                        return new Avm1NativeFunction(args =>
                        {
                            if (args.Count < 2)
                                return false;

                            string eventName = ToAvm1String(args[0]);
                            Avm1Object eventMap = avmObject.Get("__eventListeners") as Avm1Object;

                            if (eventMap == null)
                            {
                                eventMap = CreateObject();
                                avmObject.Set("__eventListeners", eventMap);
                            }

                            List<object> listeners = eventMap.Get(eventName) as List<object>;

                            if (listeners == null)
                            {
                                listeners = new List<object>();
                                eventMap.Set(eventName, listeners);
                            }

                            if (!listeners.Contains(args[1]))
                                listeners.Add(args[1]);

                            return true;
                        });
                    case "removeeventlistener":
                        return new Avm1NativeFunction(args =>
                        {
                            if (args.Count < 2 ||
                                !(avmObject.Get("__eventListeners") is Avm1Object eventMap) ||
                                !(eventMap.Get(ToAvm1String(args[0])) is List<object> listeners))
                            {
                                return false;
                            }

                            return listeners.Remove(args[1]);
                        });
                    case "dispatchevent":
                        return new Avm1NativeFunction(args =>
                            args.Count > 0 && DispatchEvent(avmObject, args[0]));
                    case "addlistener":
                        return new Avm1NativeFunction(args =>
                        {
                            List<object> listeners = avmObject.Get("__listeners") as List<object>;

                            if (listeners == null)
                            {
                                listeners = new List<object>();
                                avmObject.Set("__listeners", listeners);
                            }

                            if (args.Count > 0 && !listeners.Contains(args[0]))
                                listeners.Add(args[0]);

                            return args.Count > 0;
                        });
                    case "removelistener":
                        return new Avm1NativeFunction(args =>
                            args.Count > 0 && avmObject.Get("__listeners") is List<object> listeners &&
                            listeners.Remove(args[0]));
                    case "broadcastmessage":
                        return new Avm1NativeFunction(args => BroadcastMessage(avmObject, args));
                    case "call" when IsCallable(avmObject):
                        return new Avm1NativeFunction(args =>
                        {
                            object receiver = args.Count > 0 ? args[0] : RootObject;
                            List<object> callArguments = new List<object>();

                            for (int i = 1; i < args.Count; i++)
                                callArguments.Add(args[i]);

                            return CallValue(avmObject, callArguments, receiver);
                        });
                    case "apply" when IsCallable(avmObject):
                        return new Avm1NativeFunction(args =>
                        {
                            object receiver = args.Count > 0 ? args[0] : RootObject;
                            IReadOnlyList<object> callArguments = args.Count > 1 &&
                                args[1] is IList<object> listArguments
                                    ? new List<object>(listArguments)
                                    : EmptyArguments;
                            return CallValue(avmObject, callArguments, receiver);
                        });
                }

                return Undefined;
            }

            if (target is IList<object> list)
            {
                if (string.Equals(name, "length", StringComparison.OrdinalIgnoreCase))
                    return list.Count;

                if (int.TryParse(name, out int index) && index >= 0 && index < list.Count)
                    return list[index];

                object method = GetArrayMethod(list, name);
                return method ?? Undefined;
            }

            if (target is string text)
            {
                if (string.Equals(name, "length", StringComparison.OrdinalIgnoreCase))
                    return text.Length;

                object method = GetStringMethod(text, name);
                return method ?? Undefined;
            }

            return Undefined;
        }

        private bool DispatchEvent(Avm1Object dispatcher, object eventValue)
        {
            string eventName = eventValue is Avm1Object eventObject
                ? ToAvm1String(eventObject.Get("type"))
                : ToAvm1String(eventValue);

            if (string.IsNullOrEmpty(eventName) ||
                !(dispatcher.Get("__eventListeners") is Avm1Object eventMap) ||
                !(eventMap.Get(eventName) is List<object> listeners))
            {
                return false;
            }

            object[] eventArguments = { eventValue };
            List<object> snapshot = new List<object>(listeners);

            for (int i = 0; i < snapshot.Count; i++)
            {
                object listener = snapshot[i];

                if (IsCallable(listener))
                {
                    CallValue(listener, eventArguments, dispatcher);
                }
                else if (listener is Avm1Object listenerObject)
                {
                    object handler = GetMember(listenerObject, eventName);

                    if (!IsCallable(handler))
                        handler = GetMember(listenerObject, "handleEvent");

                    if (IsCallable(handler))
                        CallValue(handler, eventArguments, listenerObject);
                }
            }

            return true;
        }

        private object BroadcastMessage(Avm1Object broadcaster, IReadOnlyList<object> arguments)
        {
            if (arguments == null || arguments.Count == 0 ||
                !(broadcaster.Get("__listeners") is List<object> listeners))
            {
                return false;
            }

            string messageName = ToAvm1String(arguments[0]);
            List<object> messageArguments = new List<object>();

            for (int i = 1; i < arguments.Count; i++)
                messageArguments.Add(arguments[i]);

            List<object> snapshot = new List<object>(listeners);

            for (int i = 0; i < snapshot.Count; i++)
            {
                if (!(snapshot[i] is Avm1Object listener))
                    continue;

                object handler = GetMember(listener, messageName);

                if (IsCallable(handler))
                    CallValue(handler, messageArguments, listener);
            }

            return true;
        }

        private object GetArrayMethod(IList<object> list, string name)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "push":
                    return new Avm1NativeFunction(args =>
                    {
                        for (int i = 0; i < args.Count; i++) list.Add(args[i]);
                        return list.Count;
                    });
                case "pop":
                    return new Avm1NativeFunction(args =>
                    {
                        if (list.Count == 0) return Undefined;
                        object value = list[list.Count - 1];
                        list.RemoveAt(list.Count - 1);
                        return value;
                    });
                case "shift":
                    return new Avm1NativeFunction(args =>
                    {
                        if (list.Count == 0) return Undefined;
                        object value = list[0];
                        list.RemoveAt(0);
                        return value;
                    });
                case "unshift":
                    return new Avm1NativeFunction(args =>
                    {
                        for (int i = 0; i < args.Count; i++) list.Insert(i, args[i]);
                        return list.Count;
                    });
                case "join":
                    return new Avm1NativeFunction(args => string.Join(
                        args.Count > 0 ? ToAvm1String(args[0]) : ",",
                        ToStringArray(list)
                    ));
                case "slice":
                    return new Avm1NativeFunction(args =>
                    {
                        int start = NormalizeIndex(args.Count > 0 ? ToNumber(args[0]) : 0d, list.Count);
                        int end = NormalizeIndex(args.Count > 1 ? ToNumber(args[1]) : list.Count, list.Count);
                        List<object> result = new List<object>();

                        for (int i = start; i < Math.Max(start, end); i++) result.Add(list[i]);
                        return result;
                    });
                case "splice":
                    return new Avm1NativeFunction(args =>
                    {
                        int start = NormalizeIndex(args.Count > 0 ? ToNumber(args[0]) : 0d, list.Count);
                        int deleteCount = args.Count > 1
                            ? Math.Max(0, Math.Min(list.Count - start, (int)ToNumber(args[1])))
                            : list.Count - start;
                        List<object> removed = new List<object>();

                        for (int i = 0; i < deleteCount; i++)
                        {
                            removed.Add(list[start]);
                            list.RemoveAt(start);
                        }

                        for (int i = 2; i < args.Count; i++) list.Insert(start + i - 2, args[i]);
                        return removed;
                    });
                case "concat":
                    return new Avm1NativeFunction(args =>
                    {
                        List<object> result = new List<object>(list);

                        for (int i = 0; i < args.Count; i++)
                        {
                            if (args[i] is IList<object> nested)
                            {
                                for (int n = 0; n < nested.Count; n++) result.Add(nested[n]);
                            }
                            else
                            {
                                result.Add(args[i]);
                            }
                        }

                        return result;
                    });
                case "reverse":
                    return new Avm1NativeFunction(args =>
                    {
                        for (int left = 0, right = list.Count - 1; left < right; left++, right--)
                        {
                            object value = list[left];
                            list[left] = list[right];
                            list[right] = value;
                        }

                        return list;
                    });
                case "indexof":
                    return new Avm1NativeFunction(args =>
                    {
                        object sought = args.Count > 0 ? args[0] : Undefined;

                        for (int i = 0; i < list.Count; i++)
                            if (ValuesEqual(list[i], sought)) return i;

                        return -1;
                    });
                case "tostring":
                    return new Avm1NativeFunction(args => string.Join(",", ToStringArray(list)));
                default:
                    return null;
            }
        }

        private static object GetStringMethod(string text, string name)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "charat": return new Avm1NativeFunction(args =>
                {
                    int index = (int)NumberAt(args, 0);
                    return index >= 0 && index < text.Length ? text[index].ToString() : string.Empty;
                });
                case "charcodeat": return new Avm1NativeFunction(args =>
                {
                    int index = (int)NumberAt(args, 0);
                    return index >= 0 && index < text.Length ? (double)text[index] : double.NaN;
                });
                case "indexof": return new Avm1NativeFunction(args => text.IndexOf(
                    args.Count > 0 ? ToAvm1String(args[0]) : string.Empty,
                    Math.Max(0, args.Count > 1 ? (int)ToNumber(args[1]) : 0),
                    StringComparison.Ordinal
                ));
                case "lastindexof": return new Avm1NativeFunction(args => text.LastIndexOf(
                    args.Count > 0 ? ToAvm1String(args[0]) : string.Empty,
                    StringComparison.Ordinal
                ));
                case "substr": return new Avm1NativeFunction(args =>
                {
                    int start = NormalizeIndex(args.Count > 0 ? ToNumber(args[0]) : 0d, text.Length);
                    int length = args.Count > 1
                        ? Math.Max(0, Math.Min(text.Length - start, (int)ToNumber(args[1])))
                        : text.Length - start;
                    return text.Substring(start, length);
                });
                case "substring": return new Avm1NativeFunction(args =>
                {
                    int start = Math.Max(0, Math.Min(text.Length, (int)NumberAt(args, 0)));
                    int end = args.Count > 1
                        ? Math.Max(0, Math.Min(text.Length, (int)ToNumber(args[1])))
                        : text.Length;
                    if (start > end) { int swap = start; start = end; end = swap; }
                    return text.Substring(start, end - start);
                });
                case "slice": return new Avm1NativeFunction(args =>
                {
                    int start = NormalizeIndex(args.Count > 0 ? ToNumber(args[0]) : 0d, text.Length);
                    int end = NormalizeIndex(args.Count > 1 ? ToNumber(args[1]) : text.Length, text.Length);
                    return text.Substring(start, Math.Max(0, end - start));
                });
                case "tolowercase": return new Avm1NativeFunction(args => text.ToLowerInvariant());
                case "touppercase": return new Avm1NativeFunction(args => text.ToUpperInvariant());
                case "split": return new Avm1NativeFunction(args => new List<object>(
                    Array.ConvertAll(
                        text.Split(new[] { args.Count > 0 ? ToAvm1String(args[0]) : "," }, StringSplitOptions.None),
                        value => (object)value
                    )
                ));
                case "tostring":
                case "valueof": return new Avm1NativeFunction(args => text);
                default: return null;
            }
        }

        private static int NormalizeIndex(double value, int length)
        {
            if (double.IsNaN(value)) return 0;
            int index = (int)value;
            if (index < 0) index = Math.Max(0, length + index);
            return Math.Max(0, Math.Min(length, index));
        }

        private static string[] ToStringArray(IList<object> list)
        {
            string[] values = new string[list.Count];

            for (int i = 0; i < list.Count; i++)
                values[i] = ReferenceEquals(list[i], Undefined) || list[i] == null
                    ? string.Empty
                    : ToAvm1String(list[i]);

            return values;
        }

        private void SetMember(object target, string name, object value)
        {
            if (target is Avm1Object avmObject)
            {
                if (avmObject.Get("__setters") is Avm1Object setterMap &&
                    setterMap.Get(name) is object setter && IsCallable(setter))
                {
                    CallValue(setter, new object[] { value }, avmObject);
                    return;
                }

                avmObject.Set(name, value);
                return;
            }

            if (target is List<object> list &&
                string.Equals(name, "length", StringComparison.OrdinalIgnoreCase))
            {
                int requestedLength = Math.Max(0, (int)ToNumber(value));

                while (list.Count > requestedLength) list.RemoveAt(list.Count - 1);
                while (list.Count < requestedLength) list.Add(Undefined);
                return;
            }

            if (target is List<object> indexedList && int.TryParse(name, out int index) && index >= 0)
            {
                while (indexedList.Count <= index)
                    indexedList.Add(Undefined);

                indexedList[index] = value;
            }
        }

        private void StorePropertyAccessor(
            Avm1Object target,
            string mapName,
            string propertyName,
            object accessor
        )
        {
            if (target == null || string.IsNullOrEmpty(propertyName) || !IsCallable(accessor))
                return;

            Avm1Object map = target.Get(mapName) as Avm1Object;

            if (map == null)
            {
                map = CreateObject();
                target.Set(mapName, map);
            }

            map.Set(propertyName, accessor);
        }

        private static Dictionary<string, object> EnsureLocals(ExecutionContext context)
        {
            if (context.Locals == null)
                context.Locals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            return context.Locals;
        }

        private static List<object> PopArguments(ExecutionContext context)
        {
            int count = ClampArgumentCount(ToNumber(Pop(context)));
            List<object> arguments = new List<object>(count);

            for (int i = 0; i < count; i++)
                arguments.Add(Pop(context));

            return arguments;
        }

        private static int ClampArgumentCount(double value)
        {
            if (double.IsNaN(value) || value <= 0d)
                return 0;

            return Math.Min(4096, (int)value);
        }

        private static void BinaryNumber(ExecutionContext context, Func<double, double, double> operation)
        {
            double right = ToNumber(Pop(context));
            double left = ToNumber(Pop(context));
            Push(context, operation(left, right));
        }

        private static void BinaryInteger(ExecutionContext context, Func<int, int, int> operation)
        {
            int right = (int)ToNumber(Pop(context));
            int left = (int)ToNumber(Pop(context));
            Push(context, operation(left, right));
        }

        private static void BinaryCompare(ExecutionContext context, Func<object, object, bool> operation)
        {
            object right = Pop(context);
            object left = Pop(context);
            Push(context, operation(left, right));
        }

        private static object AddValues(object left, object right)
        {
            if (left is string || right is string)
                return ToAvm1String(left) + ToAvm1String(right);

            return ToNumber(left) + ToNumber(right);
        }

        private static int CompareValues(object left, object right)
        {
            if (left is string || right is string)
                return string.Compare(ToAvm1String(left), ToAvm1String(right), StringComparison.Ordinal);

            return ToNumber(left).CompareTo(ToNumber(right));
        }

        private static bool ValuesEqual(object left, object right)
        {
            if (ReferenceEquals(left, right))
                return true;

            bool leftNullish = left == null || ReferenceEquals(left, Undefined);
            bool rightNullish = right == null || ReferenceEquals(right, Undefined);

            if (leftNullish || rightNullish)
                return leftNullish && rightNullish;

            if (left is string || right is string)
                return ToAvm1String(left) == ToAvm1String(right);

            return Math.Abs(ToNumber(left) - ToNumber(right)) < 0.0000001d;
        }

        private static double ToNumber(object value)
        {
            if (ReferenceEquals(value, Undefined) || value == null)
                return value == null ? 0d : double.NaN;
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is uint ui) return ui;
            if (value is long l) return l;
            if (value is bool b) return b ? 1d : 0d;
            string text = ToAvm1String(value).Trim();

            if (text.Length == 0)
                return 0d;

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
            {
                return hex;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return parsed;
            return double.NaN;
        }

        private static bool ToBoolean(object value)
        {
            if (value == null || ReferenceEquals(value, Undefined)) return false;
            if (value is bool boolean) return boolean;
            if (value is string text) return text.Length > 0;
            if (value is Avm1Object || value is IList<object>) return true;
            double number = ToNumber(value);
            return !double.IsNaN(number) && Math.Abs(number) > double.Epsilon;
        }

        private static string ToAvm1String(object value)
        {
            if (ReferenceEquals(value, Undefined)) return "undefined";
            if (value == null) return "null";
            if (value is bool boolean) return boolean ? "true" : "false";
            if (value is string text) return text;
            if (value is IList<object> list) return string.Join(",", ToStringArray(list));
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static string TypeOf(object value)
        {
            if (ReferenceEquals(value, Undefined)) return "undefined";
            if (value == null) return "null";
            if (value is bool) return "boolean";
            if (value is string) return "string";
            if (value is Avm1Function || value is Avm1NativeFunction) return "function";
            if (value is float || value is double || value is int || value is long || value is uint) return "number";
            return "object";
        }

        private static object NormalizePublicValue(object value)
        {
            return ReferenceEquals(value, Undefined) ? null : value;
        }

        private static string PropertyName(int index)
        {
            switch (index)
            {
                case 0: return "_x";
                case 1: return "_y";
                case 2: return "_xscale";
                case 3: return "_yscale";
                case 4: return "_currentframe";
                case 5: return "_totalframes";
                case 6: return "_alpha";
                case 7: return "_visible";
                case 8: return "_width";
                case 9: return "_height";
                case 10: return "_rotation";
                case 11: return "_target";
                case 12: return "_framesloaded";
                case 13: return "_name";
                case 14: return "_droptarget";
                case 15: return "_url";
                case 16: return "_highquality";
                case 17: return "_focusrect";
                case 18: return "_soundbuftime";
                case 19: return "_quality";
                case 20: return "_xmouse";
                case 21: return "_ymouse";
                default: return "_property" + index;
            }
        }

        private static object ConstantAt(List<string> pool, int index)
        {
            return index >= 0 && index < pool.Count ? pool[index] : Undefined;
        }

        private static double ReadSwfDouble(byte[] code, ref int p, int end)
        {
            if (p + 8 > end)
            {
                p = end;
                return 0d;
            }

            byte[] bytes = new byte[8];
            Array.Copy(code, p + 4, bytes, 0, 4);
            Array.Copy(code, p, bytes, 4, 4);
            p += 8;
            return BitConverter.ToDouble(bytes, 0);
        }

        private static string ReadString(byte[] code, ref int p, int end)
        {
            int start = p;

            while (p < end && code[p] != 0)
                p++;

            string result = Encoding.UTF8.GetString(code, start, p - start);

            if (p < end)
                p++;

            return result;
        }

        private static int ReadUInt16(byte[] code, int offset)
        {
            return code[offset] | (code[offset + 1] << 8);
        }

        private static short ReadInt16(byte[] code, int offset)
        {
            return (short)ReadUInt16(code, offset);
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static void Push(ExecutionContext context, object value)
        {
            context.Stack.Add(value);
        }

        private static object Pop(ExecutionContext context)
        {
            int index = context.Stack.Count - 1;

            if (index < 0)
                return Undefined;

            object value = context.Stack[index];
            context.Stack.RemoveAt(index);
            return value;
        }

        private static object Peek(ExecutionContext context)
        {
            return context.Stack.Count > 0 ? context.Stack[context.Stack.Count - 1] : Undefined;
        }

        private static bool IsCallable(object value)
        {
            return value is Avm1Function || value is Avm1NativeFunction;
        }

        private static double NumberAt(IReadOnlyList<object> values, int index)
        {
            return index >= 0 && index < values.Count ? ToNumber(values[index]) : 0d;
        }

        private static readonly IReadOnlyList<object> EmptyArguments = new object[0];

        private sealed class ExecutionContext
        {
            public readonly List<object> Stack = new List<object>();
            public readonly object[] Registers = new object[256];
            public Dictionary<string, object> Locals;
            public readonly List<string> ConstantPool;
            public readonly bool IsFunction;
            public readonly List<Dictionary<string, object>> OuterLocals =
                new List<Dictionary<string, object>>();
            public readonly List<Avm1Object> ScopeObjects = new List<Avm1Object>();
            public object Target;
            public object OriginalTarget;
            public bool Returned;
            public object ReturnValue = Undefined;

            public ExecutionContext(
                Dictionary<string, object> locals,
                List<string> constantPool,
                bool isFunction
            )
            {
                Locals = locals;
                ConstantPool = constantPool;
                IsFunction = isFunction;
            }
        }

        private sealed class Avm1Function : Avm1Object
        {
            public readonly string Name;
            public readonly Avm1Parameter[] Parameters;
            public readonly byte RegisterCount;
            public readonly ushort Flags;
            public readonly byte[] Code;
            public readonly List<string> ConstantPool;
            public Dictionary<string, object> CapturedLocals;
            public List<Dictionary<string, object>> CapturedOuterLocals =
                new List<Dictionary<string, object>>();
            public List<Avm1Object> CapturedScopes = new List<Avm1Object>();
            public object DefiningTarget;

            public Avm1Function(
                string name,
                Avm1Parameter[] parameters,
                byte registerCount,
                ushort flags,
                byte[] code,
                List<string> constantPool,
                StringComparer comparer
            ) : base(comparer)
            {
                Name = name;
                Parameters = parameters;
                RegisterCount = registerCount;
                Flags = flags;
                Code = code;
                ConstantPool = constantPool;

                Avm1Object prototype = new Avm1Object(comparer);
                prototype.Set("constructor", this);
                Set("prototype", prototype);
            }
        }

        private sealed class Avm1ThrownException : Exception
        {
            public readonly object Value;

            public Avm1ThrownException(object value)
            {
                Value = value;
            }
        }

        private sealed class Avm1SuperReference
        {
            public readonly object Receiver;
            public readonly object SuperClass;

            public Avm1SuperReference(object receiver, object superClass)
            {
                Receiver = receiver;
                SuperClass = superClass;
            }
        }

        private readonly struct Avm1Parameter
        {
            public readonly string Name;
            public readonly byte Register;

            public Avm1Parameter(string name, byte register)
            {
                Name = name;
                Register = register;
            }
        }
    }

    public sealed class Avm1NativeFunction : Avm1Object
    {
        private readonly Func<IReadOnlyList<object>, object> callback;

        public Avm1NativeFunction(Func<IReadOnlyList<object>, object> callback)
        {
            this.callback = callback;
            Avm1Object prototype = new Avm1Object();
            prototype.Set("constructor", this);
            Set("prototype", prototype);
        }

        public object Invoke(IReadOnlyList<object> arguments)
        {
            return callback != null ? callback(arguments) : null;
        }
    }

    public class Avm1Object
    {
        private readonly Dictionary<string, object> members;

        internal StringComparer NameComparer { get; }

        public Avm1Object Prototype { get; set; }

        public Avm1Object() : this(StringComparer.Ordinal)
        {
        }

        internal Avm1Object(StringComparer comparer)
        {
            NameComparer = comparer ?? StringComparer.Ordinal;
            members = new Dictionary<string, object>(NameComparer);
        }

        public object Get(string name)
        {
            return TryGet(name, out object value) ? value : null;
        }

        public bool TryGet(string name, out object value)
        {
            string key = name ?? string.Empty;

            if (members.TryGetValue(key, out value))
                return true;

            Avm1Object current = Prototype;
            int guard = 0;

            while (current != null && guard++ < 256)
            {
                if (current.members.TryGetValue(key, out value))
                    return true;

                current = current.Prototype;
            }

            value = null;
            return false;
        }

        public bool TryGetOwn(string name, out object value)
        {
            return members.TryGetValue(name ?? string.Empty, out value);
        }

        public void Set(string name, object value)
        {
            if (!string.IsNullOrEmpty(name))
                members[name] = value;
        }

        public bool Remove(string name)
        {
            return members.Remove(name ?? string.Empty);
        }

        public void CopyMembersTo(Avm1Object destination)
        {
            if (destination == null)
                return;

            foreach (KeyValuePair<string, object> member in members)
                destination.Set(member.Key, member.Value);
        }

        public List<string> GetEnumerableMemberNames()
        {
            HashSet<string> found = new HashSet<string>(NameComparer);
            List<string> result = new List<string>();
            Avm1Object current = this;
            int guard = 0;

            while (current != null && guard++ < 256)
            {
                foreach (string name in current.members.Keys)
                {
                    if (found.Add(name))
                        result.Add(name);
                }

                current = current.Prototype;
            }

            return result;
        }
    }
}
