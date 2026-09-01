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
            int failedInternal = 0;
            int failedVanilla = 0;

            foreach (ThingCategoryDef cat in DefDatabase<ThingCategoryDef>.AllDefsListForReading)
            {
                if (cat.childCategories.NullOrEmpty() && cat.childThingDefs.NullOrEmpty()) continue;
                checkedCount++;

                // Check 1, internal: the memoised rollups must agree with an unmemoised walk of
                // the same def set.
                //
                // Do NOT sum WealthIndex.WealthOf(childCategory) here. WealthOf(cat) runs
                // WealthOfCategoryRaw bottom-up, which populates CategoryCache for every
                // descendant as a side effect, so a later WealthOf(child) just reads back the
                // float already summed into the parent -- a comparison against itself that can
                // never catch a broken recursion. WealthOf(ThingDef) reads the untouched per-def
                // dictionary instead.
                //
                // The enumeration is WealthIndex.CategoryDefs, NOT cat.DescendantThingDefs. That
                // was the flaw in the previous version of this check: DescendantThingDefs recurses
                // every child category, including resourceReadoutRoot ones, which is precisely the
                // rule the rollups had wrong. The check restated our assumption instead of testing
                // it, and passed while the Foods row over-reported by ~49k items.
                float parent = WealthIndex.WealthOf(cat);
                float sum = 0f;
                foreach (ThingDef def in WealthIndex.CategoryDefs(cat))
                    sum += WealthIndex.WealthOf(def);

                if (Mathf.Abs(parent - sum) > 0.5f)
                {
                    failedInternal++;
                    Log.Warning($"[Wealth Readout] {cat.defName} internal: rollup={parent:F2} walk={sum:F2}");
                }

                // Check 2, against vanilla: our traversal's def set must be the one the ROW covers.
                //
                // This is the check that has teeth. ResourceCounter.GetCountIn computes the number
                // printed on the row by its own independent recursion; WealthIndex.StoredCountOf
                // sums vanilla's own per-def counts over the def set our rollups use. If our
                // traversal rule drifts from vanilla's -- a missing resourceReadoutRoot skip, a
                // wrongly included subtree -- these two disagree immediately and loudly, because
                // one of them is not ours.
                int ours = WealthIndex.StoredCountOf(cat, map);
                int vanilla = map.resourceCounter.GetCountIn(cat);
                if (ours != vanilla)
                {
                    failedVanilla++;
                    Log.Error($"[Wealth Readout] {cat.defName} SCOPE MISMATCH: our def set stores " +
                              $"{ours}, vanilla's GetCountIn says {vanilla}. The row and the " +
                              $"tooltip are describing different things.");
                }
            }

            bool pass = failedInternal == 0 && failedVanilla == 0;
            Log.Message($"[Wealth Readout] Category summing: {checkedCount} checked, " +
                        $"{failedInternal} internal mismatches, {failedVanilla} vanilla scope " +
                        $"mismatches -> {(pass ? "PASS" : "FAIL")}");
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

            // Unclamped diff logged alongside the clamped elsewhere figure so a developer can
            // see total<stored (raw diff negative) at a glance instead of having to infer it from
            // elsewhere==0, which is indistinguishable from a genuine zero. ElsewhereCount itself
            // stays clamped -- that clamp is specified and correct; this is diagnostic-only.
            Log.Message($"[Wealth Readout] Steel: total={total} stored={stored} " +
                        $"rawDiff={total - stored} elsewhere={elsewhere} " +
                        $"wealth={WealthIndex.WealthOf(steel):F2}");
        }

        // Companion to the steel report above, but through TotalCountOf(ThingCategoryDef)
        // instead of CountOf(ThingDef) -- the steel action never runs the category recursion, so
        // without this TotalCountOf would ship with no call site at all. ResourcesRaw is used
        // because it is a vanilla category (steel, wood, stone chunks, etc.) that a normal
        // colony reliably has both defined and stored, unlike a hand-picked leaf ThingDef.
        //
        // This mirrors exactly what Task 6's tooltip will compute for a hovered category, so it
        // doubles as an early check on that path: run this, then drop a ResourcesRaw item
        // outside every stockpile with the debug spawner and run it again. stored must be
        // unchanged; elsewhere must have risen by the stack size.
        [DebugAction(Category, "Report stored vs elsewhere (category)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReportStoredVsElsewhereCategory()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            WealthIndex.Rebuild(map);

            // ResourcesRaw specifically because it exercises BOTH branches of the recursion rather
            // than only the leaf one: Core/Defs/ThingCategoryDefs/ThingCategories.xml gives it two
            // child categories (PlantMatter and StoneBlocks), while ThingDefs such as Steel list it
            // directly in their thingCategories. A category with no children would silently test
            // half of TotalCountOfCategoryRaw and look like it passed.
            //
            // GetNamedSilentFail rather than GetNamed: a total conversion can remove or rename a
            // vanilla category, and GetNamed would log its own less specific error and still return
            // null, throwing an NRE out of a dev-menu action instead of saying what to do about it.
            ThingCategoryDef cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ResourcesRaw");
            if (cat == null)
            {
                Log.Error("[Wealth Readout] ThingCategoryDef 'ResourcesRaw' not found -- a mod has " +
                          "removed or renamed it. Point this action at another category that has " +
                          "child categories, or the recursion goes untested.");
                return;
            }

            int total = WealthIndex.TotalCountOf(cat);
            int stored = map.resourceCounter.GetCountIn(cat);
            int elsewhere = WealthIndex.ElsewhereCount(total, stored);

            // Unclamped diff logged for the same reason as the steel report above.
            Log.Message($"[Wealth Readout] {cat.label}: total={total} stored={stored} " +
                        $"rawDiff={total - stored} elsewhere={elsewhere}");
        }

        // Formatting only. Runs without a map so it can be exercised from the main menu, which is
        // also the cheapest place to catch a missing translation key.
        //
        // This ASSERTS rather than only printing, because the failure it exists to catch is silent:
        // Translator.Translate returns the raw key string when a key is missing, without throwing
        // and without logging. So a Keyed file that failed to load -- a typo in a key name, a
        // LoadFolders path that stopped matching, an XML parse error -- would still produce a
        // clean-looking run, and the tooltip would read "WealthReadout.Line.Wealth" in game.
        // Printing alone would leave that to whoever happened to read Player.log carefully.
        [DebugAction(Category, "Print sample tooltips", allowedGameStates = AllowedGameStates.Entry)]
        private static void PrintSampleTooltips()
        {
            string a = TooltipText.Build("Foods", 1190f, 0.026f, 240, 70);
            string b = TooltipText.Build("Plasteel", 1840f, 0.041f, 460, 0);
            string c = TooltipText.Build("Chocolate", 0f, 0f, 0, 0);

            Log.Message($"[Wealth Readout] Sample A:\n{a}\n\nSample B (nothing elsewhere):\n{b}" +
                        $"\n\nSample C (zero wealth):\n{c}");

            // Language-independent: an unresolved key leaks its own name into the output, whatever
            // language is active. This half of the check works on a translated run too.
            bool keysResolved = true;
            foreach (string key in new[] { "WealthReadout.Line.Wealth", "WealthReadout.Line.Split" })
            {
                if (a.Contains(key) || b.Contains(key) || c.Contains(key))
                {
                    keysResolved = false;
                    Log.Error($"[Wealth Readout] Translation key '{key}' did not resolve -- it is " +
                              "appearing verbatim in the tooltip. Check that " +
                              "Languages/English/Keyed/WealthReadout.xml is present, well-formed, " +
                              "and that the key name matches the string passed to .Translate().");
                }
            }

            // Structural checks that also hold under translation: B has no split line because
            // nothing is elsewhere, A does because 70 are. Catches the suppression logic inverting.
            bool structureOk = a.Split('\n').Length == 3 && b.Split('\n').Length == 2;
            if (!structureOk)
            {
                Log.Error("[Wealth Readout] Split-line suppression is wrong: sample A (70 " +
                          "elsewhere) must have 3 lines and sample B (0 elsewhere) must have 2. " +
                          $"Got {a.Split('\n').Length} and {b.Split('\n').Length}.");
            }

            // Exact-text comparison only makes sense against the English Keyed file; on any other
            // active language the expected strings are legitimately different, so it is skipped
            // rather than reported as a failure.
            bool englishOk = true;
            if (LanguageDatabase.activeLanguage == LanguageDatabase.defaultLanguage)
            {
                const string expectedA = "Foods\n1,190 silver \u00b7 2.6% of colony wealth\n240 stored \u00b7 70 elsewhere";
                if (a != expectedA)
                {
                    englishOk = false;
                    Log.Error($"[Wealth Readout] Sample A text mismatch.\nExpected:\n{expectedA}\nGot:\n{a}");
                }
            }
            else
            {
                Log.Message("[Wealth Readout] Active language is not English; exact-text comparison " +
                            "skipped. Key resolution and structure were still checked.");
            }

            bool pass = keysResolved && structureOk && englishOk;
            Log.Message($"[Wealth Readout] Sample tooltips: {(pass ? "PASS" : "FAIL")}");
        }

        // Verification item 6. The rebuild is one full-map walk -- the same pass WealthWatcher runs
        // every 5000 ticks -- so the question is not whether it is cheap but whether one of them
        // inside a single frame is survivable on a heavy map.
        [DebugAction(Category, "Profile index rebuild",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ProfileRebuild()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // Warm once so the measurement is not dominated by first-call JIT.
            WealthIndex.Rebuild(map);

            var sw = new System.Diagnostics.Stopwatch();
            const int runs = 20;
            sw.Start();
            for (int i = 0; i < runs; i++) WealthIndex.Rebuild(map);
            sw.Stop();

            double perRun = sw.Elapsed.TotalMilliseconds / runs;
            Log.Message($"[Wealth Readout] Rebuild: {perRun:F2} ms/run over {runs} runs " +
                        $"(map {map.Size.x}x{map.Size.z}, wealth {map.wealthWatcher.WealthTotal:F0})");
        }

        // Guards the one assumption the simple-mode patch makes that can drift silently.
        //
        // ReadoutPatches.SimpleIconTipId must reproduce the uniqueId vanilla's DrawIcon produces,
        // or our tip lands under a different id and the player sees two stacked tooltips instead of
        // one replaced. The expression below is copied verbatim from the 1.6 decompile of
        // ResourceReadout.DrawIcon; if someone edits the helper, this stops matching.
        [DebugAction(Category, "Check simple-mode tooltip id", allowedGameStates = AllowedGameStates.Entry)]
        private static void CheckSimpleTipId()
        {
            int checked_ = 0;
            int mismatches = 0;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!def.PlayerAcquirable || !def.CountAsResource) continue;

                // Verbatim from ResourceReadout.DrawIcon.
                TaggedString vanillaText = def.LabelCap + ": " + def.description.CapitalizeFirst();
                int expected = new TipSignal(vanillaText).uniqueId;

                int actual = ReadoutPatches.SimpleIconTipId(def);
                checked_++;
                if (actual != expected)
                {
                    mismatches++;
                    if (mismatches <= 5)
                    {
                        Log.Error($"[Wealth Readout] Tip id mismatch for {def.defName}: " +
                                  $"expected {expected}, got {actual}");
                    }
                }
            }

            Log.Message($"[Wealth Readout] Simple-mode tooltip id: " +
                        $"{(mismatches == 0 ? "PASS" : "FAIL")} ({checked_} defs, {mismatches} mismatched)");
        }
    }
}
