namespace OpenSWFUnity.Runtime.Parser
{
    public class SwfColorTransform
    {
        public bool HasAddTerms;
        public bool HasMultTerms;

        public int RedMult = 256;
        public int GreenMult = 256;
        public int BlueMult = 256;
        public int AlphaMult = 256;

        public int RedAdd;
        public int GreenAdd;
        public int BlueAdd;
        public int AlphaAdd;

        public static SwfColorTransform Identity
        {
            get
            {
                return new SwfColorTransform();
            }
        }

        public float AlphaMultiplier01
        {
            get
            {
                return AlphaMult / 256f;
            }
        }

        public override string ToString()
        {
            return
                "ColorTransform " +
                "MultRGBA=(" + RedMult + "," + GreenMult + "," + BlueMult + "," + AlphaMult + ") " +
                "AddRGBA=(" + RedAdd + "," + GreenAdd + "," + BlueAdd + "," + AlphaAdd + ")";
        }
    }
}