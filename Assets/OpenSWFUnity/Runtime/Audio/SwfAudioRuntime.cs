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
    public sealed class SwfAudioRuntime : MonoBehaviour
    {
        private readonly Dictionary<ushort, AudioClip> clipsById = new Dictionary<ushort, AudioClip>();
        private readonly Dictionary<string, ushort> exportIds = new Dictionary<string, ushort>();
        private readonly List<string> temporaryFiles = new List<string>();
        private readonly List<ushort> pendingSoundIds = new List<ushort>();

        private AudioSource audioSource;
        private Coroutine loadingCoroutine;

        public bool IsLoading { get; private set; }

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();

            if (audioSource == null)
                audioSource = gameObject.AddComponent<AudioSource>();

            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.loop = false;
        }

        public void Initialize(SwfParser parser)
        {
            if (loadingCoroutine != null)
                StopCoroutine(loadingCoroutine);

            ClearLoadedAudio();
            exportIds.Clear();
            pendingSoundIds.Clear();

            if (parser == null)
                return;

            foreach (KeyValuePair<string, ushort> asset in parser.ExportedAssets)
            {
                exportIds[asset.Key] = asset.Value;
            }

            loadingCoroutine = StartCoroutine(LoadSounds(parser.Sounds));
        }

        public bool PlayExported(string exportName)
        {
            if (string.IsNullOrEmpty(exportName))
                return false;

            if (!exportIds.TryGetValue(exportName, out ushort soundId))
                return false;

            return PlaySound(soundId);
        }

        public bool PlaySound(ushort soundId)
        {
            if (soundId == 0)
                return false;

            if (!clipsById.TryGetValue(soundId, out AudioClip clip) || clip == null)
            {
                if (IsLoading)
                {
                    pendingSoundIds.Add(soundId);
                    return true;
                }

                return false;
            }

            audioSource.PlayOneShot(clip);
            return true;
        }

        public void StopSound(ushort soundId)
        {
            for (int i = pendingSoundIds.Count - 1; i >= 0; i--)
            {
                if (pendingSoundIds[i] == soundId)
                    pendingSoundIds.RemoveAt(i);
            }

            // Unity PlayOneShot does not expose per-voice stopping. Until the mixer-backed
            // voice table lands, stop the source so SWF SyncStop is still respected.
            if (audioSource != null)
                audioSource.Stop();
        }

        public void StopAll()
        {
            if (audioSource != null)
                audioSource.Stop();

            pendingSoundIds.Clear();
        }

        private IEnumerator LoadSounds(List<DefineSoundTag> sounds)
        {
            IsLoading = true;

            if (sounds != null)
            {
                for (int i = 0; i < sounds.Count; i++)
                {
                    DefineSoundTag sound = sounds[i];

                    if (sound == null || !sound.IsMp3 || sound.SoundData == null || sound.SoundData.Length == 0)
                        continue;

                    yield return LoadMp3(sound);
                }
            }

            IsLoading = false;
            loadingCoroutine = null;
        }

        private IEnumerator LoadMp3(DefineSoundTag sound)
        {
            string fileName = "openswf_sound_" + sound.SoundId + ".mp3";
            string filePath = Path.Combine(Application.temporaryCachePath, fileName);

            try
            {
                File.WriteAllBytes(filePath, sound.SoundData);
                temporaryFiles.Add(filePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Could not stage SWF MP3 sound " + sound.SoundId + ": " + e.Message);
                yield break;
            }

            string fileUri = new Uri(filePath).AbsoluteUri;

            using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.MPEG);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning(
                    "Could not decode SWF MP3 sound " + sound.SoundId + ": " + request.error
                );
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);

            if (clip != null)
            {
                clip.name = "SWF Sound " + sound.SoundId;
                clipsById[sound.SoundId] = clip;
                PlayPending(sound.SoundId, clip);
            }
        }

        private void PlayPending(ushort soundId, AudioClip clip)
        {
            for (int i = pendingSoundIds.Count - 1; i >= 0; i--)
            {
                if (pendingSoundIds[i] != soundId)
                    continue;

                pendingSoundIds.RemoveAt(i);

                if (audioSource != null && clip != null)
                    audioSource.PlayOneShot(clip);
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

        private void OnDestroy()
        {
            ClearLoadedAudio();
        }
    }
}
