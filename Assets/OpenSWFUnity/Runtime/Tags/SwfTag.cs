namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfTag
    {
        public int Code;
        public int Length;
        public int DataStart;
        public string Name;

        public override string ToString()
        {
            return $"Tag: {Name} Code: {Code} Length: {Length} DataStart: {DataStart}";
        }
    }
}