using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UltrabotMod
{
    [BepInPlugin("com.ultrabot.mod", "Ultrabot RL Bridge", "0.1.0")]
    public class UltrabotPlugin : BaseUnityPlugin
    {
        public static UltrabotPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private TcpBridge _bridge;
        private GameStateReader _stateReader;
        private ActionExecutor _actionExecutor;
        private StyleTracker _styleTracker;
        private DebugHUD _hud;

        private bool _botActive = false;

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
                _styleTracker = new StyleTracker();
                _hud = new DebugHUD();
                _bridge = new TcpBridge(_stateReader, _actionExecutor, _styleTracker);
                _bridge.HUD = _hud;

                _bridge.StartListener();
                StartCoroutine(MainLoop());

                Log.LogError("[ULTRABOT] Plugin initialized. F7=HUD, F8=toggle, F9=stop");
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

                // Hotkeys
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

                yield return null;
            }
        }

        private void OnGUI()
        {
            _hud?.Draw();
        }

        private void OnDestroy()
        {
            _bridge?.Shutdown();
            _actionExecutor?.ReleaseAll();
        }
    }
}
