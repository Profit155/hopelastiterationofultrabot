using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// Hybrid action executor:
    /// - Movement: via NewMovement.inputDir reflection (game's physics handles the rest)
    /// - Camera: direct rotationX/Y with smoothing
    /// - Discrete actions (jump/dash/slide/punch): direct calls WITH game-state checks
    /// - Fire1/Fire2: via InputManager.InputSource.Fire1/Fire2 InputActionState
    ///   (weapons read these in their Update() — this is the ONLY way to fire)
    ///
    /// Action layout (22 floats):
    ///   [0]  move_forward   (-1 to 1)
    ///   [1]  move_right     (-1 to 1)
    ///   [2]  look_yaw       (-1 to 1) * sensitivity
    ///   [3]  look_pitch     (-1 to 1) * sensitivity
    ///   [4]  jump            (>0.5 = press)
    ///   [5]  dash            (>0.5 = press)
    ///   [6]  slide           (>0.5 = press)
    ///   [7]  fire_primary    (>0.5 = hold)
    ///   [8]  fire_secondary  (>0.5 = hold)
    ///   [9]  punch           (>0.5 = press)
    ///   [10-15] swap_weapon_1-6
    ///   [16] coin_throw      (>0.5 = press)
    ///   [17] whiplash        (>0.5 = press)
    ///   [18] slam            (>0.5 = press)
    ///   [19] swap_variation  (>0.5 = press)
    ///   [20] rail_charge     (>0.5 = hold)
    ///   [21] noop
    /// </summary>
    public class ActionExecutor
    {
        public const int ActionSize = 22;
        public float LookSensitivity = 2f;

        private NewMovement _player;
        private GunControl _gunControl;
        private FistControl _fistControl;
        private CameraController _camera;
        private HookArm _hookArm;

        // Reflection for private members
        private FieldInfo _inputDirField;
        private MethodInfo _dodgeMethod;
        private MethodInfo _startSlideMethod;

        // Fire injection via PlayerInput InputActionState fields
        private object _playerInput; // InputManager.InputSource (PlayerInput object)
        private InputActionState _fire1State;
        private InputActionState _fire2State;
        private MethodInfo _setIsPressed;
        private MethodInfo _setPerformedFrame;
        private bool _fireSystemReady;
        private bool _loggedFireSetup;

        // Edge detection
        private bool[] _prevButtons = new bool[ActionSize];

        // === Spam / jitter metrics, read by TcpBridge after Execute() ===
        public float CameraJitter;
        public float MoveJitter;
        public int WastedActions;
        public int DashUsed;
        public int PunchUsed;
        public int SlamTriggered;
        public int WeaponSwitches;
        public int WhiplashFired;
        public int FireToggles;
        public int ButtonsHeldCount;

        private float _prevRawYaw, _prevRawPitch;
        private float _prevMoveFwd, _prevMoveRight;

        // Camera smoothing
        private float _smoothYaw;
        private float _smoothPitch;

        // Cooldowns (frames)
        private int _jumpCooldown;
        private int _dashCooldown;
        private int _slideCooldown;
        private int _punchCooldown;
        private int _weaponSwitchCooldown;
        private int _whiplashCooldown;
        private int _slamCooldown;

        private const int JumpCD = 15;
        private const int DashCD = 20;
        private const int SlideCD = 30;
        private const int PunchCD = 20;
        private const int WeaponCD = 60;
        private const int WhiplashCD = 30;
        private const int SlamCD = 30;

        public void RefreshReferences()
        {
            _player = UnityEngine.Object.FindObjectOfType<NewMovement>();
            _gunControl = UnityEngine.Object.FindObjectOfType<GunControl>();
            _fistControl = UnityEngine.Object.FindObjectOfType<FistControl>();
            _camera = UnityEngine.Object.FindObjectOfType<CameraController>();
            _hookArm = UnityEngine.Object.FindObjectOfType<HookArm>();

            if (_player != null)
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                _inputDirField = typeof(NewMovement).GetField("inputDir", flags);
                _dodgeMethod = typeof(NewMovement).GetMethod("Dodge", flags);
                _startSlideMethod = typeof(NewMovement).GetMethod("StartSlide", flags);

                if (_inputDirField == null)
                    UltrabotPlugin.Log.LogError("[ULTRABOT] inputDir field NOT FOUND!");
            }

            // Setup fire injection
            SetupFireSystem();
        }

        private void SetupFireSystem()
        {
            if (_fireSystemReady) return;

            try
            {
                // InputManager is MonoSingleton — find it
                var im = UnityEngine.Object.FindObjectOfType<InputManager>();
                if (im == null)
                {
                    if (!_loggedFireSetup)
                        UltrabotPlugin.Log.LogError("[ULTRABOT] InputManager not found yet");
                    return;
                }

                // Get InputSource property (returns PlayerInput)
                var inputSourceProp = typeof(InputManager).GetProperty("InputSource",
                    BindingFlags.Public | BindingFlags.Instance);
                if (inputSourceProp == null)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] InputSource property NOT FOUND");
                    return;
                }

                _playerInput = inputSourceProp.GetValue(im);
                if (_playerInput == null)
                {
                    if (!_loggedFireSetup)
                        UltrabotPlugin.Log.LogError("[ULTRABOT] InputSource is null (not ready yet)");
                    return;
                }

                var piType = _playerInput.GetType();

                // Get Fire1 and Fire2 fields (public InputActionState fields on PlayerInput)
                var fire1Field = piType.GetField("Fire1", BindingFlags.Public | BindingFlags.Instance);
                var fire2Field = piType.GetField("Fire2", BindingFlags.Public | BindingFlags.Instance);

                if (fire1Field == null || fire2Field == null)
                {
                    UltrabotPlugin.Log.LogError($"[ULTRABOT] Fire1/Fire2 fields not found on {piType.Name}. Fields: {string.Join(", ", Array.ConvertAll(piType.GetFields(BindingFlags.Public | BindingFlags.Instance), f => f.Name))}");
                    return;
                }

                _fire1State = fire1Field.GetValue(_playerInput) as InputActionState;
                _fire2State = fire2Field.GetValue(_playerInput) as InputActionState;

                if (_fire1State == null || _fire2State == null)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] Fire1/Fire2 InputActionState are null");
                    return;
                }

                // Cache reflection for InputActionState private setters
                var stateType = typeof(InputActionState);
                _setIsPressed = stateType.GetMethod("set_IsPressed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _setPerformedFrame = stateType.GetMethod("set_PerformedFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (_setIsPressed == null || _setPerformedFrame == null)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] InputActionState setters NOT FOUND");
                    return;
                }

                _fireSystemReady = true;
                _loggedFireSetup = true;

                // Log all available InputActionState fields on PlayerInput
                var allFields = piType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                var actionNames = new List<string>();
                foreach (var f in allFields)
                {
                    if (f.FieldType == stateType)
                        actionNames.Add(f.Name);
                }
                actionNames.Sort();
                UltrabotPlugin.Log.LogError($"[ULTRABOT] Fire system READY! PlayerInput actions: {string.Join(", ", actionNames)}");
            }
            catch (Exception e)
            {
                if (!_loggedFireSetup)
                {
                    UltrabotPlugin.Log.LogError($"[ULTRABOT] SetupFireSystem error: {e}");
                    _loggedFireSetup = true;
                }
            }
        }

        private void SetFireInput(InputActionState state, bool pressed)
        {
            if (state == null || _setIsPressed == null) return;
            _setIsPressed.Invoke(state, new object[] { pressed });
            if (pressed)
                _setPerformedFrame.Invoke(state, new object[] { Time.frameCount });
        }

        public void Execute(float[] actions)
        {
            if (_player == null || _player.dead)
            {
                RefreshReferences();
                if (_player == null) return;
            }

            // Keep trying to get fire system ready
            if (!_fireSystemReady)
                SetupFireSystem();

            // Tick cooldowns
            if (_jumpCooldown > 0) _jumpCooldown--;
            if (_dashCooldown > 0) _dashCooldown--;
            if (_slideCooldown > 0) _slideCooldown--;
            if (_punchCooldown > 0) _punchCooldown--;
            if (_weaponSwitchCooldown > 0) _weaponSwitchCooldown--;
            if (_whiplashCooldown > 0) _whiplashCooldown--;
            if (_slamCooldown > 0) _slamCooldown--;

            // Reset per-step spam/jitter counters
            CameraJitter = 0f;
            MoveJitter = 0f;
            WastedActions = 0;
            DashUsed = 0;
            PunchUsed = 0;
            SlamTriggered = 0;
            WeaponSwitches = 0;
            WhiplashFired = 0;
            FireToggles = 0;
            ButtonsHeldCount = 0;

            // --- Camera / Aiming (with smoothing) ---
            if (_camera != null)
            {
                float rawYaw = actions[2] * LookSensitivity;
                float rawPitch = actions[3] * LookSensitivity;

                // Camera jitter: sign-flip detection on raw input
                if (rawYaw * _prevRawYaw < 0f)
                    CameraJitter += Mathf.Abs(rawYaw - _prevRawYaw);
                if (rawPitch * _prevRawPitch < 0f)
                    CameraJitter += Mathf.Abs(rawPitch - _prevRawPitch);
                _prevRawYaw = rawYaw;
                _prevRawPitch = rawPitch;

                _smoothYaw = Mathf.Lerp(_smoothYaw, rawYaw, 0.3f);
                _smoothPitch = Mathf.Lerp(_smoothPitch, rawPitch, 0.3f);
                _camera.rotationX += _smoothYaw;
                _camera.rotationY += _smoothPitch;
                _camera.rotationY = Mathf.Clamp(
                    _camera.rotationY,
                    _camera.minimumY,
                    _camera.maximumY);
            }

            // Move jitter: sign-flip detection on move axes
            float moveFwd = actions[0];
            float moveRight = actions[1];
            if (moveFwd * _prevMoveFwd < 0f)
                MoveJitter += Mathf.Abs(moveFwd - _prevMoveFwd);
            if (moveRight * _prevMoveRight < 0f)
                MoveJitter += Mathf.Abs(moveRight - _prevMoveRight);
            _prevMoveFwd = moveFwd;
            _prevMoveRight = moveRight;

            // --- Movement via inputDir ---
            if (_player.activated && _inputDirField != null)
            {
                var forward = _player.transform.forward;
                var right = _player.transform.right;
                forward.y = 0; forward.Normalize();
                right.y = 0; right.Normalize();

                var moveDir = forward * actions[0] + right * actions[1];
                if (moveDir.magnitude > 1f)
                    moveDir.Normalize();

                _inputDirField.SetValue(_player, moveDir);
            }

            // --- Discrete actions ---
            bool jump = actions[4] > 0.5f;
            bool dash = actions[5] > 0.5f;
            bool slide = actions[6] > 0.5f;
            bool firePrimary = actions[7] > 0.5f;
            bool fireSecondary = actions[8] > 0.5f;
            bool punch = actions[9] > 0.5f;
            bool whiplash = actions[17] > 0.5f;
            bool slam = actions[18] > 0.5f;
            bool swapVar = actions[19] > 0.5f;

            // JUMP — only if on ground
            if (jump && !_prevButtons[4] && _jumpCooldown <= 0 && _player.activated)
            {
                var gc = _player.gc;
                if (gc != null && gc.onGround)
                {
                    _player.Jump();
                    _jumpCooldown = JumpCD;
                }
                else
                {
                    WastedActions++;
                }
            }

            // DASH — only if stamina available
            if (dash && !_prevButtons[5])
            {
                if (_dashCooldown <= 0 && _player.activated && _player.boostCharge >= 100f)
                {
                    _dodgeMethod?.Invoke(_player, null);
                    _dashCooldown = DashCD;
                    DashUsed++;
                }
                else
                {
                    WastedActions++;
                }
            }

            // SLIDE — only if on ground
            if (slide && !_player.sliding && _slideCooldown <= 0 && _player.activated)
            {
                var gc = _player.gc;
                if (gc != null && gc.onGround)
                {
                    _startSlideMethod?.Invoke(_player, null);
                    _slideCooldown = SlideCD;
                }
                else
                {
                    WastedActions++;
                }
            }
            else if (!slide && _player.sliding)
            {
                _player.StopSlide();
            }

            // SLAM — only if in the air
            if (slam && !_prevButtons[18])
            {
                if (_slamCooldown <= 0 && _player.falling)
                {
                    _player.Slamdown(1f);
                    _slamCooldown = SlamCD;
                    SlamTriggered++;
                }
                else
                {
                    WastedActions++;
                }
            }

            // Anti-pattern: bot tries to jump+slide simultaneously (in-air slide = Slamdown)
            if (jump && slide && !_prevButtons[4] && !_prevButtons[6])
                SlamTriggered++;

            // WHIPLASH — only if equipped
            if (whiplash && !_prevButtons[17])
            {
                if (_whiplashCooldown <= 0 && _hookArm != null && _hookArm.equipped)
                {
                    _hookArm.SendMessage("ThrowHook", SendMessageOptions.DontRequireReceiver);
                    _whiplashCooldown = WhiplashCD;
                    WhiplashFired++;
                }
                else
                {
                    WastedActions++;
                }
            }

            // PUNCH — only if ready
            if (punch && !_prevButtons[9])
            {
                if (_punchCooldown <= 0 && _fistControl != null && _fistControl.currentPunch != null
                    && _fistControl.currentPunch.ready && _fistControl.fistCooldown <= 0f)
                {
                    _fistControl.currentPunch.PunchStart();
                    _punchCooldown = PunchCD;
                    PunchUsed++;
                }
                else
                {
                    WastedActions++;
                }
            }

            // WEAPON SLOTS
            if (_gunControl != null)
            {
                if (_weaponSwitchCooldown <= 0)
                {
                    bool switched = false;
                    for (int slot = 0; slot < 6; slot++)
                    {
                        if (actions[10 + slot] > 0.5f && !_prevButtons[10 + slot])
                        {
                            if (slot == _gunControl.currentSlotIndex)
                            {
                                WastedActions++;
                            }
                            else
                            {
                                _gunControl.SwitchWeapon(slot, null, true, false, false);
                                _weaponSwitchCooldown = WeaponCD;
                                WeaponSwitches++;
                                switched = true;
                            }
                            break;
                        }
                    }

                    if (!switched && swapVar && !_prevButtons[19] && _weaponSwitchCooldown <= 0)
                    {
                        int nextVar = (_gunControl.currentVariationIndex + 1);
                        _gunControl.SwitchWeapon(
                            _gunControl.currentSlotIndex, nextVar,
                            false, false, true);
                        _weaponSwitchCooldown = WeaponCD;
                        WeaponSwitches++;
                    }
                }
                else
                {
                    // On cooldown but pressed: count wasted edges
                    for (int slot = 0; slot < 6; slot++)
                        if (actions[10 + slot] > 0.5f && !_prevButtons[10 + slot])
                            WastedActions++;
                    if (swapVar && !_prevButtons[19])
                        WastedActions++;
                }
            }

            // FireToggles & ButtonsHeldCount
            if (firePrimary != _prevButtons[7]) FireToggles++;
            if (fireSecondary != _prevButtons[8]) FireToggles++;

            ButtonsHeldCount = (jump ? 1 : 0) + (dash ? 1 : 0) + (slide ? 1 : 0)
                + (firePrimary ? 1 : 0) + (fireSecondary ? 1 : 0) + (punch ? 1 : 0)
                + (whiplash ? 1 : 0) + (slam ? 1 : 0) + (swapVar ? 1 : 0);
            for (int s = 0; s < 6; s++)
                if (actions[10 + s] > 0.5f) ButtonsHeldCount++;

            // FIRE — via PlayerInput's InputActionState (weapons read these in Update())
            if (_fireSystemReady)
            {
                SetFireInput(_fire1State, firePrimary);
                SetFireInput(_fire2State, fireSecondary);
            }

            // Save button states for edge detection
            for (int i = 0; i < ActionSize; i++)
                _prevButtons[i] = actions[i] > 0.5f;
        }

        public void ReleaseAll()
        {
            if (_fireSystemReady)
            {
                SetFireInput(_fire1State, false);
                SetFireInput(_fire2State, false);
            }

            _prevButtons = new bool[ActionSize];
            _smoothYaw = 0f;
            _smoothPitch = 0f;
            _prevRawYaw = 0f;
            _prevRawPitch = 0f;
            _prevMoveFwd = 0f;
            _prevMoveRight = 0f;

            if (_player != null && _inputDirField != null)
                _inputDirField.SetValue(_player, Vector3.zero);
        }

        public void SetTimeScale(float scale)
        {
            Time.timeScale = Mathf.Clamp(scale, 0.1f, 10f);
        }

        public void ResetLevel()
        {
            var sm = UnityEngine.Object.FindObjectOfType<StatsManager>();
            if (sm != null)
                sm.Restart();
        }
    }
}
