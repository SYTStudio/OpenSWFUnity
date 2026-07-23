using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using OpenSWFUnity.Runtime.Parser;
using OpenSWFUnity.Runtime.Tags;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenSWFUnity.Runtime.Audio
{
    // SWF audio playback.
    //
    // Event sounds are decoded once into an AudioClip and then played through a pool
    // of AudioSources, one per voice, so a sound can be stopped, looped, panned and
    // levelled individually. Streaming audio is assembled from the per-frame
    // SoundStreamBlock tags into a single clip whose playback position is slaved to
    // the timeline, which is what keeps it in sync across seeks and pauses.
    public sealed class SwfAudioRuntime : MonoBehaviour
    {
        private const int VoiceCount = 16;

        private readonly Dictionary<ushort, AudioClip> clipsById = new Dictionary<ushort, AudioClip>();
        private readonly Dictionary<string, ushort> exportIds = new Dictionary<string, ushort>();
        private readonly List<string> temporaryFiles = new List<string>();
        private readonly List<PendingSoundRequest> pendingSounds =
            new List<PendingSoundRequest>();
        private readonly HashSet<int> reportedFormats = new HashSet<int>();
        private readonly Dictionary<ushort, DefineSoundTag> soundsById =
            new Dictionary<ushort, DefineSoundTag>();
        private readonly Queue<DefineSoundTag> unityDecodeQueue =
            new Queue<DefineSoundTag>();
        private readonly HashSet<ushort> queuedUnitySoundIds = new HashSet<ushort>();
        private readonly Dictionary<ushort, SpriteStreamState> spriteStreams =
            new Dictionary<ushort, SpriteStreamState>();
        private readonly HashSet<ushort> visibleSpriteStreams = new HashSet<ushort>();

        private Voice[] voices;
        private AudioSource streamSource;
        private AudioClip streamClip;
        private Coroutine loadingCoroutine;
        private SwfParser parser;

        // Timeline frame the stream starts on, and how long one frame of it lasts.
        private float streamSecondsPerFrame;
        private bool streamActive;

        public bool IsLoading { get; private set; }
        public int DecodedClipCount => clipsById.Count;
        public int ActiveVoiceCount { get; private set; }
        public bool HasStream => streamClip != null;

        // One playing sound. The source is reused for the life of the runtime; only
        // its clip and settings change.
        private sealed class Voice
        {
            public AudioSource Source;
            public ushort SoundId;
            public bool InUse;
        }

        private sealed class PendingSoundRequest
        {
            public ushort SoundId;
            public SwfSoundInfo Info;
        }

        private sealed class SpriteStreamState
        {
            public ushort SpriteId;
            public SwfSoundStreamHead Head;
            public List<SwfSoundStreamBlock> Blocks;
            public AudioSource Source;
            public AudioClip Clip;
            public bool Loading;
            public int FirstFrame;
            public float SecondsPerFrame;
        }

        private void Awake()
        {
            EnsureVoices();
        }

        private void EnsureVoices()
        {
            if (voices != null)
                return;

            voices = new Voice[VoiceCount];

            for (int i = 0; i < VoiceCount; i++)
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.loop = false;
                voices[i] = new Voice { Source = source };
            }

            streamSource = gameObject.AddComponent<AudioSource>();
            streamSource.playOnAwake = false;
            streamSource.spatialBlend = 0f;
            streamSource.loop = false;
        }

        public void Initialize(SwfParser swfParser)
        {
            StopAllCoroutines();

            EnsureVoices();
            StopAll();
            ClearLoadedAudio();
            exportIds.Clear();
            pendingSounds.Clear();
            reportedFormats.Clear();
            soundsById.Clear();
            unityDecodeQueue.Clear();
            queuedUnitySoundIds.Clear();
            spriteStreams.Clear();
            visibleSpriteStreams.Clear();
            IsLoading = false;
            loadingCoroutine = null;
            parser = swfParser;

            if (swfParser == null)
                return;

            foreach (KeyValuePair<string, ushort> asset in swfParser.ExportedAssets)
                exportIds[asset.Key] = asset.Value;

            for (int i = 0; i < swfParser.Sounds.Count; i++)
            {
                DefineSoundTag sound = swfParser.Sounds[i];

                if (sound != null)
                    soundsById[sound.SoundId] = sound;
            }

            ReportUnsupportedFormats(swfParser);
            DecodeInternalSounds(swfParser);
            BuildStream(swfParser);

            // MP3 event sounds are decoded on first use. A large game can carry
            // hundreds of clips it may never play in this session; decoding every
            // one during startup caused both a long load and repeated frame spikes.
        }

        private void ReportUnsupportedFormats(SwfParser swfParser)
        {
            foreach (int format in swfParser.UnsupportedSoundFormats)
            {
                if (!reportedFormats.Add(format))
                    continue;

                Debug.LogWarning(
                    "SWF audio: " + SwfSoundFormats.Describe(format) +
                    " is not supported by this build. Sounds in that format will not play; " +
                    "no silent substitute is inserted for them."
                );
            }
        }

        // PCM and ADPCM decode synchronously in managed code, so their clips exist
        // before the first frame is drawn.
        private void DecodeInternalSounds(SwfParser swfParser)
        {
            for (int i = 0; i < swfParser.Sounds.Count; i++)
            {
                DefineSoundTag sound = swfParser.Sounds[i];

                if (sound == null || !SwfSoundFormats.IsDecodedInternally(sound.SoundFormat))
                    continue;

                SwfDecodedSound decoded = SwfSoundDecoder.Decode(sound);

                if (!decoded.HasSamples)
                {
                    Debug.LogWarning(
                        "SWF audio: sound " + sound.SoundId + " (" +
                        SwfSoundFormats.Describe(sound.SoundFormat) + ") failed to decode: " +
                        (decoded.Failure ?? "no samples produced"));
                    continue;
                }

                clipsById[sound.SoundId] = CreateClip(
                    "SWF Sound " + sound.SoundId, decoded);
            }
        }

        private static AudioClip CreateClip(string name, SwfDecodedSound decoded)
        {
            AudioClip clip = AudioClip.Create(
                name,
                decoded.FrameCount,
                decoded.Channels,
                decoded.SampleRate,
                false);

            clip.SetData(decoded.Samples, 0);
            return clip;
        }

        // ---- streaming --------------------------------------------------------

        // Concatenates the per-frame blocks into one clip. Holding the whole stream
        // lets playback be positioned directly from the timeline frame, which is what
        // makes seeks and frame jumps land in the right place.
        private void BuildStream(SwfParser swfParser)
        {
            SwfSoundStreamHead head = swfParser.SoundStreamHead;

            if (head == null || swfParser.SoundStreamBlocks.Count == 0)
                return;

            if (!SwfSoundFormats.IsDecodedInternally(head.StreamFormat))
            {
                // MP3 streams need Unity's decoder over a concatenated payload, which
                // is handled with the event sounds; anything else has no decoder.
                if (!SwfSoundFormats.IsDecodedByUnity(head.StreamFormat))
                {
                    Debug.LogWarning(
                        "SWF audio: streaming sound is " +
                        SwfSoundFormats.Describe(head.StreamFormat) +
                        ", which this build cannot decode. The timeline will play silently.");
                    return;
                }

                StartCoroutine(BuildMp3Stream(swfParser, head));
                return;
            }

            int channels = head.StreamIsStereo ? 2 : 1;
            List<float> samples = new List<float>(
                swfParser.SoundStreamBlocks.Count * Math.Max(1, (int)head.SamplesPerFrame) * channels);

            for (int i = 0; i < swfParser.SoundStreamBlocks.Count; i++)
            {
                SwfSoundStreamBlock block = swfParser.SoundStreamBlocks[i];
                SwfDecodedSound decoded = SwfSoundDecoder.Decode(
                    block.Data,
                    head.StreamFormat,
                    head.StreamSampleRate,
                    head.StreamIs16Bit,
                    head.StreamIsStereo);

                if (decoded.HasSamples)
                    samples.AddRange(decoded.Samples);
            }

            if (samples.Count == 0)
            {
                Debug.LogWarning("SWF audio: streaming blocks produced no samples.");
                return;
            }

            SwfDecodedSound stream = new SwfDecodedSound
            {
                Samples = samples.ToArray(),
                Channels = channels,
                SampleRate = head.StreamSampleRate
            };

            streamClip = CreateClip("SWF Stream", stream);
            streamSource.clip = streamClip;
            streamSecondsPerFrame = head.SamplesPerFrame > 0 && head.StreamSampleRate > 0
                ? (float)head.SamplesPerFrame / head.StreamSampleRate
                : 0f;
        }

        private IEnumerator BuildMp3Stream(SwfParser swfParser, SwfSoundStreamHead head)
        {
            using MemoryStream buffer = new MemoryStream();

            for (int i = 0; i < swfParser.SoundStreamBlocks.Count; i++)
            {
                byte[] block = swfParser.SoundStreamBlocks[i].Data;

                if (block != null)
                    buffer.Write(block, 0, block.Length);
            }

            yield return LoadClipFromBytes(buffer.ToArray(), "swf_stream.mp3", clip =>
            {
                streamClip = clip;
                streamSource.clip = clip;
                streamSecondsPerFrame = head.SamplesPerFrame > 0 && head.StreamSampleRate > 0
                    ? (float)head.SamplesPerFrame / head.StreamSampleRate
                    : 0f;
            });
        }

        // Positions the stream to match the playhead. Called when the timeline jumps
        // rather than every frame, so ordinary playback is left to run freely.
        public void SyncStreamToFrame(int frameIndex, bool playing)
        {
            if (streamClip == null || streamSource == null)
                return;

            if (!playing)
            {
                if (streamSource.isPlaying)
                    streamSource.Pause();

                streamActive = false;
                return;
            }

            float target = streamSecondsPerFrame > 0f
                ? frameIndex * streamSecondsPerFrame
                : 0f;

            if (target >= streamClip.length)
            {
                streamSource.Stop();
                streamActive = false;
                return;
            }

            // Re-seeking every frame would stutter; only correct when the drift is
            // larger than a frame's worth of audio.
            float drift = Mathf.Abs(streamSource.time - target);

            if (!streamSource.isPlaying)
            {
                streamSource.time = target;
                streamSource.Play();
                streamActive = true;
                return;
            }

            if (streamSecondsPerFrame > 0f && drift > streamSecondsPerFrame * 2f)
                streamSource.time = target;
        }

        public void PauseStream()
        {
            if (streamSource != null && streamSource.isPlaying)
                streamSource.Pause();

            streamActive = false;
        }

        public void StopStream()
        {
            if (streamSource != null)
                streamSource.Stop();

            streamActive = false;
        }

        public bool IsStreamPlaying => streamActive && streamSource != null && streamSource.isPlaying;

        // ---- nested sprite streams ------------------------------------------

        public void BeginSpriteStreamFrame()
        {
            visibleSpriteStreams.Clear();
        }

        public void SyncSpriteStream(DefineSpriteTag sprite, int frameIndex, bool playing)
        {
            if (sprite?.SoundStreamHead == null ||
                sprite.SoundStreamBlocks == null ||
                sprite.SoundStreamBlocks.Count == 0)
            {
                return;
            }

            ushort spriteId = sprite.SpriteId;
            visibleSpriteStreams.Add(spriteId);

            if (!spriteStreams.TryGetValue(spriteId, out SpriteStreamState state))
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 0f;
                source.loop = false;
                state = new SpriteStreamState
                {
                    SpriteId = spriteId,
                    Head = sprite.SoundStreamHead,
                    Blocks = sprite.SoundStreamBlocks,
                    Source = source,
                    FirstFrame = sprite.SoundStreamBlocks[0].FrameIndex,
                    SecondsPerFrame = sprite.SoundStreamHead.SamplesPerFrame > 0 &&
                        sprite.SoundStreamHead.StreamSampleRate > 0
                            ? (float)sprite.SoundStreamHead.SamplesPerFrame /
                              sprite.SoundStreamHead.StreamSampleRate
                            : 0f
                };
                spriteStreams[spriteId] = state;
            }

            if (state.Clip == null)
            {
                if (!state.Loading)
                {
                    state.Loading = true;
                    StartCoroutine(BuildSpriteStream(state));
                }

                return;
            }

            SyncSpriteStreamSource(state, frameIndex, playing);
        }

        public void EndSpriteStreamFrame()
        {
            foreach (KeyValuePair<ushort, SpriteStreamState> pair in spriteStreams)
            {
                if (visibleSpriteStreams.Contains(pair.Key))
                    continue;

                AudioSource source = pair.Value.Source;

                if (source != null && source.isPlaying)
                    source.Stop();
            }
        }

        private IEnumerator BuildSpriteStream(SpriteStreamState state)
        {
            SwfSoundStreamHead head = state.Head;

            if (SwfSoundFormats.IsDecodedByUnity(head.StreamFormat))
            {
                using MemoryStream buffer = new MemoryStream();

                for (int i = 0; i < state.Blocks.Count; i++)
                {
                    byte[] block = state.Blocks[i].Data;

                    if (block != null)
                        buffer.Write(block, 0, block.Length);
                }

                yield return LoadClipFromBytes(
                    buffer.ToArray(),
                    "swf_sprite_stream_" + state.SpriteId + ".mp3",
                    clip =>
                    {
                        state.Clip = clip;
                        state.Source.clip = clip;
                    });
            }
            else if (SwfSoundFormats.IsDecodedInternally(head.StreamFormat))
            {
                int channels = head.StreamIsStereo ? 2 : 1;
                List<float> samples = new List<float>();

                for (int i = 0; i < state.Blocks.Count; i++)
                {
                    SwfDecodedSound decoded = SwfSoundDecoder.Decode(
                        state.Blocks[i].Data,
                        head.StreamFormat,
                        head.StreamSampleRate,
                        head.StreamIs16Bit,
                        head.StreamIsStereo);

                    if (decoded.HasSamples)
                        samples.AddRange(decoded.Samples);

                    if ((i & 7) == 7)
                        yield return null;
                }

                if (samples.Count > 0)
                {
                    state.Clip = CreateClip(
                        "SWF Sprite Stream " + state.SpriteId,
                        new SwfDecodedSound
                        {
                            Samples = samples.ToArray(),
                            Channels = channels,
                            SampleRate = head.StreamSampleRate
                        });
                    state.Source.clip = state.Clip;
                }
            }

            state.Loading = false;
        }

        private static void SyncSpriteStreamSource(
            SpriteStreamState state,
            int frameIndex,
            bool playing)
        {
            AudioSource source = state.Source;

            if (source == null || state.Clip == null)
                return;

            if (!playing)
            {
                if (source.isPlaying)
                    source.Pause();

                return;
            }

            int relativeFrame = frameIndex - state.FirstFrame;

            if (relativeFrame < 0)
            {
                if (source.isPlaying)
                    source.Stop();

                return;
            }

            float target = state.SecondsPerFrame > 0f
                ? relativeFrame * state.SecondsPerFrame
                : 0f;

            if (target >= state.Clip.length)
            {
                if (source.isPlaying)
                    source.Stop();

                return;
            }

            if (!source.isPlaying)
            {
                source.time = target;
                source.Play();
                return;
            }

            if (state.SecondsPerFrame > 0f &&
                Mathf.Abs(source.time - target) > state.SecondsPerFrame * 2f)
            {
                source.time = target;
            }
        }

        // ---- event sounds -----------------------------------------------------

        public bool PlayExported(string exportName)
        {
            if (string.IsNullOrEmpty(exportName))
                return false;

            return exportIds.TryGetValue(exportName, out ushort soundId) && PlaySound(soundId);
        }

        public bool PlaySound(ushort soundId)
        {
            return PlaySound(soundId, null);
        }

        // Honours the SOUNDINFO attached to the StartSound: stop, no-multiple, loop
        // count and envelope-derived level.
        public bool PlaySound(ushort soundId, SwfSoundInfo info)
        {
            if (soundId == 0)
                return false;

            if (info != null && info.SyncStop)
            {
                StopSound(soundId);
                return true;
            }

            if (info != null && info.SyncNoMultiple && IsPlaying(soundId))
                return true;

            if (!clipsById.TryGetValue(soundId, out AudioClip clip) || clip == null)
            {
                if (soundsById.TryGetValue(soundId, out DefineSoundTag sound) &&
                    SwfSoundFormats.IsDecodedByUnity(sound.SoundFormat))
                {
                    if (info == null || !info.SyncNoMultiple || !HasPendingSound(soundId))
                    {
                        pendingSounds.Add(new PendingSoundRequest
                        {
                            SoundId = soundId,
                            Info = info
                        });
                    }

                    QueueUnityDecodedSound(sound);
                    return true;
                }

                return false;
            }

            Voice voice = AcquireVoice();

            if (voice == null)
                return false;

            voice.SoundId = soundId;
            voice.InUse = true;
            voice.Source.clip = clip;
            int loopCount = info != null ? info.EffectiveLoopCount : 1;
            voice.Source.loop = loopCount > 1;
            voice.Source.volume = ResolveVolume(info);
            voice.Source.panStereo = ResolvePan(info);
            voice.Source.time = ResolveStartTime(info, clip);
            float startTime = voice.Source.time;
            voice.Source.Play();

            if (loopCount > 1)
            {
                // AudioSource.loop by itself is infinite. SWF SOUNDINFO loops are a
                // finite count; scheduling the stop on Unity's DSP clock keeps it
                // exact even if vector rasterization stalls the main thread.
                double firstPlaySeconds = Mathf.Max(0.001f, clip.length - startTime);
                double totalSeconds = firstPlaySeconds +
                    Mathf.Max(0, loopCount - 1) * (double)clip.length;
                voice.Source.SetScheduledEndTime(AudioSettings.dspTime + totalSeconds);
            }

            return true;
        }

        // The envelope's first point sets the starting level; full per-sample envelope
        // shaping is not applied, and a multi-point envelope says so once.
        private float ResolveVolume(SwfSoundInfo info)
        {
            if (info?.Envelope == null || info.Envelope.Count == 0)
                return 1f;

            SwfSoundEnvelopePoint first = info.Envelope[0];
            float left = first.LeftLevel / 32768f;
            float right = first.RightLevel / 32768f;

            if (info.Envelope.Count > 1 && reportedFormats.Add(-1))
            {
                Debug.LogWarning(
                    "SWF audio: a sound uses a multi-point volume envelope. The initial " +
                    "level is applied, but the envelope is not animated over the sound.");
            }

            return Mathf.Clamp01(Mathf.Max(left, right));
        }

        private static float ResolvePan(SwfSoundInfo info)
        {
            if (info?.Envelope == null || info.Envelope.Count == 0)
                return 0f;

            SwfSoundEnvelopePoint first = info.Envelope[0];
            float left = first.LeftLevel;
            float right = first.RightLevel;
            float total = left + right;

            // Pan is the balance between the two channel levels, mapped to -1..1.
            return total <= 0f ? 0f : Mathf.Clamp((right - left) / total, -1f, 1f);
        }

        private static float ResolveStartTime(SwfSoundInfo info, AudioClip clip)
        {
            if (info == null || !info.HasInPoint || clip.frequency <= 0)
                return 0f;

            float seconds = (float)info.InPoint / clip.frequency;
            return seconds >= clip.length ? 0f : Mathf.Max(0f, seconds);
        }

        private Voice AcquireVoice()
        {
            EnsureVoices();

            for (int i = 0; i < voices.Length; i++)
            {
                Voice voice = voices[i];

                if (!voice.Source.isPlaying)
                    return voice;
            }

            // Every voice is busy: reuse the first, which is the oldest still playing.
            // Flash drops the excess too rather than growing without bound.
            return voices[0];
        }

        public bool IsPlaying(ushort soundId)
        {
            if (voices == null)
                return false;

            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].InUse && voices[i].SoundId == soundId && voices[i].Source.isPlaying)
                    return true;
            }

            return false;
        }

        // Stops only the voices carrying this sound, leaving everything else audible.
        public void StopSound(ushort soundId)
        {
            for (int i = pendingSounds.Count - 1; i >= 0; i--)
            {
                if (pendingSounds[i].SoundId == soundId)
                    pendingSounds.RemoveAt(i);
            }

            if (voices == null)
                return;

            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].SoundId != soundId || !voices[i].InUse)
                    continue;

                voices[i].Source.Stop();
                voices[i].InUse = false;
            }
        }

        public void StopAll()
        {
            if (voices != null)
            {
                for (int i = 0; i < voices.Length; i++)
                {
                    voices[i].Source.Stop();
                    voices[i].InUse = false;
                }
            }

            StopStream();

            foreach (SpriteStreamState state in spriteStreams.Values)
            {
                if (state.Source != null)
                    state.Source.Stop();
            }

            pendingSounds.Clear();
        }

        public void SetVolume(float volume)
        {
            float clamped = Mathf.Clamp01(volume);

            if (voices != null)
            {
                for (int i = 0; i < voices.Length; i++)
                    voices[i].Source.volume = clamped;
            }

            if (streamSource != null)
                streamSource.volume = clamped;
        }

        private void Update()
        {
            if (voices == null)
                return;

            // Retiring finished voices keeps IsPlaying honest without polling Unity
            // from the timeline path.
            int active = 0;

            for (int i = 0; i < voices.Length; i++)
            {
                if (voices[i].InUse && !voices[i].Source.isPlaying)
                    voices[i].InUse = false;

                if (voices[i].InUse)
                    active++;
            }

            ActiveVoiceCount = active;
        }

        // ---- Unity-decoded formats -------------------------------------------

        private void QueueUnityDecodedSound(DefineSoundTag sound)
        {
            if (sound == null || !queuedUnitySoundIds.Add(sound.SoundId))
                return;

            unityDecodeQueue.Enqueue(sound);
            IsLoading = true;

            if (loadingCoroutine == null)
                loadingCoroutine = StartCoroutine(LoadQueuedUnitySounds());
        }

        private IEnumerator LoadQueuedUnitySounds()
        {
            while (unityDecodeQueue.Count > 0)
            {
                DefineSoundTag sound = unityDecodeQueue.Dequeue();
                bool decoded = false;

                if (sound != null && sound.SoundData != null && sound.SoundData.Length > 0)
                {
                    yield return LoadClipFromBytes(
                        sound.SoundData,
                        "openswf_sound_" + sound.SoundId + ".mp3",
                        clip =>
                        {
                            decoded = true;
                            clip.name = "SWF Sound " + sound.SoundId;
                            clipsById[sound.SoundId] = clip;
                        });
                }

                if (sound != null)
                {
                    queuedUnitySoundIds.Remove(sound.SoundId);

                    if (decoded)
                    {
                        PlayPending(sound.SoundId);
                    }
                    else
                    {
                        for (int i = pendingSounds.Count - 1; i >= 0; i--)
                        {
                            if (pendingSounds[i].SoundId == sound.SoundId)
                                pendingSounds.RemoveAt(i);
                        }
                    }
                }

                // Keep multiple first-use decodes from beginning on the same frame.
                if (unityDecodeQueue.Count > 0)
                    yield return null;
            }

            IsLoading = false;
            loadingCoroutine = null;
        }

        // Unity's MP3 decoder only reads from a URL, so the payload is staged to the
        // temporary cache. The file is deleted as soon as the clip is resident.
        private IEnumerator LoadClipFromBytes(byte[] payload, string fileName, Action<AudioClip> onLoaded)
        {
            string filePath = Path.Combine(Application.temporaryCachePath, fileName);

            try
            {
                File.WriteAllBytes(filePath, payload);
                temporaryFiles.Add(filePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("SWF audio: could not stage " + fileName + ": " + e.Message);
                yield break;
            }

            using UnityWebRequest request =
                UnityWebRequestMultimedia.GetAudioClip(new Uri(filePath).AbsoluteUri, AudioType.MPEG);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("SWF audio: could not decode " + fileName + ": " + request.error);
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);

            if (clip != null)
                onLoaded(clip);
        }

        private void PlayPending(ushort soundId)
        {
            for (int i = 0; i < pendingSounds.Count;)
            {
                PendingSoundRequest pending = pendingSounds[i];

                if (pending.SoundId != soundId)
                {
                    i++;
                    continue;
                }

                pendingSounds.RemoveAt(i);
                PlaySound(soundId, pending.Info);
            }
        }

        private bool HasPendingSound(ushort soundId)
        {
            for (int i = 0; i < pendingSounds.Count; i++)
            {
                if (pendingSounds[i].SoundId == soundId)
                    return true;
            }

            return false;
        }

        private void ClearLoadedAudio()
        {
            foreach (AudioClip clip in clipsById.Values)
            {
                if (clip != null)
                    Destroy(clip);
            }

            clipsById.Clear();

            if (streamClip != null)
            {
                Destroy(streamClip);
                streamClip = null;
            }

            foreach (SpriteStreamState state in spriteStreams.Values)
            {
                if (state.Clip != null)
                    Destroy(state.Clip);

                if (state.Source != null)
                    Destroy(state.Source);
            }

            for (int i = 0; i < temporaryFiles.Count; i++)
            {
                try
                {
                    if (File.Exists(temporaryFiles[i]))
                        File.Delete(temporaryFiles[i]);
                }
                catch
                {
                    // Temporary cache cleanup is best effort only.
                }
            }

            temporaryFiles.Clear();
        }

        public string DescribeDiagnostics()
        {
            return "SWF audio: decodedClips=" + clipsById.Count +
                   " activeVoices=" + ActiveVoiceCount +
                   " stream=" + (streamClip != null ? streamClip.length.ToString("0.00") + "s" : "none") +
                   " pending=" + pendingSounds.Count +
                   (parser != null && parser.UnsupportedSoundFormats.Count > 0
                       ? " unsupportedFormats=" + parser.UnsupportedSoundFormats.Count
                       : string.Empty);
        }

        private void OnDestroy()
        {
            ClearLoadedAudio();
        }
    }
}
