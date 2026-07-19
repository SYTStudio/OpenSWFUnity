using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    public class DefineButton2Tag
    {
        public ushort ButtonId;
        public bool TrackAsMenu;
        public List<SwfButtonRecord> Records = new List<SwfButtonRecord>();
        public List<SwfButtonCondAction> Actions = new List<SwfButtonCondAction>();

        public override string ToString()
        {
            return
                "DefineButton2 ButtonId=" + ButtonId +
                " TrackAsMenu=" + TrackAsMenu +
                " Records=" + Records.Count +
                " Actions=" + Actions.Count;
        }
    }
}
