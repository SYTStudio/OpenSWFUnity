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
        private readonly List<ushort> pendingSoundIds = new List<ushort>();
        private readonly HashSet<int> reportedFormats = new HashSet<int>();

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
            if (loadingCoroutine != null)
                StopCoroutine(loadingCoroutine);

            EnsureVoices();
            StopAll();
            ClearLoadedAudio();
            exportIds.Clear();
            pendingSoundIds.Clear();
            reportedFormats.Clear();
            parser = swfParser;

            if (swfParser == null)
                return;

            foreach (KeyValuePair<string, ushort> asset in swfParser.ExportedAssets)
                exportIds[asset.Key] = asset.Value;

            ReportUnsupportedFormats(swfParser);
            DecodeInternalSounds(swfParser);
            BuildStream(swfParser);

            // Only the MP3 assets need Unity's decoder, and only those go through a
            // coroutine; everything else is already resident by this point.
            loadingCoroutine = StartCoroutine(LoadUnityDecodedSounds(swfParser.Sounds));
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
                // Still decoding: remember the request so it fires once ready.
                if (IsLoading)
                {
                    pendingSoundIds.Add(soundId);
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
            voice.Source.loop = info != null && info.EffectiveLoopCount > 1;
            voice.Source.volume = ResolveVolume(info);
            voice.Source.panStereo = ResolvePan(info);
            voice.Source.time = ResolveStartTime(info, clip);
            voice.Source.Play();
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
            for (int i = pendingSoundIds.Count - 1; i >= 0; i--)
            {
                if (pendingSoundIds[i] == soundId)
                    pendingSoundIds.RemoveAt(i);
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
            pendingSoundIds.Clear();
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

        private IEnumerator LoadUnityDecodedSounds(List<DefineSoundTag> sounds)
        {
            IsLoading = true;

            if (sounds != null)
            {
                for (int i = 0; i < sounds.Count; i++)
                {
                    DefineSoundTag sound = sounds[i];

                    if (sound == null || !SwfSoundFormats.IsDecodedByUnity(sound.SoundFormat) ||
                        sound.SoundData == null || sound.SoundData.Length == 0)
                    {
                        continue;
                    }

                    yield return LoadClipFromBytes(
                        sound.SoundData,
                        "openswf_sound_" + sound.SoundId + ".mp3",
                        clip =>
                        {
                            clip.name = "SWF Sound " + sound.SoundId;
                            clipsById[sound.SoundId] = clip;
                            PlayPending(sound.SoundId);
                        });
                }
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
            for (int i = pendingSoundIds.Count - 1; i >= 0; i--)
            {
                if (pendingSoundIds[i] != soundId)
                    continue;

                pendingSoundIds.RemoveAt(i);
                PlaySound(soundId);
            }
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
                   " pending=" + pendingSoundIds.Count +
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
