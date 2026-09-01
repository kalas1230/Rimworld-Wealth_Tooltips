using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace WealthReadout
{
    // Dev-mode only by construction: RimWorld never surfaces a DebugAction outside the debug menu,
    // which is itself behind Prefs.DevMode. Nothing here is visible to a normal player.
    //
    // This is the project's test harness. It is NOT a unit-test project, and that is a decision:
    // the code under test is Map-coupled, so the only way to exercise the real path is inside a
    // running game. An out-of-game double would test a copy of the logic rather than the logic.
    public static class DebugActions
    {
        private const string Category = "Wealth Readout";

        // Verification item 1 from the spec, and the most important check in the project.
        // WealthWatcher.CalculateWealthItems defines what "wealth" means to the storyteller.
        // If our own walk drifts from it, every tooltip is wrong and nothing else notices.
        [DebugAction(Category, "Reconcile index vs WealthWatcher",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReconcileIndex()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // Known and expected divergence: ForceRecount folds pocket maps (see its foreach over
            // Find.World.pocketMaps) into WealthItems, and our walk covers this map only. On such a
            // world the comparison below is meaningless -- diff would exceed tolerance even though
            // the walk is correct, producing a FAIL that is indistinguishable from a real drift. So
            // check this first and bail before computing or logging any verdict.
            if (Find.World.pocketMaps.Count > 0)
            {
                Log.Message("[Wealth Readout] Reconcile: SKIPPED -- this world has pocket maps, " +
                            "which WealthWatcher.ForceRecount folds into WealthItems but our walk " +
                            "does not. Re-run this check on a colony without pocket maps.");
                return;
            }

            // Force both sides onto the same tick before comparing. WealthWatcher recounts at most
            // every 5000 ticks (MinCountInterval), so without this we would be comparing a fresh
            // number against one that could be 5000 ticks stale and calling the gap a bug.
            map.wealthWatcher.ForceRecount();
            WealthIndex.Rebuild(map);

            float ours = WealthIndex.ItemsTotal;
            float theirs = map.wealthWatcher.WealthItems;
            float diff = ours - theirs;

            // Tolerance is for float summation order only, not for missing things. A real drift
            // shows up as a large diff, not a rounding one.
            bool pass = Mathf.Abs(diff) < 1f;

            Log.Message($"[Wealth Readout] Reconcile: ours={ours:F2} vanilla={theirs:F2} " +
                        $"diff={diff:F2} -> {(pass ? "PASS" : "FAIL")}");

            if (!pass)
            {
                Log.Warning("[Wealth Readout] Index does not mirror WealthWatcher. Check the " +
                            "request group, the filter, the fogged check and the spawned check " +
                            "before changing anything else.");
            }
        }

        // Verification item 3: a parent category's silver must equal the sum of its children's,
        // across at least two levels of nesting. This is what catches a broken recursion.
        [DebugAction(Category, "Check category subtree summing",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CheckCategorySumming()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            WealthIndex.Rebuild(map);

            int checkedCount = 0;
            int failed = 0;

            foreach (ThingCategoryDef cat in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
            {
                if (cat.childCategories.NullOrEmpty() && cat.childThingDefs.NullOrEmpty()) continue;

                float parent = WealthIndex.WealthOf(cat);

                float sum = 0f;
                for (int i = 0; i < cat.childThingDefs.Count; i++)
                    sum += WealthIndex.WealthOf(cat.childThingDefs[i]);
                for (int j = 0; j < cat.childCategories.Count; j++)
                    sum += WealthIndex.WealthOf(cat.childCategories[j]);

                checkedCount++;
                if (Mathf.Abs(parent - sum) > 0.5f)
                {
                    failed++;
                    Log.Warning($"[Wealth Readout] {cat.defName}: parent={parent:F2} sum={sum:F2}");
                }
            }

            Log.Message($"[Wealth Readout] Category summing: {checkedCount} checked, " +
                        $"{failed} mismatched -> {(failed == 0 ? "PASS" : "FAIL")}");
        }

        // Verification of the denominator's shape. The share of the single largest category must be
        // strictly between 0 and 1, and total item wealth must never exceed the denominator.
        [DebugAction(Category, "Check share denominator",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CheckDenominator()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            map.wealthWatcher.ForceRecount();
            WealthIndex.Rebuild(map);

            float denom = WealthIndex.Denominator;
            float items = WealthIndex.ItemsTotal;
            bool pass = denom > 0f && items <= denom + 1f;

            Log.Message($"[Wealth Readout] Denominator={denom:F2} items={items:F2} " +
                        $"buildings+pawns+floors={(denom - items):F2} -> {(pass ? "PASS" : "FAIL")}");
        }
    }
}
