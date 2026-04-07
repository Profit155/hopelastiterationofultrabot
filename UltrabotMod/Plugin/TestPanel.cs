using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// In-game test panel for verifying all bot actions work correctly.
    /// Toggle with F6. Each button fires the corresponding action for a short burst.
    /// Green = action executed, Red = action failed/not available.
    /// </summary>
    public class TestPanel
    {
        private bool _visible = false;
        private GUIStyle _btnStyle;
        private GUIStyle _btnGreen;
        private GUIStyle _btnRed;
        private GUIStyle _btnActive;
        private GUIStyle _headerStyle;
        private GUIStyle _bgStyle;
        private GUIStyle _summaryStyle;
        private GUIStyle _failStyle;
        private GUIStyle _infoStyle;
        private bool _stylesReady;
        private Vector2 _scrollPos;

        // Input system refs
        private InputManager _inputManager;
        private PropertyInfo _inputSourceProp;
        private NewMovement _player;
        private GunControl _gunControl;
        private FistControl _fistControl;
        private CameraController _camera;
        private HookArm _hookArm;
        private FieldInfo _inputDirField;

        // InputActionState setters
        private MethodInfo _setIsPressed;
        private MethodInfo _setPerformedFrame;
        private MethodInfo _setCanceledFrame;

        // FieldInfo for each action (metadata only — get fresh state each frame)
        private Dictionary<string, FieldInfo> _actionFieldInfos = new Dictionary<string, FieldInfo>();
        // Names of all discovered actions (for UI)
        private HashSet<string> _knownActionNames = new HashSet<string>();
        private bool _inputReady;

        // Active test tracking
        private Dictionary<string, float> _activeTests = new Dictionary<string, float>();   // name -> end time
        private HashSet<string> _activeTestFirstFrameDone = new HashSet<string>(); // track if PerformedFrame was already set

        // One-shot actions: only set PerformedFrame on first frame, then just IsPressed
        // Held actions: set PerformedFrame every frame (for weapons that re-check)
        private static readonly HashSet<string> _heldActions = new HashSet<string>
            { "Fire1", "Fire2", "Slide" };
        private Dictionary<string, TestResult> _results = new Dictionary<string, TestResult>();
        private const float TestDuration = 0.5f; // seconds each test runs

        // Queued actions from OnGUI to apply in Update (correct timing for game input)
        private HashSet<string> _queuedPresses = new HashSet<string>();
        // Queued direct method calls from OnGUI
        private HashSet<string> _queuedDirectCalls = new HashSet<string>();

        private enum TestResult { None, Success, Failed }

        // Movement test state
        private string _activeMovement = null;
        private float _movementEndTime;

        // Look test state
        private string _activeLook = null;
        private float _lookEndTime;

        public void Toggle() => _visible = !_visible;
        public bool IsVisible => _visible;

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
            }

            SetupInputSystem();
        }

        private void SetupInputSystem()
        {
            if (_inputReady) return;

            try
            {
                _inputManager = UnityEngine.Object.FindObjectOfType<InputManager>();
                if (_inputManager == null) return;

                _inputSourceProp = typeof(InputManager).GetProperty("InputSource",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_inputSourceProp == null) return;

                var sampleInput = _inputSourceProp.GetValue(_inputManager);
                if (sampleInput == null) return;

                var piType = sampleInput.GetType();
                var stateType = typeof(InputActionState);

                _setIsPressed = stateType.GetMethod("set_IsPressed",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _setPerformedFrame = stateType.GetMethod("set_PerformedFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _setCanceledFrame = stateType.GetMethod("set_CanceledFrame",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (_setIsPressed == null || _setPerformedFrame == null) return;

                // Cache FieldInfo for all InputActionState fields (metadata only, NOT objects)
                _actionFieldInfos.Clear();
                _knownActionNames.Clear();
                var allFields = piType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var f in allFields)
                {
                    if (f.FieldType == stateType)
                    {
                        _actionFieldInfos[f.Name] = f;
                        _knownActionNames.Add(f.Name);
                    }
                }

                _inputReady = _actionFieldInfos.Count > 0;
                if (_inputReady)
                    UltrabotPlugin.Log.LogError($"[TESTPANEL] Input ready! {_actionFieldInfos.Count} actions found.");
            }
            catch (Exception e)
            {
                UltrabotPlugin.Log.LogError($"[TESTPANEL] Setup error: {e.Message}");
            }
        }

        /// <summary>
        /// Get FRESH InputActionState from current InputManager.InputSource.
        /// Must be called each frame — InputSource may return different objects.
        /// </summary>
        private InputActionState GetCurrentState(string name)
        {
            if (_inputManager == null || _inputSourceProp == null) return null;
            if (!_actionFieldInfos.TryGetValue(name, out var fieldInfo)) return null;
            var currentInput = _inputSourceProp.GetValue(_inputManager);
            if (currentInput == null) return null;
            return fieldInfo.GetValue(currentInput) as InputActionState;
        }

        /// <summary>
        /// Queue an InputActionState press — actual input is applied in Update()
        /// so it happens during the correct frame phase (not OnGUI which is too late).
        /// </summary>
        private void PressAction(string name)
        {
            if (!_knownActionNames.Contains(name))
            {
                _results[name] = TestResult.Failed;
                return;
            }

            _queuedPresses.Add(name);
            _activeTests[name] = Time.time + TestDuration;
            _results[name] = TestResult.Success;
        }

        private void ReleaseAction(string name)
        {
            var state = GetCurrentState(name);
            if (state == null) return;
            try
            {
                _setIsPressed.Invoke(state, new object[] { false });
                if (_setCanceledFrame != null)
                    _setCanceledFrame.Invoke(state, new object[] { Time.frameCount });
            }
            catch { }
        }

        /// <summary>
        /// Called from MonoBehaviour.Update() — runs in the same phase as game scripts.
        /// This is critical: InputActionState.PerformedFrame must be set BEFORE game
        /// scripts check WasPerformedThisFrame in their own Update().
        /// Coroutines run AFTER Update, so setting PerformedFrame there is always 1 frame late.
        /// </summary>
        public void EarlyUpdate()
        {
            if (!_visible) return;
            // Re-acquire references if player was destroyed (scene change, death)
            if (_player == null || _player.Equals(null))
                _inputReady = false;
            if (!_inputReady) RefreshReferences();

            // --- InputActionState presses (MUST be in Update phase) ---
            // Get FRESH state from InputManager.InputSource each frame!
            // InputSource is a property that may return different objects.

            // Process queued presses from OnGUI
            foreach (var name in _queuedPresses)
            {
                var state = GetCurrentState(name);
                if (state != null)
                {
                    try
                    {
                        _setIsPressed.Invoke(state, new object[] { true });
                        _setPerformedFrame.Invoke(state, new object[] { Time.frameCount });
                    }
                    catch { _results[name] = TestResult.Failed; }
                }
            }
            _queuedPresses.Clear();

            // Re-press active tests each frame
            foreach (var kv in _activeTests)
            {
                if (Time.time < kv.Value)
                {
                    var state = GetCurrentState(kv.Key);
                    if (state != null)
                    {
                        try
                        {
                            _setIsPressed.Invoke(state, new object[] { true });
                            // Only set PerformedFrame on first frame for one-shot actions
                            // (Jump, Dodge, Punch, etc.) to avoid spamming.
                            // Held actions (Fire1, Fire2, Slide) re-set every frame.
                            if (_heldActions.Contains(kv.Key) || !_activeTestFirstFrameDone.Contains(kv.Key))
                            {
                                _setPerformedFrame.Invoke(state, new object[] { Time.frameCount });
                                _activeTestFirstFrameDone.Add(kv.Key);
                            }
                        }
                        catch { }
                    }
                }
            }

            // Release expired action tests
            var toRemove = new List<string>();
            foreach (var kv in _activeTests)
            {
                if (Time.time >= kv.Value)
                {
                    ReleaseAction(kv.Key);
                    _activeTestFirstFrameDone.Remove(kv.Key);
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var key in toRemove)
                _activeTests.Remove(key);

            // --- Direct method calls (also need Update phase for some) ---
            foreach (var call in _queuedDirectCalls)
            {
                ExecuteDirectCall(call);
            }
            _queuedDirectCalls.Clear();
        }

        private void ExecuteDirectCall(string call)
        {
            try
            {
                switch (call)
                {
                    case "Slam":
                        // Slam = Slide while airborne. Game handles it internally.
                        if (_player != null && _player.falling)
                        {
                            PressAction("Slide");
                            _results[call] = TestResult.Success;
                        }
                        else if (_player != null)
                        {
                            _results[call] = TestResult.Failed;
                            UltrabotPlugin.Log.LogWarning("[TESTPANEL] Slam requires airborne (falling=true). Jump first!");
                        }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "Whiplash":
                        // HookArm has no ThrowHook method — uses Hook InputActionState
                        if (_knownActionNames.Contains("Hook"))
                        {
                            PressAction("Hook");
                            _results[call] = TestResult.Success;
                        }
                        else
                        {
                            _results[call] = TestResult.Failed;
                            UltrabotPlugin.Log.LogError("[TESTPANEL] Hook InputActionState not found");
                        }
                        break;
                    case "SwitchW1":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(0, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "SwitchW2":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(1, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "SwitchW3":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(2, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "SwitchW4":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(3, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "SwitchW5":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(4, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "SwitchW6":
                        if (_gunControl != null) { _gunControl.SwitchWeapon(5, null, true, false, false); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "ScrollArm":
                        if (_fistControl != null) { _fistControl.ScrollArm(); _results[call] = TestResult.Success; }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "Blue Fist":
                        if (_fistControl != null)
                        {
                            _fistControl.ForceArm(0, true);
                            // Also trigger punch so the fist actually attacks
                            PressAction("Punch");
                            _results[call] = TestResult.Success;
                        }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "Red Fist":
                        if (_fistControl != null)
                        {
                            _fistControl.ForceArm(1, true);
                            // Also trigger punch so the fist actually attacks
                            PressAction("Punch");
                            _results[call] = TestResult.Success;
                        }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "StopMove":
                        if (_player != null)
                        {
                            _player.StopMovement();
                            if (_player.rb != null) _player.rb.velocity = Vector3.zero;
                            _results[call] = TestResult.Success;
                        }
                        else _results[call] = TestResult.Failed;
                        break;
                    case "Respawn":
                        if (_player != null)
                        {
                            if (_player.dead)
                            {
                                _player.Respawn();
                                _results[call] = TestResult.Success;
                            }
                            else
                            {
                                // If alive, restart level instead
                                var sm = UnityEngine.Object.FindObjectOfType<StatsManager>();
                                if (sm != null) { sm.Restart(); _results[call] = TestResult.Success; }
                                else _results[call] = TestResult.Failed;
                            }
                        }
                        else _results[call] = TestResult.Failed;
                        break;
                }
            }
            catch (Exception e)
            {
                UltrabotPlugin.Log.LogError($"[TESTPANEL] Direct call '{call}' error: {e.Message}");
                _results[call] = TestResult.Failed;
            }
        }

        /// <summary>
        /// Called from coroutine — handles movement/look which are persistent state (not frame-sensitive).
        /// </summary>
        public void LateUpdate()
        {
            if (!_visible) return;

            // Movement test
            if (_activeMovement != null)
            {
                if (Time.time >= _movementEndTime)
                {
                    if (_inputDirField != null && _player != null)
                        _inputDirField.SetValue(_player, Vector3.zero);
                    _activeMovement = null;
                }
                else if (_inputDirField != null && _player != null)
                {
                    Vector3 dir = Vector3.zero;
                    var fwd = _player.transform.forward; fwd.y = 0; fwd.Normalize();
                    var right = _player.transform.right; right.y = 0; right.Normalize();
                    switch (_activeMovement)
                    {
                        case "Forward": dir = fwd; break;
                        case "Backward": dir = -fwd; break;
                        case "Left": dir = -right; break;
                        case "Right": dir = right; break;
                    }
                    _inputDirField.SetValue(_player, dir);
                }
            }

            // Look test — use Time.deltaTime for frame-rate independent rotation
            if (_activeLook != null && _camera != null)
            {
                if (Time.time >= _lookEndTime)
                {
                    _activeLook = null;
                }
                else
                {
                    // ~90°/sec — at 0.5s test duration = ~45° total rotation
                    float speed = 90f * Time.deltaTime;
                    switch (_activeLook)
                    {
                        // ULTRAKILL: rotationX = pitch (vertical), rotationY = yaw (horizontal)
                        case "Yaw Left": _camera.rotationY -= speed; break;
                        case "Yaw Right": _camera.rotationY += speed; break;
                        case "Pitch Up": _camera.rotationX = Mathf.Clamp(_camera.rotationX + speed, -80f, 80f); break;
                        case "Pitch Down": _camera.rotationX = Mathf.Clamp(_camera.rotationX - speed, -80f, 80f); break;
                    }
                }
            }
        }

        private void InitStyles()
        {
            if (_stylesReady) return;

            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            _btnStyle.normal.textColor = Color.white;
            _btnStyle.padding = new RectOffset(6, 6, 4, 4);

            _btnGreen = new GUIStyle(_btnStyle);
            var greenTex = MakeTex(new Color(0.1f, 0.5f, 0.1f, 0.9f));
            _btnGreen.normal.background = greenTex;
            _btnGreen.hover.background = greenTex;

            _btnRed = new GUIStyle(_btnStyle);
            var redTex = MakeTex(new Color(0.6f, 0.1f, 0.1f, 0.9f));
            _btnRed.normal.background = redTex;
            _btnRed.hover.background = redTex;

            _btnActive = new GUIStyle(_btnStyle);
            var yellowTex = MakeTex(new Color(0.7f, 0.6f, 0.0f, 0.9f));
            _btnActive.normal.background = yellowTex;
            _btnActive.hover.background = yellowTex;

            _headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _headerStyle.normal.textColor = Color.cyan;

            _bgStyle = new GUIStyle(GUI.skin.box);
            _bgStyle.normal.background = MakeTex(new Color(0, 0, 0, 0.85f));

            _summaryStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _summaryStyle.normal.textColor = Color.white;

            _failStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _failStyle.normal.textColor = Color.red;

            _infoStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _infoStyle.normal.textColor = Color.white;

            _stylesReady = true;
        }

        private Texture2D MakeTex(Color col)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, col);
            tex.Apply();
            return tex;
        }

        private GUIStyle GetButtonStyle(string name)
        {
            if (_activeTests.ContainsKey(name) || _activeMovement == name || _activeLook == name)
                return _btnActive;
            if (_results.TryGetValue(name, out var r))
            {
                if (r == TestResult.Success) return _btnGreen;
                if (r == TestResult.Failed) return _btnRed;
            }
            return _btnStyle;
        }

        public void Draw()
        {
            if (!_visible) return;
            InitStyles();

            float panelW = 420;
            float panelH = Screen.height - 40;
            float panelX = 10;
            float panelY = 20;

            GUI.Box(new Rect(panelX, panelY, panelW, panelH), "", _bgStyle);

            float contentH = 1400; // estimated content height
            _scrollPos = GUI.BeginScrollView(
                new Rect(panelX, panelY, panelW, panelH),
                _scrollPos,
                new Rect(0, 0, panelW - 20, contentH)
            );

            float y = 10;
            float x = 10;
            float bw = 90;
            float bh = 30;
            float gap = 5;
            float sectionW = panelW - 30;

            // Title
            GUI.Label(new Rect(x, y, sectionW, 25), "=== ACTION TEST PANEL (F6) ===", _headerStyle);
            y += 30;

            string status = _inputReady
                ? $"Input: READY ({_actionFieldInfos.Count} actions)"
                : "Input: NOT READY (enter a level)";
            GUI.Label(new Rect(x, y, sectionW, 20), status, _headerStyle);
            y += 25;

            // ---- MOVEMENT ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Movement (inputDir) ---", _headerStyle);
            y += 25;
            string[] moveDirs = { "Forward", "Backward", "Left", "Right" };
            float mx = x;
            foreach (var dir in moveDirs)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), dir, GetButtonStyle(dir)))
                {
                    if (_player != null && _inputDirField != null)
                    {
                        _activeMovement = dir;
                        _movementEndTime = Time.time + TestDuration;
                        _results[dir] = TestResult.Success;
                    }
                    else _results[dir] = TestResult.Failed;
                }
                mx += bw + gap;
            }
            y += bh + gap;

            // ---- LOOK ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Camera (rotationX/Y) ---", _headerStyle);
            y += 25;
            string[] lookDirs = { "Yaw Left", "Yaw Right", "Pitch Up", "Pitch Down" };
            mx = x;
            foreach (var dir in lookDirs)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), dir, GetButtonStyle(dir)))
                {
                    if (_camera != null)
                    {
                        _activeLook = dir;
                        _lookEndTime = Time.time + TestDuration;
                        _results[dir] = TestResult.Success;
                    }
                    else _results[dir] = TestResult.Failed;
                }
                mx += bw + gap;
            }
            y += bh + gap;

            // ---- INPUT ACTIONS (InputActionState) ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- InputActionState Actions ---", _headerStyle);
            y += 25;

            // Group actions logically
            string[][] actionGroups = new string[][]
            {
                new[] { "Jump", "Dodge", "Slide", "Punch" },
                new[] { "Fire1", "Fire2", "Hook", "ChangeFist" },
                new[] { "Slot1", "Slot2", "Slot3", "Slot4" },
                new[] { "Slot5", "Slot6", "LastWeapon" },
                new[] { "NextWeapon", "PrevWeapon" },
                new[] { "NextVariation", "PreviousVariation" },
                new[] { "SelectVariant1", "SelectVariant2", "SelectVariant3" },
            };

            foreach (var group in actionGroups)
            {
                mx = x;
                foreach (var action in group)
                {
                    if (GUI.Button(new Rect(mx, y, bw, bh), action, GetButtonStyle(action)))
                    {
                        PressAction(action);
                    }
                    mx += bw + gap;
                }
                y += bh + gap;
            }

            // ---- DIRECT METHOD CALLS ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Direct Methods ---", _headerStyle);
            y += 25;

            // All direct methods queued to Update() for correct timing
            string[] directRow1 = { "Slam", "Whiplash" };
            mx = x;
            foreach (var btn in directRow1)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), btn, GetButtonStyle(btn)))
                    _queuedDirectCalls.Add(btn);
                mx += bw + gap;
            }
            y += bh + gap;

            // Weapon slot switches
            string[] weaponRow1 = { "SwitchW1", "SwitchW2", "SwitchW3" };
            mx = x;
            foreach (var btn in weaponRow1)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), btn, GetButtonStyle(btn)))
                    _queuedDirectCalls.Add(btn);
                mx += bw + gap;
            }
            y += bh + gap;

            string[] weaponRow2 = { "SwitchW4", "SwitchW5", "SwitchW6" };
            mx = x;
            foreach (var btn in weaponRow2)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), btn, GetButtonStyle(btn)))
                    _queuedDirectCalls.Add(btn);
                mx += bw + gap;
            }
            y += bh + gap;

            string[] directRow2 = { "ScrollArm", "Blue Fist", "Red Fist" };
            mx = x;
            foreach (var btn in directRow2)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), btn, GetButtonStyle(btn)))
                    _queuedDirectCalls.Add(btn);
                mx += bw + gap;
            }
            y += bh + gap;

            string[] directRow3 = { "StopMove", "Respawn" };
            mx = x;
            foreach (var btn in directRow3)
            {
                if (GUI.Button(new Rect(mx, y, bw, bh), btn, GetButtonStyle(btn)))
                    _queuedDirectCalls.Add(btn);
                mx += bw + gap;
            }

            y += bh + gap;

            // ---- RUN ALL TEST ----
            y += 10;
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Batch Test ---", _headerStyle);
            y += 25;

            if (GUI.Button(new Rect(x, y, 180, 35), "TEST ALL INPUTS", _btnStyle))
            {
                RunAllInputTests();
            }

            if (GUI.Button(new Rect(x + 190, y, 100, 35), "CLEAR", _btnStyle))
            {
                _results.Clear();
                _activeTests.Clear();
                _activeTestFirstFrameDone.Clear();
                _activeMovement = null;
                _activeLook = null;
            }
            y += 40;

            // ---- RESULTS SUMMARY ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Results ---", _headerStyle);
            y += 25;

            int passed = 0, failed = 0, untested = 0;
            foreach (var kv in _results)
            {
                if (kv.Value == TestResult.Success) passed++;
                else if (kv.Value == TestResult.Failed) failed++;
            }
            // Count untested InputActionStates
            foreach (var name in _knownActionNames)
            {
                if (!_results.ContainsKey(name)) untested++;
            }

            var summaryStyle = _summaryStyle;
            GUI.Label(new Rect(x, y, sectionW, 20),
                $"Passed: {passed}  |  Failed: {failed}  |  Untested: {untested}", summaryStyle);
            y += 22;

            // List failed ones
            foreach (var kv in _results)
            {
                if (kv.Value == TestResult.Failed)
                {
                    var failStyle = _failStyle;
                    GUI.Label(new Rect(x + 10, y, sectionW, 18), $"FAIL: {kv.Key}", failStyle);
                    y += 18;
                }
            }

            y += 20;

            // ---- STATE INFO ----
            GUI.Label(new Rect(x, y, sectionW, 22), "--- Player State ---", _headerStyle);
            y += 25;
            var infoStyle = _infoStyle;

            if (_player != null)
            {
                GUI.Label(new Rect(x, y, sectionW, 18), $"HP: {_player.hp}  Dead: {_player.dead}  Activated: {_player.activated}", infoStyle);
                y += 18;
                GUI.Label(new Rect(x, y, sectionW, 18), $"OnGround: {(_player.gc != null && _player.gc.onGround)}  Sliding: {_player.sliding}  Falling: {_player.falling}", infoStyle);
                y += 18;
                GUI.Label(new Rect(x, y, sectionW, 18), $"BoostCharge: {_player.boostCharge:F0}  Jumping: {_player.jumping}", infoStyle);
                y += 18;
                GUI.Label(new Rect(x, y, sectionW, 18), $"Pos: {_player.transform.position:F1}", infoStyle);
                y += 18;
                GUI.Label(new Rect(x, y, sectionW, 18), $"Vel: {_player.rb.velocity:F1}  Speed: {_player.rb.velocity.magnitude:F1}", infoStyle);
                y += 18;
            }
            else
            {
                GUI.Label(new Rect(x, y, sectionW, 18), "Player: NOT FOUND (enter a level)", infoStyle);
                y += 18;
            }

            if (_camera != null)
            {
                // ULTRAKILL: rotationX = pitch, rotationY = yaw
                GUI.Label(new Rect(x, y, sectionW, 18), $"CamYaw(rotY): {_camera.rotationY:F1}  CamPitch(rotX): {_camera.rotationX:F1}", infoStyle);
                y += 18;
            }

            if (_gunControl != null)
            {
                int slotCount = _gunControl.slots != null ? _gunControl.slots.Count : 0;
                int totalWeapons = _gunControl.allWeapons != null ? _gunControl.allWeapons.Count : 0;
                GUI.Label(new Rect(x, y, sectionW, 18), $"Weapon: slot{_gunControl.currentSlotIndex} var{_gunControl.currentVariationIndex}  Slots:{slotCount} Total:{totalWeapons}", infoStyle);
                y += 18;
            }

            if (_fistControl != null)
            {
                string fistType = _fistControl.currentPunch != null ? _fistControl.currentPunch.type.ToString() : "null";
                bool fistReady = _fistControl.currentPunch != null && _fistControl.currentPunch.ready;
                GUI.Label(new Rect(x, y, sectionW, 18), $"Fist: {fistType}  Ready: {fistReady}  CD: {_fistControl.fistCooldown:F2}", infoStyle);
                y += 18;
            }

            if (_hookArm != null)
            {
                GUI.Label(new Rect(x, y, sectionW, 18), $"HookArm equipped: {_hookArm.equipped}", infoStyle);
                y += 18;
            }

            GUI.EndScrollView();
        }

        private void RunAllInputTests()
        {
            if (!_inputReady)
            {
                RefreshReferences();
                if (!_inputReady) return;
            }

            // Test all InputActionState fields (press and schedule release)
            // Skip Move, Look, WheelLook, Pause, Stats — these are analog or UI
            string[] skipActions = { "Move", "Look", "WheelLook", "Pause", "Stats" };
            var skipSet = new HashSet<string>(skipActions);

            foreach (var name in _knownActionNames)
            {
                if (skipSet.Contains(name)) continue;
                PressAction(name);
            }

            // Test movement
            if (_player != null && _inputDirField != null)
            {
                _activeMovement = "Forward";
                _movementEndTime = Time.time + TestDuration;
                _results["Forward"] = TestResult.Success;
            }

            // Test look
            if (_camera != null)
            {
                _activeLook = "Yaw Right";
                _lookEndTime = Time.time + TestDuration;
                _results["Yaw Right"] = TestResult.Success;
            }
        }
    }
}
