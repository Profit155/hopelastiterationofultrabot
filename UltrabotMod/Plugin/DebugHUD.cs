using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// On-screen debug overlay showing reward components and bot state.
    /// Toggle with F7.
    /// </summary>
    public class DebugHUD
    {
        private bool _visible = true;
        private GUIStyle _style;
        private GUIStyle _bgStyle;

        // Stats updated each step from TcpBridge
        public float LastReward;
        public float CumulativeReward;
        public int TotalSteps;
        public int EpisodeSteps;
        public int Kills;
        public int StyleScore;
        public int RankIndex;
        public int Parries;
        public int Headshots;
        public float ExplorationDist;
        public float MaxExplorationDist;
        public int DamageTaken;
        public int Deaths;
        public bool Connected;
        public float Hp;

        // Reward breakdown
        public float RewStyle;
        public float RewKills;
        public float RewParry;
        public float RewHeadshot;
        public float RewRank;
        public float RewDamage;
        public float RewDeath;
        public float RewExplore;
        public float RewHeight;
        public float RewFacing;
        public float RewStepCost;

        // Anti-spam reward breakdown
        public float RewJitter;
        public float RewWasted;
        public float RewBank;
        public float RewSlamAbuse;
        public float RewSwitchSpam;
        public float RewWhipSpam;
        public float RewFireTog;
        public float RewMash;

        // NavMesh status
        public bool NavAgentActive;
        public float NavAgentDist;

        private static readonly string[] RankNames = {
            "D", "C", "B", "A", "S", "SS", "SSS", "ULTRAKILL"
        };

        public void Toggle()
        {
            _visible = !_visible;
        }

        public void Draw()
        {
            if (!_visible) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                };
                _style.normal.textColor = Color.white;

                _bgStyle = new GUIStyle(GUI.skin.box);
                var bgTex = new Texture2D(1, 1);
                bgTex.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
                bgTex.Apply();
                _bgStyle.normal.background = bgTex;
            }

            float w = 300;
            float h = 640;
            float x = Screen.width - w - 10;
            float y = 10;

            GUI.Box(new Rect(x, y, w, h), "", _bgStyle);

            float ly = y + 5;
            float lh = 18;
            float lx = x + 10;

            string rankName = (RankIndex >= 0 && RankIndex < RankNames.Length)
                ? RankNames[RankIndex] : "?";

            DrawLine(ref ly, lx, lh, "=== ULTRABOT DEBUG ===");
            DrawLine(ref ly, lx, lh, $"Connected: {(Connected ? "YES" : "NO")}  NavMesh: {(NavAgentActive ? "ON" : "OFF")}");
            DrawLine(ref ly, lx, lh, $"Steps: {EpisodeSteps} (total: {TotalSteps})");
            DrawLine(ref ly, lx, lh, $"HP: {Hp:F0}  Rank: {rankName}");
            DrawLine(ref ly, lx, lh, "");
            DrawLine(ref ly, lx, lh, "--- Episode Stats ---");
            DrawLine(ref ly, lx, lh, $"Kills: {Kills}  Style: {StyleScore}");
            DrawLine(ref ly, lx, lh, $"Parries: {Parries}  Headshots: {Headshots}");
            DrawLine(ref ly, lx, lh, $"Dmg taken: {DamageTaken}  Deaths: {Deaths}");
            DrawLine(ref ly, lx, lh, $"Explore: {ExplorationDist:F1}m (max: {MaxExplorationDist:F1}m)");
            DrawLine(ref ly, lx, lh, $"Nav target dist: {NavAgentDist:F1}m");
            DrawLine(ref ly, lx, lh, "");
            DrawLine(ref ly, lx, lh, "--- Reward (last step) ---");
            DrawLine(ref ly, lx, lh, $"Style:     {RewStyle:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Kills:     {RewKills:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Parry:     {RewParry:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Headshot:  {RewHeadshot:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Rank:      {RewRank:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Explore:   {RewExplore:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Height:    {RewHeight:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Facing:    {RewFacing:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Damage:    {RewDamage:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Death:     {RewDeath:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Step cost: {RewStepCost:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, "--- Anti-spam ---");
            DrawLine(ref ly, lx, lh, $"Jitter:    {RewJitter:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Wasted:    {RewWasted:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Bank:      {RewBank:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"SlamAbuse: {RewSlamAbuse:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"SwitchSpm: {RewSwitchSpam:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"WhipSpm:   {RewWhipSpam:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"FireTog:   {RewFireTog:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Mash:      {RewMash:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, "");
            DrawLine(ref ly, lx, lh, $"Step total:  {LastReward:+0.000;-0.000}");
            DrawLine(ref ly, lx, lh, $"Episode sum: {CumulativeReward:+0.0;-0.0}");
        }

        private void DrawLine(ref float y, float x, float h, string text)
        {
            GUI.Label(new Rect(x, y, 280, h), text, _style);
            y += h;
        }
    }
}
