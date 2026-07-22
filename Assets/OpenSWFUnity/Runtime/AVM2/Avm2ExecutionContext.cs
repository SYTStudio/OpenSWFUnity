using System;
using OpenSWFUnity.Runtime.AVM2.Abc;
using OpenSWFUnity.Runtime.AVM2.Bytecode;

namespace OpenSWFUnity.Runtime.AVM2
{
    // One AVM2 activation frame: operand stack, register file, and scope stack.
    //
    // Kept deliberately separate from the runtime so the execution model can be
    // built and tested independently of ABC parsing. Frames are sized from the
    // method body's declared max_stack / local_count / max_scope_depth, which the
    // format guarantees are sufficient; exceeding them means the body lied and is
    // treated as an error rather than grown silently.
    public sealed class Avm2ExecutionContext
    {
        private object[] stack;
        private object[] locals;
        private object[] scopes;

        public int StackDepth { get; private set; }
        public int ScopeDepth { get; private set; }
        public int LocalCount => locals != null ? locals.Length : 0;
        public AbcMethodBody Body { get; private set; }

        // Decoded instructions for Body, and the file whose constant pool its indices
        // refer to. Both are set by the interpreter when it enters the frame.
        public Avm2MethodCode Code;
        public AbcFile File;
        public AbcMethodInfo Method;

        // The class whose member this frame is executing, when it has one.
        // `super` is resolved from here rather than from the receiver's class,
        // which during a base constructor is still the most-derived type and
        // would make the super chain loop back on itself.
        public Values.Avm2Class DeclaringClass;

        // Scopes captured where this method's closure was created. Lookups that miss
        // the frame's own scope stack continue through these, outermost last.
        public object[] OuterScope = Array.Empty<object>();

        public void Initialize(AbcMethodBody body)
        {
            Body = body ?? throw new ArgumentNullException(nameof(body));

            int stackCapacity = Math.Max(1, body.MaxStack);
            int localCapacity = Math.Max(1, body.LocalCount);
            int scopeCapacity = Math.Max(1, body.MaxScopeDepth);

            if (stack == null || stack.Length < stackCapacity)
                stack = new object[stackCapacity];

            if (locals == null || locals.Length < localCapacity)
                locals = new object[localCapacity];

            if (scopes == null || scopes.Length < scopeCapacity)
                scopes = new object[scopeCapacity];

            Array.Clear(stack, 0, stack.Length);
            Array.Clear(locals, 0, locals.Length);
            Array.Clear(scopes, 0, scopes.Length);

            StackDepth = 0;
            ScopeDepth = 0;
        }

        // Cleared rather than released: a pooled frame that kept references would
        // hold whole object graphs alive between calls.
        public void Reset()
        {
            if (stack != null)
                Array.Clear(stack, 0, stack.Length);

            if (locals != null)
                Array.Clear(locals, 0, locals.Length);

            if (scopes != null)
                Array.Clear(scopes, 0, scopes.Length);

            StackDepth = 0;
            ScopeDepth = 0;
            Body = null;
            Code = null;
            File = null;
            Method = null;
            DeclaringClass = null;
            OuterScope = Array.Empty<object>();
        }

        // Everything visible from this frame, outermost first, as one array. A
        // closure created here keeps it so the function can still resolve names from
        // its defining context after this frame is gone.
        public object[] CaptureScopeChain()
        {
            int total = OuterScope.Length + ScopeDepth;

            if (total == 0)
                return Array.Empty<object>();

            object[] captured = new object[total];
            Array.Copy(OuterScope, 0, captured, 0, OuterScope.Length);
            Array.Copy(scopes, 0, captured, OuterScope.Length, ScopeDepth);
            return captured;
        }

        // Scope lookup walks inward-out: this frame's own scopes first, then the
        // captured chain.
        public int TotalScopeDepth => OuterScope.Length + ScopeDepth;

        public object GetScopeAt(int index)
        {
            if (index < OuterScope.Length)
                return OuterScope[index];

            int local = index - OuterScope.Length;
            return local >= 0 && local < ScopeDepth ? scopes[local] : null;
        }

        // Discards operand stack contents without touching locals or scopes, which is
        // what entering an exception handler requires.
        public void ClearStack()
        {
            if (stack != null)
                Array.Clear(stack, 0, stack.Length);

            StackDepth = 0;
        }

        public void Push(object value)
        {
            if (StackDepth >= stack.Length)
            {
                throw new InvalidOperationException(
                    "AVM2 operand stack overflow: method declared max_stack=" +
                    (Body != null ? Body.MaxStack : 0)
                );
            }

            stack[StackDepth++] = value;
        }

        public object Pop()
        {
            if (StackDepth <= 0)
                throw new InvalidOperationException("AVM2 operand stack underflow");

            object value = stack[--StackDepth];
            stack[StackDepth] = null;
            return value;
        }

        public object Peek()
        {
            if (StackDepth <= 0)
                throw new InvalidOperationException("AVM2 operand stack underflow");

            return stack[StackDepth - 1];
        }

        public object GetLocal(int index)
        {
            if (index < 0 || index >= locals.Length)
            {
                throw new InvalidOperationException(
                    "AVM2 local index " + index + " is outside the declared local_count " +
                    locals.Length
                );
            }

            return locals[index];
        }

        public void SetLocal(int index, object value)
        {
            if (index < 0 || index >= locals.Length)
            {
                throw new InvalidOperationException(
                    "AVM2 local index " + index + " is outside the declared local_count " +
                    locals.Length
                );
            }

            locals[index] = value;
        }

        public void PushScope(object value)
        {
            if (ScopeDepth >= scopes.Length)
            {
                throw new InvalidOperationException(
                    "AVM2 scope stack overflow: method declared max_scope_depth=" +
                    (Body != null ? Body.MaxScopeDepth : 0)
                );
            }

            scopes[ScopeDepth++] = value;
        }

        public object PopScope()
        {
            if (ScopeDepth <= 0)
                throw new InvalidOperationException("AVM2 scope stack underflow");

            object value = scopes[--ScopeDepth];
            scopes[ScopeDepth] = null;
            return value;
        }

        public object GetScope(int index)
        {
            if (index < 0 || index >= ScopeDepth)
                throw new InvalidOperationException("AVM2 scope index " + index + " is out of range");

            return scopes[index];
        }
    }
}
