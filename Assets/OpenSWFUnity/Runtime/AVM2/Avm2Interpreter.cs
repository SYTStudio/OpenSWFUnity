using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenSWFUnity.Runtime.AVM2.Abc;
using OpenSWFUnity.Runtime.AVM2.Bytecode;
using OpenSWFUnity.Runtime.AVM2.Values;

namespace OpenSWFUnity.Runtime.AVM2
{
    // The AVM2 execution engine.
    //
    // A register machine: each method gets locals, an operand stack and a scope
    // stack sized from its declared body header. Instructions are decoded once per
    // method and cached, so the loop below indexes an array rather than re-reading
    // bytes.
    //
    // Any opcode this build cannot execute correctly raises Avm2UnsupportedException
    // rather than being skipped. Skipping would leave the operand stack the wrong
    // depth and produce results that look plausible but are wrong, which is far
    // harder to diagnose than an abandoned method.
    public sealed class Avm2Interpreter
    {
        private readonly Avm2Domain domain;
        private readonly Avm2Builtins builtins;
        private readonly Avm2Diagnostics diagnostics;

        // Decoded bodies, keyed by the body they came from.
        private readonly Dictionary<AbcMethodBody, Avm2MethodCode> decodedBodies =
            new Dictionary<AbcMethodBody, Avm2MethodCode>();

        private readonly Stack<Avm2ExecutionContext> framePool =
            new Stack<Avm2ExecutionContext>();

        private int callDepth;
        private int remainingInstructions;
        private int timeCheckCountdown;
        private DateTime deadline;
        private bool executionActive;

        public int MaxCallDepth { get; set; } = AbcLimits.MaxCallDepth;
        public int InstructionBudget { get; set; } = AbcLimits.MaxInstructionsPerEntry;

        // Wall-clock ceiling for one outermost invocation. Guards against a script
        // that stays under the instruction budget but still stalls the frame.
        public double TimeBudgetSeconds { get; set; } = 2.0;

        public int ExecutedInstructionCount { get; private set; }

        public Avm2Interpreter(Avm2Domain domain, Avm2Builtins builtins, Avm2Diagnostics diagnostics)
        {
            this.domain = domain;
            this.builtins = builtins;
            this.diagnostics = diagnostics;
        }

        // ---- entry points -----------------------------------------------------

        public void RunScriptInitialiser(AbcFile file, AbcScriptInfo script)
        {
            if (file == null || script == null || domain.IsScriptInitialised(script))
                return;

            // Marked before running so a definition the initialiser itself touches
            // does not start a second pass over the same script.
            domain.MarkScriptInitialised(script);

            AbcMethodInfo method = file.GetMethod(script.InitialiserIndex);

            if (method == null)
            {
                diagnostics.ReportUnsupportedStructure(
                    "script initialiser references missing method #" + script.InitialiserIndex);
                return;
            }

            object[] scope = { domain.Global };
            Invoke(method, domain.Global, Array.Empty<object>(), scope);
        }

        public object CallFunction(object callable, object[] arguments)
        {
            return CallValue(callable, null, arguments ?? Array.Empty<object>());
        }

        // ---- invocation -------------------------------------------------------

        public object Invoke(
            AbcMethodInfo method,
            object receiver,
            object[] arguments,
            object[] outerScope
        )
        {
            return Invoke(method, receiver, arguments, outerScope, null);
        }

        public object Invoke(
            AbcMethodInfo method,
            object receiver,
            object[] arguments,
            object[] outerScope,
            Avm2Class declaringClass
        )
        {
            if (method == null)
                return Avm2Undefined.Value;

            if (method.Body == null)
            {
                // Native and interface methods have no bytecode. Reaching one means a
                // host implementation is missing, which is worth naming.
                diagnostics.ReportUnsupportedStructure(
                    "method '" + (method.Name ?? "<anonymous>") + "' has no body (native or interface)");
                return Avm2Undefined.Value;
            }

            if (callDepth >= MaxCallDepth)
            {
                throw new Avm2AbortException(
                    "AVM2 call depth limit of " + MaxCallDepth + " reached in '" +
                    (method.Name ?? "<anonymous>") + "'");
            }

            // The depth counter alone is not enough: one AVM2 call costs several CLR
            // frames, and the interpreter's dispatch frame is large, so the physical
            // stack can run out well before the logical limit. Probing for real
            // headroom turns that into a clean abort instead of a StackOverflowException,
            // which .NET cannot catch and which would kill the process.
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
            }
            catch (InsufficientExecutionStackException)
            {
                throw new Avm2AbortException(
                    "AVM2 ran out of stack at call depth " + callDepth + " in '" +
                    (method.Name ?? "<anonymous>") + "'");
            }

            bool outermost = !executionActive;

            if (outermost)
            {
                executionActive = true;
                remainingInstructions = InstructionBudget;
                timeCheckCountdown = TimeCheckInterval;
                deadline = DateTime.UtcNow.AddSeconds(TimeBudgetSeconds);
            }

            callDepth++;
            Avm2ExecutionContext frame = RentFrame();

            try
            {
                frame.Initialize(method.Body);
                frame.Method = method;
                frame.File = method.OwnerFile;
                frame.DeclaringClass = declaringClass;
                frame.OuterScope = outerScope ?? Array.Empty<object>();
                frame.Code = GetDecodedBody(method);

                if (frame.Code == null || !frame.Code.IsValid)
                    return Avm2Undefined.Value;

                BindArguments(frame, method, receiver, arguments);
                return Run(frame);
            }
            finally
            {
                callDepth--;
                ReturnFrame(frame);

                if (outermost)
                    executionActive = false;
            }
        }

        // locals[0] is `this`; declared parameters follow, then rest/arguments if the
        // method asked for them. Everything beyond is undefined.
        private void BindArguments(
            Avm2ExecutionContext frame,
            AbcMethodInfo method,
            object receiver,
            object[] arguments
        )
        {
            frame.SetLocal(0, receiver ?? Avm2Undefined.Value);

            int parameterCount = method.ParameterCount;
            int supplied = arguments != null ? arguments.Length : 0;

            for (int i = 0; i < parameterCount; i++)
            {
                int local = i + 1;

                if (local >= frame.LocalCount)
                    break;

                if (i < supplied)
                {
                    frame.SetLocal(local, arguments[i]);
                    continue;
                }

                // Missing arguments take their declared default when the method
                // declares optionals; the optionals array covers the tail parameters.
                frame.SetLocal(local, DefaultForParameter(method, i));
            }

            if (method.NeedsRest || method.NeedsArguments)
            {
                Avm2Array rest = new Avm2Array { Class = builtins.ArrayClass };
                int start = method.NeedsArguments ? 0 : parameterCount;

                for (int i = start; i < supplied; i++)
                    rest.Items.Add(arguments[i]);

                int restLocal = parameterCount + 1;

                if (restLocal < frame.LocalCount)
                    frame.SetLocal(restLocal, rest);
            }
        }

        private object DefaultForParameter(AbcMethodInfo method, int parameterIndex)
        {
            if (!method.HasOptional || method.Optionals == null || method.OwnerFile == null)
                return Avm2Undefined.Value;

            // Optionals describe the last N parameters, so the mapping is offset by
            // however many required parameters come first.
            int firstOptional = method.ParameterCount - method.Optionals.Length;
            int optionalIndex = parameterIndex - firstOptional;

            if (optionalIndex < 0 || optionalIndex >= method.Optionals.Length)
                return Avm2Undefined.Value;

            AbcOptionalDetail detail = method.Optionals[optionalIndex];
            return ConstantValue(method.OwnerFile, detail.ValueKind, detail.ValueIndex);
        }

        // ---- the loop ---------------------------------------------------------

        private const int TimeCheckInterval = 4096;

        private object Run(Avm2ExecutionContext frame)
        {
            Avm2Instruction[] instructions = frame.Code.Instructions;
            AbcFile file = frame.File;
            int ip = 0;

            while (ip >= 0 && ip < instructions.Length)
            {
                if (--remainingInstructions < 0)
                {
                    throw new Avm2AbortException(
                        "AVM2 instruction budget of " + InstructionBudget + " exhausted");
                }

                // Checked periodically rather than per instruction: reading the clock
                // every opcode would cost more than the work being measured.
                if (--timeCheckCountdown <= 0)
                {
                    timeCheckCountdown = TimeCheckInterval;

                    if (DateTime.UtcNow > deadline)
                    {
                        throw new Avm2AbortException(
                            "AVM2 time budget of " + TimeBudgetSeconds + "s exceeded");
                    }
                }

                ExecutedInstructionCount++;
                Avm2Instruction instruction = instructions[ip];

                try
                {
                    int next = Step(frame, file, instruction, ip, out bool returned, out object result);

                    if (returned)
                        return result;

                    ip = next;
                }
                catch (Avm2ThrownException thrown)
                {
                    int handler = FindExceptionHandler(frame, instruction.Offset, thrown.Value);

                    if (handler < 0)
                        throw;

                    ip = handler;
                }
            }

            return Avm2Undefined.Value;
        }

        // Locates a handler covering the faulting offset whose declared type accepts
        // the thrown value, and prepares the frame to enter it.
        private int FindExceptionHandler(Avm2ExecutionContext frame, int offset, object thrown)
        {
            AbcExceptionInfo[] handlers = frame.Body.Exceptions;

            if (handlers == null || handlers.Length == 0)
                return -1;

            for (int i = 0; i < handlers.Length; i++)
            {
                AbcExceptionInfo handler = handlers[i];

                if (offset < handler.From || offset >= handler.To)
                    continue;

                if (!HandlerAccepts(frame, handler, thrown))
                    continue;

                int target = frame.Code.IndexForOffset(handler.Target);

                if (target < 0)
                    continue;

                // A handler starts with an empty stack carrying only the exception.
                frame.ClearStack();
                frame.Push(thrown);
                return target;
            }

            return -1;
        }

        private bool HandlerAccepts(Avm2ExecutionContext frame, AbcExceptionInfo handler, object thrown)
        {
            // Type index 0 is `catch (e:*)`, which takes anything.
            if (handler.ExceptionTypeIndex == 0 || frame.File == null)
                return true;

            if (!domain.TryResolveQName(frame.File, handler.ExceptionTypeIndex, out Avm2QName typeName))
                return true;

            if (typeName.Local == "*" || typeName.Local.Length == 0)
                return true;

            Avm2Class thrownClass = builtins.GetClassOf(thrown);

            if (thrownClass == null)
                return false;

            if (!domain.TryGetGlobal(typeName, out object typeValue) || !(typeValue is Avm2Class target))
                return false;

            return thrownClass.IsSubclassOf(target);
        }

        // Executes one instruction. Returns the next instruction index; sets
        // `returned` when the method is finished.
        private int Step(
            Avm2ExecutionContext frame,
            AbcFile file,
            Avm2Instruction instruction,
            int ip,
            out bool returned,
            out object result
        )
        {
            returned = false;
            result = null;
            byte opcode = instruction.OpCode;

            switch (opcode)
            {
                // ---- no-ops and control ----
                case 0x02: // nop
                case 0x09: // label
                case 0xF0: // debugline
                case 0xF1: // debugfile
                case 0xEF: // debug
                case 0xF2: // bkptline
                case 0xF3: // timestamp
                case 0x01: // bkpt
                    return ip + 1;

                case 0x47: // returnvoid
                    returned = true;
                    result = Avm2Undefined.Value;
                    return -1;

                case 0x48: // returnvalue
                    returned = true;
                    result = frame.Pop();
                    return -1;

                case 0x03: // throw
                    throw new Avm2ThrownException(frame.Pop());

                // ---- constants ----
                case 0x20: frame.Push(null); return ip + 1;                       // pushnull
                case 0x21: frame.Push(Avm2Undefined.Value); return ip + 1;        // pushundefined
                case 0x26: frame.Push(true); return ip + 1;                       // pushtrue
                case 0x27: frame.Push(false); return ip + 1;                      // pushfalse
                case 0x28: frame.Push(double.NaN); return ip + 1;                 // pushnan
                case 0x24: frame.Push((int)(sbyte)instruction.OperandA); return ip + 1; // pushbyte
                case 0x25: frame.Push(instruction.OperandA); return ip + 1;       // pushshort

                case 0x2C: // pushstring
                    frame.Push(file.ConstantPool.GetString(instruction.OperandA));
                    return ip + 1;

                case 0x2D: // pushint
                    frame.Push(GetInt(file, instruction.OperandA));
                    return ip + 1;

                case 0x2E: // pushuint
                    frame.Push(GetUInt(file, instruction.OperandA));
                    return ip + 1;

                case 0x2F: // pushdouble
                    frame.Push(GetDouble(file, instruction.OperandA));
                    return ip + 1;

                case 0x31: // pushnamespace
                {
                    AbcNamespace ns = file.ConstantPool.GetNamespace(instruction.OperandA);
                    frame.Push(new Avm2Namespace(ns != null ? ns.Name : string.Empty));
                    return ip + 1;
                }

                // ---- stack ----
                case 0x29: frame.Pop(); return ip + 1;              // pop
                case 0x2A: frame.Push(frame.Peek()); return ip + 1; // dup

                case 0x2B: // swap
                {
                    object a = frame.Pop();
                    object b = frame.Pop();
                    frame.Push(a);
                    frame.Push(b);
                    return ip + 1;
                }

                // ---- locals ----
                case 0xD0: frame.Push(frame.GetLocal(0)); return ip + 1;
                case 0xD1: frame.Push(frame.GetLocal(1)); return ip + 1;
                case 0xD2: frame.Push(frame.GetLocal(2)); return ip + 1;
                case 0xD3: frame.Push(frame.GetLocal(3)); return ip + 1;
                case 0xD4: frame.SetLocal(0, frame.Pop()); return ip + 1;
                case 0xD5: frame.SetLocal(1, frame.Pop()); return ip + 1;
                case 0xD6: frame.SetLocal(2, frame.Pop()); return ip + 1;
                case 0xD7: frame.SetLocal(3, frame.Pop()); return ip + 1;

                case 0x62: frame.Push(frame.GetLocal(instruction.OperandA)); return ip + 1; // getlocal
                case 0x63: frame.SetLocal(instruction.OperandA, frame.Pop()); return ip + 1; // setlocal

                case 0x08: // kill
                    frame.SetLocal(instruction.OperandA, Avm2Undefined.Value);
                    return ip + 1;

                case 0x92: // inclocal
                    frame.SetLocal(instruction.OperandA,
                        Avm2Convert.ToNumber(frame.GetLocal(instruction.OperandA)) + 1d);
                    return ip + 1;

                case 0x94: // declocal
                    frame.SetLocal(instruction.OperandA,
                        Avm2Convert.ToNumber(frame.GetLocal(instruction.OperandA)) - 1d);
                    return ip + 1;

                case 0xC2: // inclocal_i
                    frame.SetLocal(instruction.OperandA,
                        Avm2Convert.ToInt32(frame.GetLocal(instruction.OperandA)) + 1);
                    return ip + 1;

                case 0xC3: // declocal_i
                    frame.SetLocal(instruction.OperandA,
                        Avm2Convert.ToInt32(frame.GetLocal(instruction.OperandA)) - 1);
                    return ip + 1;

                // ---- arithmetic ----
                case 0xA0: // add
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    frame.Push(Avm2Convert.Add(left, right));
                    return ip + 1;
                }

                case 0xA1: return Numeric(frame, ip, (l, r) => l - r);
                case 0xA2: return Numeric(frame, ip, (l, r) => l * r);
                case 0xA3: return Numeric(frame, ip, (l, r) => l / r);
                case 0xA4: return Numeric(frame, ip, (l, r) => l % r);

                case 0x90: // negate
                    frame.Push(-Avm2Convert.ToNumber(frame.Pop()));
                    return ip + 1;

                case 0xC4: // negate_i
                    frame.Push(-Avm2Convert.ToInt32(frame.Pop()));
                    return ip + 1;

                case 0x91: // increment
                    frame.Push(Avm2Convert.ToNumber(frame.Pop()) + 1d);
                    return ip + 1;

                case 0x93: // decrement
                    frame.Push(Avm2Convert.ToNumber(frame.Pop()) - 1d);
                    return ip + 1;

                case 0xC0: // increment_i
                    frame.Push(Avm2Convert.ToInt32(frame.Pop()) + 1);
                    return ip + 1;

                case 0xC1: // decrement_i
                    frame.Push(Avm2Convert.ToInt32(frame.Pop()) - 1);
                    return ip + 1;

                case 0xC5: return Integer(frame, ip, (l, r) => l + r); // add_i
                case 0xC6: return Integer(frame, ip, (l, r) => l - r); // subtract_i
                case 0xC7: return Integer(frame, ip, (l, r) => l * r); // multiply_i

                case 0xA8: return Integer(frame, ip, (l, r) => l & r);
                case 0xA9: return Integer(frame, ip, (l, r) => l | r);
                case 0xAA: return Integer(frame, ip, (l, r) => l ^ r);
                case 0xA5: return Integer(frame, ip, (l, r) => l << (r & 31));
                case 0xA6: return Integer(frame, ip, (l, r) => l >> (r & 31));

                case 0xA7: // urshift
                {
                    int shift = Avm2Convert.ToInt32(frame.Pop()) & 31;
                    uint value = Avm2Convert.ToUint32(frame.Pop());
                    frame.Push((double)(value >> shift));
                    return ip + 1;
                }

                case 0x97: // bitnot
                    frame.Push(~Avm2Convert.ToInt32(frame.Pop()));
                    return ip + 1;

                // ---- comparison ----
                case 0xAB: // equals
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    frame.Push(Avm2Convert.LooseEquals(left, right));
                    return ip + 1;
                }

                case 0xAC: // strictequals
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    frame.Push(Avm2Convert.StrictEquals(left, right));
                    return ip + 1;
                }

                case 0xAD: return Relational(frame, ip, Relation.Less);
                case 0xAE: return Relational(frame, ip, Relation.LessEqual);
                case 0xAF: return Relational(frame, ip, Relation.Greater);
                case 0xB0: return Relational(frame, ip, Relation.GreaterEqual);

                case 0x96: // not
                    frame.Push(!Avm2Convert.ToBoolean(frame.Pop()));
                    return ip + 1;

                // ---- conversion ----
                case 0x70: frame.Push(Avm2Convert.ToString(frame.Pop())); return ip + 1;  // convert_s
                case 0x85: frame.Push(Avm2Convert.ToString(frame.Pop())); return ip + 1;  // coerce_s
                case 0x73: frame.Push(Avm2Convert.ToInt32(frame.Pop())); return ip + 1;   // convert_i
                case 0x74: frame.Push(Avm2Convert.ToUint32(frame.Pop())); return ip + 1;  // convert_u
                case 0x75: frame.Push(Avm2Convert.ToNumber(frame.Pop())); return ip + 1;  // convert_d
                case 0x76: frame.Push(Avm2Convert.ToBoolean(frame.Pop())); return ip + 1; // convert_b
                case 0x82: return ip + 1;                                                 // coerce_a: no-op

                case 0x77: // convert_o
                {
                    object value = frame.Peek();

                    if (Avm2Convert.IsNullOrUndefined(value))
                        throw new Avm2ThrownException(MakeError("TypeError", "Cannot convert null to object"));

                    return ip + 1;
                }

                case 0x80: // coerce
                    frame.Push(Coerce(file, instruction.OperandA, frame.Pop()));
                    return ip + 1;

                case 0x95: // typeof
                    frame.Push(Avm2Convert.TypeOf(frame.Pop()));
                    return ip + 1;

                case 0xB2: // istype
                {
                    object value = frame.Pop();
                    frame.Push(IsType(file, instruction.OperandA, value));
                    return ip + 1;
                }

                case 0xB3: // istypelate
                {
                    object type = frame.Pop();
                    object value = frame.Pop();
                    frame.Push(IsTypeLate(value, type));
                    return ip + 1;
                }

                case 0x86: // astype
                {
                    object value = frame.Pop();
                    frame.Push(IsType(file, instruction.OperandA, value) ? value : null);
                    return ip + 1;
                }

                case 0x87: // astypelate
                {
                    object type = frame.Pop();
                    object value = frame.Pop();
                    frame.Push(IsTypeLate(value, type) ? value : null);
                    return ip + 1;
                }

                case 0xB1: // instanceof
                {
                    object type = frame.Pop();
                    object value = frame.Pop();
                    frame.Push(IsTypeLate(value, type));
                    return ip + 1;
                }

                case 0xB4: // in
                {
                    object target = frame.Pop();
                    object name = frame.Pop();
                    frame.Push(HasProperty(target, Avm2QName.Public(Avm2Convert.ToString(name))));
                    return ip + 1;
                }

                // ---- branching ----
                case 0x10: return frame.Code.IndexForOffset(instruction.OperandB); // jump

                case 0x11: return Branch(frame, instruction, ip, Avm2Convert.ToBoolean(frame.Pop()));
                case 0x12: return Branch(frame, instruction, ip, !Avm2Convert.ToBoolean(frame.Pop()));

                case 0x13: // ifeq
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    return Branch(frame, instruction, ip, Avm2Convert.LooseEquals(left, right));
                }

                case 0x14: // ifne
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    return Branch(frame, instruction, ip, !Avm2Convert.LooseEquals(left, right));
                }

                case 0x19: // ifstricteq
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    return Branch(frame, instruction, ip, Avm2Convert.StrictEquals(left, right));
                }

                case 0x1A: // ifstrictne
                {
                    object right = frame.Pop();
                    object left = frame.Pop();
                    return Branch(frame, instruction, ip, !Avm2Convert.StrictEquals(left, right));
                }

                case 0x15: return CompareBranch(frame, instruction, ip, Relation.Less, false);
                case 0x16: return CompareBranch(frame, instruction, ip, Relation.LessEqual, false);
                case 0x17: return CompareBranch(frame, instruction, ip, Relation.Greater, false);
                case 0x18: return CompareBranch(frame, instruction, ip, Relation.GreaterEqual, false);

                // The "not" forms branch when the comparison is false *or* undefined,
                // which is why they cannot be written as the negation of the above.
                case 0x0C: return CompareBranch(frame, instruction, ip, Relation.Less, true);
                case 0x0D: return CompareBranch(frame, instruction, ip, Relation.LessEqual, true);
                case 0x0E: return CompareBranch(frame, instruction, ip, Relation.Greater, true);
                case 0x0F: return CompareBranch(frame, instruction, ip, Relation.GreaterEqual, true);

                case 0x1B: // lookupswitch
                {
                    int index = Avm2Convert.ToInt32(frame.Pop());
                    int[] targets = instruction.SwitchTargets;

                    if (targets == null || targets.Length == 0)
                        return ip + 1;

                    // Cases occupy all but the last slot, which holds the default.
                    int caseCount = targets.Length - 1;
                    int offset = index >= 0 && index < caseCount
                        ? targets[index]
                        : targets[caseCount];

                    int resolved = frame.Code.IndexForOffset(offset);
                    return resolved >= 0 ? resolved : ip + 1;
                }

                // ---- scope ----
                case 0x30: // pushscope
                    frame.PushScope(frame.Pop());
                    return ip + 1;

                case 0x1C: // pushwith
                    frame.PushScope(frame.Pop());
                    return ip + 1;

                case 0x1D: // popscope
                    frame.PopScope();
                    return ip + 1;

                case 0x64: // getglobalscope
                    frame.Push(domain.Global);
                    return ip + 1;

                case 0x65: // getscopeobject
                    frame.Push(frame.GetScopeAt(frame.OuterScope.Length + instruction.OperandA));
                    return ip + 1;

                case 0x5D: // findpropstrict
                case 0x5E: // findproperty
                {
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, null);
                    object holder = FindPropertyHolder(frame, name);

                    if (holder == null && opcode == 0x5D)
                    {
                        throw new Avm2ThrownException(MakeError(
                            "ReferenceError",
                            "Variable " + name + " is not defined"));
                    }

                    frame.Push(holder ?? (object)domain.Global);
                    return ip + 1;
                }

                case 0x5F: // finddef
                {
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, null);
                    domain.TryGetGlobal(name, out object definition);
                    frame.Push(definition ?? domain.Global);
                    return ip + 1;
                }

                case 0x60: // getlex
                {
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, null);
                    object holder = FindPropertyHolder(frame, name);

                    if (holder == null)
                    {
                        throw new Avm2ThrownException(MakeError(
                            "ReferenceError",
                            "Variable " + name + " is not defined"));
                    }

                    frame.Push(GetProperty(holder, name));
                    return ip + 1;
                }

                // ---- property access ----
                case 0x66: // getproperty
                {
                    object runtimeName = RuntimeNameFor(frame, file, instruction.OperandA);
                    object target = frame.Pop();
                    frame.Push(GetPropertyDynamic(frame, file, instruction.OperandA, target, runtimeName));
                    return ip + 1;
                }

                case 0x61: // setproperty
                case 0x68: // initproperty
                {
                    object value = frame.Pop();
                    object runtimeName = RuntimeNameFor(frame, file, instruction.OperandA);
                    object target = frame.Pop();
                    SetPropertyDynamic(frame, file, instruction.OperandA, target, runtimeName, value);
                    return ip + 1;
                }

                case 0x6A: // deleteproperty
                {
                    object runtimeName = RuntimeNameFor(frame, file, instruction.OperandA);
                    object target = frame.Pop();
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, runtimeName);
                    frame.Push(target is Avm2Object obj && obj.DeleteDynamic(name));
                    return ip + 1;
                }

                case 0x6C: // getslot
                {
                    object target = frame.Pop();
                    frame.Push(target is Avm2Object obj
                        ? obj.GetSlot(instruction.OperandA)
                        : Avm2Undefined.Value);
                    return ip + 1;
                }

                case 0x6D: // setslot
                {
                    object value = frame.Pop();
                    object target = frame.Pop();

                    if (target is Avm2Object obj)
                        obj.SetSlot(instruction.OperandA, value);

                    return ip + 1;
                }

                case 0x6E: // getglobalslot
                    frame.Push(domain.Global.GetSlot(instruction.OperandA));
                    return ip + 1;

                case 0x6F: // setglobalslot
                    domain.Global.SetSlot(instruction.OperandA, frame.Pop());
                    return ip + 1;

                // ---- calls ----
                case 0x41: // call
                {
                    object[] args = PopArguments(frame, instruction.OperandA);
                    object receiver = frame.Pop();
                    object function = frame.Pop();
                    frame.Push(CallValue(function, receiver, args));
                    return ip + 1;
                }

                case 0x46: // callproperty
                case 0x4C: // callproplex
                case 0x4F: // callpropvoid
                {
                    object[] args = PopArguments(frame, instruction.OperandB);
                    object runtimeName = RuntimeNameFor(frame, file, instruction.OperandA);
                    object target = frame.Pop();
                    object value = CallPropertyOn(frame, file, instruction.OperandA, target, runtimeName, args);

                    if (opcode != 0x4F)
                        frame.Push(value);

                    return ip + 1;
                }

                case 0x45: // callsuper
                case 0x4E: // callsupervoid
                {
                    object[] args = PopArguments(frame, instruction.OperandB);
                    object target = frame.Pop();
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, null);
                    object value = CallSuper(frame, target, name, args);

                    if (opcode != 0x4E)
                        frame.Push(value);

                    return ip + 1;
                }

                case 0x43: // callmethod
                {
                    object[] args = PopArguments(frame, instruction.OperandB);
                    object target = frame.Pop();
                    throw Unsupported(opcode,
                        "callmethod addresses a method by dispatch id (" + instruction.OperandA +
                        "), which requires a vtable this build does not construct");
                }

                case 0x44: // callstatic
                {
                    object[] args = PopArguments(frame, instruction.OperandB);
                    object receiver = frame.Pop();
                    AbcMethodInfo method = file.GetMethod(instruction.OperandA);
                    frame.Push(Invoke(method, receiver, args, frame.CaptureScopeChain(),
                        frame.DeclaringClass));
                    return ip + 1;
                }

                case 0x42: // construct
                {
                    object[] args = PopArguments(frame, instruction.OperandA);
                    object type = frame.Pop();
                    frame.Push(Construct(type, args));
                    return ip + 1;
                }

                case 0x4A: // constructprop
                {
                    object[] args = PopArguments(frame, instruction.OperandB);
                    object runtimeName = RuntimeNameFor(frame, file, instruction.OperandA);
                    object target = frame.Pop();
                    Avm2QName name = ResolveName(frame, file, instruction.OperandA, runtimeName);
                    object type = GetProperty(target, name);
                    frame.Push(Construct(type, args));
                    return ip + 1;
                }

                case 0x49: // constructsuper
                {
                    object[] args = PopArguments(frame, instruction.OperandA);
                    object receiver = frame.Pop();
                    ConstructSuper(frame, receiver, args);
                    return ip + 1;
                }

                // ---- creation ----
                case 0x55: // newobject
                {
                    Avm2Object created = new Avm2Object(builtins.ObjectClass);
                    int pairs = instruction.OperandA;

                    // Key/value pairs come off the stack in reverse order.
                    object[] values = new object[pairs];
                    object[] keys = new object[pairs];

                    for (int i = pairs - 1; i >= 0; i--)
                    {
                        values[i] = frame.Pop();
                        keys[i] = frame.Pop();
                    }

                    for (int i = 0; i < pairs; i++)
                        created.SetDynamic(Avm2QName.Public(Avm2Convert.ToString(keys[i])), values[i]);

                    frame.Push(created);
                    return ip + 1;
                }

                case 0x56: // newarray
                {
                    int count = instruction.OperandA;
                    Avm2Array array = new Avm2Array(count) { Class = builtins.ArrayClass };

                    for (int i = 0; i < count; i++)
                        array.Items.Add(null);

                    for (int i = count - 1; i >= 0; i--)
                        array.Items[i] = frame.Pop();

                    frame.Push(array);
                    return ip + 1;
                }

                case 0x57: // newactivation
                    frame.Push(CreateActivation(frame));
                    return ip + 1;

                case 0x5A: // newcatch
                    frame.Push(new Avm2Object(builtins.ObjectClass));
                    return ip + 1;

                case 0x40: // newfunction
                {
                    AbcMethodInfo method = file.GetMethod(instruction.OperandA);
                    frame.Push(Avm2Function.FromMethod(
                        method, frame.CaptureScopeChain(), frame.DeclaringClass));
                    return ip + 1;
                }

                case 0x58: // newclass
                {
                    object baseValue = frame.Pop();
                    Avm2Class baseClass = baseValue as Avm2Class;
                    frame.Push(BuildClass(file, instruction.OperandA, baseClass, frame.CaptureScopeChain()));
                    return ip + 1;
                }

                case 0x53: // applytype
                {
                    // Vector.<T> and friends: the parameters are dropped and the raw
                    // generic returned, which keeps Vector usable as an Array-alike.
                    object[] parameters = PopArguments(frame, instruction.OperandA);
                    object generic = frame.Pop();
                    diagnostics.ReportUnsupportedStructure(
                        "applytype (parameterised type) is treated as its unparameterised base");
                    frame.Push(generic);
                    return ip + 1;
                }

                // ---- iteration ----
                case 0x1F: // hasnext
                {
                    int index = Avm2Convert.ToInt32(frame.Pop());
                    object target = frame.Pop();
                    frame.Push(NextNameIndex(target, index));
                    return ip + 1;
                }

                case 0x32: // hasnext2
                {
                    object target = frame.GetLocal(instruction.OperandA);
                    int index = Avm2Convert.ToInt32(frame.GetLocal(instruction.OperandB));
                    int nextIndex = NextNameIndex(target, index);

                    if (nextIndex > 0)
                    {
                        frame.SetLocal(instruction.OperandB, nextIndex);
                        frame.Push(true);
                    }
                    else
                    {
                        frame.SetLocal(instruction.OperandA, null);
                        frame.SetLocal(instruction.OperandB, 0);
                        frame.Push(false);
                    }

                    return ip + 1;
                }

                case 0x1E: // nextname
                {
                    int index = Avm2Convert.ToInt32(frame.Pop());
                    object target = frame.Pop();
                    frame.Push(NameAtIndex(target, index));
                    return ip + 1;
                }

                case 0x23: // nextvalue
                {
                    int index = Avm2Convert.ToInt32(frame.Pop());
                    object target = frame.Pop();
                    object name = NameAtIndex(target, index);
                    frame.Push(GetProperty(target, Avm2QName.Public(Avm2Convert.ToString(name))));
                    return ip + 1;
                }

                case 0x78: // checkfilter
                    return ip + 1;

                default:
                    throw Unsupported(opcode,
                        "opcode " + Avm2OpCode.GetName(opcode) + " is not implemented");
            }
        }

        private Avm2UnsupportedException Unsupported(byte opcode, string message)
        {
            return new Avm2UnsupportedException(opcode, message);
        }

        // ---- helpers: arithmetic and branching --------------------------------

        private enum Relation { Less, LessEqual, Greater, GreaterEqual }

        private static int Numeric(Avm2ExecutionContext frame, int ip, Func<double, double, double> operation)
        {
            double right = Avm2Convert.ToNumber(frame.Pop());
            double left = Avm2Convert.ToNumber(frame.Pop());
            frame.Push(operation(left, right));
            return ip + 1;
        }

        private static int Integer(Avm2ExecutionContext frame, int ip, Func<int, int, int> operation)
        {
            int right = Avm2Convert.ToInt32(frame.Pop());
            int left = Avm2Convert.ToInt32(frame.Pop());
            frame.Push(operation(left, right));
            return ip + 1;
        }

        private static bool Satisfies(int comparison, Relation relation)
        {
            if (comparison == Avm2Convert.Unordered)
                return false;

            switch (relation)
            {
                case Relation.Less: return comparison < 0;
                case Relation.LessEqual: return comparison <= 0;
                case Relation.Greater: return comparison > 0;
                default: return comparison >= 0;
            }
        }

        private static int Relational(Avm2ExecutionContext frame, int ip, Relation relation)
        {
            object right = frame.Pop();
            object left = frame.Pop();
            frame.Push(Satisfies(Avm2Convert.Compare(left, right), relation));
            return ip + 1;
        }

        private int Branch(Avm2ExecutionContext frame, Avm2Instruction instruction, int ip, bool taken)
        {
            if (!taken)
                return ip + 1;

            int target = frame.Code.IndexForOffset(instruction.OperandB);
            return target >= 0 ? target : ip + 1;
        }

        private int CompareBranch(
            Avm2ExecutionContext frame,
            Avm2Instruction instruction,
            int ip,
            Relation relation,
            bool negated
        )
        {
            object right = frame.Pop();
            object left = frame.Pop();
            bool satisfied = Satisfies(Avm2Convert.Compare(left, right), relation);
            return Branch(frame, instruction, ip, negated ? !satisfied : satisfied);
        }

        // ---- helpers: constants ----------------------------------------------

        private static int GetInt(AbcFile file, int index)
        {
            int[] pool = file.ConstantPool.Integers;
            return index > 0 && index < pool.Length ? pool[index] : 0;
        }

        private static uint GetUInt(AbcFile file, int index)
        {
            uint[] pool = file.ConstantPool.UnsignedIntegers;
            return index > 0 && index < pool.Length ? pool[index] : 0u;
        }

        private static double GetDouble(AbcFile file, int index)
        {
            double[] pool = file.ConstantPool.Doubles;
            return index > 0 && index < pool.Length ? pool[index] : double.NaN;
        }

        // Default values in method signatures and slot traits are encoded as a pool
        // index plus the kind that selects which pool.
        private object ConstantValue(AbcFile file, byte kind, int index)
        {
            switch (kind)
            {
                case 0x03: return GetInt(file, index);
                case 0x04: return GetUInt(file, index);
                case 0x06: return GetDouble(file, index);
                case 0x01: return file.ConstantPool.GetString(index);
                case 0x0B: return true;
                case 0x0A: return false;
                case 0x0C: return null;
                case 0x00: return Avm2Undefined.Value;
                default:
                    // The namespace kinds all denote a namespace constant.
                    AbcNamespace ns = file.ConstantPool.GetNamespace(index);
                    return ns != null ? new Avm2Namespace(ns.Name) : (object)Avm2Undefined.Value;
            }
        }

        // ---- helpers: names ---------------------------------------------------

        // Multinames whose name or namespace is supplied at runtime consume extra
        // stack operands, which must be taken before the target object.
        private object RuntimeNameFor(Avm2ExecutionContext frame, AbcFile file, int multinameIndex)
        {
            AbcMultiname multiname = file.ConstantPool.GetMultiname(multinameIndex);

            if (multiname == null)
                return null;

            object name = null;

            if (multiname.HasRuntimeName)
                name = frame.Pop();

            if (multiname.HasRuntimeNamespace)
                frame.Pop();

            return name;
        }

        private Avm2QName ResolveName(
            Avm2ExecutionContext frame,
            AbcFile file,
            int multinameIndex,
            object runtimeName
        )
        {
            if (runtimeName != null)
                return Avm2QName.Public(Avm2Convert.ToString(runtimeName));

            if (domain.TryResolveQName(file, multinameIndex, out Avm2QName name))
                return name;

            AbcMultiname multiname = file.ConstantPool.GetMultiname(multinameIndex);
            return Avm2QName.Public(multiname != null ? multiname.Name : string.Empty);
        }

        // For a multiname carrying a namespace set, the applicable namespace depends
        // on which one the target actually defines; public is the fallback.
        private Avm2QName ResolveNameForTarget(
            AbcFile file,
            int multinameIndex,
            object target,
            object runtimeName
        )
        {
            if (runtimeName != null)
                return Avm2QName.Public(Avm2Convert.ToString(runtimeName));

            if (domain.TryResolveQName(file, multinameIndex, out Avm2QName resolved))
                return resolved;

            AbcMultiname multiname = file.ConstantPool.GetMultiname(multinameIndex);
            string local = multiname != null ? multiname.Name : string.Empty;
            string[] namespaces = domain.GetNamespaceSet(file, multinameIndex);

            for (int i = 0; i < namespaces.Length; i++)
            {
                Avm2QName candidate = new Avm2QName(namespaces[i], local);

                if (HasProperty(target, candidate))
                    return candidate;
            }

            return Avm2QName.Public(local);
        }

        // ---- helpers: properties ---------------------------------------------

        private object GetPropertyDynamic(
            Avm2ExecutionContext frame,
            AbcFile file,
            int multinameIndex,
            object target,
            object runtimeName
        )
        {
            // An integer name against an array is an element access, not a member.
            if (target is Avm2Array array && runtimeName != null && Avm2Convert.IsNumeric(runtimeName))
                return array.GetIndex(Avm2Convert.ToInt32(runtimeName));

            Avm2QName name = ResolveNameForTarget(file, multinameIndex, target, runtimeName);
            return GetProperty(target, name);
        }

        private void SetPropertyDynamic(
            Avm2ExecutionContext frame,
            AbcFile file,
            int multinameIndex,
            object target,
            object runtimeName,
            object value
        )
        {
            if (target is Avm2Array array && runtimeName != null && Avm2Convert.IsNumeric(runtimeName))
            {
                array.SetIndex(Avm2Convert.ToInt32(runtimeName), value);
                return;
            }

            Avm2QName name = ResolveNameForTarget(file, multinameIndex, target, runtimeName);
            SetProperty(target, name, value);
        }

        public object GetProperty(object target, Avm2QName name)
        {
            if (Avm2Convert.IsNullOrUndefined(target))
            {
                throw new Avm2ThrownException(MakeError(
                    "TypeError",
                    "Cannot read property " + name.Local + " of " + Avm2Convert.ToString(target)));
            }

            if (target is string text)
            {
                if (builtins.TryGetStringMember(text, name, out object member))
                    return member;

                return Avm2Function.FromNative(name.Local, (receiver, args) =>
                    builtins.TryCallStringMethod(
                        Avm2Convert.ToString(receiver ?? text), name, args, out object r)
                        ? r
                        : Avm2Undefined.Value).Bind(text);
            }

            if (target is Avm2Array array)
            {
                // Numeric member names index the array.
                if (int.TryParse(name.Local, out int index))
                    return array.GetIndex(index);
            }

            if (target is Avm2Class type)
            {
                if (type.TryFindStaticBinding(name, out Avm2Binding staticBinding))
                    return ReadBinding(staticBinding, type);

                if (type.TryGetDynamic(name, out object dynamicStatic))
                    return dynamicStatic;
            }

            if (target is Avm2Object obj)
            {
                Avm2Class objectClass = obj.Class;

                if (objectClass != null && objectClass.TryFindInstanceBinding(name, out Avm2Binding binding))
                    return ReadBinding(binding, obj);

                if (obj.TryGetDynamic(name, out object value))
                    return value;

                return Avm2Undefined.Value;
            }

            if (Avm2Convert.IsNumeric(target))
            {
                return Avm2Function.FromNative(name.Local, (receiver, args) =>
                    builtins.TryCallNumberMethod(receiver ?? target, name, args, out object r)
                        ? r
                        : Avm2Undefined.Value).Bind(target);
            }

            return Avm2Undefined.Value;
        }

        private object ReadBinding(Avm2Binding binding, object receiver)
        {
            switch (binding.Kind)
            {
                case Avm2BindingKind.Slot:
                case Avm2BindingKind.Constant:
                    if (binding.NativeGetter != null)
                        return binding.NativeGetter(receiver, Array.Empty<object>());

                    if (binding.ConstantValue != null && binding.SlotId == 0)
                        return binding.ConstantValue;

                    return receiver is Avm2Object holder
                        ? holder.GetSlot(binding.SlotId)
                        : Avm2Undefined.Value;

                case Avm2BindingKind.Method:
                    if (binding.NativeFunction != null)
                        return binding.NativeFunction.Bind(receiver);

                    return Avm2Function.FromMethod(
                        binding.Method,
                        binding.DeclaringClass?.CapturedScope ?? Array.Empty<object>(),
                        binding.DeclaringClass).Bind(receiver);

                case Avm2BindingKind.Getter:
                case Avm2BindingKind.GetterSetter:
                    if (binding.NativeGetter != null)
                        return binding.NativeGetter(receiver, Array.Empty<object>());

                    if (binding.Getter != null)
                    {
                        return Invoke(binding.Getter, receiver, Array.Empty<object>(),
                            binding.DeclaringClass?.CapturedScope ?? Array.Empty<object>(),
                            binding.DeclaringClass);
                    }

                    return Avm2Undefined.Value;

                case Avm2BindingKind.Setter:
                    // Write-only property read as undefined, which is what AS3 does.
                    return Avm2Undefined.Value;

                default:
                    return binding.ConstantValue ?? Avm2Undefined.Value;
            }
        }

        public void SetProperty(object target, Avm2QName name, object value)
        {
            if (Avm2Convert.IsNullOrUndefined(target))
            {
                throw new Avm2ThrownException(MakeError(
                    "TypeError",
                    "Cannot set property " + name.Local + " on " + Avm2Convert.ToString(target)));
            }

            if (target is Avm2Array array && int.TryParse(name.Local, out int index))
            {
                array.SetIndex(index, value);
                return;
            }

            if (target is Avm2Class type && type.TryFindStaticBinding(name, out Avm2Binding staticBinding))
            {
                WriteBinding(staticBinding, type, value);
                return;
            }

            if (target is Avm2Object obj)
            {
                Avm2Class objectClass = obj.Class;

                if (objectClass != null && objectClass.TryFindInstanceBinding(name, out Avm2Binding binding))
                {
                    WriteBinding(binding, obj, value);
                    return;
                }

                obj.SetDynamic(name, value);
            }
        }

        private void WriteBinding(Avm2Binding binding, object receiver, object value)
        {
            switch (binding.Kind)
            {
                case Avm2BindingKind.Slot:
                    if (binding.NativeSetter != null)
                    {
                        binding.NativeSetter(receiver, new[] { value });
                        return;
                    }

                    if (receiver is Avm2Object holder)
                        holder.SetSlot(binding.SlotId, value);

                    return;

                case Avm2BindingKind.Setter:
                case Avm2BindingKind.GetterSetter:
                    if (binding.NativeSetter != null)
                    {
                        binding.NativeSetter(receiver, new[] { value });
                        return;
                    }

                    if (binding.Setter != null)
                    {
                        Invoke(binding.Setter, receiver, new[] { value },
                            binding.DeclaringClass?.CapturedScope ?? Array.Empty<object>(),
                            binding.DeclaringClass);
                    }

                    return;

                case Avm2BindingKind.Constant:
                    // Constants silently ignore writes outside their initialiser.
                    return;

                default:
                    if (receiver is Avm2Object target)
                        target.SetDynamic(binding.Name, value);

                    return;
            }
        }

        private bool HasProperty(object target, Avm2QName name)
        {
            if (target is Avm2Class type)
                return type.StaticBindings.ContainsKey(name) || type.HasDynamic(name);

            if (target is Avm2Object obj)
            {
                if (obj.Class != null && obj.Class.InstanceBindings.ContainsKey(name))
                    return true;

                return obj.HasDynamic(name);
            }

            if (target is string text)
                return builtins.TryGetStringMember(text, name, out _);

            return false;
        }

        // Walks the scope chain inward-out, then the global object, looking for the
        // object that actually carries the name.
        private object FindPropertyHolder(Avm2ExecutionContext frame, Avm2QName name)
        {
            for (int i = frame.TotalScopeDepth - 1; i >= 0; i--)
            {
                object scope = frame.GetScopeAt(i);

                if (scope != null && HasProperty(scope, name))
                    return scope;
            }

            if (domain.TryGetGlobal(name, out _))
                return domain.Global;

            return null;
        }

        // ---- helpers: calls ---------------------------------------------------

        private object[] PopArguments(Avm2ExecutionContext frame, int count)
        {
            if (count <= 0)
                return Array.Empty<object>();

            object[] args = new object[count];

            for (int i = count - 1; i >= 0; i--)
                args[i] = frame.Pop();

            return args;
        }

        public object CallValue(object callable, object receiver, object[] arguments)
        {
            arguments ??= Array.Empty<object>();

            if (callable is Avm2Function function)
            {
                object target = function.HasBoundReceiver ? function.BoundReceiver : receiver;

                if (function.IsNative)
                    return function.Native(target, arguments) ?? Avm2Undefined.Value;

                return Invoke(function.Method, target, arguments, function.CapturedScope,
                    function.DeclaringClass);
            }

            // Calling a class is a coercion in AS3: int("3"), String(x), Array(...).
            if (callable is Avm2Class type)
            {
                if (type.NativeConstruct != null)
                    return type.NativeConstruct(arguments);

                return arguments.Length > 0 ? arguments[0] : Avm2Undefined.Value;
            }

            throw new Avm2ThrownException(MakeError(
                "TypeError",
                "value is not a function"));
        }

        private object CallPropertyOn(
            Avm2ExecutionContext frame,
            AbcFile file,
            int multinameIndex,
            object target,
            object runtimeName,
            object[] args
        )
        {
            if (Avm2Convert.IsNullOrUndefined(target))
            {
                throw new Avm2ThrownException(MakeError(
                    "TypeError",
                    "Cannot call a method of " + Avm2Convert.ToString(target)));
            }

            Avm2QName name = ResolveNameForTarget(file, multinameIndex, target, runtimeName);

            // Strings and numbers carry their methods natively rather than through a
            // bindings table, so they are dispatched directly.
            if (target is string text && builtins.TryCallStringMethod(text, name, args, out object stringResult))
                return stringResult;

            if (Avm2Convert.IsNumeric(target) &&
                builtins.TryCallNumberMethod(target, name, args, out object numberResult))
            {
                return numberResult;
            }

            object member = GetProperty(target, name);

            if (member is Avm2Function || member is Avm2Class)
                return CallValue(member, target, args);

            throw new Avm2ThrownException(MakeError(
                "TypeError",
                "Property " + name.Local + " is not a function"));
        }

        private object CallSuper(Avm2ExecutionContext frame, object receiver, Avm2QName name, object[] args)
        {
            Avm2Class declaring = frame.DeclaringClass ?? FindDeclaringClass(frame);
            Avm2Class super = declaring?.Super;

            if (super != null && super.TryFindInstanceBinding(name, out Avm2Binding binding))
            {
                object member = ReadBinding(binding, receiver);

                if (member is Avm2Function || member is Avm2Class)
                    return CallValue(member, receiver, args);
            }

            // Falling back to the receiver's own resolution keeps a super call working
            // when the declaring class could not be recovered from the frame.
            object fallback = GetProperty(receiver, name);

            if (fallback is Avm2Function || fallback is Avm2Class)
                return CallValue(fallback, receiver, args);

            return Avm2Undefined.Value;
        }

        // The class whose scope this frame was entered with, used to find `super`.
        private Avm2Class FindDeclaringClass(Avm2ExecutionContext frame)
        {
            for (int i = frame.TotalScopeDepth - 1; i >= 0; i--)
            {
                if (frame.GetScopeAt(i) is Avm2Class type)
                    return type;
            }

            return null;
        }

        private void ConstructSuper(Avm2ExecutionContext frame, object receiver, object[] args)
        {
            // Strictly the class this frame belongs to. Deriving it from the receiver
            // would resolve to the most-derived type at every level, so a base
            // constructor would call itself forever.
            Avm2Class declaring = frame.DeclaringClass ?? FindDeclaringClass(frame);
            Avm2Class super = declaring?.Super;

            if (super == null)
                return;

            if (super.IsNative)
            {
                // A native base has no bytecode constructor; its state is already set
                // up by the instance's own allocation.
                return;
            }

            if (super.Constructor != null)
                Invoke(super.Constructor, receiver, args, super.CapturedScope ?? Array.Empty<object>(), super);
        }

        public object Construct(object type, object[] args)
        {
            if (!(type is Avm2Class cls))
            {
                throw new Avm2ThrownException(MakeError(
                    "TypeError",
                    "value is not a constructor"));
            }

            if (cls.NativeConstruct != null)
                return cls.NativeConstruct(args);

            Avm2Object instance = AllocateInstance(cls);

            if (cls.Constructor != null)
                Invoke(cls.Constructor, instance, args, cls.CapturedScope ?? Array.Empty<object>(), cls);

            return instance;
        }

        // A user class that extends a host class must be built by that host class, or
        // it gets a plain object where the runtime expects a specific backing type -
        // `class Main extends Sprite` would produce something the display tree cannot
        // hold. The nearest native ancestor allocates, then the instance is retagged
        // with the derived class and given that class's slot layout.
        private Avm2Object AllocateInstance(Avm2Class cls)
        {
            Avm2Class ancestor = cls.Super;
            int guard = 0;

            while (ancestor != null && guard++ < 256)
            {
                if (ancestor.NativeConstruct != null)
                {
                    object allocated = ancestor.NativeConstruct(Array.Empty<object>());

                    // Native constructors for the primitive wrappers return values
                    // rather than objects; those cannot back a class instance.
                    if (allocated is Avm2Object native)
                    {
                        native.Class = cls;
                        ResizeSlots(native, cls.InstanceSlotCount);
                        return native;
                    }

                    break;
                }

                ancestor = ancestor.Super;
            }

            return new Avm2Object(cls);
        }

        private static void ResizeSlots(Avm2Object instance, int slotCount)
        {
            if (slotCount <= 0)
                return;

            object[] slots = new object[slotCount];
            object[] existing = instance.Slots;
            int copied = existing != null ? Math.Min(existing.Length, slotCount) : 0;

            for (int i = 0; i < copied; i++)
                slots[i] = existing[i];

            for (int i = copied; i < slotCount; i++)
                slots[i] = Avm2Undefined.Value;

            instance.Slots = slots;
        }

        // ---- helpers: classes -------------------------------------------------

        private Avm2Class BuildClass(AbcFile file, int classIndex, Avm2Class baseClass, object[] scope)
        {
            AbcInstanceInfo instance = file.GetInstance(classIndex);
            AbcClassInfo classInfo = classIndex >= 0 && classIndex < file.Classes.Count
                ? file.Classes[classIndex]
                : null;

            if (instance == null || classInfo == null)
            {
                throw new Avm2ThrownException(MakeError(
                    "ReferenceError",
                    "newclass refers to missing class #" + classIndex));
            }

            domain.TryResolveQName(file, instance.NameIndex, out Avm2QName name);

            Avm2Class type = new Avm2Class(name)
            {
                Super = baseClass,
                Instance = instance,
                Static = classInfo,
                IsInterface = instance.IsInterface,
                IsSealed = instance.IsSealed,
                IsDynamic = !instance.IsSealed,
                CapturedScope = scope,
                Class = builtins.ClassClass,
                Constructor = file.GetMethod(instance.InitialiserIndex),
                StaticInitialiser = file.GetMethod(classInfo.StaticInitialiserIndex)
            };

            // Inherited members are copied in so a lookup is a single dictionary hit
            // rather than a walk up the chain on every access.
            if (baseClass != null)
            {
                foreach (KeyValuePair<Avm2QName, Avm2Binding> entry in baseClass.InstanceBindings)
                    type.InstanceBindings[entry.Key] = entry.Value;
            }

            for (int i = 0; i < instance.InterfaceCount; i++)
            {
                if (domain.TryResolveQName(file, instance.InterfaceIndices[i], out Avm2QName interfaceName))
                    type.Interfaces.Add(interfaceName);
            }

            int instanceSlots = baseClass?.InstanceSlotCount ?? 0;
            AddTraits(file, type, instance.Traits, type.InstanceBindings, ref instanceSlots, false);
            type.InstanceSlotCount = instanceSlots;

            int staticSlots = 0;
            AddTraits(file, type, classInfo.Traits, type.StaticBindings, ref staticSlots, true);
            type.StaticSlotCount = staticSlots;
            type.Slots = staticSlots > 0 ? new object[staticSlots] : Array.Empty<object>();

            for (int i = 0; i < type.Slots.Length; i++)
                type.Slots[i] = Avm2Undefined.Value;

            // A class is on the scope chain of its own members, which is how code
            // inside a method resolves the class's own name and its package.
            object[] memberScope = new object[(scope?.Length ?? 0) + 1];

            if (scope != null)
                Array.Copy(scope, memberScope, scope.Length);

            memberScope[memberScope.Length - 1] = type;
            type.CapturedScope = memberScope;

            RunStaticInitialiser(type, scope);
            return type;
        }

        private void AddTraits(
            AbcFile file,
            Avm2Class owner,
            List<AbcTrait> traits,
            Dictionary<Avm2QName, Avm2Binding> bindings,
            ref int slotCursor,
            bool isStatic
        )
        {
            if (traits == null)
                return;

            for (int i = 0; i < traits.Count; i++)
            {
                AbcTrait trait = traits[i];

                if (!domain.TryResolveQName(file, trait.NameIndex, out Avm2QName name))
                    name = Avm2QName.Public(trait.Name ?? string.Empty);

                switch (trait.Kind)
                {
                    case AbcTraitKind.Slot:
                    case AbcTraitKind.Const:
                    {
                        // slot_id 0 means "assign the next free slot".
                        int slotId = trait.SlotId != 0 ? trait.SlotId : slotCursor + 1;

                        if (slotId > slotCursor)
                            slotCursor = slotId;

                        bindings[name] = new Avm2Binding
                        {
                            Name = name,
                            Kind = trait.Kind == AbcTraitKind.Const
                                ? Avm2BindingKind.Constant
                                : Avm2BindingKind.Slot,
                            SlotId = slotId,
                            DeclaringClass = owner,
                            IsStatic = isStatic,
                            ConstantValue = trait.ValueIndex != 0
                                ? ConstantValue(file, trait.ValueKind, trait.ValueIndex)
                                : null
                        };
                        break;
                    }

                    case AbcTraitKind.Method:
                    case AbcTraitKind.Function:
                        bindings[name] = new Avm2Binding
                        {
                            Name = name,
                            Kind = Avm2BindingKind.Method,
                            Method = file.GetMethod(trait.MethodIndex),
                            DeclaringClass = owner,
                            IsStatic = isStatic
                        };
                        break;

                    case AbcTraitKind.Getter:
                    case AbcTraitKind.Setter:
                    {
                        // A property with both halves arrives as two traits that must
                        // merge into one binding, or the second would hide the first.
                        bindings.TryGetValue(name, out Avm2Binding existing);

                        if (existing == null || !existing.IsAccessor || existing.DeclaringClass != owner)
                        {
                            existing = new Avm2Binding
                            {
                                Name = name,
                                DeclaringClass = owner,
                                IsStatic = isStatic
                            };
                        }

                        if (trait.Kind == AbcTraitKind.Getter)
                            existing.Getter = file.GetMethod(trait.MethodIndex);
                        else
                            existing.Setter = file.GetMethod(trait.MethodIndex);

                        existing.Kind = existing.Getter != null && existing.Setter != null
                            ? Avm2BindingKind.GetterSetter
                            : existing.Getter != null
                                ? Avm2BindingKind.Getter
                                : Avm2BindingKind.Setter;

                        bindings[name] = existing;
                        break;
                    }

                    case AbcTraitKind.Class:
                    {
                        int slotId = trait.SlotId != 0 ? trait.SlotId : slotCursor + 1;

                        if (slotId > slotCursor)
                            slotCursor = slotId;

                        bindings[name] = new Avm2Binding
                        {
                            Name = name,
                            Kind = Avm2BindingKind.Slot,
                            SlotId = slotId,
                            DeclaringClass = owner,
                            IsStatic = isStatic
                        };
                        break;
                    }
                }
            }
        }

        private void RunStaticInitialiser(Avm2Class type, object[] scope)
        {
            if (type.StaticInitialiserRun || type.StaticInitialiser?.Body == null)
                return;

            type.StaticInitialiserRun = true;

            // The class itself is in scope for its own static initialiser.
            object[] classScope = new object[(scope?.Length ?? 0) + 1];

            if (scope != null)
                Array.Copy(scope, classScope, scope.Length);

            classScope[classScope.Length - 1] = type;
            Invoke(type.StaticInitialiser, type, Array.Empty<object>(), classScope, type);
        }

        private Avm2Object CreateActivation(Avm2ExecutionContext frame)
        {
            // An activation object holds the locals a nested function closes over. Its
            // shape comes from the enclosing body's own traits.
            int slots = frame.Body?.Traits != null ? frame.Body.Traits.Count : 0;
            Avm2Object activation = new Avm2Object
            {
                Slots = slots > 0 ? new object[slots] : Array.Empty<object>()
            };

            for (int i = 0; i < activation.Slots.Length; i++)
                activation.Slots[i] = Avm2Undefined.Value;

            return activation;
        }

        // ---- helpers: types and iteration ------------------------------------

        private bool IsType(AbcFile file, int multinameIndex, object value)
        {
            if (!domain.TryResolveQName(file, multinameIndex, out Avm2QName typeName))
                return false;

            if (!domain.TryGetGlobal(typeName, out object typeValue))
                return false;

            return IsTypeLate(value, typeValue);
        }

        private bool IsTypeLate(object value, object type)
        {
            if (!(type is Avm2Class target) || Avm2Convert.IsNullOrUndefined(value))
                return false;

            Avm2Class valueClass = builtins.GetClassOf(value);
            return valueClass != null && valueClass.IsSubclassOf(target);
        }

        // Returns the 1-based index of the next enumerable property, or 0 when the
        // iteration is finished.
        private int NextNameIndex(object target, int index)
        {
            if (!(target is Avm2Object obj))
                return 0;

            if (obj is Avm2Array array)
                return index < array.Length ? index + 1 : 0;

            return index < obj.DynamicCount ? index + 1 : 0;
        }

        private object NameAtIndex(object target, int index)
        {
            if (!(target is Avm2Object obj) || index <= 0)
                return Avm2Undefined.Value;

            if (obj is Avm2Array array)
                return index <= array.Length ? (index - 1).ToString() : (object)Avm2Undefined.Value;

            int cursor = 1;

            foreach (Avm2QName name in obj.DynamicNames)
            {
                if (cursor == index)
                    return name.Local;

                cursor++;
            }

            return Avm2Undefined.Value;
        }

        private object Coerce(AbcFile file, int multinameIndex, object value)
        {
            if (!domain.TryResolveQName(file, multinameIndex, out Avm2QName typeName))
                return value;

            // The primitive coercions are the ones that actually change a value; a
            // class type only narrows the static type, which the interpreter does not
            // track, so the value passes through.
            switch (typeName.Local)
            {
                case "int": return Avm2Convert.ToInt32(value);
                case "uint": return Avm2Convert.ToUint32(value);
                case "Number": return Avm2Convert.ToNumber(value);
                case "String": return Avm2Convert.IsNullOrUndefined(value)
                    ? value
                    : Avm2Convert.ToString(value);
                case "Boolean": return Avm2Convert.ToBoolean(value);
                default: return value;
            }
        }

        internal object MakeError(string className, string message)
        {
            if (domain.TryGetGlobal(Avm2QName.Public(className), out object type) &&
                type is Avm2Class errorClass && errorClass.NativeConstruct != null)
            {
                return errorClass.NativeConstruct(new object[] { message });
            }

            return message;
        }

        // ---- frames -----------------------------------------------------------

        private Avm2ExecutionContext RentFrame()
        {
            return framePool.Count > 0 ? framePool.Pop() : new Avm2ExecutionContext();
        }

        private void ReturnFrame(Avm2ExecutionContext frame)
        {
            if (frame == null || framePool.Count >= 64)
                return;

            frame.Reset();
            framePool.Push(frame);
        }

        private Avm2MethodCode GetDecodedBody(AbcMethodInfo method)
        {
            AbcMethodBody body = method.Body;

            if (decodedBodies.TryGetValue(body, out Avm2MethodCode cached))
                return cached;

            Avm2MethodCode decoded = Avm2CodeReader.Decode(body);
            decodedBodies[body] = decoded;

            if (!decoded.IsValid)
            {
                diagnostics.ReportUndecodableMethod(
                    "'" + (method.Name ?? "<anonymous>") + "'",
                    decoded.FailureReason);
            }

            return decoded;
        }
    }
}
