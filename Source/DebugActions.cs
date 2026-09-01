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

                // Do NOT sum WealthIndex.WealthOf(childCategory) here. WealthOf(cat) runs
                // WealthOfCategoryRaw bottom-up, which populates CategoryCache for every
                // descendant category as a side effect of computing parent's own total. A
                // subsequent WealthOf(childCategory) call then just reads that cache entry
                // back -- the exact float already summed inside parent's own computation --
                // so that comparison is against itself and can never catch a broken
                // recursion. Instead walk cat.DescendantThingDefs, vanilla's own independent
                // enumeration (ThingCategoryDef.ThisAndChildCategoryDefs recursing
                // childCategories), and sum WealthOf(ThingDef) -- the untouched,
                // cache-independent per-def dictionary -- over it. DescendantThingDefs
                // yields duplicates rather than deduping (the dedup is a separate private
                // field, allChildThingDefsCached), which matches the double-counting
                // semantics of the childCategories recursion in WealthOfCategoryRaw, so a
                // mismatch here is a genuine bug and not a semantic difference between paths.
                float sum = 0f;
                foreach (ThingDef def in cat.DescendantThingDefs)
                    sum += WealthIndex.WealthOf(def);

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

            // Same reasoning as ReconcileIndex above: ForceRecount folds pocket-map wealth
            // into vanilla's WealthItems/WealthTotal, but WealthIndex.Rebuild walks this map
            // only. On a world with pocket maps our itemsTotal and vanilla's WealthItems are
            // measuring different scopes, so the equality check below would fail spuriously
            // even when Denominator is computed correctly. Bail before computing or logging
            // any verdict.
            if (Find.World.pocketMaps.Count > 0)
            {
                Log.Message("[Wealth Readout] Denominator check: SKIPPED -- this world has " +
                            "pocket maps, which WealthWatcher.ForceRecount folds into " +
                            "WealthItems/WealthTotal but our walk does not. Re-run this check " +
                            "on a colony without pocket maps.");
                return;
            }

            // Force both sides onto the same tick before comparing, same as ReconcileIndex.
            map.wealthWatcher.ForceRecount();
            WealthIndex.Rebuild(map);

            WealthWatcher ww = map.wealthWatcher;
            float itemsTotal = WealthIndex.ItemsTotal;
            float nonItems = ww.WealthTotal - ww.WealthItems;
            float denom = WealthIndex.Denominator;
            float diff = denom - ww.WealthTotal;

            // With both sides on the same tick and no pocket maps in play, our itemsTotal
            // should equal vanilla's WealthItems, so Denominator (itemsTotal + nonItems)
            // should equal vanilla's WealthTotal up to float summation order. A real mismatch
            // here means the Denominator formula itself is wrong, not a staleness artifact --
            // unlike the old items<=denom+1 check, which was true by construction because
            // WealthTotal's non-items remainder can never be negative.
            bool pass = Mathf.Abs(diff) < 1f;

            Log.Message($"[Wealth Readout] Denominator={denom:F2} itemsTotal={itemsTotal:F2} " +
                        $"nonItems={nonItems:F2} vanillaWealthTotal={ww.WealthTotal:F2} " +
                        $"diff={diff:F2} -> {(pass ? "PASS" : "FAIL")}");
        }

        // Verification item 2: something outside storage must move the elsewhere figure and must
        // NOT move the row's stored count.
        //
        // Run this, then drop a stack outside every stockpile with the debug spawner and run it
        // again. stored must be unchanged; elsewhere must have risen by the stack size.
        [DebugAction(Category, "Report stored vs elsewhere (steel)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReportStoredVsElsewhere()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            WealthIndex.Rebuild(map);

            ThingDef steel = ThingDefOf.Steel;
            int total = WealthIndex.CountOf(steel);
            int stored = map.resourceCounter.GetCount(steel);
            int elsewhere = WealthIndex.ElsewhereCount(total, stored);

            Log.Message($"[Wealth Readout] Steel: total={total} stored={stored} " +
                        $"elsewhere={elsewhere} wealth={WealthIndex.WealthOf(steel):F2}");
        }
    }
}
