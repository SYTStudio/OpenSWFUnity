namespace OpenSWFUnity.Runtime.Parser
{
    public struct SwfRect
    {
        public int XMin;
        public int XMax;
        public int YMin;
        public int YMax;

        public int WidthTwips => XMax - XMin;
        public int HeightTwips => YMax - YMin;

        public float XMinPixels => XMin / 20f;
        public float XMaxPixels => XMax / 20f;
        public float YMinPixels => YMin / 20f;
        public float YMaxPixels => YMax / 20f;

        public float WidthPixels => WidthTwips / 20f;
        public float HeightPixels => HeightTwips / 20f;

        public override string ToString()
        {
            return $"Rect Twips[XMin={XMin}, XMax={XMax}, YMin={YMin}, YMax={YMax}] " +
                   $"Pixels[W={WidthPixels}, H={HeightPixels}]";
        }
    }
}