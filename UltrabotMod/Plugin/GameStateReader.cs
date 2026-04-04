using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace UltrabotMod
{
    public class GameStateReader
    {
        public const int MaxEnemies = 10;
        public const int MaxProjectiles = 8;
        public const int PerEnemyFeatures = 10;
        public const int PerProjectileFeatures = 8;

        // Raycasts: 12 horizontal rays (every 30deg) + 4 vertical (up/down/forward-down/back-down)
        // + 8 diagonal = 24 total rays, each gives normalized distance (0-1)
        public const int NumRays = 24;
        public const float RayMaxDist = 30f;

        // NavMesh hint: direction to next checkpoint (3) + distance (1) + hasPath (1)
        public const int NavFeatures = 5;

        // Player: 41 + Rays: 24 + Nav: 5 = 70
        public const int PlayerFeatures = 41;
        public const int SpatialFeatures = NumRays + NavFeatures; // 29

        public const int TotalObsSize =
            PlayerFeatures + SpatialFeatures +
            MaxEnemies * PerEnemyFeatures +
            MaxProjectiles * PerProjectileFeatures;
        // 41 + 29 + 100 + 64 = 234

        private NewMovement _player;
        private GunControl _gunControl;
        private WeaponCharges _weaponCharges;
        private StyleHUD _styleHud;
        private StatsManager _statsManager;
        private StyleCalculator _styleCalc;

        // Cached checkpoint target
        private CheckPoint[] _checkpoints;
        private NavMeshPath _navPath;
        private int _navQueryFrame;

        public bool IsReady => _player != null && !_player.dead;

        public void RefreshReferences()
        {
            _player = Object.FindObjectOfType<NewMovement>();
            _gunControl = Object.FindObjectOfType<GunControl>();
            _weaponCharges = Object.FindObjectOfType<WeaponCharges>();
            _styleHud = Object.FindObjectOfType<StyleHUD>();
            _statsManager = Object.FindObjectOfType<StatsManager>();
            _styleCalc = Object.FindObjectOfType<StyleCalculator>();
            _checkpoints = Object.FindObjectsOfType<CheckPoint>();
            _navPath = new NavMeshPath();
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

            // --- Player state (41 floats) ---
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

            // --- Raycasts (24 floats) — "eyes" for walls/floors/gaps ---
            WriteRaycasts(obs, ref idx, pos);

            // --- NavMesh hint (5 floats) — "GPS" to next checkpoint ---
            WriteNavHint(obs, ref idx, pos);

            // --- Enemies (100 floats) ---
            var enemies = GatherEnemies(pos);
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
        /// Cast 24 rays around the player to detect walls, floors, and gaps.
        /// Layout:
        ///   [0-11]  12 horizontal rays at eye level, every 30 degrees
        ///   [12-15] 4 vertical: up, down, forward-down(45deg), back-down(45deg)
        ///   [16-23] 8 diagonal rays (45deg up/down in cardinal directions)
        /// Each value = normalized hit distance (0=touching wall, 1=nothing within range)
        /// </summary>
        private void WriteRaycasts(float[] obs, ref int idx, Vector3 pos)
        {
            var eyePos = pos + Vector3.up * 1.5f; // approximate eye height
            var forward = _player.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            var right = new Vector3(forward.z, 0, -forward.x); // perpendicular

            // 12 horizontal rays every 30 degrees
            for (int i = 0; i < 12; i++)
            {
                float angle = i * 30f;
                var dir = Quaternion.Euler(0, angle, 0) * forward;
                obs[idx++] = CastRay(eyePos, dir);
            }

            // 4 vertical rays
            obs[idx++] = CastRay(eyePos, Vector3.up);                          // ceiling
            obs[idx++] = CastRay(pos, Vector3.down);                            // floor
            obs[idx++] = CastRay(eyePos, (forward + Vector3.down).normalized);  // forward-down
            obs[idx++] = CastRay(eyePos, (-forward + Vector3.down).normalized); // back-down

            // 8 diagonal rays (4 directions × up/down 45deg)
            Vector3[] cardinals = { forward, -forward, right, -right };
            foreach (var card in cardinals)
            {
                obs[idx++] = CastRay(eyePos, (card + Vector3.up).normalized);   // up-diagonal
                obs[idx++] = CastRay(eyePos, (card + Vector3.down).normalized); // down-diagonal
            }
        }

        private float CastRay(Vector3 origin, Vector3 direction)
        {
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, RayMaxDist))
                return hit.distance / RayMaxDist; // 0 = touching, ~1 = far
            return 1f; // nothing hit = max distance
        }

        /// <summary>
        /// Use Unity NavMesh to find direction to the nearest unactivated checkpoint.
        /// Gives the bot a "GPS" that works through portals (since ULTRAKILL bakes
        /// portal NavMeshLinks into the navmesh).
        /// Returns: dirX, dirY, dirZ (normalized), distance (normalized), hasPath (0/1)
        /// </summary>
        private void WriteNavHint(float[] obs, ref int idx, Vector3 playerPos)
        {
            // Only requery NavMesh every 10 frames for performance
            int frame = Time.frameCount;
            bool shouldQuery = (frame - _navQueryFrame) >= 10;

            if (shouldQuery && _checkpoints != null && _checkpoints.Length > 0)
            {
                _navQueryFrame = frame;

                // Find nearest unactivated checkpoint
                Vector3 target = Vector3.zero;
                float bestDist = float.MaxValue;
                bool foundTarget = false;

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

                if (!foundTarget)
                {
                    // All checkpoints activated — try to find exit/door
                    // Fall back to furthest checkpoint as general direction
                    foreach (var cp in _checkpoints)
                    {
                        if (cp == null) continue;
                        float d = Vector3.Distance(playerPos, cp.transform.position);
                        if (d > bestDist || !foundTarget)
                        {
                            bestDist = d;
                            target = cp.transform.position;
                            foundTarget = true;
                        }
                    }
                }

                if (foundTarget)
                {
                    // Sample positions onto NavMesh
                    NavMeshHit navHit;
                    Vector3 navStart = playerPos;
                    Vector3 navEnd = target;

                    if (NavMesh.SamplePosition(playerPos, out navHit, 5f, NavMesh.AllAreas))
                        navStart = navHit.position;
                    if (NavMesh.SamplePosition(target, out navHit, 5f, NavMesh.AllAreas))
                        navEnd = navHit.position;

                    if (NavMesh.CalculatePath(navStart, navEnd, NavMesh.AllAreas, _navPath)
                        && _navPath.corners.Length >= 2)
                    {
                        // Direction to first path corner (next waypoint)
                        Vector3 nextPoint = _navPath.corners[1];
                        Vector3 dir = (nextPoint - playerPos).normalized;

                        // Total path distance
                        float totalDist = 0f;
                        for (int i = 0; i < _navPath.corners.Length - 1; i++)
                            totalDist += Vector3.Distance(_navPath.corners[i], _navPath.corners[i + 1]);

                        obs[idx++] = dir.x;
                        obs[idx++] = dir.y;
                        obs[idx++] = dir.z;
                        obs[idx++] = Mathf.Clamp01(totalDist / 200f); // normalized
                        obs[idx++] = 1f; // hasPath
                        return;
                    }
                }
            }
            else if (!shouldQuery)
            {
                // Reuse last frame's data (already in obs array from last write)
                idx += NavFeatures;
                return;
            }

            // No path found — zero out
            obs[idx++] = 0f;
            obs[idx++] = 0f;
            obs[idx++] = 0f;
            obs[idx++] = 1f; // max distance = unknown
            obs[idx++] = 0f; // no path
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
