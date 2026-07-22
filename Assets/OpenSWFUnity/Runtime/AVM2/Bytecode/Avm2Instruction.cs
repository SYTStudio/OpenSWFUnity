namespace OpenSWFUnity.Runtime.AVM2.Bytecode
{
    // A single decoded instruction.
    //
    // Operands are kept as plain integers rather than boxed values so a decoded
    // method costs one array and no per-instruction allocation. LookupSwitch is the
    // only variable-width form and is the only case that carries a side array.
    public struct Avm2Instruction
    {
        public int Offset;
        public byte OpCode;
        public int OperandA;
        public int OperandB;

        // lookupswitch only: absolute targets, with the default case last.
        public int[] SwitchTargets;

        public string Name => Avm2OpCode.GetName(OpCode);

        public bool IsBranch
        {
            get
            {
                Avm2OpCodeInfo info = Avm2OpCode.Get(OpCode);
                return info.Shape == Avm2OperandShape.Signed24 ||
                       info.Shape == Avm2OperandShape.LookupSwitch;
            }
        }

        public bool IsTerminal =>
            OpCode == Avm2OpCode.ReturnVoid ||
            OpCode == Avm2OpCode.ReturnValue ||
            OpCode == Avm2OpCode.Throw;

        public override string ToString()
        {
            Avm2OpCodeInfo info = Avm2OpCode.Get(OpCode);

            if (!info.Defined)
                return Offset + ": <unknown 0x" + OpCode.ToString("X2") + ">";

            switch (info.Shape)
            {
                case Avm2OperandShape.None:
                    return Offset + ": " + info.Name;
                case Avm2OperandShape.U30U30:
                case Avm2OperandShape.Debug:
                    return Offset + ": " + info.Name + " " + OperandA + ", " + OperandB;
                case Avm2OperandShape.Signed24:
                    return Offset + ": " + info.Name + " -> " + OperandB;
                case Avm2OperandShape.LookupSwitch:
                    return Offset + ": " + info.Name + " cases=" +
                           (SwitchTargets != null ? SwitchTargets.Length : 0);
                default:
                    return Offset + ": " + info.Name + " " + OperandA;
            }
        }
    }
}
