using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using Unity.Cinemachine;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The first-person camera. Owner only.
    ///
    /// The camera is deliberately *not* a child of the body. The body is moved by prediction, which
    /// means it moves once per network tick — 30 times a second — while the screen refreshes at 60 or
    /// 144. Parenting the camera to it hands that stepping straight to the player's eyes, and no
    /// amount of Cinemachine damping downstream can recover motion that was never sampled. So this
    /// rig owns a detached target transform, follows the body position through an exponential filter
    /// tuned to one tick, and takes rotation from the mouse at frame rate, where it belongs. Yaw is
    /// read from the same reader the motor replicates, so the camera and the body always agree even
    /// though neither is driving the other.
    ///
    /// Cinemachine sits between the target and the actual Camera rather than the camera being moved
    /// directly, because everything later that steals the view — spectating a dead friend (#26), a
    /// vehicle chase camera, the revive machine's animation — is a priority change on a second
    /// CinemachineCamera and a blend, instead of a pile of if-statements in here.
    /// </summary>
    [DefaultExecutionOrder(-100)] // Before CinemachineBrain, which has no declared order of its own.
    public class PlayerCameraRig : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] PlayerInputReader _input;
        [SerializeField] PlayerMotor _motor;
        [SerializeField] RagdollController _ragdoll;
        [SerializeField] ShockState _shock;
        [SerializeField] Health _health;

        [Tooltip("Followed while limp, because the body root stops moving when the ragdoll takes over.")]
        [SerializeField] Transform _headBone;

        [Tooltip("Pitched to match the look direction so melee and taser can be aimed up and down.")]
        [SerializeField] Transform _aimOrigin;

        [Header("Lens")]
        [SerializeField] float _baseFov = 70f;

        [Tooltip("Field of view while sprinting. The widening is most of what sprinting feels like.")]
        [SerializeField] float _sprintFov = 78f;

        [Tooltip("Seconds for the field of view to close most of the gap to its target.")]
        [SerializeField] float _fovResponse = 0.18f;

        [Header("Follow")]
        [Tooltip("Seconds to close most of the gap to the body. Roughly one tick; see the class note.")]
        [SerializeField] float _followResponse = 0.035f;

        [Tooltip("Slower while limp: physics bones jitter, and the camera should not repeat it.")]
        [SerializeField] float _ragdollFollowResponse = 0.09f;

        [Tooltip("Jumps further than this are teleports — respawn, revive — and are not smoothed.")]
        [SerializeField] float _snapDistance = 1.5f;

        [Header("Head bob")]
        [Tooltip("Steps per second at full speed.")]
        [SerializeField] float _bobFrequency = 9.5f;

        [SerializeField] float _bobAmplitude = 0.045f;
        [SerializeField] float _bobLateral = 0.03f;

        [Tooltip("Degrees of roll at the extremes of the bob. Small: this is where nausea comes from.")]
        [SerializeField] float _bobRoll = 0.6f;

        [Header("Shake")]
        [Tooltip("Noise samples per second. Low values read as a wobble, high ones as a rattle.")]
        [SerializeField] float _shakeFrequency = 18f;

        [SerializeField] float _shakeMaxAngle = 4f;
        [SerializeField] float _shakeMaxOffset = 0.06f;

        [Tooltip("Trauma lost per second. A punch should be gone in well under a second.")]
        [SerializeField] float _traumaDecay = 1.4f;

        [Tooltip("Trauma from a hit that takes all of your health. Scaled down for smaller hits.")]
        [SerializeField] float _hitShake = 1.2f;

        // Created at runtime, owner only. Building these into the prefab would put four cameras in one
        // process during a local four-player test, all fighting the same brain.
        Transform _target;
        CinemachineCamera _camera;

        float _pitch;
        float _fov;
        float _bobPhase;
        float _bobRollAngle;
        float _trauma;

        Vector3 _followed;
        bool _followValid;

        bool _logCamera;
        int _framesSinceLog;
        float _worstFrameMs;
        float _peakTrauma;
        int _limpFrames;
        int _healthEvents;
        int _shockedFrames;

        const float LogIntervalSeconds = 2f;
        float _nextLogAt;

        /// <summary>Where the eyes are, in world space. What a HUD raycast or a UI marker should use.</summary>
        public Transform Eyes => _target;

        /// <summary>
        /// Adds a one-off kick, 0..1. Trauma rather than an amplitude so that several hits landing
        /// together build on each other instead of the last one overwriting the rest.
        /// </summary>
        public void AddShake(float amount) => _trauma = Mathf.Clamp01(_trauma + Mathf.Clamp01(amount));

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Spectators run no camera at all. Their view of this body is the NetworkTransform.
            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            _logCamera = CommandLine.HasFlag("-cameraLog");

            EnsureBrain();
            BuildCamera();

            _fov = _baseFov;
            _pitch = _input != null ? _input.Pitch : 0f;
            _followValid = false;
            _nextLogAt = Time.time + LogIntervalSeconds;

            if (_health != null) _health.Changed += OnHealthChanged;

            Debug.Log($"[PlayerCameraRig] Owner {OwnerId} camera live at fov {_baseFov}.");
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_health != null) _health.Changed -= OnHealthChanged;

            // The camera is not parented to the body, so despawning the body would otherwise leave it
            // hanging in the scene, still the highest-priority view of nothing.
            if (_camera != null) Destroy(_camera.gameObject);
            if (_target != null) Destroy(_target.gameObject);

            _camera = null;
            _target = null;
        }

        /// <summary>
        /// The brain is what turns a CinemachineCamera into pixels. SceneBootstrap puts one on the
        /// greybox scene camera, but scenes made later may not, and a missing brain fails silently:
        /// everything runs, the camera simply never moves. Adding it here costs nothing and removes a
        /// whole class of "why is the view stuck" reports.
        /// </summary>
        static void EnsureBrain()
        {
            Camera main = Camera.main;
            if (main == null)
            {
                Debug.LogWarning("[PlayerCameraRig] No camera tagged MainCamera; nothing will render.");
                return;
            }

            if (main.TryGetComponent(out CinemachineBrain _)) return;

            main.gameObject.AddComponent<CinemachineBrain>();
        }

        /// <summary>
        /// Hard lock plus rotate-with-target: the camera is exactly the target, with no damping of its
        /// own. All of the smoothing in this rig is applied to the target before Cinemachine sees it,
        /// which keeps one filter in the chain instead of two that fight each other.
        /// </summary>
        void BuildCamera()
        {
            _target = new GameObject($"PlayerCameraTarget (owner {OwnerId})").transform;
            _target.SetPositionAndRotation(EyePosition(), LookRotation());

            var go = new GameObject($"PlayerCamera (owner {OwnerId})");
            _camera = go.AddComponent<CinemachineCamera>();
            _camera.Target.TrackingTarget = _target;
            _camera.Lens.FieldOfView = _baseFov;

            // Cinemachine's lens overwrites the brain camera's every frame, so the clip planes have
            // to be set here too or the draw distance changes the moment a player spawns.
            _camera.Lens.NearClipPlane = CameraTuning.NearPlane;
            _camera.Lens.FarClipPlane = CameraTuning.FarPlane;
            _camera.Priority.Value = 10;

            go.AddComponent<CinemachineHardLockToTarget>();
            go.AddComponent<CinemachineRotateWithFollowTarget>();
        }

        void LateUpdate()
        {
            if (_target == null) return;

            float dt = Time.deltaTime;
            bool limp = _ragdoll != null && _ragdoll.IsRagdolled;

            UpdatePitch();
            UpdateTrauma(dt);

            Vector3 eye = Follow(EyePosition(), limp ? _ragdollFollowResponse : _followResponse, dt);
            Quaternion look = LookRotation();

            // Bob and shake are added after the follow filter, not before it: they are supposed to be
            // sharp. Smoothing a footstep is the same as deleting it.
            if (!limp)
            {
                eye += look * Bob(dt);
                look *= Quaternion.Euler(0f, 0f, _bobRollAngle);
            }

            float shake = _trauma * _trauma; // Squared, so small trauma is felt as a nudge, not a jolt.
            if (shake > 0f)
            {
                eye += look * ShakeOffset(shake);
                look *= ShakeRotation(shake);
            }

            _target.SetPositionAndRotation(eye, look);

            UpdateFov(dt);
            AimAlongView();

            if (_logCamera) LogCamera(dt);
        }

        /// <summary>
        /// Pitch is read rather than owned: the reader already clamps it, and having one clamp means a
        /// sensitivity change cannot make the camera and the aim direction disagree.
        /// </summary>
        void UpdatePitch()
        {
            if (_input != null && _input.IsBound) _pitch = _input.Pitch;
        }

        Quaternion LookRotation()
        {
            float yaw = _input != null && _input.IsBound ? _input.Yaw : transform.eulerAngles.y;
            return Quaternion.Euler(_pitch, yaw, 0f);
        }

        /// <summary>
        /// Where the eyes should be this frame, unfiltered. While limp this is the head bone, because
        /// the body root is frozen wherever the character was standing when it fell over.
        /// </summary>
        Vector3 EyePosition()
        {
            bool limp = _ragdoll != null && _ragdoll.IsRagdolled;
            if (limp && _headBone != null) return _headBone.position;

            float eyeHeight = _motor != null ? _motor.EyeHeight : 1.55f;
            return transform.position + Vector3.up * eyeHeight;
        }

        /// <summary>
        /// Exponential follow. Written as 1 - e^(-dt/tau) rather than a fixed lerp factor so the
        /// result is the same at 60fps and at 240 — a plain Lerp with a constant t is a different
        /// filter at every frame rate, which is why cameras written that way feel wrong on a fast
        /// machine and floaty on a slow one.
        /// </summary>
        Vector3 Follow(Vector3 goal, float response, float dt)
        {
            if (!_followValid)
            {
                _followed = goal;
                _followValid = true;
                return _followed;
            }

            if ((goal - _followed).sqrMagnitude > _snapDistance * _snapDistance)
            {
                _followed = goal;
                return _followed;
            }

            float t = response <= 0f ? 1f : 1f - Mathf.Exp(-dt / response);
            _followed = Vector3.Lerp(_followed, goal, t);
            return _followed;
        }

        /// <summary>
        /// A figure of eight, driven by distance travelled rather than by time: stopping mid-stride
        /// leaves the head where it was instead of rocking on the spot, and walking backwards bobs at
        /// the walking rate, not the sprinting one.
        /// </summary>
        Vector3 Bob(float dt)
        {
            if (_motor == null) return Vector3.zero;

            Vector3 flat = _motor.Velocity;
            flat.y = 0f;

            float speed = flat.magnitude;

            // Airborne there is no ground to push off, so there is nothing to bob about.
            if (!_motor.IsGrounded || speed < 0.1f)
            {
                // Unwind rather than cut. Dropping the offset to zero on the frame you stop walking is
                // a visible snap; decaying it is the head settling.
                _bobRollAngle = Mathf.MoveTowards(_bobRollAngle, 0f, dt * 20f);
                return Vector3.zero;
            }

            float scale = Mathf.Clamp01(speed / 7.5f); // 7.5 is the sprint speed; walking bobs less.
            _bobPhase += dt * _bobFrequency * scale;

            // Vertical at twice the rate of the sway: two footfalls per full left-right cycle.
            float vertical = Mathf.Sin(_bobPhase * 2f) * _bobAmplitude * scale;
            float lateral = Mathf.Sin(_bobPhase) * _bobLateral * scale;

            _bobRollAngle = Mathf.Sin(_bobPhase) * _bobRoll * scale;

            return new Vector3(lateral, vertical, 0f);
        }

        /// <summary>
        /// The impact hook. Scaled by how hard the hit was, so a stray punch is a nudge and being run
        /// over is the whole screen. Healing raises health and must not shake anything.
        ///
        /// This fires on every peer, but only the owner has a camera, so only the owner subscribes.
        /// </summary>
        void OnHealthChanged(float previous, float current)
        {
            float lost = previous - current;
            if (lost <= 0f || _health.Max <= 0f) return;

            _healthEvents++;
            AddShake(lost / _health.Max * _hitShake);
        }

        /// <summary>
        /// Trauma decays on its own, but the taser holds it up: ShockState.CameraShake is an amplitude
        /// that stays set for as long as the shock lasts, so flooring the trauma at that value gives a
        /// continuous rattle without a coroutine ticking it.
        /// </summary>
        void UpdateTrauma(float dt)
        {
            _trauma = Mathf.Max(0f, _trauma - _traumaDecay * dt);

            if (_shock == null) return;
            if (_shock.CameraShake > 0f) _shockedFrames++;
            _trauma = Mathf.Max(_trauma, Mathf.Clamp01(_shock.CameraShake));
        }

        // Perlin rather than Random: consecutive samples are related, so the camera wanders instead of
        // teleporting every frame. Each axis reads a different row of the noise so they never agree.
        Vector3 ShakeOffset(float shake)
        {
            float t = Time.time * _shakeFrequency;
            return new Vector3(Noise(0f, t), Noise(11f, t), 0f) * (_shakeMaxOffset * shake);
        }

        Quaternion ShakeRotation(float shake)
        {
            float t = Time.time * _shakeFrequency;
            float amount = _shakeMaxAngle * shake;

            return Quaternion.Euler(Noise(23f, t) * amount,
                                    Noise(37f, t) * amount,
                                    Noise(53f, t) * amount);
        }

        static float Noise(float row, float t) => Mathf.PerlinNoise(row, t) * 2f - 1f;

        void UpdateFov(float dt)
        {
            if (_camera == null) return;

            bool sprinting = _motor != null && _motor.IsGrounded
                             && _input != null && _input.Sprint && _input.Move.sqrMagnitude > 0.01f;

            float goal = sprinting ? _sprintFov : _baseFov;
            float t = _fovResponse <= 0f ? 1f : 1f - Mathf.Exp(-dt / _fovResponse);

            _fov = Mathf.Lerp(_fov, goal, t);
            _camera.Lens.FieldOfView = _fov;
        }

        /// <summary>
        /// Points the aim origin where the camera looks, so a punch or a taser shot goes at whatever is
        /// under the crosshair rather than straight out of the chest at eye level.
        ///
        /// Only the rotation is touched. The weapons send a direction and the server resolves the hit
        /// from its own copy of this transform, so moving the local one would change nothing that is
        /// transmitted — and would quietly desync what the player sees from what the server checks.
        /// The server only validates the horizontal angle, so pitch is free. See AimValidation.
        /// </summary>
        void AimAlongView()
        {
            if (_aimOrigin == null) return;
            _aimOrigin.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        /// <summary>
        /// Frame timing, not camera state: the acceptance test for this issue is "smooth 60fps, no
        /// jitter while ragdolled", and the worst frame in an interval is the only part of that a
        /// headless run can actually report. Off unless -cameraLog is passed.
        ///
        /// Trauma and the ragdoll are reported as an interval peak and a frame count rather than as
        /// whatever they happen to be when the line is printed. Both are transients — a taser shock
        /// is under a second and trauma decays faster than that — so sampling them every two seconds
        /// almost always reports zero on a run where they fired dozens of times.
        /// </summary>
        void LogCamera(float dt)
        {
            bool limp = _ragdoll != null && _ragdoll.IsRagdolled;

            _framesSinceLog++;
            _worstFrameMs = Mathf.Max(_worstFrameMs, dt * 1000f);
            _peakTrauma = Mathf.Max(_peakTrauma, _trauma);
            if (limp) _limpFrames++;

            if (Time.time < _nextLogAt) return;

            float average = _framesSinceLog > 0 ? LogIntervalSeconds * 1000f / _framesSinceLog : 0f;

            Debug.Log($"[PlayerCameraRig] owner {OwnerId}: {_framesSinceLog} frames, "
                      + $"average {average:F1}ms, worst {_worstFrameMs:F1}ms, "
                      + $"fov {_fov:F1}, peak trauma {_peakTrauma:F2}, "
                      + $"ragdolled {_limpFrames}/{_framesSinceLog} frames, "
                      + $"hits {_healthEvents}, shocked {_shockedFrames} frames, "
                      + $"eye at {_target.position}.");

            _nextLogAt = Time.time + LogIntervalSeconds;
            _framesSinceLog = 0;
            _worstFrameMs = 0f;
            _peakTrauma = 0f;
            _limpFrames = 0;
            _healthEvents = 0;
            _shockedFrames = 0;
        }
    }
}
