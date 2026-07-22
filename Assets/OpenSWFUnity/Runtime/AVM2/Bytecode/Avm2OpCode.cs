namespace OpenSWFUnity.Runtime.AVM2.Bytecode
{
    // How an instruction's operands are encoded. Decoding cannot proceed past an
    // instruction without knowing this, so an unrecognised opcode is fatal to a
    // linear scan rather than something that can be stepped over.
    public enum Avm2OperandShape
    {
        None,
        U8,
        U30,
        U30U30,
        Signed24,
        Debug,
        LookupSwitch
    }

    public struct Avm2OpCodeInfo
    {
        public string Name;
        public Avm2OperandShape Shape;
        public bool Defined;
    }

    // The AVM2 instruction set, indexed by opcode byte.
    public static class Avm2OpCode
    {
        public const byte Nop = 0x02;
        public const byte Throw = 0x03;
        public const byte Jump = 0x10;
        public const byte LookupSwitch = 0x1B;
        public const byte ReturnVoid = 0x47;
        public const byte ReturnValue = 0x48;
        public const byte Debug = 0xEF;

        private static readonly Avm2OpCodeInfo[] Table = new Avm2OpCodeInfo[256];

        static Avm2OpCode()
        {
            Define(0x01, "bkpt", Avm2OperandShape.None);
            Define(0x02, "nop", Avm2OperandShape.None);
            Define(0x03, "throw", Avm2OperandShape.None);
            Define(0x04, "getsuper", Avm2OperandShape.U30);
            Define(0x05, "setsuper", Avm2OperandShape.U30);
            Define(0x06, "dxns", Avm2OperandShape.U30);
            Define(0x07, "dxnslate", Avm2OperandShape.None);
            Define(0x08, "kill", Avm2OperandShape.U30);
            Define(0x09, "label", Avm2OperandShape.None);
            Define(0x0C, "ifnlt", Avm2OperandShape.Signed24);
            Define(0x0D, "ifnle", Avm2OperandShape.Signed24);
            Define(0x0E, "ifngt", Avm2OperandShape.Signed24);
            Define(0x0F, "ifnge", Avm2OperandShape.Signed24);
            Define(0x10, "jump", Avm2OperandShape.Signed24);
            Define(0x11, "iftrue", Avm2OperandShape.Signed24);
            Define(0x12, "iffalse", Avm2OperandShape.Signed24);
            Define(0x13, "ifeq", Avm2OperandShape.Signed24);
            Define(0x14, "ifne", Avm2OperandShape.Signed24);
            Define(0x15, "iflt", Avm2OperandShape.Signed24);
            Define(0x16, "ifle", Avm2OperandShape.Signed24);
            Define(0x17, "ifgt", Avm2OperandShape.Signed24);
            Define(0x18, "ifge", Avm2OperandShape.Signed24);
            Define(0x19, "ifstricteq", Avm2OperandShape.Signed24);
            Define(0x1A, "ifstrictne", Avm2OperandShape.Signed24);
            Define(0x1B, "lookupswitch", Avm2OperandShape.LookupSwitch);
            Define(0x1C, "pushwith", Avm2OperandShape.None);
            Define(0x1D, "popscope", Avm2OperandShape.None);
            Define(0x1E, "nextname", Avm2OperandShape.None);
            Define(0x1F, "hasnext", Avm2OperandShape.None);
            Define(0x20, "pushnull", Avm2OperandShape.None);
            Define(0x21, "pushundefined", Avm2OperandShape.None);
            Define(0x23, "nextvalue", Avm2OperandShape.None);
            Define(0x24, "pushbyte", Avm2OperandShape.U8);
            Define(0x25, "pushshort", Avm2OperandShape.U30);
            Define(0x26, "pushtrue", Avm2OperandShape.None);
            Define(0x27, "pushfalse", Avm2OperandShape.None);
            Define(0x28, "pushnan", Avm2OperandShape.None);
            Define(0x29, "pop", Avm2OperandShape.None);
            Define(0x2A, "dup", Avm2OperandShape.None);
            Define(0x2B, "swap", Avm2OperandShape.None);
            Define(0x2C, "pushstring", Avm2OperandShape.U30);
            Define(0x2D, "pushint", Avm2OperandShape.U30);
            Define(0x2E, "pushuint", Avm2OperandShape.U30);
            Define(0x2F, "pushdouble", Avm2OperandShape.U30);
            Define(0x30, "pushscope", Avm2OperandShape.None);
            Define(0x31, "pushnamespace", Avm2OperandShape.U30);
            Define(0x32, "hasnext2", Avm2OperandShape.U30U30);

            // Alchemy / domain-memory access. No operands; the address is on the stack.
            Define(0x35, "li8", Avm2OperandShape.None);
            Define(0x36, "li16", Avm2OperandShape.None);
            Define(0x37, "li32", Avm2OperandShape.None);
            Define(0x38, "lf32", Avm2OperandShape.None);
            Define(0x39, "lf64", Avm2OperandShape.None);
            Define(0x3A, "si8", Avm2OperandShape.None);
            Define(0x3B, "si16", Avm2OperandShape.None);
            Define(0x3C, "si32", Avm2OperandShape.None);
            Define(0x3D, "sf32", Avm2OperandShape.None);
            Define(0x3E, "sf64", Avm2OperandShape.None);

            Define(0x40, "newfunction", Avm2OperandShape.U30);
            Define(0x41, "call", Avm2OperandShape.U30);
            Define(0x42, "construct", Avm2OperandShape.U30);
            Define(0x43, "callmethod", Avm2OperandShape.U30U30);
            Define(0x44, "callstatic", Avm2OperandShape.U30U30);
            Define(0x45, "callsuper", Avm2OperandShape.U30U30);
            Define(0x46, "callproperty", Avm2OperandShape.U30U30);
            Define(0x47, "returnvoid", Avm2OperandShape.None);
            Define(0x48, "returnvalue", Avm2OperandShape.None);
            Define(0x49, "constructsuper", Avm2OperandShape.U30);
            Define(0x4A, "constructprop", Avm2OperandShape.U30U30);
            Define(0x4C, "callproplex", Avm2OperandShape.U30U30);
            Define(0x4E, "callsupervoid", Avm2OperandShape.U30U30);
            Define(0x4F, "callpropvoid", Avm2OperandShape.U30U30);
            Define(0x50, "sxi1", Avm2OperandShape.None);
            Define(0x51, "sxi8", Avm2OperandShape.None);
            Define(0x52, "sxi16", Avm2OperandShape.None);
            Define(0x53, "applytype", Avm2OperandShape.U30);
            Define(0x55, "newobject", Avm2OperandShape.U30);
            Define(0x56, "newarray", Avm2OperandShape.U30);
            Define(0x57, "newactivation", Avm2OperandShape.None);
            Define(0x58, "newclass", Avm2OperandShape.U30);
            Define(0x59, "getdescendants", Avm2OperandShape.U30);
            Define(0x5A, "newcatch", Avm2OperandShape.U30);
            Define(0x5D, "findpropstrict", Avm2OperandShape.U30);
            Define(0x5E, "findproperty", Avm2OperandShape.U30);
            Define(0x5F, "finddef", Avm2OperandShape.U30);
            Define(0x60, "getlex", Avm2OperandShape.U30);
            Define(0x61, "setproperty", Avm2OperandShape.U30);
            Define(0x62, "getlocal", Avm2OperandShape.U30);
            Define(0x63, "setlocal", Avm2OperandShape.U30);
            Define(0x64, "getglobalscope", Avm2OperandShape.None);
            Define(0x65, "getscopeobject", Avm2OperandShape.U8);
            Define(0x66, "getproperty", Avm2OperandShape.U30);
            Define(0x68, "initproperty", Avm2OperandShape.U30);
            Define(0x6A, "deleteproperty", Avm2OperandShape.U30);
            Define(0x6C, "getslot", Avm2OperandShape.U30);
            Define(0x6D, "setslot", Avm2OperandShape.U30);
            Define(0x6E, "getglobalslot", Avm2OperandShape.U30);
            Define(0x6F, "setglobalslot", Avm2OperandShape.U30);
            Define(0x70, "convert_s", Avm2OperandShape.None);
            Define(0x71, "esc_xelem", Avm2OperandShape.None);
            Define(0x72, "esc_xattr", Avm2OperandShape.None);
            Define(0x73, "convert_i", Avm2OperandShape.None);
            Define(0x74, "convert_u", Avm2OperandShape.None);
            Define(0x75, "convert_d", Avm2OperandShape.None);
            Define(0x76, "convert_b", Avm2OperandShape.None);
            Define(0x77, "convert_o", Avm2OperandShape.None);
            Define(0x78, "checkfilter", Avm2OperandShape.None);
            Define(0x80, "coerce", Avm2OperandShape.U30);
            Define(0x82, "coerce_a", Avm2OperandShape.None);
            Define(0x85, "coerce_s", Avm2OperandShape.None);
            Define(0x86, "astype", Avm2OperandShape.U30);
            Define(0x87, "astypelate", Avm2OperandShape.None);
            Define(0x90, "negate", Avm2OperandShape.None);
            Define(0x91, "increment", Avm2OperandShape.None);
            Define(0x92, "inclocal", Avm2OperandShape.U30);
            Define(0x93, "decrement", Avm2OperandShape.None);
            Define(0x94, "declocal", Avm2OperandShape.U30);
            Define(0x95, "typeof", Avm2OperandShape.None);
            Define(0x96, "not", Avm2OperandShape.None);
            Define(0x97, "bitnot", Avm2OperandShape.None);
            Define(0xA0, "add", Avm2OperandShape.None);
            Define(0xA1, "subtract", Avm2OperandShape.None);
            Define(0xA2, "multiply", Avm2OperandShape.None);
            Define(0xA3, "divide", Avm2OperandShape.None);
            Define(0xA4, "modulo", Avm2OperandShape.None);
            Define(0xA5, "lshift", Avm2OperandShape.None);
            Define(0xA6, "rshift", Avm2OperandShape.None);
            Define(0xA7, "urshift", Avm2OperandShape.None);
            Define(0xA8, "bitand", Avm2OperandShape.None);
            Define(0xA9, "bitor", Avm2OperandShape.None);
            Define(0xAA, "bitxor", Avm2OperandShape.None);
            Define(0xAB, "equals", Avm2OperandShape.None);
            Define(0xAC, "strictequals", Avm2OperandShape.None);
            Define(0xAD, "lessthan", Avm2OperandShape.None);
            Define(0xAE, "lessequals", Avm2OperandShape.None);
            Define(0xAF, "greaterthan", Avm2OperandShape.None);
            Define(0xB0, "greaterequals", Avm2OperandShape.None);
            Define(0xB1, "instanceof", Avm2OperandShape.None);
            Define(0xB2, "istype", Avm2OperandShape.U30);
            Define(0xB3, "istypelate", Avm2OperandShape.None);
            Define(0xB4, "in", Avm2OperandShape.None);
            Define(0xC0, "increment_i", Avm2OperandShape.None);
            Define(0xC1, "decrement_i", Avm2OperandShape.None);
            Define(0xC2, "inclocal_i", Avm2OperandShape.U30);
            Define(0xC3, "declocal_i", Avm2OperandShape.U30);
            Define(0xC4, "negate_i", Avm2OperandShape.None);
            Define(0xC5, "add_i", Avm2OperandShape.None);
            Define(0xC6, "subtract_i", Avm2OperandShape.None);
            Define(0xC7, "multiply_i", Avm2OperandShape.None);
            Define(0xD0, "getlocal0", Avm2OperandShape.None);
            Define(0xD1, "getlocal1", Avm2OperandShape.None);
            Define(0xD2, "getlocal2", Avm2OperandShape.None);
            Define(0xD3, "getlocal3", Avm2OperandShape.None);
            Define(0xD4, "setlocal0", Avm2OperandShape.None);
            Define(0xD5, "setlocal1", Avm2OperandShape.None);
            Define(0xD6, "setlocal2", Avm2OperandShape.None);
            Define(0xD7, "setlocal3", Avm2OperandShape.None);
            Define(0xEF, "debug", Avm2OperandShape.Debug);
            Define(0xF0, "debugline", Avm2OperandShape.U30);
            Define(0xF1, "debugfile", Avm2OperandShape.U30);
            Define(0xF2, "bkptline", Avm2OperandShape.U30);
            Define(0xF3, "timestamp", Avm2OperandShape.None);
        }

        private static void Define(byte opcode, string name, Avm2OperandShape shape)
        {
            Table[opcode] = new Avm2OpCodeInfo
            {
                Name = name,
                Shape = shape,
                Defined = true
            };
        }

        public static Avm2OpCodeInfo Get(byte opcode)
        {
            return Table[opcode];
        }

        public static bool IsDefined(byte opcode)
        {
            return Table[opcode].Defined;
        }

        public static string GetName(byte opcode)
        {
            Avm2OpCodeInfo info = Table[opcode];
            return info.Defined ? info.Name : "0x" + opcode.ToString("X2");
        }

        public static int DefinedCount
        {
            get
            {
                int count = 0;

                for (int i = 0; i < Table.Length; i++)
                {
                    if (Table[i].Defined)
                        count++;
                }

                return count;
            }
        }
    }
}
