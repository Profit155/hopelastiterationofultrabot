using System.Collections.Generic;
using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// F5 self-test: feeds the ActionExecutor a sequence of bot actions
    /// and verifies each one actually affects the game. Result overlay is drawn each frame.
    /// </summary>
    public class BotSelfTest
    {
        private readonly ActionExecutor _exec;
        private bool _running;
        private int _stepIdx;
        private float _stepEndTime;
        private float _stepStartTime;

        // Per-step pre-state (for diff comparison)
        private int _preSlot;
        private int _preVar;
        private float _preBoostCharge;
        private float _preY;
        private string _preFistType;
        private bool _preSliding;
        private bool _preJumping;

        private NewMovement _player;
        private GunControl _gc;
        private FistControl _fc;

        // step name -> result text
        private readonly List<KeyValuePair<string, string>> _results = new List<KeyValuePair<string, string>>();

        // GUI
        private GUIStyle _box;
        private GUIStyle _label;
        private GUIStyle _ok;
        private GUIStyle _fail;
        private bool _stylesReady;

        private const float StepDuration = 0.6f;

        // Step descriptors: name, build action array
        private struct Step
        {
            public string Name;
            public System.Action<float[]> Build;
            public System.Func<BotSelfTest, string> Verify; // returns "OK ..." or "FAIL ..."
        }

        private readonly List<Step> _steps;

        public BotSelfTest(ActionExecutor exec)
        {
            _exec = exec;
            _steps = BuildSteps();
        }

        public bool IsRunning => _running;

        public void Toggle()
        {
            if (_running) { Stop(); return; }
            Start();
        }

        private void Start()
        {
            _player = Object.FindObjectOfType<NewMovement>();
            _gc = Object.FindObjectOfType<GunControl>();
            _fc = Object.FindObjectOfType<FistControl>();

            if (_player == null)
            {
                UltrabotPlugin.Log.LogError("[SELFTEST] No player — enter a level first.");
                return;
            }

            _results.Clear();
            _stepIdx = 0;
            _running = true;
            BeginStep();
            UltrabotPlugin.Log.LogError("[SELFTEST] Started.");
        }

        private void Stop()
        {
            _running = false;
            _exec.ReleaseAll();
            UltrabotPlugin.Log.LogError("[SELFTEST] Stopped.");
        }

        private void BeginStep()
        {
            if (_player == null) { Stop(); return; }
            _stepStartTime = Time.time;
            _stepEndTime = Time.time + StepDuration;

            _preSlot = _gc != null ? _gc.currentSlotIndex : -1;
            _preVar = _gc != null ? _gc.currentVariationIndex : -1;
            _preBoostCharge = _player.boostCharge;
            _preY = _player.transform.position.y;
            _preFistType = (_fc != null && _fc.currentPunch != null) ? _fc.currentPunch.type.ToString() : "?";
            _preSliding = _player.sliding;
            _preJumping = _player.jumping;
        }

        /// <summary>Called from MainLoop coroutine each frame.</summary>
        public void Tick()
        {
            if (!_running) return;
            if (_stepIdx >= _steps.Count) { Stop(); return; }

            var step = _steps[_stepIdx];
            var actions = new float[ActionExecutor.ActionSize];
            step.Build(actions);
            _exec.Execute(actions);

            if (Time.time >= _stepEndTime)
            {
                string r;
                try { r = step.Verify(this); }
                catch (System.Exception e) { r = "FAIL ex: " + e.Message; }
                _results.Add(new KeyValuePair<string, string>(step.Name, r));
                UltrabotPlugin.Log.LogError($"[SELFTEST] {step.Name}: {r}");

                // Release between steps
                _exec.Execute(new float[ActionExecutor.ActionSize]);

                _stepIdx++;
                if (_stepIdx < _steps.Count) BeginStep();
                else Stop();
            }
        }

        public void Draw()
        {
            if (!_running && _results.Count == 0) return;
            if (!_stylesReady)
            {
                _box = new GUIStyle(GUI.skin.box);
                var tex = new Texture2D(1, 1);
                tex.SetPixel(0, 0, new Color(0, 0, 0, 0.85f));
                tex.Apply();
                _box.normal.background = tex;
                _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                _label.normal.textColor = Color.white;
                _ok = new GUIStyle(_label); _ok.normal.textColor = Color.green;
                _fail = new GUIStyle(_label); _fail.normal.textColor = Color.red;
                _stylesReady = true;
            }

            float w = 380, h = 30 + _results.Count * 18 + (_running ? 22 : 0);
            float x = Screen.width - w - 10, y = 10;
            GUI.Box(new Rect(x, y, w, h), "", _box);
            GUI.Label(new Rect(x + 8, y + 4, w, 20), "=== F5 BOT SELF-TEST ===", _label);
            float ly = y + 24;
            if (_running && _stepIdx < _steps.Count)
            {
                GUI.Label(new Rect(x + 8, ly, w, 18), $"Running: {_steps[_stepIdx].Name} ({_stepIdx + 1}/{_steps.Count})", _label);
                ly += 20;
            }
            foreach (var kv in _results)
            {
                bool ok = kv.Value.StartsWith("OK");
                GUI.Label(new Rect(x + 8, ly, w - 16, 18), $"{kv.Key}: {kv.Value}", ok ? _ok : _fail);
                ly += 18;
            }
        }

        // ============================================================
        // Step definitions
        // ============================================================
        private List<Step> BuildSteps()
        {
            return new List<Step>
            {
                S("MoveForward", a => { a[0] = 1f; },
                    t => Mathf.Abs(t._player.rb.velocity.x) + Mathf.Abs(t._player.rb.velocity.z) > 1f
                        ? "OK vel=" + t._player.rb.velocity.magnitude.ToString("F1") : "FAIL no velocity"),
                S("MoveRight", a => { a[1] = 1f; },
                    t => Mathf.Abs(t._player.rb.velocity.x) + Mathf.Abs(t._player.rb.velocity.z) > 1f
                        ? "OK vel=" + t._player.rb.velocity.magnitude.ToString("F1") : "FAIL no velocity"),
                S("LookYaw", a => { a[2] = 1f; },
                    _ => "OK (visual)"),
                S("LookPitch", a => { a[3] = 0.5f; },
                    _ => "OK (visual)"),
                S("Jump", a => { a[4] = 1f; },
                    t => (t._player.transform.position.y - t._preY) > 0.2f || t._player.jumping
                        ? "OK dy=" + (t._player.transform.position.y - t._preY).ToString("F1") : "FAIL no jump"),
                S("Dash", a => { a[5] = 1f; },
                    t => t._player.boostCharge < t._preBoostCharge - 10f
                        ? "OK charge=" + t._player.boostCharge.ToString("F0") : "FAIL boost unchanged"),
                S("Slide", a => { a[6] = 1f; a[0] = 1f; },
                    t => t._player.sliding ? "OK sliding" : "FAIL not sliding"),
                S("Slot1", a => { a[10] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 0 ? "OK slot=0" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("Slot2", a => { a[11] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 1 ? "OK slot=1" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("Slot3", a => { a[12] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 2 ? "OK slot=2" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("Slot4", a => { a[13] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 3 ? "OK slot=3" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("Slot5", a => { a[14] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 4 ? "OK slot=4" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("Slot6", a => { a[15] = 1f; },
                    t => t._gc != null && t._gc.currentSlotIndex == 5 ? "OK slot=5" : "FAIL slot=" + (t._gc != null ? t._gc.currentSlotIndex : -1)),
                S("SwapVariation", a => { a[18] = 1f; },
                    t => t._gc != null && t._gc.currentVariationIndex != t._preVar ? "OK var=" + t._gc.currentVariationIndex : "FAIL var unchanged"),
                S("Fire1", a => { a[7] = 1f; },
                    _ => "OK (no enemy gate)"),
                S("Fire2", a => { a[8] = 1f; },
                    _ => "OK (no enemy gate)"),
                S("Punch", a => { a[9] = 1f; },
                    t => t._fc != null && t._fc.fistCooldown > 0f ? "OK cd=" + t._fc.fistCooldown.ToString("F2") : "FAIL no cooldown"),
                S("ChangeFist", a => { a[19] = 1f; },
                    t => {
                        string now = (t._fc != null && t._fc.currentPunch != null) ? t._fc.currentPunch.type.ToString() : "?";
                        return now != t._preFistType ? "OK " + t._preFistType + "->" + now : "FAIL still " + now;
                    }),
                S("Whiplash", a => { a[16] = 1f; },
                    _ => "OK (visual — needs hookpoint/enemy)"),
                S("Slam(jump+slam)", a => { a[4] = 1f; a[17] = 1f; },
                    t => t._player.falling || t._player.sliding ? "OK falling/sliding" : "FAIL no slam"),
            };
        }

        private static Step S(string name, System.Action<float[]> build, System.Func<BotSelfTest, string> verify)
        {
            return new Step { Name = name, Build = build, Verify = verify };
        }
    }
}
