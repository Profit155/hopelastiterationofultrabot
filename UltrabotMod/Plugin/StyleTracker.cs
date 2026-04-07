using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UltrabotMod
{
    /// <summary>
    /// Tracks style bonuses, kills, parries, and damage per step via Harmony patches.
    /// Call ConsumeEvents() each RL step to get and reset accumulated data.
    /// </summary>
    public class StyleTracker
    {
        // Accumulated between steps
        public static int AccumulatedStylePoints = 0;
        public static int AccumulatedKills = 0;
        public static int AccumulatedDamageTaken = 0;
        public static int AccumulatedParries = 0;
        public static int AccumulatedHeadshots = 0;
        public static int AccumulatedMultikillCount = 0;
        public static List<string> AccumulatedBonuses = new List<string>();

        /// <summary>Consume all accumulated events since last call.</summary>
        public StepEvents ConsumeEvents()
        {
            var events = new StepEvents
            {
                StylePointsGained = AccumulatedStylePoints,
                KillsThisStep = AccumulatedKills,
                DamageTakenThisStep = AccumulatedDamageTaken,
                ParriesThisStep = AccumulatedParries,
                HeadshotsThisStep = AccumulatedHeadshots,
                MultikillCount = AccumulatedMultikillCount,
                Bonuses = new List<string>(AccumulatedBonuses)
            };

            AccumulatedStylePoints = 0;
            AccumulatedKills = 0;
            AccumulatedDamageTaken = 0;
            AccumulatedParries = 0;
            AccumulatedHeadshots = 0;
            AccumulatedMultikillCount = 0;
            AccumulatedBonuses.Clear();

            return events;
        }

        public void Reset()
        {
            AccumulatedStylePoints = 0;
            AccumulatedKills = 0;
            AccumulatedDamageTaken = 0;
            AccumulatedParries = 0;
            AccumulatedHeadshots = 0;
            AccumulatedMultikillCount = 0;
            AccumulatedBonuses.Clear();
        }
    }

    public struct StepEvents
    {
        public int StylePointsGained;
        public int KillsThisStep;
        public int DamageTakenThisStep;
        public int ParriesThisStep;
        public int HeadshotsThisStep;
        public int MultikillCount;
        public List<string> Bonuses;
    }

    // ============================================================
    // Harmony Patches
    // ============================================================

    /// <summary>Patch StyleHUD.AddPoints to track every style bonus.</summary>
    [HarmonyPatch(typeof(StyleHUD), "AddPoints")]
    public static class StyleHUD_AddPoints_Patch
    {
        static void Postfix(int points, string pointID)
        {
            StyleTracker.AccumulatedStylePoints += points;

            if (!string.IsNullOrEmpty(pointID))
            {
                StyleTracker.AccumulatedBonuses.Add($"{pointID}:{points}");

                // Detect specific high-value actions by pointID
                string id = pointID.ToLowerInvariant();
                if (id.Contains("parry") || id.Contains("chargeback"))
                    StyleTracker.AccumulatedParries++;
                if (id.Contains("headshot") || id.Contains("headshotcombo"))
                    StyleTracker.AccumulatedHeadshots++;
                if (id.Contains("multikill") || id.Contains("multi"))
                    StyleTracker.AccumulatedMultikillCount++;
            }
        }
    }

    /// <summary>Patch StyleHUD.RemovePoints to track style loss (from damage).</summary>
    [HarmonyPatch(typeof(StyleHUD), "RemovePoints")]
    public static class StyleHUD_RemovePoints_Patch
    {
        static void Postfix(int points)
        {
            StyleTracker.AccumulatedStylePoints -= points;
        }
    }

    /// <summary>Patch EnemyIdentifier.Death to count kills.</summary>
    [HarmonyPatch(typeof(EnemyIdentifier), "Death", new System.Type[] { typeof(bool) })]
    public static class EnemyIdentifier_Death_Patch
    {
        static void Postfix(EnemyIdentifier __instance)
        {
            if (!__instance.dontCountAsKills)
                StyleTracker.AccumulatedKills++;
        }
    }

    /// <summary>Patch NewMovement.GetHurt to track damage taken.</summary>
    [HarmonyPatch(typeof(NewMovement), "GetHurt")]
    public static class NewMovement_GetHurt_Patch
    {
        static void Postfix(int damage)
        {
            StyleTracker.AccumulatedDamageTaken += damage;
        }
    }

    // ============================================================
    // Input Injection Patches
    // ============================================================
    // Problem: Our MonoBehaviour.Update() may run AFTER game scripts' Update().
    // WasPerformedThisFrame checks are frame-sensitive — if we set PerformedFrame
    // after the game already checked it, it's missed. Fire1/Fire2 work because
    // weapons check IsPressed (persistent), not WasPerformedThisFrame.
    //
    // Fix: Harmony Prefix on key game scripts' Update() to apply our input BEFORE
    // they read it. This guarantees correct timing regardless of execution order.

    /// <summary>
    /// Static holder for pending input actions.
    /// Set by ActionExecutor/TestPanel, consumed by Harmony prefix patches.
    /// </summary>
    public static class InputInjector
    {
        // Callback set by UltrabotPlugin — applies all pending InputActionState changes
        public static System.Action ApplyInputs;

        // Track which frame we last applied, to avoid double-apply
        public static int LastAppliedFrame = -1;

        public static void TryApply()
        {
            if (ApplyInputs != null && LastAppliedFrame != Time.frameCount)
            {
                LastAppliedFrame = Time.frameCount;
                ApplyInputs();
            }
        }
    }

    /// <summary>
    /// Prefix on NewMovement.Update — apply inputs before movement reads them.
    /// This ensures Jump, Dodge, Slide WasPerformedThisFrame is set correctly.
    /// </summary>
    [HarmonyPatch(typeof(NewMovement), "Update")]
    public static class NewMovement_Update_InputPrefix
    {
        static void Prefix()
        {
            InputInjector.TryApply();
        }
    }

    /// <summary>
    /// Prefix on GunControl.Update — apply inputs before weapon slot switching.
    /// </summary>
    [HarmonyPatch(typeof(GunControl), "Update")]
    public static class GunControl_Update_InputPrefix
    {
        static void Prefix()
        {
            InputInjector.TryApply();
        }
    }

    /// <summary>
    /// Prefix on HookArm.Update — apply inputs before hook reads Hook state.
    /// </summary>
    [HarmonyPatch(typeof(HookArm), "Update")]
    public static class HookArm_Update_InputPrefix
    {
        static void Prefix()
        {
            InputInjector.TryApply();
        }
    }

    /// <summary>
    /// Prefix on FistControl.Update — apply inputs before punch reads Punch state.
    /// </summary>
    [HarmonyPatch(typeof(FistControl), "Update")]
    public static class FistControl_Update_InputPrefix
    {
        static void Prefix()
        {
            InputInjector.TryApply();
        }
    }
}
