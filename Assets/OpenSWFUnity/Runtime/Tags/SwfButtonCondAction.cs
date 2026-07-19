namespace OpenSWFUnity.Runtime.Tags
{
    public class SwfButtonCondAction
    {
        public ushort Conditions;
        public byte[] ActionBytes;

        public bool TriggersOnIdleToOverUp => (Conditions & 0x0001) != 0;
        public bool TriggersOnOverUpToIdle => (Conditions & 0x0002) != 0;
        public bool TriggersOnPress => (Conditions & 0x0004) != 0;
        public bool TriggersOnRelease => (Conditions & 0x0008) != 0;
        public bool TriggersOnDragOut => (Conditions & 0x0010) != 0;
        public bool TriggersOnDragOver => (Conditions & 0x0020) != 0;
        public bool TriggersOnReleaseOutside => (Conditions & 0x0040) != 0;
        public bool TriggersOnIdleToOverDown => (Conditions & 0x0080) != 0;
        public bool TriggersOnOverDownToIdle => (Conditions & 0x0100) != 0;
        public int KeyPressCode => (Conditions >> 9) & 0x7F;

        public bool MatchesTransition(ushort transitionFlag)
        {
            return (Conditions & transitionFlag) != 0;
        }
    }
}
