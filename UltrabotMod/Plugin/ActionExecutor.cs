using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// Action executor — all actions go through InputActionState (the game's input system)
    /// except movement (inputDir) and camera (direct rotationX/Y).
    ///
    /// Action layout (20 floats):
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
    ///   [16] whiplash        (>0.5 = press)
    ///   [17] slam            (>0.5 = press)
    ///   [18] swap_variation  (>0.5 = press)
    ///   [19] change_fist     (>0.5 = press)
    /// </summary>
    public class ActionExecutor
    {
        public const int ActionSize = 20;
        public float LookSensitivity = 5f;

        private NewMovement _player;
        private GunControl _gunControl;
        private FistControl _fistControl;
        private CameraController _camera;
        private HookArm _hookArm;

        // Reflection for movement (no InputActionState for this)
        private FieldInfo _inputDirField;

        // Navigation target (set by GameStateReader)
        private Vector3 _navTarget;

        // Input system — we cache reflection metadata but get InputActionState FRESH each frame
        // because InputManager.InputSource is a property that may return different objects
        private InputManager _inputManager;
        private PropertyInfo _inputSourceProp;
        private Dictionary<string, FieldInfo> _actionFieldInfos = new Dictionary<string, FieldInfo>();
        private MethodInfo _setIsPressed;
        private MethodInfo _setPerformedFrame;
        private MethodInfo _setCanceledFrame;
        private bool _inputSystemReady;
        private bool _loggedSetup;

        // Edge detection
        private bool[] _prevButtons = new bool[ActionSize];

        // Pending InputActionState — set in Execute() (coroutine), applied in ApplyPendingInputStates() (Update)
        private bool _hasPendingInputs;
        private bool _pJump, _pDash, _pSlide, _pPunch, _pFire1, _pFire2, _pHook;
        private bool _pChangeFist, _pChangeFistReleased;

        // Camera smoothing
        private float _smoothYaw;
        private float _smoothPitch;

        // Cached nearest enemy (updated each frame for aim assist + fire gate)
        private EnemyIdentifier _nearestEnemy;

        // Cooldowns (frames) — safety nets on top of game's own checks
        private int _weaponSwitchCooldown;
        private int _whiplashCooldown;
        private int _slamCooldown;
        private int _changeFistCooldown;

        private const int WeaponCD = 60;
        private const int WhiplashCD = 30;
        private const int SlamCD = 30;
        private const int ChangeFistCD = 20;
        private const float WallSlideCheckDistance = 1f;
        private const float WallSlideRayHeight = 0.5f;
        private const float AimAssistPitchLimit = 20f;
        private const float SafePitchLimit = 80f;

        public void RefreshReferences()
        {
            _player = UnityEngine.Object.FindObjectOfType<NewMovement>();
            _gunControl = UnityEngine.Object.FindObjectOfType<GunControl>();
            _fistControl = UnityEngine.Object.FindObjectOfType<FistControl>();
            _camera = UnityEngine.Object.FindObjectOfType<CameraController>();
            _hookArm = UnityEngine.Object.FindObjectOfType<HookArm>();

            if (_player != null)
            {
                _inputDirField = typeof(NewMovement).GetField("inputDir",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (_inputDirField == null)
                    UltrabotPlugin.Log.LogError("[ULTRABOT] inputDir field NOT FOUND!");
            }

            SetupInputSystem();
        }

        public bool IsNavAgentActive => _navTarget != Vector3.zero;
        public float NavTargetDistance
        {
            get
            {
                if (_player != null && _navTarget != Vector3.zero)
                    return Vector3.Distance(_player.transform.position, _navTarget);
                return 0f;
            }
        }

        /// <summary>
        /// Get movement direction toward target (straight line).
        /// GameStateReader provides the navmesh-calculated direction in observations.
        /// </summary>
        public Vector3 GetNavDesiredDirection()
        {
            if (_player != null && _navTarget != Vector3.zero)
            {
                Vector3 toTarget = _navTarget - _player.transform.position;
                toTarget.y = 0;
                if (toTarget.magnitude > 2f)
                    return toTarget.normalized;
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Set navigation target — called by GameStateReader.
        /// </summary>
        public void SetNavDestination(Vector3 target)
        {
            _navTarget = target;
        }

        private void SetupInputSystem()
        {
            if (_inputSystemReady) return;

            try
            {
                _inputManager = UnityEngine.Object.FindObjectOfType<InputManager>();
                if (_inputManager == null)
                {
                    if (!_loggedSetup)
                        UltrabotPlugin.Log.LogError("[ULTRABOT] InputManager not found yet");
                    return;
                }

                _inputSourceProp = typeof(InputManager).GetProperty("InputSource",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_inputSourceProp == null)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] InputSource property NOT FOUND");
                    return;
                }

                // Get a sample to discover the PlayerInput type and fields
                var sampleInput = _inputSourceProp.GetValue(_inputManager);
                if (sampleInput == null)
                {
                    if (!_loggedSetup)
                        UltrabotPlugin.Log.LogError("[ULTRABOT] InputSource is null (not ready yet)");
                    return;
                }

                var piType = sampleInput.GetType();

                // Cache InputActionState private setters (these never change)
                var stateType = typeof(InputActionState);
                _setIsPressed = stateType.GetMethod("set_IsPressed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _setPerformedFrame = stateType.GetMethod("set_PerformedFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _setCanceledFrame = stateType.GetMethod("set_CanceledFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (_setIsPressed == null || _setPerformedFrame == null)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] InputActionState setters NOT FOUND");
                    return;
                }

                // Cache FieldInfo for each action (metadata only, NOT the state objects)
                string[] fieldNames = { "Fire1", "Fire2", "Jump", "Dodge", "Slide", "Punch", "Hook", "ChangeFist" };
                _actionFieldInfos.Clear();

                foreach (var name in fieldNames)
                {
                    var field = piType.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    if (field == null)
                    {
                        UltrabotPlugin.Log.LogError($"[ULTRABOT] PlayerInput.{name} NOT FOUND");
                        return;
                    }
                    _actionFieldInfos[name] = field;
                }

                _prevActionStatesByName.Clear();
                _inputSystemReady = true;
                _loggedSetup = true;

                // Log all available actions
                var allFields = piType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                var actionNames = new List<string>();
                foreach (var f in allFields)
                {
                    if (f.FieldType == stateType)
                        actionNames.Add(f.Name);
                }
                actionNames.Sort();
                UltrabotPlugin.Log.LogError($"[ULTRABOT] Input system READY! Actions: {string.Join(", ", actionNames)}");
            }
            catch (Exception e)
            {
                if (!_loggedSetup)
                {
                    UltrabotPlugin.Log.LogError($"[ULTRABOT] SetupInputSystem error: {e}");
                    _loggedSetup = true;
                }
            }
        }

        /// <summary>
        /// Get the CURRENT InputActionState for a named action.
        /// Must be called each frame because InputManager.InputSource may return different objects.
        /// </summary>
        private InputActionState GetCurrentActionState(string name)
        {
            if (_inputManager == null || _inputSourceProp == null) return null;
            if (!_actionFieldInfos.TryGetValue(name, out var fieldInfo)) return null;

            var currentInput = _inputSourceProp.GetValue(_inputManager);
            if (currentInput == null) return null;

            return fieldInfo.GetValue(currentInput) as InputActionState;
        }

        // Edge detection keyed by action NAME (not object ref, since objects change each frame)
        private readonly Dictionary<string, bool> _prevActionStatesByName = new Dictionary<string, bool>();

        /// <summary>
        /// Set InputActionState by name. Gets the CURRENT state from InputManager.InputSource each call.
        /// This is critical — InputSource may return different PlayerInput objects, so cached refs go stale.
        /// </summary>
        private void SetActionInput(string actionName, bool pressed)
        {
            var state = GetCurrentActionState(actionName);
            if (state == null || _setIsPressed == null) return;

            bool wasPressed = _prevActionStatesByName.TryGetValue(actionName, out bool prev) && prev;
            _setIsPressed.Invoke(state, new object[] { pressed });

            if (pressed && !wasPressed)
                _setPerformedFrame.Invoke(state, new object[] { Time.frameCount });
            else if (!pressed && wasPressed && _setCanceledFrame != null)
                _setCanceledFrame.Invoke(state, new object[] { Time.frameCount });

            _prevActionStatesByName[actionName] = pressed;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteVector(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsValidDirection(Vector3 value)
        {
            return IsFiniteVector(value) && value.sqrMagnitude > 0.0001f;
        }

        public void Execute(float[] actions)
        {
            if (_player == null || _player.dead)
            {
                RefreshReferences();
                if (_player == null) return;
            }

            if (!_inputSystemReady)
                SetupInputSystem();

            // Tick cooldowns
            if (_weaponSwitchCooldown > 0) _weaponSwitchCooldown--;
            if (_whiplashCooldown > 0) _whiplashCooldown--;
            if (_slamCooldown > 0) _slamCooldown--;
            if (_changeFistCooldown > 0) _changeFistCooldown--;

            // --- Find nearest enemy (used for aim assist + fire gate) ---
            _nearestEnemy = null;
            {
                var enemies = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
                float bestDist = float.MaxValue;
                foreach (var e in enemies)
                {
                    if (e.dead || e.health <= 0) continue;
                    float d = (e.transform.position - _player.transform.position).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; _nearestEnemy = e; }
                }
            }
            var nearestEnemy = _nearestEnemy;

            // --- Camera / Aiming: soft aim assist + RL control ---
            if (_camera != null)
            {
                // RL look input
                float rawYaw = actions[2] * LookSensitivity;
                float rawPitch = actions[3] * LookSensitivity;

                // Soft aim assist — gently pull camera toward nearest enemy
                float aimYaw = 0f, aimPitch = 0f;
                if (nearestEnemy != null)
                {
                    Vector3 toEnemy = nearestEnemy.transform.position - _player.transform.position;
                    float dist = IsFiniteVector(toEnemy) ? toEnemy.magnitude : 0f;
                    if (dist > 0.5f && dist < 100f)
                    {
                        Vector3 dirH = Vector3.ProjectOnPlane(toEnemy, Vector3.up);
                        float hDist = IsFiniteVector(dirH) ? dirH.magnitude : 0f;
                        if (hDist > 0.1f)
                        {
                            // Yaw: horizontal aim toward enemy
                            float targetYaw = Mathf.Atan2(dirH.x, dirH.z) * Mathf.Rad2Deg;
                            if (IsFinite(targetYaw))
                            {
                                float yawDelta = Mathf.DeltaAngle(_camera.rotationY, targetYaw);
                                if (IsFinite(yawDelta))
                                    aimYaw = yawDelta * 0.10f;
                            }

                            // Pitch: only pull toward enemy, clamp to ±30° from horizon
                            float vertAngle = Mathf.Atan2(toEnemy.y, hDist) * Mathf.Rad2Deg;
                            if (IsFinite(vertAngle))
                            {
                                float targetPitch = Mathf.Clamp(-vertAngle, -AimAssistPitchLimit, AimAssistPitchLimit);
                                float pitchDelta = targetPitch - _camera.rotationX;
                                if (IsFinite(pitchDelta))
                                    aimPitch = Mathf.Clamp(pitchDelta * 0.035f, -2f, 2f);
                            }
                        }
                    }
                }

                // Combine: aim assist + RL adjustment
                _smoothYaw = Mathf.Lerp(IsFinite(_smoothYaw) ? _smoothYaw : 0f, IsFinite(rawYaw) ? rawYaw : 0f, 0.5f);
                _smoothPitch = Mathf.Lerp(IsFinite(_smoothPitch) ? _smoothPitch : 0f, IsFinite(rawPitch) ? rawPitch : 0f, 0.5f);
                float finalYaw = (IsFinite(aimYaw) ? aimYaw : 0f) + _smoothYaw;
                float finalPitch = (IsFinite(aimPitch) ? aimPitch : 0f) + _smoothPitch;
                float minPitch = Mathf.Max(_camera.minimumY, -SafePitchLimit);
                float maxPitch = Mathf.Min(_camera.maximumY, SafePitchLimit);
                float nextYaw = IsFinite(finalYaw) ? _camera.rotationY + finalYaw : _camera.rotationY;
                float nextPitch = IsFinite(finalPitch) ? _camera.rotationX + finalPitch : _camera.rotationX;
                nextPitch = Mathf.Clamp(nextPitch, minPitch, maxPitch);

                Vector3 proposedLook = Quaternion.Euler(-nextPitch, nextYaw, 0f) * Vector3.forward;
                if (IsFinite(nextYaw) && IsFinite(nextPitch) && IsValidDirection(proposedLook))
                {
                    _camera.rotationY = nextYaw;
                    _camera.rotationX = nextPitch;
                }
            }

            // --- Movement: nav direction + RL correction ---
            if (_player.activated && _inputDirField != null)
            {
                var forward = _player.transform.forward;
                var right = _player.transform.right;
                forward.y = 0; forward.Normalize();
                right.y = 0; right.Normalize();

                // RL movement input (strafe, retreat, etc.)
                var rlDir = forward * actions[0] + right * actions[1];

                // NavMeshAgent desired direction (pathfinding to target)
                var navDir = GetNavDesiredDirection();

                Vector3 moveDir;
                if (navDir.magnitude > 0.01f)
                {
                    // Blend: 60% nav direction + 40% RL direction
                    // Bot follows navmesh path but RL can strafe/dodge
                    moveDir = navDir * 0.6f + rlDir * 0.4f;
                }
                else
                {
                    // No nav path — RL has full control (air, no navmesh area)
                    moveDir = rlDir;
                }

                if (moveDir.magnitude > 1f)
                    moveDir.Normalize();

                if (IsValidDirection(moveDir))
                {
                    Vector3 rayOrigin = _player.transform.position + Vector3.up * WallSlideRayHeight;
                    if (Physics.Raycast(rayOrigin, moveDir.normalized, out RaycastHit wallHit, WallSlideCheckDistance, ~0, QueryTriggerInteraction.Ignore))
                    {
                        Vector3 wallNormal = wallHit.normal;
                        if (Mathf.Abs(wallNormal.y) < 0.7f && IsValidDirection(wallNormal))
                        {
                            Vector3 slideDir = Vector3.ProjectOnPlane(moveDir, wallNormal);
                            moveDir = IsValidDirection(slideDir) ? slideDir.normalized : Vector3.zero;
                        }
                    }
                }

                _inputDirField.SetValue(_player, moveDir);
            }

            // --- All discrete actions via InputActionState ---
            bool jump = actions[4] > 0.5f;
            bool dash = actions[5] > 0.5f;
            bool slide = actions[6] > 0.5f;
            bool firePrimary = actions[7] > 0.5f;
            bool fireSecondary = actions[8] > 0.5f;
            bool punch = actions[9] > 0.5f;
            bool whiplash = actions[16] > 0.5f;
            bool slam = actions[17] > 0.5f;
            bool swapVar = actions[18] > 0.5f;
            bool changeFist = actions[19] > 0.5f;

            // Check if any enemy is roughly in front (fire gate)
            bool enemyInFront = false;
            if (nearestEnemy != null)
            {
                Vector3 toE = nearestEnemy.transform.position - _player.transform.position;
                float eDist = toE.magnitude;
                if (eDist > 0.1f)
                {
                    float dot = Vector3.Dot(_player.transform.forward, toE / eDist);
                    // Allow fire if enemy within ~70° cone AND within 50m
                    // OR within melee range (5m, any angle — for punch)
                    enemyInFront = (dot > 0.3f && eDist < 50f) || eDist < 5f;
                }
            }

            // Store pending InputActionState — will be applied in ApplyPendingInputStates()
            // which runs from MonoBehaviour.Update() (same phase as game scripts).
            // Coroutines run AFTER Update, so setting PerformedFrame here would be 1 frame late.
            if (_inputSystemReady)
            {
                // No enemy gate — RL learns when to fire (needed for railcannon charge,
                // coin tosses, rocket riding, breakables). Wasted-shot penalty lives in reward.
                bool f1Allowed = firePrimary;
                bool f2Allowed = fireSecondary;

                _pJump = jump && !_prevButtons[4];
                _pDash = dash && !_prevButtons[5];
                _pSlide = slide;
                _pPunch = punch && !_prevButtons[9];
                _pFire1 = f1Allowed;
                _pFire2 = f2Allowed;
                // Whiplash via Hook InputActionState (HookArm has no ThrowHook method!)
                _pHook = whiplash && !_prevButtons[16];
                _pChangeFist = changeFist && !_prevButtons[19] && _changeFistCooldown <= 0;
                _pChangeFistReleased = !changeFist && _prevButtons[19];
                _hasPendingInputs = true;
            }

            // SLAM — trigger via Slide InputActionState while falling
            // The game handles slam internally when it sees Slide + player.falling
            // Slamdown(1f) is wrong — it just stops movement in air
            if (slam && !_prevButtons[17] && _player.falling)
            {
                _pSlide = true; // will be applied via Harmony prefix
            }

            // WEAPON SLOTS
            if (_gunControl != null && _weaponSwitchCooldown <= 0)
            {
                for (int slot = 0; slot < 6; slot++)
                {
                    if (actions[10 + slot] > 0.5f && !_prevButtons[10 + slot])
                    {
                        _gunControl.SwitchWeapon(slot, null, true, false, false);
                        _weaponSwitchCooldown = WeaponCD;
                        break;
                    }
                }

                if (swapVar && !_prevButtons[18] && _weaponSwitchCooldown <= 0)
                {
                    int nextVar = (_gunControl.currentVariationIndex + 1);
                    _gunControl.SwitchWeapon(
                        _gunControl.currentSlotIndex, nextVar,
                        false, false, true);
                    _weaponSwitchCooldown = WeaponCD;
                }
            }

            // Save button states for edge detection
            for (int i = 0; i < ActionSize; i++)
                _prevButtons[i] = actions[i] > 0.5f;
        }

        /// <summary>
        /// Apply pending InputActionState changes. Must be called from MonoBehaviour.Update()
        /// so PerformedFrame is set in the same phase where game scripts check WasPerformedThisFrame.
        /// </summary>
        public void ApplyPendingInputStates()
        {
            if (!_hasPendingInputs || !_inputSystemReady) return;
            _hasPendingInputs = false;

            // Get FRESH InputActionState each frame from InputManager.InputSource
            SetActionInput("Jump", _pJump);
            SetActionInput("Dodge", _pDash);
            SetActionInput("Slide", _pSlide);
            SetActionInput("Punch", _pPunch);
            SetActionInput("Fire1", _pFire1);
            SetActionInput("Fire2", _pFire2);
            SetActionInput("Hook", _pHook);

            if (_pChangeFist && _fistControl != null)
            {
                _fistControl.ForceArm(1, true);
                _changeFistCooldown = ChangeFistCD;
            }
            else if (_pChangeFistReleased && _fistControl != null)
            {
                _fistControl.ForceArm(0, true);
            }
        }

        public void ReleaseAll()
        {
            if (_inputSystemReady)
            {
                SetActionInput("Fire1", false);
                SetActionInput("Fire2", false);
                SetActionInput("Jump", false);
                SetActionInput("Dodge", false);
                SetActionInput("Slide", false);
                SetActionInput("Punch", false);
                SetActionInput("Hook", false);
                SetActionInput("ChangeFist", false);
            }

            _prevButtons = new bool[ActionSize];
            _prevActionStatesByName.Clear();
            _smoothYaw = 0f;
            _smoothPitch = 0f;

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
