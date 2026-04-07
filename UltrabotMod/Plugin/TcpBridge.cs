using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace UltrabotMod
{
    public class TcpBridge
    {
        private const int Port = 7865;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private bool _running = true;
        private bool _listenerStarted = false;
        public bool IsListening => _listenerStarted && _listener != null && _listener.Server.IsBound;
        public bool IsConnected => _client != null && _client.Connected;

        private readonly GameStateReader _stateReader;
        private readonly ActionExecutor _actionExecutor;
        private readonly StyleTracker _styleTracker;

        // Debug HUD reference
        public DebugHUD HUD;

        // Previous reward state
        private int _prevStyleScore;
        private int _prevHp;
        private int _prevKills;
        private int _prevRankIndex;

        // Exploration tracking
        private Vector3 _spawnPos;
        private bool _spawnPosLocked; // wait for player to land before locking spawn pos
        private float _maxDistFromSpawn;
        private int _totalSteps;
        private int _episodeSteps;
        private float _cumulativeReward;

        // Reset synchronization
        private bool _resetPending;
        private int _resetWaitFrames;
        private const int ResetWaitFrameCount = 3;       // minimum wait before checking readiness
        private const int ResetTimeoutFrameCount = 600;  // hard timeout (~10s @60fps)
        private int _resetElapsedFrames;

        // Death state caching — capture stats before game wipes them
        private bool _deathHandled;
        private int _deathRankIndex;

        // Cumulative episode stats for HUD
        private int _totalKills;
        private int _totalParries;
        private int _totalHeadshots;
        private int _totalDamageTaken;
        private int _totalDeaths;

        // Track last exploration distance for HUD
        private float _lastExploreDist;

        public TcpBridge(GameStateReader stateReader, ActionExecutor actionExecutor, StyleTracker styleTracker)
        {
            _stateReader = stateReader;
            _actionExecutor = actionExecutor;
            _styleTracker = styleTracker;
        }

        public void StartListener()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), Port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(1);
                _listenerStarted = true;
                var ep = (IPEndPoint)_listener.LocalEndpoint;
                UltrabotPlugin.Log.LogError($"[ULTRABOT] TCP LISTENING on {ep.Address}:{ep.Port} active={_listener.Server.IsBound}");
            }
            catch (Exception e)
            {
                UltrabotPlugin.Log.LogError($"[ULTRABOT] TCP bind FAILED: {e}");
                _running = false;
            }
        }

        public void ProcessMessages()
        {
            if (!_running || !_listenerStarted || _resetPending) return;

            if (_client == null || !_client.Connected)
            {
                try
                {
                    if (_listener.Pending())
                    {
                        _stream?.Close();
                        _client?.Close();
                        _client = _listener.AcceptTcpClient();
                        _client.NoDelay = true;
                        _client.ReceiveTimeout = 5000;
                        _stream = _client.GetStream();
                        ResetTrackingState();
                        UltrabotPlugin.Log.LogError("[ULTRABOT] Python agent connected!");
                    }
                }
                catch (Exception e)
                {
                    UltrabotPlugin.Log.LogWarning($"TCP accept error: {e.Message}");
                }
                return;
            }

            try
            {
                if (_stream == null || !_stream.DataAvailable) return;

                byte[] header = ReadExact(4);
                if (header == null)
                {
                    DisconnectClient();
                    return;
                }
                int msgType = BitConverter.ToInt32(header, 0);

                switch (msgType)
                {
                    case 0: HandleStep(); break;
                    case 1: HandleReset(); break;
                    case 2: HandleClose(); break;
                    case 3: HandleSetSpeed(); break;
                    case 4: HandleGetInfo(); break;
                }
            }
            catch (Exception e)
            {
                UltrabotPlugin.Log.LogError($"[ULTRABOT] ProcessMessages error: {e.Message}\n{e.StackTrace}");
                DisconnectClient();
            }
        }

        private void HandleStep()
        {
            byte[] actionBytes = ReadExact(ActionExecutor.ActionSize * 4);
            if (actionBytes == null) return;

            float[] actions = new float[ActionExecutor.ActionSize];
            Buffer.BlockCopy(actionBytes, 0, actions, 0, actionBytes.Length);

            _actionExecutor.Execute(actions);

            var events = _styleTracker.ConsumeEvents();
            float[] obs = _stateReader.GetObservation();
            var info = _stateReader.GetRewardInfo();

            // Cache death state — only trigger penalty once
            bool isFirstDeathFrame = info.Dead && !_deathHandled;
            if (isFirstDeathFrame)
            {
                _deathHandled = true;
                _deathRankIndex = info.RankIndex;
            }

            // Wasted shot detection: fire pressed but no enemy in front cone (~60°, <60m)
            bool firePressed = actions[7] > 0.5f || actions[8] > 0.5f;
            bool wastedShot = false;
            if (firePressed)
            {
                var pl = UnityEngine.Object.FindObjectOfType<NewMovement>();
                if (pl != null)
                {
                    bool anyEnemyInFront = false;
                    var enemiesW = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
                    foreach (var e in enemiesW)
                    {
                        if (e.dead || e.health <= 0) continue;
                        Vector3 to = e.transform.position - pl.transform.position;
                        float d = to.magnitude;
                        if (d < 0.1f || d > 60f) continue;
                        float dot = Vector3.Dot(pl.transform.forward, to / d);
                        if (dot > 0.5f) { anyEnemyInFront = true; break; }
                    }
                    // Wasted only if NO style/kill happened this step either (charging railcannon → style soon)
                    wastedShot = !anyEnemyInFront && events.KillsThisStep == 0 && events.StylePointsGained <= 0;
                }
            }

            float reward = CalculateReward(info, events, isFirstDeathFrame, wastedShot);

            _prevStyleScore = info.StyleScore;
            _prevHp = info.Hp;
            _prevKills = info.Kills;
            _prevRankIndex = info.RankIndex;

            _totalSteps++;
            _episodeSteps++;
            _cumulativeReward += reward;

            // Update HUD
            if (HUD != null)
            {
                HUD.Connected = true;
                HUD.LastReward = reward;
                HUD.CumulativeReward = _cumulativeReward;
                HUD.TotalSteps = _totalSteps;
                HUD.EpisodeSteps = _episodeSteps;
                HUD.Kills = _totalKills;
                HUD.StyleScore = info.StyleScore;
                HUD.RankIndex = info.RankIndex;
                HUD.Parries = _totalParries;
                HUD.Headshots = _totalHeadshots;
                HUD.DamageTaken = _totalDamageTaken;
                HUD.Deaths = _totalDeaths;
                HUD.ExplorationDist = _lastExploreDist;
                HUD.MaxExplorationDist = _maxDistFromSpawn;
                HUD.Hp = info.Hp;
            }

            SendStepResponse(obs, reward, info.Dead, info);
        }

        private void HandleReset()
        {
            _actionExecutor.ReleaseAll();
            _actionExecutor.ResetLevel();
            _styleTracker.Reset();
            _deathHandled = false;

            _resetPending = true;
            _resetWaitFrames = ResetWaitFrameCount;
            _resetElapsedFrames = 0;
        }

        public void ProcessPendingReset()
        {
            if (!_resetPending) return;
            if (_stream == null) { _resetPending = false; return; }

            _resetElapsedFrames++;
            _resetWaitFrames--;
            if (_resetWaitFrames > 0) return;

            // Readiness gate: refresh refs each tick and require player alive + reader ready.
            // Hard timeout prevents indefinite hang on broken loads.
            _stateReader.RefreshReferences();
            var probe = UnityEngine.Object.FindObjectOfType<NewMovement>();
            bool ready = probe != null && !probe.dead && _stateReader.IsReady;
            if (!ready && _resetElapsedFrames < ResetTimeoutFrameCount)
                return; // try again next frame
            if (!ready)
                UltrabotPlugin.Log.LogError($"[ULTRABOT] Reset timeout after {_resetElapsedFrames} frames — sending obs anyway");

            var player = UnityEngine.Object.FindObjectOfType<NewMovement>();
            if (player != null)
                _spawnPos = player.transform.position;
            _spawnPosLocked = false; // will lock when player touches ground
            _maxDistFromSpawn = 0f;
            _episodeSteps = 0;
            _cumulativeReward = 0f;
            _totalKills = 0;
            _totalParries = 0;
            _totalHeadshots = 0;
            _totalDamageTaken = 0;
            _totalDeaths = 0;

            var info = _stateReader.GetRewardInfo();
            _prevStyleScore = info.StyleScore;
            _prevHp = info.Hp;
            _prevKills = info.Kills;
            _prevRankIndex = info.RankIndex;

            float[] obs = _stateReader.GetObservation();
            try
            {
                SendObservation(obs);
            }
            catch (Exception e)
            {
                UltrabotPlugin.Log.LogError($"[ULTRABOT] Reset send failed: {e.Message}");
                DisconnectClient();
            }
            _resetPending = false;
        }

        private void HandleClose()
        {
            _actionExecutor.ReleaseAll();
            _actionExecutor.SetTimeScale(1f);
            if (HUD != null) HUD.Connected = false;
            DisconnectClient();
        }

        private void HandleSetSpeed()
        {
            byte[] data = ReadExact(4);
            if (data == null) return;
            float speed = BitConverter.ToSingle(data, 0);
            _actionExecutor.SetTimeScale(speed);
            SendInt(1);
        }

        private void HandleGetInfo()
        {
            string info = _stateReader.GetLevelInfo();
            byte[] strBytes = Encoding.UTF8.GetBytes(info);
            byte[] lenBytes = BitConverter.GetBytes(strBytes.Length);
            _stream.Write(lenBytes, 0, 4);
            _stream.Write(strBytes, 0, strBytes.Length);
        }

        private float CalculateReward(RewardInfo info, StepEvents events, bool isFirstDeathFrame, bool wastedShot)
        {
            float reward = 0f;

            // === Combat rewards (dominant) ===
            float rewStyle = events.StylePointsGained * 0.02f;
            float rewKills = events.KillsThisStep * 5.0f;
            float rewParry = events.ParriesThisStep * 5.0f;
            float rewHeadshot = events.HeadshotsThisStep * 2.0f;
            float rewDamage = events.DamageTakenThisStep * -0.3f;

            // Death penalty — only once, scales with rank achieved
            float rewDeath = 0f;
            if (isFirstDeathFrame)
            {
                rewDeath = -5f - (_deathRankIndex * 2f);
            }

            // Rank change
            int rankDelta = info.RankIndex - _prevRankIndex;
            float rewRank = 0f;
            if (rankDelta > 0) rewRank = rankDelta * 1.0f;
            else if (rankDelta < 0) rewRank = rankDelta * 0.5f;

            // Multikill
            if (events.MultikillCount > 1)
                rewKills += (events.MultikillCount - 1) * 0.5f;

            // === Exploration reward (reduced — combat should dominate) ===
            float rewExplore = 0f;
            float rewHeight = 0f;
            var player = UnityEngine.Object.FindObjectOfType<NewMovement>();
            if (player != null)
            {
                // Lock spawn position when player first touches ground (after lift/fall)
                if (!_spawnPosLocked && player.gc != null && player.gc.onGround)
                {
                    _spawnPos = player.transform.position;
                    _spawnPosLocked = true;
                    _maxDistFromSpawn = 0f;
                }

                if (_spawnPosLocked)
                {
                    Vector3 delta = player.transform.position - _spawnPos;
                    delta.y = 0;
                    float dist = delta.magnitude;
                    _lastExploreDist = dist;
                    if (dist > _maxDistFromSpawn + 1f)
                    {
                        rewExplore = (dist - _maxDistFromSpawn) * 0.01f;
                        _maxDistFromSpawn = dist;
                    }

                    float heightAboveSpawn = player.transform.position.y - _spawnPos.y;
                    if (heightAboveSpawn > 10f)
                        rewHeight = -0.01f * (heightAboveSpawn - 10f);
                }
            }

            // === Facing enemy bonus — encourage looking at enemies ===
            float rewFacing = 0f;
            var enemies = UnityEngine.Object.FindObjectsOfType<EnemyIdentifier>();
            if (player != null)
            {
                float bestDot = -1f;
                foreach (var e in enemies)
                {
                    if (e.dead || e.health <= 0) continue;
                    Vector3 toEnemy = e.transform.position - player.transform.position;
                    float eDist = toEnemy.magnitude;
                    if (eDist > 30f || eDist < 0.1f) continue;
                    float dot = Vector3.Dot(player.transform.forward, toEnemy / eDist);
                    if (dot > bestDot) bestDot = dot;
                }
                if (bestDot > 0.5f)
                    rewFacing = 0.001f;
            }

            // === Sum up ===
            float rewStepCost = -0.002f;
            float rewWastedShot = wastedShot ? -0.05f : 0f;
            reward = rewStyle + rewKills + rewParry + rewHeadshot + rewRank
                   + rewDamage + rewDeath + rewExplore + rewHeight + rewFacing + rewStepCost + rewWastedShot;

            // Update cumulative stats
            _totalKills += events.KillsThisStep;
            _totalParries += events.ParriesThisStep;
            _totalHeadshots += events.HeadshotsThisStep;
            _totalDamageTaken += events.DamageTakenThisStep;
            if (info.Dead) _totalDeaths++;

            // Update HUD breakdown
            if (HUD != null)
            {
                HUD.RewStyle = rewStyle;
                HUD.RewKills = rewKills;
                HUD.RewParry = rewParry;
                HUD.RewHeadshot = rewHeadshot;
                HUD.RewRank = rewRank;
                HUD.RewDamage = rewDamage;
                HUD.RewDeath = rewDeath;
                HUD.RewExplore = rewExplore;
                HUD.RewHeight = rewHeight;
                HUD.RewFacing = rewFacing;
                HUD.RewStepCost = rewStepCost;
                HUD.NavAgentActive = _actionExecutor.IsNavAgentActive;
                HUD.NavAgentDist = _actionExecutor.NavTargetDistance;
            }

            return reward;
        }

        // --- Wire format helpers ---

        private void SendStepResponse(float[] obs, float reward, bool done, RewardInfo info)
        {
            int obsBytes = obs.Length * 4;
            int totalSize = 4 + obsBytes + 4 + 1 + 4 + 4 + 4;
            byte[] buf = new byte[totalSize];
            int offset = 0;

            Buffer.BlockCopy(BitConverter.GetBytes(obs.Length), 0, buf, offset, 4);
            offset += 4;
            Buffer.BlockCopy(obs, 0, buf, offset, obsBytes);
            offset += obsBytes;
            Buffer.BlockCopy(BitConverter.GetBytes(reward), 0, buf, offset, 4);
            offset += 4;
            buf[offset++] = done ? (byte)1 : (byte)0;
            Buffer.BlockCopy(BitConverter.GetBytes(info.StyleScore), 0, buf, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(info.Kills), 0, buf, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(info.RankIndex), 0, buf, offset, 4);
            _stream.Write(buf, 0, buf.Length);
        }

        private void SendObservation(float[] obs)
        {
            int obsBytes = obs.Length * 4;
            byte[] buf = new byte[4 + obsBytes];
            Buffer.BlockCopy(BitConverter.GetBytes(obs.Length), 0, buf, 0, 4);
            Buffer.BlockCopy(obs, 0, buf, 4, obsBytes);
            _stream.Write(buf, 0, buf.Length);
        }

        private void SendInt(int value)
        {
            byte[] buf = BitConverter.GetBytes(value);
            _stream.Write(buf, 0, 4);
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n;
                try
                {
                    n = _stream.Read(buf, read, count - read);
                }
                catch (System.IO.IOException)
                {
                    UltrabotPlugin.Log.LogError("[ULTRABOT] TCP read timeout — disconnecting");
                    return null;
                }
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }

        private void ResetTrackingState()
        {
            _prevStyleScore = 0;
            _prevHp = 100;
            _prevKills = 0;
            _prevRankIndex = 0;
            _maxDistFromSpawn = 0;
            _episodeSteps = 0;
            _cumulativeReward = 0;
            _deathHandled = false;
        }

        private void DisconnectClient()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public void Shutdown()
        {
            _running = false;
            DisconnectClient();
            try { _listener?.Stop(); } catch { }
            _listenerStarted = false;
        }
    }
}
