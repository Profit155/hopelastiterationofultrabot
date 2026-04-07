using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace UltrabotMod
{
    public class GameStateReader
    {
        public const int MaxEnemies = 10;
        public const int MaxProjectiles = 8;
        public const int PerEnemyFeatures = 10;
        public const int PerProjectileFeatures = 8;

        public const int NumRays = 24;
        public const float RayMaxDist = 30f;

        // NavMesh hint: direction (3) + distance (1) + hasPath (1)
        public const int NavFeatures = 5;

        // Aim hint: yaw_delta (1) + pitch_delta (1) + has_target (1) + in_frustum (1)
        public const int AimFeatures = 4;

        // Player: 44 + Rays: 24 + Nav: 5 + Aim: 4 = 77
        public const int PlayerFeatures = 44;
        public const int SpatialFeatures = NumRays + NavFeatures + AimFeatures; // 33

        public const int TotalObsSize =
            PlayerFeatures + SpatialFeatures +
            MaxEnemies * PerEnemyFeatures +
            MaxProjectiles * PerProjectileFeatures;
        // 44 + 33 + 100 + 64 = 241

        private NewMovement _player;
        private GunControl _gunControl;
        private WeaponCharges _weaponCharges;
        private StyleHUD _styleHud;
        private StatsManager _statsManager;
        private StyleCalculator _styleCalc;
        private FistControl _fistControl;
        private CameraController _camera;

        // ActionExecutor reference for NavMeshAgent destination updates
        private ActionExecutor _actionExecutor;

        // Cached checkpoint target
        private CheckPoint[] _checkpoints;
        private NavMeshPath _navPath;
        private int _navQueryFrame;
        private const int NavQueryIntervalFrames = 1;
        private const float NavSampleRadius = 5f;
        private const float NavCornerAdvanceDistance = 1.25f;
        private const float NavFinalCornerReachDistance = 0.2f;
        private const float NavCornerPassPadding = 0.05f;

        // Cached nav hint values (reused on non-query frames)
        private float _cachedNavDirX, _cachedNavDirY, _cachedNavDirZ;
        private float _cachedNavDist = 1f;
        private float _cachedNavHasPath;
        private const float DoorOpenDistance = 8f;
        private const int DoorCacheIntervalFrames = 30;
        private bool _doorReflectionResolved;
        private Type _doorType;
        private FieldInfo _doorOpenField;
        private FieldInfo _doorLockedField;
        private MethodInfo _doorOpenMethod;
        private Object[] _cachedDoors;
        private int _doorCacheFrame;

        public bool IsReady => _player != null && !_player.dead;

        public void SetActionExecutor(ActionExecutor executor)
        {
            _actionExecutor = executor;
        }

        public void RefreshReferences()
        {
            _player = Object.FindObjectOfType<NewMovement>();
            _gunControl = Object.FindObjectOfType<GunControl>();
            _weaponCharges = Object.FindObjectOfType<WeaponCharges>();
            _styleHud = Object.FindObjectOfType<StyleHUD>();
            _statsManager = Object.FindObjectOfType<StatsManager>();
            _styleCalc = Object.FindObjectOfType<StyleCalculator>();
            _fistControl = Object.FindObjectOfType<FistControl>();
            _camera = Object.FindObjectOfType<CameraController>();
            _checkpoints = Object.FindObjectsOfType<CheckPoint>();
            _navPath = new NavMeshPath();

            // Reset nav cache so new episode never sees stale previous-episode data
            _navQueryFrame = -NavQueryIntervalFrames;
            _cachedNavDirX = 0f;
            _cachedNavDirY = 0f;
            _cachedNavDirZ = 0f;
            _cachedNavDist = 1f;
            _cachedNavHasPath = 0f;
            _cachedDoors = null;
            _doorCacheFrame = 0;
            if (_actionExecutor != null)
                _actionExecutor.ClearNavDestination();
        }

        public float[] GetObservation()
        {
            if (_player == null) RefreshReferences();
            if (_player == null) return new float[TotalObsSize];

            var obs = new float[TotalObsSize];
            int idx = 0;

            var pos = _player.transform.position;
            var vel = _player.rb.velocity;
            var look = _player.transform.forward;

            // --- Player state (44 floats) ---
            obs[idx++] = pos.x;
            obs[idx++] = pos.y;
            obs[idx++] = pos.z;
            obs[idx++] = vel.x;
            obs[idx++] = vel.y;
            obs[idx++] = vel.z;
            obs[idx++] = look.x;
            obs[idx++] = look.y;
            obs[idx++] = look.z;
            obs[idx++] = _player.hp / 100f;
            obs[idx++] = _player.antiHp / 100f;
            obs[idx++] = _player.boostCharge / 300f;
            obs[idx++] = _player.sliding ? 1f : 0f;
            obs[idx++] = _player.jumping ? 1f : 0f;
            obs[idx++] = _player.falling ? 1f : 0f;

            var gc = _player.gc;
            obs[idx++] = (gc != null && gc.onGround) ? 1f : 0f;

            if (_gunControl != null)
            {
                obs[idx++] = _gunControl.currentSlotIndex / 6f;
                obs[idx++] = _gunControl.currentVariationIndex / 3f;
                obs[idx++] = _gunControl.killCharge;
            }
            else idx += 3;

            for (int slot = 0; slot < 6; slot++)
            {
                if (_styleHud != null && _gunControl != null)
                {
                    var slots = _gunControl.slots;
                    if (slot < slots.Count && slots[slot].Count > 0)
                        obs[idx++] = _styleHud.GetFreshness(slots[slot][0]) / 10f;
                    else obs[idx++] = 0f;
                }
                else obs[idx++] = 0f;
            }

            if (_weaponCharges != null)
            {
                obs[idx++] = _weaponCharges.rev0charge;
                obs[idx++] = _weaponCharges.rev1charge;
                obs[idx++] = _weaponCharges.rev2charge;
                obs[idx++] = _weaponCharges.raicharge;
                obs[idx++] = _weaponCharges.rocketcharge;
                obs[idx++] = _weaponCharges.naiAmmo / 100f;
                obs[idx++] = _weaponCharges.naiSaws / 100f;
                obs[idx++] = _weaponCharges.punchStamina;
            }
            else idx += 8;

            obs[idx++] = (_styleHud != null) ? _styleHud.rankIndex / 7f : 0f;

            if (_styleCalc != null)
            {
                obs[idx++] = _styleCalc.multikillCount / 10f;
                obs[idx++] = _styleCalc.airTime / 10f;
            }
            else idx += 2;

            obs[idx++] = (_weaponCharges != null) ? 1f - _weaponCharges.raicharge : 1f;
            obs[idx++] = Mathf.Floor(_player.boostCharge / 100f) / 3f;

            // --- Enemy-facing features (3 floats) — helps bot learn to aim ---
            var enemies = GatherEnemies(pos);
            WriteEnemyFacing(obs, ref idx, pos, look, enemies);

            // --- Raycasts (24 floats) ---
            WriteRaycasts(obs, ref idx, pos);

            // --- NavMesh hint (5 floats) — GPS to enemy or checkpoint ---
            WriteNavHint(obs, ref idx, pos, enemies);

            // --- Aim hint (4 floats) — tells RL where to look ---
            WriteAimHint(obs, ref idx, pos, enemies);

            // --- Enemies (100 floats) ---
            for (int i = 0; i < MaxEnemies; i++)
            {
                if (i < enemies.Count)
                {
                    var e = enemies[i];
                    var rel = e.transform.position - pos;
                    obs[idx++] = rel.x / 50f;
                    obs[idx++] = rel.y / 50f;
                    obs[idx++] = rel.z / 50f;
                    obs[idx++] = e.health / 100f;
                    obs[idx++] = (float)e.enemyType / 42f;
                    obs[idx++] = e.isBoss ? 1f : 0f;
                    obs[idx++] = e.dead ? 1f : 0f;
                    obs[idx++] = rel.magnitude / 100f;
                    obs[idx++] = GetWeightClass(e.enemyType);
                    obs[idx++] = IsMeleeOnly(e.enemyType) ? 1f : 0f;
                }
                else idx += PerEnemyFeatures;
            }

            // --- Projectiles (64 floats) ---
            var projectiles = GatherProjectiles(pos);
            for (int i = 0; i < MaxProjectiles; i++)
            {
                if (i < projectiles.Count)
                {
                    var p = projectiles[i];
                    var rel = p.transform.position - pos;
                    var pRb = p.GetComponent<Rigidbody>();
                    var pVel = pRb != null ? pRb.velocity : Vector3.zero;
                    obs[idx++] = rel.x / 50f;
                    obs[idx++] = rel.y / 50f;
                    obs[idx++] = rel.z / 50f;
                    obs[idx++] = pVel.x / 50f;
                    obs[idx++] = pVel.y / 50f;
                    obs[idx++] = pVel.z / 50f;
                    obs[idx++] = p.damage / 100f;
                    obs[idx++] = rel.magnitude / 100f;
                }
                else idx += PerProjectileFeatures;
            }

            return obs;
        }

        /// <summary>
        /// 3 floats: dot product with nearest enemy, distance, line-of-sight
        /// </summary>
        private void WriteEnemyFacing(float[] obs, ref int idx, Vector3 pos, Vector3 look, List<EnemyIdentifier> enemies)
        {
            if (enemies.Count > 0)
            {
                var nearest = enemies[0];
                Vector3 toEnemy = nearest.transform.position - pos;
                float dist = toEnemy.magnitude;
                Vector3 dirToEnemy = dist > 0.01f ? toEnemy / dist : Vector3.zero;

                // Dot product: 1 = looking straight at, -1 = looking away
                obs[idx++] = Vector3.Dot(look, dirToEnemy);
                // Distance normalized
                obs[idx++] = Mathf.Clamp01(dist / 100f);
                // Line-of-sight: raycast to enemy
                RaycastHit hit;
                float los = 0f;
                if (Physics.Raycast(pos + Vector3.up * 1.5f, dirToEnemy, out hit, dist + 1f))
                {
                    var eid = hit.collider.GetComponentInParent<EnemyIdentifier>();
                    if (eid != null) los = 1f;
                }
                obs[idx++] = los;
            }
            else
            {
                obs[idx++] = 0f; // no enemy
                obs[idx++] = 1f; // max distance
                obs[idx++] = 0f; // no LOS
            }
        }

        private void WriteRaycasts(float[] obs, ref int idx, Vector3 pos)
        {
            var eyePos = pos + Vector3.up * 1.5f;
            var forward = _player.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            var right = new Vector3(forward.z, 0, -forward.x);

            for (int i = 0; i < 12; i++)
            {
                float angle = i * 30f;
                var dir = Quaternion.Euler(0, angle, 0) * forward;
                obs[idx++] = CastRay(eyePos, dir);
            }

            obs[idx++] = CastRay(eyePos, Vector3.up);
            obs[idx++] = CastRay(pos, Vector3.down);
            obs[idx++] = CastRay(eyePos, (forward + Vector3.down).normalized);
            obs[idx++] = CastRay(eyePos, (-forward + Vector3.down).normalized);

            Vector3[] cardinals = { forward, -forward, right, -right };
            foreach (var card in cardinals)
            {
                obs[idx++] = CastRay(eyePos, (card + Vector3.up).normalized);
                obs[idx++] = CastRay(eyePos, (card + Vector3.down).normalized);
            }
        }

        private float CastRay(Vector3 origin, Vector3 direction)
        {
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, RayMaxDist))
                return hit.distance / RayMaxDist;
            return 1f;
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private bool TryGetSteeringCorner(Vector3 playerPos, out Vector3 steeringCorner, out float remainingDistance)
        {
            steeringCorner = Vector3.zero;
            remainingDistance = 0f;

            if (_navPath == null || _navPath.corners == null || _navPath.corners.Length == 0)
                return false;

            Vector3 flatPlayer = Flatten(playerPos);
            int steeringIndex = -1;

            for (int i = 1; i < _navPath.corners.Length; i++)
            {
                Vector3 flatCorner = Flatten(_navPath.corners[i]);
                float reachDistance = (i == _navPath.corners.Length - 1)
                    ? NavFinalCornerReachDistance
                    : NavCornerAdvanceDistance;

                float cornerDistance = Vector3.Distance(flatPlayer, flatCorner);

                Vector3 flatPrevCorner = Flatten(_navPath.corners[i - 1]);
                Vector3 segment = flatCorner - flatPrevCorner;
                float segmentLength = segment.magnitude;
                bool passedCorner = false;

                if (segmentLength > 0.001f)
                {
                    Vector3 segmentDir = segment / segmentLength;
                    float progress = Vector3.Dot(flatPlayer - flatPrevCorner, segmentDir);
                    passedCorner = progress >= (segmentLength - NavCornerPassPadding);
                }

                if (cornerDistance <= reachDistance || passedCorner)
                    continue;

                steeringIndex = i;
                break;
            }

            if (steeringIndex < 0)
                return false;

            steeringCorner = _navPath.corners[steeringIndex];
            remainingDistance = Vector3.Distance(playerPos, steeringCorner);
            for (int i = steeringIndex; i < _navPath.corners.Length - 1; i++)
                remainingDistance += Vector3.Distance(_navPath.corners[i], _navPath.corners[i + 1]);

            return true;
        }

        /// <summary>
        /// NavMesh GPS — priority: nearest enemy > unactivated checkpoint > level exit.
        /// Also sets NavMeshAgent destination on ActionExecutor.
        /// </summary>
        private void WriteNavHint(float[] obs, ref int idx, Vector3 playerPos, List<EnemyIdentifier> enemies)
        {
            int frame = Time.frameCount;
            bool shouldQuery = (frame - _navQueryFrame) >= NavQueryIntervalFrames;

            if (shouldQuery)
            {
                _navQueryFrame = frame;

                Vector3 target = Vector3.zero;
                bool foundTarget = false;

                // Priority 1: nearest alive enemy
                if (enemies.Count > 0)
                {
                    target = enemies[0].transform.position;
                    foundTarget = true;
                }

                // Priority 2: nearest unactivated checkpoint
                if (!foundTarget && _checkpoints != null)
                {
                    float bestDist = float.MaxValue;
                    foreach (var cp in _checkpoints)
                    {
                        if (cp == null || cp.activated) continue;
                        float d = Vector3.Distance(playerPos, cp.transform.position);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            target = cp.transform.position;
                            foundTarget = true;
                        }
                    }
                }

                // Priority 3: try to find level exit (FinalDoor)
                if (!foundTarget)
                {
                    var door = Object.FindObjectOfType<FinalDoor>();
                    if (door != null)
                    {
                        target = door.transform.position;
                        foundTarget = true;
                    }
                }

                // Compute path FIRST — only arm executor steering if path is valid.
                bool pathOk = false;
                Vector3 nextCorner = Vector3.zero;
                float remainingDistance = 0f;
                if (foundTarget)
                {
                    NavMeshHit navHit;
                    Vector3 navStart = playerPos;
                    Vector3 navEnd = target;

                    if (NavMesh.SamplePosition(playerPos, out navHit, NavSampleRadius, NavMesh.AllAreas))
                        navStart = navHit.position;
                    if (NavMesh.SamplePosition(target, out navHit, NavSampleRadius, NavMesh.AllAreas))
                        navEnd = navHit.position;

                    if (NavMesh.CalculatePath(navStart, navEnd, NavMesh.AllAreas, _navPath)
                        && (_navPath.status == NavMeshPathStatus.PathComplete
                            || _navPath.status == NavMeshPathStatus.PathPartial)
                        && _navPath.corners.Length >= 2
                        && TryGetSteeringCorner(playerPos, out nextCorner, out remainingDistance))
                    {
                        Vector3 dir = (nextCorner - playerPos).normalized;

                        _cachedNavDirX = dir.x;
                        _cachedNavDirY = dir.y;
                        _cachedNavDirZ = dir.z;
                        _cachedNavDist = Mathf.Clamp01(remainingDistance / 200f);
                        _cachedNavHasPath = 1f;
                        pathOk = true;
                    }
                }

                if (!pathOk)
                {
                    TryOpenNearbyDoors(playerPos, _player.transform.forward);
                    _cachedNavDirX = 0f;
                    _cachedNavDirY = 0f;
                    _cachedNavDirZ = 0f;
                    _cachedNavDist = 1f;
                    _cachedNavHasPath = 0f;
                }

                // Steering target = next corner if path valid, otherwise CLEAR
                // (RL gets full control instead of being pulled at unreachable target).
                if (_actionExecutor != null)
                {
                    if (pathOk)
                        _actionExecutor.SetNavDestination(nextCorner);
                    else
                        _actionExecutor.ClearNavDestination();
                }
            }

            obs[idx++] = _cachedNavDirX;
            obs[idx++] = _cachedNavDirY;
            obs[idx++] = _cachedNavDirZ;
            obs[idx++] = _cachedNavDist;
            obs[idx++] = _cachedNavHasPath;
        }

        private void TryOpenNearbyDoors(Vector3 playerPos, Vector3 playerForward)
        {
            if (!EnsureDoorReflection())
                return;

            int frame = Time.frameCount;
            if (_cachedDoors == null || (frame - _doorCacheFrame) >= DoorCacheIntervalFrames)
            {
                _doorCacheFrame = frame;
                _cachedDoors = Object.FindObjectsOfType(_doorType);
            }

            if (_cachedDoors == null || _doorOpenField == null || _doorLockedField == null || _doorOpenMethod == null)
                return;

            Vector3 forward = playerForward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();

            for (int i = 0; i < _cachedDoors.Length; i++)
            {
                var door = _cachedDoors[i];
                var doorComponent = door as Component;
                if (door == null || doorComponent == null)
                    continue;

                object openValue = _doorOpenField.GetValue(door);
                if (openValue is bool isOpen && isOpen)
                    continue;

                object lockedValue = _doorLockedField.GetValue(door);
                if (lockedValue is bool isLocked && isLocked)
                    continue;

                Vector3 toDoor = doorComponent.transform.position - playerPos;
                float distance = toDoor.magnitude;
                if (distance <= 0.01f || distance > DoorOpenDistance)
                    continue;

                Vector3 toDoorDir = toDoor / distance;
                if (Vector3.Dot(toDoorDir, forward) <= 0.3f)
                    continue;

                _doorOpenMethod.Invoke(door, new object[] { false, false });
            }
        }

        private bool EnsureDoorReflection()
        {
            if (_doorType != null && _doorOpenField != null && _doorLockedField != null && _doorOpenMethod != null)
                return true;

            if (_doorReflectionResolved)
                return false;

            _doorReflectionResolved = true;
            _doorType = Type.GetType("Door");

            if (_doorType == null)
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && _doorType == null; i++)
                {
                    Type[] types;
                    try
                    {
                        types = assemblies[i].GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types;
                    }

                    if (types == null)
                        continue;

                    for (int j = 0; j < types.Length; j++)
                    {
                        var candidate = types[j];
                        if (candidate != null && candidate.Name == "Door")
                        {
                            _doorType = candidate;
                            break;
                        }
                    }
                }
            }

            if (_doorType == null)
                return false;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _doorOpenField = _doorType.GetField("open", flags);
            _doorLockedField = _doorType.GetField("locked", flags);
            _doorOpenMethod = _doorType.GetMethod("Open", flags, null, new[] { typeof(bool), typeof(bool) }, null);
            return _doorOpenField != null && _doorLockedField != null && _doorOpenMethod != null;
        }

        /// <summary>
        /// Aim hint (4 floats): yaw delta, pitch delta to nearest enemy, has_target, in_frustum.
        /// Tells RL WHERE to look without forcing the camera.
        /// </summary>
        private void WriteAimHint(float[] obs, ref int idx, Vector3 playerPos, List<EnemyIdentifier> enemies)
        {
            if (_camera == null || enemies.Count == 0)
            {
                obs[idx++] = 0f; // yaw delta
                obs[idx++] = 0f; // pitch delta
                obs[idx++] = 0f; // has_target
                obs[idx++] = 0f; // in_frustum
                return;
            }

            // Find nearest visible enemy (prefer LOS)
            EnemyIdentifier target = enemies[0];

            Vector3 toEnemy = target.transform.position - playerPos;
            float dist = toEnemy.magnitude;
            if (dist < 0.1f)
            {
                obs[idx++] = 0f;
                obs[idx++] = 0f;
                obs[idx++] = 1f;
                obs[idx++] = 1f;
                return;
            }

            // Calculate yaw and pitch deltas (how much camera needs to turn)
            Vector3 dirToEnemy = toEnemy / dist;

            // Current camera forward
            float camYawRad = _camera.rotationY * Mathf.Deg2Rad;
            float camPitchRad = _camera.rotationX * Mathf.Deg2Rad;
            Vector3 camForward = new Vector3(
                Mathf.Sin(camYawRad) * Mathf.Cos(camPitchRad),
                -Mathf.Sin(camPitchRad),
                Mathf.Cos(camYawRad) * Mathf.Cos(camPitchRad)
            );

            // Target yaw (horizontal angle)
            float targetYaw = Mathf.Atan2(dirToEnemy.x, dirToEnemy.z) * Mathf.Rad2Deg;
            float yawDelta = Mathf.DeltaAngle(_camera.rotationY, targetYaw);

            // Target pitch (vertical angle)
            float targetPitch = -Mathf.Asin(dirToEnemy.y) * Mathf.Rad2Deg;
            float pitchDelta = targetPitch - _camera.rotationX;

            // Normalize to [-1, 1] range (±180 yaw, ±90 pitch)
            obs[idx++] = Mathf.Clamp(yawDelta / 180f, -1f, 1f);
            obs[idx++] = Mathf.Clamp(pitchDelta / 90f, -1f, 1f);
            obs[idx++] = 1f; // has_target

            // Check if enemy is in camera frustum (rough check: dot product > 0.5 = ~60 degree cone)
            float dot = Vector3.Dot(_player.transform.forward, dirToEnemy);
            obs[idx++] = dot > 0.5f ? 1f : 0f;
        }

        public RewardInfo GetRewardInfo()
        {
            var info = new RewardInfo();
            if (_statsManager != null)
            {
                info.StyleScore = _statsManager.stylePoints;
                info.Kills = _statsManager.kills;
                info.Time = _statsManager.seconds;
            }
            if (_player != null)
            {
                info.Hp = _player.hp;
                info.Dead = _player.dead;
            }
            if (_styleHud != null)
                info.RankIndex = _styleHud.rankIndex;
            if (_styleCalc != null)
                info.MultikillCount = _styleCalc.multikillCount;
            return info;
        }

        public string GetLevelInfo()
        {
            string scene = SceneHelper.CurrentScene ?? "unknown";
            int level = SceneHelper.CurrentLevelNumber;
            return $"scene={scene},level={level}";
        }

        private float GetWeightClass(EnemyType type)
        {
            switch (type)
            {
                case EnemyType.Filth: case EnemyType.Stray: case EnemyType.Schism:
                case EnemyType.Soldier: case EnemyType.Drone: case EnemyType.Streetcleaner:
                    return 0f;
                case EnemyType.Stalker: case EnemyType.Mannequin: case EnemyType.Virtue: case EnemyType.V2:
                    return 0.33f;
                case EnemyType.Cerberus: case EnemyType.Swordsmachine: case EnemyType.Mindflayer:
                case EnemyType.Ferryman: case EnemyType.Gabriel: case EnemyType.GabrielSecond:
                case EnemyType.MinosPrime: case EnemyType.SisyphusPrime: case EnemyType.Guttertank:
                    return 0.66f;
                case EnemyType.MaliciousFace: case EnemyType.HideousMass: case EnemyType.Idol:
                case EnemyType.Gutterman: case EnemyType.Leviathan: case EnemyType.Minotaur:
                    return 1f;
                default: return 0.5f;
            }
        }

        private bool IsMeleeOnly(EnemyType type) => type == EnemyType.Idol;

        private List<EnemyIdentifier> GatherEnemies(Vector3 playerPos)
        {
            var all = Object.FindObjectsOfType<EnemyIdentifier>();
            var alive = new List<EnemyIdentifier>();
            foreach (var e in all)
                if (!e.dead && e.health > 0)
                    alive.Add(e);
            alive.Sort((a, b) =>
            {
                float da = (a.transform.position - playerPos).sqrMagnitude;
                float db = (b.transform.position - playerPos).sqrMagnitude;
                return da.CompareTo(db);
            });
            return alive;
        }

        private List<Projectile> GatherProjectiles(Vector3 playerPos)
        {
            var all = Object.FindObjectsOfType<Projectile>();
            var hostile = new List<Projectile>();
            foreach (var p in all)
                if (!p.friendly && !p.playerBullet)
                    hostile.Add(p);
            hostile.Sort((a, b) =>
            {
                float da = (a.transform.position - playerPos).sqrMagnitude;
                float db = (b.transform.position - playerPos).sqrMagnitude;
                return da.CompareTo(db);
            });
            return hostile;
        }
    }

    public struct RewardInfo
    {
        public int StyleScore;
        public int Kills;
        public float Time;
        public int Hp;
        public bool Dead;
        public int RankIndex;
        public int MultikillCount;
    }
}
