using System.Collections.Generic;

namespace OpenSWFUnity.Runtime.Tags
{
    // The SOUNDINFO record attached to a StartSound. It controls how an already
    // defined sound is played: where to start and stop within it, how many times to
    // repeat, and an optional volume envelope.
    public sealed class SwfSoundInfo
    {
        public bool SyncStop;

        // "No multiple": if the sound is already playing, do not start it again.
        public bool SyncNoMultiple;

        public bool HasEnvelope;
        public bool HasLoops;
        public bool HasOutPoint;
        public bool HasInPoint;

        public uint InPoint;
        public uint OutPoint;
        public ushort LoopCount;

        // Envelope points are (sample position, left level, right level) with levels
        // in 0..32768.
        public List<SwfSoundEnvelopePoint> Envelope;

        public int EffectiveLoopCount => HasLoops && LoopCount > 1 ? LoopCount : 1;

        public override string ToString()
        {
            return "SoundInfo stop=" + SyncStop + " noMultiple=" + SyncNoMultiple +
                   " loops=" + EffectiveLoopCount +
                   (HasInPoint ? " in=" + InPoint : string.Empty) +
                   (HasOutPoint ? " out=" + OutPoint : string.Empty) +
                   (HasEnvelope ? " envelope=" + (Envelope?.Count ?? 0) : string.Empty);
        }
    }

    public struct SwfSoundEnvelopePoint
    {
        public uint Position44;
        public ushort LeftLevel;
        public ushort RightLevel;
    }

    // A StartSound / StartSound2 occurrence on a timeline frame.
    public sealed class SwfStartSound
    {
        public ushort SoundId;
        public string SoundClassName;
        public SwfSoundInfo Info;
    }

    // SoundStreamHead / SoundStreamHead2: declares the format of the audio that the
    // following SoundStreamBlock tags carry, and how many samples belong to each
    // timeline frame. That per-frame count is what ties the stream to the playhead.
    public sealed class SwfSoundStreamHead
    {
        public int PlaybackSampleRate;
        public bool PlaybackIs16Bit;
        public bool PlaybackIsStereo;

        public int StreamFormat;
        public int StreamSampleRate;
        public bool StreamIs16Bit;
        public bool StreamIsStereo;
        public ushort SamplesPerFrame;
        public short LatencySeek;

        public override string ToString()
        {
            return "SoundStreamHead format=" + StreamFormat +
                   " rate=" + StreamSampleRate +
                   " stereo=" + StreamIsStereo +
                   " samplesPerFrame=" + SamplesPerFrame;
        }
    }

    // One frame's worth of streaming audio.
    public sealed class SwfSoundStreamBlock
    {
        public int FrameIndex;
        public byte[] Data;

        // MP3 blocks carry their own sample count and seek offset ahead of the frames.
        public ushort SampleCount;
        public short SeekSamples;
    }
}
