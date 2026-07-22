namespace OpenSWFUnity.Runtime.AVM2.Abc
{
    // Ceilings applied while parsing ABC data.
    //
    // Every count in an ABC file is a u30 read straight from the stream, so a
    // truncated or hostile file can legitimately encode "one billion methods".
    // Sizing a collection from such a count allocates gigabytes before any data is
    // read, which takes down the player rather than the script. The parser instead
    // rejects any count that could not possibly be backed by the remaining bytes,
    // and clamps the rest against these limits.
    public static class AbcLimits
    {
        // Smallest number of bytes any pool entry can occupy. Used to reject counts
        // that the remaining stream could never satisfy, before allocating for them.
        public const int MinimumBytesPerEntry = 1;

        public const int MaxConstantPoolEntries = 512 * 1024;
        public const int MaxMethods = 256 * 1024;
        public const int MaxClasses = 64 * 1024;
        public const int MaxScripts = 64 * 1024;
        public const int MaxTraitsPerOwner = 64 * 1024;
        public const int MaxParameters = 1024;
        public const int MaxInterfaces = 4096;
        public const int MaxExceptionHandlers = 64 * 1024;
        public const int MaxMethodCodeLength = 8 * 1024 * 1024;
        public const int MaxStringLength = 4 * 1024 * 1024;
        public const int MaxNamespaceSetEntries = 4096;
        public const int MaxMultinameTypeParameters = 256;

        // Guards the runtime rather than the parser: how deep AS3 calls may nest and
        // how many instructions a single entry point may retire once execution
        // exists. Kept here so every AVM2 ceiling is stated in one place.
        public const int MaxCallDepth = 256;
        public const int MaxInstructionsPerEntry = 5000000;
    }
}
