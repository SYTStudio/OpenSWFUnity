using System;
using System.Collections.Generic;
using OpenSWFUnity.Runtime.AVM2.Abc;

namespace OpenSWFUnity.Runtime.AVM2.Bytecode
{
    // Result of decoding one method body.
    public sealed class Avm2MethodCode
    {
        public Avm2Instruction[] Instructions = Array.Empty<Avm2Instruction>();

        // Maps a bytecode offset to its index in Instructions, or -1 where the offset
        // falls inside an instruction. Branch validation uses it to prove every target
        // lands on a real instruction boundary.
        public int[] OffsetToIndex = Array.Empty<int>();

        public bool IsValid;
        public string FailureReason;

        // Opcodes encountered that this build has no semantics for. Decoding still
        // succeeded, so these are reportable rather than fatal.
        public List<byte> UnsupportedOpCodes;

        public int InstructionCount => Instructions.Length;

        public int IndexForOffset(int offset)
        {
            return offset >= 0 && offset < OffsetToIndex.Length ? OffsetToIndex[offset] : -1;
        }
    }

    // Linear-sweep decoder for a method body.
    //
    // AVM2 bytecode is not self-synchronising: operand widths vary and an unknown
    // opcode leaves no way to find the next instruction. Decoding therefore stops at
    // the first byte it cannot interpret and reports where, instead of guessing and
    // producing convincing nonsense.
    public static class Avm2CodeReader
    {
        public static Avm2MethodCode Decode(AbcMethodBody body)
        {
            Avm2MethodCode result = new Avm2MethodCode();

            if (body?.Code == null || body.Code.Length == 0)
            {
                result.IsValid = true;
                return result;
            }

            byte[] code = body.Code;
            List<Avm2Instruction> instructions = new List<Avm2Instruction>(
                Math.Min(code.Length, 1024)
            );
            int[] offsetToIndex = new int[code.Length + 1];

            for (int i = 0; i < offsetToIndex.Length; i++)
                offsetToIndex[i] = -1;

            int position = 0;

            while (position < code.Length)
            {
                int start = position;
                byte opcode = code[position++];
                Avm2OpCodeInfo info = Avm2OpCode.Get(opcode);

                if (!info.Defined)
                {
                    result.FailureReason =
                        "undefined opcode 0x" + opcode.ToString("X2") + " at offset " + start;
                    Finish(result, instructions, offsetToIndex, false);
                    return result;
                }

                Avm2Instruction instruction = new Avm2Instruction
                {
                    Offset = start,
                    OpCode = opcode
                };

                try
                {
                    switch (info.Shape)
                    {
                        case Avm2OperandShape.None:
                            break;

                        case Avm2OperandShape.U8:
                            instruction.OperandA = ReadU8(code, ref position);
                            break;

                        case Avm2OperandShape.U30:
                            instruction.OperandA = (int)ReadU30(code, ref position);
                            break;

                        case Avm2OperandShape.U30U30:
                            instruction.OperandA = (int)ReadU30(code, ref position);
                            instruction.OperandB = (int)ReadU30(code, ref position);
                            break;

                        case Avm2OperandShape.Signed24:
                        {
                            int delta = ReadS24(code, ref position);
                            instruction.OperandA = delta;
                            // Branches are relative to the byte after the operand, so the
                            // absolute target is only known once the operand is consumed.
                            instruction.OperandB = position + delta;
                            break;
                        }

                        case Avm2OperandShape.Debug:
                            instruction.OperandA = ReadU8(code, ref position);
                            instruction.OperandB = (int)ReadU30(code, ref position);
                            ReadU8(code, ref position);
                            ReadU30(code, ref position);
                            break;

                        case Avm2OperandShape.LookupSwitch:
                        {
                            int defaultDelta = ReadS24(code, ref position);
                            uint caseCount = ReadU30(code, ref position);

                            // The encoded count is one less than the number of cases.
                            if (caseCount > AbcLimits.MaxExceptionHandlers)
                                throw new AbcFormatException("lookupswitch case count " + caseCount, start);

                            int total = (int)caseCount + 1;
                            int[] targets = new int[total + 1];

                            for (int i = 0; i < total; i++)
                                targets[i] = start + ReadS24(code, ref position);

                            // Default is stored last so callers can treat the array as
                            // "cases, then fallback".
                            targets[total] = start + defaultDelta;
                            instruction.SwitchTargets = targets;
                            break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    result.FailureReason =
                        "truncated operands for " + info.Name + " at offset " + start +
                        ": " + exception.Message;
                    Finish(result, instructions, offsetToIndex, false);
                    return result;
                }

                offsetToIndex[start] = instructions.Count;
                instructions.Add(instruction);
            }

            Finish(result, instructions, offsetToIndex, true);
            ValidateBranchTargets(result, body);
            return result;
        }

        private static void Finish(
            Avm2MethodCode result,
            List<Avm2Instruction> instructions,
            int[] offsetToIndex,
            bool valid
        )
        {
            result.Instructions = instructions.ToArray();
            result.OffsetToIndex = offsetToIndex;
            result.IsValid = valid;
        }

        // Every branch must land on an instruction boundary. A target in the middle of
        // an instruction would decode as a different program on each entry, so it is
        // treated as corruption rather than something to run.
        private static void ValidateBranchTargets(Avm2MethodCode result, AbcMethodBody body)
        {
            int codeLength = body.CodeLength;

            for (int i = 0; i < result.Instructions.Length; i++)
            {
                Avm2Instruction instruction = result.Instructions[i];
                Avm2OpCodeInfo info = Avm2OpCode.Get(instruction.OpCode);

                if (info.Shape == Avm2OperandShape.Signed24)
                {
                    if (!IsInstructionBoundary(result, instruction.OperandB, codeLength))
                    {
                        result.IsValid = false;
                        result.FailureReason =
                            info.Name + " at offset " + instruction.Offset +
                            " branches to " + instruction.OperandB +
                            ", which is not an instruction boundary";
                        return;
                    }
                }
                else if (info.Shape == Avm2OperandShape.LookupSwitch &&
                         instruction.SwitchTargets != null)
                {
                    for (int t = 0; t < instruction.SwitchTargets.Length; t++)
                    {
                        if (IsInstructionBoundary(result, instruction.SwitchTargets[t], codeLength))
                            continue;

                        result.IsValid = false;
                        result.FailureReason =
                            "lookupswitch at offset " + instruction.Offset +
                            " has target " + instruction.SwitchTargets[t] +
                            ", which is not an instruction boundary";
                        return;
                    }
                }
            }

            for (int i = 0; body.Exceptions != null && i < body.Exceptions.Length; i++)
            {
                AbcExceptionInfo handler = body.Exceptions[i];

                if (IsInstructionBoundary(result, handler.Target, codeLength))
                    continue;

                result.IsValid = false;
                result.FailureReason =
                    "exception handler " + i + " targets " + handler.Target +
                    ", which is not an instruction boundary";
                return;
            }
        }

        // A branch to exactly codeLength falls off the end of the method, which some
        // compilers emit for an unreachable trailing jump; treat it as in-bounds.
        private static bool IsInstructionBoundary(Avm2MethodCode result, int offset, int codeLength)
        {
            if (offset == codeLength)
                return true;

            return result.IndexForOffset(offset) >= 0;
        }

        private static byte ReadU8(byte[] code, ref int position)
        {
            if (position >= code.Length)
                throw new AbcFormatException("Unexpected end of method code", position);

            return code[position++];
        }

        private static int ReadS24(byte[] code, ref int position)
        {
            if (position + 3 > code.Length)
                throw new AbcFormatException("Unexpected end of method code", position);

            int value = code[position] | (code[position + 1] << 8) | (code[position + 2] << 16);
            position += 3;

            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xFF000000);

            return value;
        }

        private static uint ReadU30(byte[] code, ref int position)
        {
            uint value = 0;
            int shift = 0;

            for (int i = 0; i < 5; i++)
            {
                if (position >= code.Length)
                    throw new AbcFormatException("Unexpected end of method code", position);

                byte current = code[position++];
                value |= (uint)(current & 0x7F) << shift;

                if ((current & 0x80) == 0)
                    return value;

                shift += 7;
            }

            throw new AbcFormatException("Variable-length integer exceeds five bytes", position);
        }
    }
}
