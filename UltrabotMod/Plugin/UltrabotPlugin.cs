using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UltrabotMod
{
    [BepInPlugin("com.ultrabot.mod", "Ultrabot RL Bridge", "0.1.0")]
    [DefaultExecutionOrder(-32000)] // Run BEFORE all game scripts so InputActionState is set before they check it
    public class UltrabotPlugin : BaseUnityPlugin
    {
        public static UltrabotPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private TcpBridge _bridge;
        private GameStateReader _stateReader;
        private ActionExecutor _actionExecutor;
        private StyleTracker _styleTracker;
        private DebugHUD _hud;
        private TestPanel _testPanel;
        private BotSelfTest _selfTest;

        private bool _botActive = false;

        /// <summary>
        /// MonoBehaviour.Update() — runs in the same phase as game scripts.
        /// InputActionState must be set HERE so PerformedFrame == Time.frameCount
        /// when game scripts check WasPerformedThisFrame in their Update().
        /// Coroutines run AFTER Update, so they're always 1 frame late for input.
        /// </summary>
        private void Update()
        {
            // Wire up InputInjector — Harmony prefixes on game Update() methods
            // will call this BEFORE the game reads input, guaranteeing correct timing.
            InputInjector.ApplyInputs = ApplyAllInputs;

            // Also apply here as fallback (for scripts without Harmony prefix)
            InputInjector.TryApply();

            // Hotkeys (moved here from coroutine for consistent timing)
            if (Input.GetKeyDown(KeyCode.F5))
                _selfTest?.Toggle();

            if (Input.GetKeyDown(KeyCode.F6))
                _testPanel?.Toggle();

            if (Input.GetKeyDown(KeyCode.F7))
                _hud?.Toggle();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                _botActive = !_botActive;
                if (!_botActive)
                    _actionExecutor.ReleaseAll();
                Log.LogError($"[ULTRABOT] Bot active: {_botActive}");
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                _botActive = false;
                _actionExecutor.ReleaseAll();
                Time.timeScale = 1f;
                Log.LogError("[ULTRABOT] Emergency stop!");
            }
        }

        /// <summary>
        /// Called by Harmony prefix patches on game Update() methods.
        /// Applies all pending InputActionState changes BEFORE the game reads them.
        /// </summary>
        private void ApplyAllInputs()
        {
            _actionExecutor?.ApplyPendingInputStates();
            _testPanel?.EarlyUpdate();
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            DontDestroyOnLoad(gameObject);

            try
            {
                var harmony = new Harmony("com.ultrabot.mod");
                harmony.PatchAll();
                Log.LogError("[ULTRABOT] Harmony patches OK.");
            }
            catch (Exception e)
            {
                Log.LogError($"[ULTRABOT] Harmony FAILED: {e}");
            }

            try
            {
                _stateReader = new GameStateReader();
                _actionExecutor = new ActionExecutor();
                _stateReader.SetActionExecutor(_actionExecutor);
                _styleTracker = new StyleTracker();
                _hud = new DebugHUD();
                _testPanel = new TestPanel();
                _selfTest = new BotSelfTest(_actionExecutor);
                _bridge = new TcpBridge(_stateReader, _actionExecutor, _styleTracker);
                _bridge.HUD = _hud;

                _bridge.StartListener();
                StartCoroutine(MainLoop());

                Log.LogError("[ULTRABOT] Plugin initialized. F5=bot self-test, F6=test panel, F7=HUD, F8=toggle, F9=stop");
            }
            catch (Exception e)
            {
                Log.LogError($"[ULTRABOT] Init FAILED: {e}");
            }
        }

        private IEnumerator MainLoop()
        {
            Log.LogError("[ULTRABOT] MainLoop coroutine running!");
            int frameCount = 0;

            while (true)
            {
                frameCount++;
                try
                {
                    _bridge.ProcessPendingReset();
                    _bridge.ProcessMessages();
                }
                catch (Exception e)
                {
                    Log.LogError($"[ULTRABOT] ProcessMessages error: {e.Message}\n{e.StackTrace}");
                }

                // Heartbeat every 300 frames
                if (frameCount % 300 == 0)
                {
                    Log.LogError($"[ULTRABOT] heartbeat frame={frameCount} listener={_bridge.IsListening} connected={_bridge.IsConnected}");
                }

                // Test panel movement/look (persistent state, ok in coroutine)
                _testPanel?.LateUpdate();

                // F5 bot self-test sequencer
                try { _selfTest?.Tick(); }
                catch (Exception e) { Log.LogError($"[ULTRABOT] SelfTest error: {e}"); }

                yield return null;
            }
        }

        private void OnGUI()
        {
            _hud?.Draw();
            _testPanel?.Draw();
            _selfTest?.Draw();
        }

        private void OnDestroy()
        {
            _bridge?.Shutdown();
            _actionExecutor?.ReleaseAll();
        }
    }
}
