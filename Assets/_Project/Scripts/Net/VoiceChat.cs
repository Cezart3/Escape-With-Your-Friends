using System;
using System.IO;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using Steamworks;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Positional voice chat. Steam captures and compresses, the server decides who is close enough
    /// to hear it, and Unity plays it out of the speaker's body so distance falloff is free.
    ///
    /// For this genre voice is not a feature, it is the delivery mechanism: almost every laugh in the
    /// game is somebody reacting out loud to a ragdoll. That is also why it is proximity voice rather
    /// than a party channel — hearing a friend get quieter as they are dragged away is the joke.
    ///
    /// The path, every capture interval:
    ///
    ///   owner:    SteamUser.ReadVoiceDataBytes -> ServerRelay (unreliable)
    ///   server:   distance test per listener   -> TargetPlay (unreliable)
    ///   listener: DecompressVoice              -> ring buffer -> streaming AudioClip
    ///
    /// **The server does the range test, not the listener.** Sending everyone every frame and letting
    /// clients turn the volume down would work and would be less code, but it also ships every word
    /// anyone says to every machine in the lobby: a bandwidth bill that grows with the square of the
    /// player count, and a free wallhack for anyone willing to read their own packets.
    ///
    /// **Unreliable, always.** A voice frame that arrives late is worse than one that never arrives.
    /// Reliable delivery would stall the stream behind a retransmit and then dump the backlog at once;
    /// dropped frames are a click, stalled frames are a robot.
    ///
    /// **A dead player speaks from their corpse.** The ghost (#26) is a purely local object — it is
    /// never spawned, so the server does not know where it is and cannot range-test against it.
    /// Replicating a ghost position just for voice would be the only reason that object exists on the
    /// wire, and the corpse is the better rule anyway: death costs you the room. You are heard where
    /// your body is, muffled, and floating off to haunt someone across the map means nobody hears you.
    ///
    /// **Open mic, no push-to-talk.** Push-to-talk protects against a reaction reaching the group
    /// late, which is precisely the thing this game is made of. A mute toggle belongs to the settings
    /// menu (#84) and is deliberately not bound here.
    ///
    /// Steam is optional everywhere else in this project and it is optional here: with no Steam there
    /// is no capture and no relay, and nothing else changes. See <see cref="SteamRuntime"/>.
    /// </summary>
    public class VoiceChat : NetworkBehaviour
    {
        /// <summary>Payload encodings. The tag is the first byte of every frame.</summary>
        enum Codec : byte
        {
            /// <summary>Steam compressed voice. Only decodable by a peer with Steam running.</summary>
            Steam = 0,

            /// <summary>Raw 16-bit PCM. Only ever produced by <c>-voiceTest</c>.</summary>
            RawPcm = 1
        }

        [Header("Range")]
        [Tooltip("Past this distance the server stops sending this speaker to a listener. Also the "
                 + "AudioSource max distance, so the fade reaches silence exactly here.")]
        [SerializeField] float _maxRange = 25f;

        [Tooltip("Inside this distance the voice plays at full volume.")]
        [SerializeField] float _fullVolumeRange = 3f;

        [Header("Capture")]
        [Tooltip("Seconds between reads of the Steam mic buffer. Steam buffers between calls, so this "
                 + "trades packet count against mouth-to-ear latency.")]
        [SerializeField] float _captureInterval = 0.1f;

        [Tooltip("Frames bigger than this are dropped rather than split: an unreliable packet has to "
                 + "fit one MTU, and Steam voice at this interval is an order of magnitude smaller.")]
        [SerializeField] int _maxFrameBytes = 900;

        [Header("Muffling")]
        [Tooltip("Volume and low-pass cutoff while downed: face down on the floor, still audible.")]
        [SerializeField] float _downedVolume = 0.75f;

        [SerializeField] float _downedCutoff = 4000f;

        [Tooltip("Volume and low-pass cutoff while dead. A corpse does not enunciate.")]
        [SerializeField] float _deadVolume = 0.5f;

        [SerializeField] float _deadCutoff = 700f;

        /// <summary>Sample rate used when Steam is not up to be asked. Steam's optimal is 24kHz.</summary>
        const int FallbackSampleRate = 24000;

        /// <summary>Ring buffer length. A listener two seconds behind is not going to catch up.</summary>
        const float BufferSeconds = 2f;

        const float OpenCutoff = 22000f;

        Health _health;

        // Capture. Owner only.
        bool _capturing;
        float _nextCapture;
        bool _warnedOversize;

        // Playback. Every peer except the speaker's own.
        AudioSource _source;
        AudioLowPassFilter _lowPass;
        int _sampleRate = FallbackSampleRate;
        readonly object _ringLock = new();
        float[] _ring;
        int _ringWrite;
        int _ringRead;
        int _ringCount;
        MemoryStream _decode;

        // -voiceTest. See Report.
        bool _testing;
        float _testUntil = -1f;
        float _testPhase;
        float _nextReport;
        int _sentFrames;
        int _sentBytes;
        int _heardFrames;
        int _heardSamples;
        int _relayedFrames;
        int _relaySkipped;

        void Awake()
        {
            _health = GetComponent<Health>();
            _maxRange = CommandLine.GetFloat("-voiceRange", _maxRange);
            _testing = CommandLine.GetFloat("-voiceTest", -1f) > 0f;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (IsOwner)
            {
                StartCapture();
                if (_testing) _testUntil = Time.time + CommandLine.GetFloat("-voiceTest", 0f);
            }
            else
            {
                BuildPlayback();
            }

            _nextReport = Time.time + 2f;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            StopCapture();

            if (_source != null) Destroy(_source.gameObject);
            _source = null;
            _lowPass = null;
        }

        void Update()
        {
            if (IsOwner) Capture();
            if (_source != null) UpdateMuffling();
            if (_testing) Report();
        }

        // ------------------------------------------------------------------ capture, on the owner

        void StartCapture()
        {
            if (!SteamRuntime.Available) return;

            SteamUser.VoiceRecord = true;
            _capturing = true;
            _sampleRate = (int)SteamUser.OptimalSampleRate;

            Debug.Log($"[VoiceChat] owner {OwnerId} recording at {_sampleRate}Hz.");
        }

        void StopCapture()
        {
            if (!_capturing) return;

            _capturing = false;
            if (SteamRuntime.Available) SteamUser.VoiceRecord = false;
        }

        void Capture()
        {
            if (Time.time < _nextCapture) return;
            _nextCapture = Time.time + _captureInterval;

            if (_testUntil > 0f && Time.time < _testUntil)
            {
                SendFrame(SynthesiseFrame(), Codec.RawPcm);
                return;
            }

            if (!_capturing || !SteamRuntime.Available || !SteamUser.HasVoiceData) return;

            byte[] compressed = SteamUser.ReadVoiceDataBytes();
            SendFrame(compressed, Codec.Steam);
        }

        void SendFrame(byte[] payload, Codec codec)
        {
            if (payload == null || payload.Length == 0) return;

            if (payload.Length + 1 > _maxFrameBytes)
            {
                // Not split. A frame this size means the capture interval is wrong, and splitting
                // would hide that behind a reassembly buffer nobody would ever look at again.
                if (!_warnedOversize)
                {
                    _warnedOversize = true;
                    Debug.LogWarning($"[VoiceChat] owner {OwnerId} produced a {payload.Length}B frame, "
                                     + $"over the {_maxFrameBytes}B cap; dropping it. Lower the "
                                     + "capture interval.");
                }

                return;
            }

            var framed = new byte[payload.Length + 1];
            framed[0] = (byte)codec;
            Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);

            _sentFrames++;
            _sentBytes += framed.Length;
            ServerRelay(framed);
        }

        /// <summary>
        /// One capture interval of a 440Hz tone, 16-bit PCM at the current rate. Exists so that
        /// <c>-voiceTest</c> can push real audio through the relay on a machine with no microphone
        /// and no Steam. See <see cref="Report"/>.
        /// </summary>
        byte[] SynthesiseFrame()
        {
            int samples = Mathf.Max(1, Mathf.RoundToInt(_sampleRate * _captureInterval));

            // Kept under the frame cap: the point is to exercise the path, not the bandwidth.
            samples = Mathf.Min(samples, (_maxFrameBytes - 1) / 2);

            var pcm = new byte[samples * 2];
            float step = 2f * Mathf.PI * 440f / _sampleRate;

            for (int i = 0; i < samples; i++)
            {
                var value = (short)(Mathf.Sin(_testPhase) * 8000f);
                _testPhase += step;

                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return pcm;
        }

        // ------------------------------------------------------------------ relay, on the server

        /// <summary>
        /// Unreliable on purpose, and opaque on purpose: the server decides <em>who</em> hears a
        /// frame, never what is in it.
        /// </summary>
        [ServerRpc]
        void ServerRelay(byte[] frame, Channel channel = Channel.Unreliable)
        {
            if (frame == null || frame.Length < 2 || frame.Length > _maxFrameBytes) return;

            Vector3 mouth = transform.position;
            float rangeSqr = _maxRange * _maxRange;

            foreach (NetworkPlayerRegistry.PlayerBody listener in NetworkPlayerRegistry.Players)
            {
                if (!listener.IsValid || listener.Object == NetworkObject) continue;

                NetworkConnection connection = listener.Object.Owner;
                if (connection == null || !connection.IsActive) continue;

                if ((listener.Object.transform.position - mouth).sqrMagnitude > rangeSqr)
                {
                    _relaySkipped++;
                    continue;
                }

                _relayedFrames++;
                TargetPlay(connection, frame);
            }
        }

        // ------------------------------------------------------------------ playback, on listeners

        [TargetRpc]
        void TargetPlay(NetworkConnection connection, byte[] frame, Channel channel = Channel.Unreliable)
        {
            if (_source == null || frame == null || frame.Length < 2) return;

            int samples = (Codec)frame[0] == Codec.RawPcm ? WriteRawPcm(frame) : WriteSteamVoice(frame);
            if (samples <= 0) return;

            _heardFrames++;
            _heardSamples += samples;
        }

        int WriteRawPcm(byte[] frame)
        {
            int samples = (frame.Length - 1) / 2;
            EnqueuePcm(frame, 1, samples);
            return samples;
        }

        int WriteSteamVoice(byte[] frame)
        {
            if (!SteamRuntime.Available) return 0;

            var compressed = new byte[frame.Length - 1];
            Buffer.BlockCopy(frame, 1, compressed, 0, compressed.Length);

            _decode.SetLength(0);
            int written;

            try
            {
                written = SteamUser.DecompressVoice(compressed, _decode);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VoiceChat] owner {OwnerId}: decode failed, frame dropped. {e.Message}");
                return 0;
            }

            if (written <= 0) return 0;

            int samples = written / 2;
            EnqueuePcm(_decode.GetBuffer(), 0, samples);
            return samples;
        }

        /// <summary>
        /// Signed 16-bit little-endian into the float ring. An overrun drops the oldest audio, not the
        /// newest: a listener who has fallen two seconds behind wants the present, not the past.
        /// </summary>
        void EnqueuePcm(byte[] pcm, int offset, int samples)
        {
            lock (_ringLock)
            {
                for (int i = 0; i < samples; i++)
                {
                    int at = offset + i * 2;
                    if (at + 1 >= pcm.Length) break;

                    var value = (short)(pcm[at] | (pcm[at + 1] << 8));
                    _ring[_ringWrite] = value / 32768f;
                    _ringWrite = (_ringWrite + 1) % _ring.Length;

                    if (_ringCount < _ring.Length) _ringCount++;
                    else _ringRead = (_ringRead + 1) % _ring.Length;
                }
            }
        }

        void BuildPlayback()
        {
            _sampleRate = SteamRuntime.Available ? (int)SteamUser.OptimalSampleRate : FallbackSampleRate;
            _ring = new float[Mathf.Max(1024, Mathf.RoundToInt(_sampleRate * BufferSeconds))];
            _decode = new MemoryStream(_sampleRate);

            var go = new GameObject($"Voice (owner {OwnerId})");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.5f, 0f);

            _source = go.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = _fullVolumeRange;
            _source.maxDistance = _maxRange;
            _source.loop = true;
            _source.playOnAwake = false;

            _lowPass = go.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = OpenCutoff;

            // A streaming clip: Unity pulls from the ring on the audio thread, so a late frame is
            // silence rather than a stall and there is never a clip allocated per utterance.
            _source.clip = AudioClip.Create($"Voice{OwnerId}", _sampleRate, 1, _sampleRate, true, ReadPcm);
            _source.Play();
        }

        /// <summary>Audio thread. Everything it touches sits behind <see cref="_ringLock"/>.</summary>
        void ReadPcm(float[] data)
        {
            lock (_ringLock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (_ringCount == 0)
                    {
                        data[i] = 0f;
                        continue;
                    }

                    data[i] = _ring[_ringRead];
                    _ringRead = (_ringRead + 1) % _ring.Length;
                    _ringCount--;
                }
            }
        }

        /// <summary>
        /// Downed and dead voices are filtered, not cut. Being able to hear the person you are
        /// dragging is most of the reason to drag them.
        /// </summary>
        void UpdateMuffling()
        {
            LifeState state = _health != null ? _health.State : LifeState.Alive;

            switch (state)
            {
                case LifeState.Downed:
                    _source.volume = _downedVolume;
                    _lowPass.cutoffFrequency = _downedCutoff;
                    break;
                case LifeState.Dead:
                    _source.volume = _deadVolume;
                    _lowPass.cutoffFrequency = _deadCutoff;
                    break;
                default:
                    _source.volume = 1f;
                    _lowPass.cutoffFrequency = OpenCutoff;
                    break;
            }
        }

        // ------------------------------------------------------------------ headless test

        /// <summary>
        /// <c>-voiceTest &lt;seconds&gt;</c> and <c>-voiceRange &lt;metres&gt;</c>.
        ///
        /// A headless build has no microphone and usually no Steam, so capture and the codec are the
        /// two things an automated run genuinely cannot exercise. Everything after them can:
        /// <c>-voiceTest</c> feeds a synthesised tone into the same <see cref="SendFrame"/> the
        /// microphone uses, tagged <see cref="Codec.RawPcm"/> so the listener skips Steam's decoder
        /// and nothing else. Framing, the unreliable relay, the server range test, the ring buffer
        /// and the muffling all run exactly as they do in a real game.
        ///
        /// It cannot claim anything was audible — under <c>-nographics</c> the audio thread may never
        /// pull a sample — so it reports what the network moved and what reached the buffer, and says
        /// nothing about sound.
        ///
        /// <c>-voiceRange</c> exists because the greybox spawn ring is 6m from the middle: neighbours
        /// land 8.5m apart and opposites 12m, so a range of 10 puts one listener inside and one
        /// outside without anybody having to walk.
        /// </summary>
        void Report()
        {
            if (Time.time < _nextReport) return;
            _nextReport = Time.time + 2f;

            if (IsOwner && _sentFrames > 0)
                Debug.Log($"[VoiceChat] -voiceTest: owner {OwnerId} sent {_sentFrames} frames, {_sentBytes}B.");

            if (IsServerStarted && (_relayedFrames > 0 || _relaySkipped > 0))
                Debug.Log($"[VoiceChat] -voiceTest: server relayed owner {OwnerId} {_relayedFrames} "
                          + $"time(s), skipped {_relaySkipped} listener-frame(s) beyond {_maxRange}m.");

            if (_heardFrames > 0)
                Debug.Log($"[VoiceChat] -voiceTest: heard owner {OwnerId}: {_heardFrames} frames, "
                          + $"{_heardSamples} samples, speaker {(_health != null ? _health.State : LifeState.Alive)}, "
                          + $"volume {_source.volume:0.00}, cutoff {_lowPass.cutoffFrequency:0}Hz.");
        }
    }
}
