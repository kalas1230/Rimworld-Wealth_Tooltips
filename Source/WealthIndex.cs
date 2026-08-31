using System.Collections.Generic;
using RimWorld;
using Verse;

namespace WealthReadout
{
    // The per-ThingDef wealth breakdown that WealthWatcher does not keep. WealthWatcher stores four
    // floats (items/buildings/pawns/floors) and nothing per-def, so the breakdown has to be built
    // here.
    //
    // Nothing in this class is scribed or persisted. It is a session cache, discarded on quit,
    // rebuilt on demand. That is what keeps the mod removable from any save (HANDOVER.md rule 1).
    public static class WealthIndex
    {
        // Not configurable, by design: the mod ships no settings page. A bad value here is a patch,
        // not a user-side workaround, which is why profiling it is a release gate.
        // For scale: WealthWatcher runs this same pass every 5000 ticks (MinCountInterval).
        private const int StalenessTicks = 1000;

        private static Map cachedMap;
        private static int cachedTick = -99999;

        private static readonly Dictionary<ThingDef, float> defWealth = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<ThingDef, int> defCount = new Dictionary<ThingDef, int>();
        private static float itemsTotal;

        private static readonly List<Thing> tmpThings = new List<Thing>();

        public static float ItemsTotal
        {
            get { EnsureFresh(); return itemsTotal; }
        }

        public static float WealthOf(ThingDef def)
        {
            EnsureFresh();
            return defWealth.TryGetValue(def, out float v) ? v : 0f;
        }

        public static int CountOf(ThingDef def)
        {
            EnsureFresh();
            return defCount.TryGetValue(def, out int v) ? v : 0;
        }

        // Called by every tooltip, which means it is called on every Repaint while the mouse rests
        // on a row -- roughly 60 times a second. It must be cheap in the common case, and it is:
        // everything below the tick check is a dictionary lookup.
        public static void EnsureFresh()
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            Map map = Find.CurrentMap;
            if (map == null) return;

            int now = Find.TickManager.TicksGame;
            if (map == cachedMap && now - cachedTick < StalenessTicks) return;

            Rebuild(map);
        }

        // Mirrors WealthWatcher.CalculateWealthItems exactly. Every clause below is there because
        // vanilla has it, not because it seemed sensible:
        //
        //   ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(HaulableEver),
        //       tmpThings, allowUnreal: false, WealthItemsFilter);
        //   for (...) if (t.SpawnedOrAnyParentSpawned && !t.PositionHeld.Fogged(map))
        //                 num += t.MarketValue * t.stackCount;
        //
        // Recursive, so it picks up items inside pawns and containers. WealthItemsFilter is
        // vanilla's own and is reused rather than reimplemented -- it excludes passing ships, map
        // components, non-player pawns and quest lodgers, and a hand-rolled copy would drift.
        public static void Rebuild(Map map)
        {
            defWealth.Clear();
            defCount.Clear();
            CategoryCache.Clear();
            itemsTotal = 0f;

            tmpThings.Clear();
            ThingOwnerUtility.GetAllThingsRecursively(
                map,
                ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                tmpThings,
                allowUnreal: false,
                WealthWatcher.WealthItemsFilter);

            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing t = tmpThings[i];
                if (!t.SpawnedOrAnyParentSpawned) continue;
                if (t.PositionHeld.Fogged(map)) continue;

                float value = t.MarketValue * t.stackCount;
                itemsTotal += value;

                ThingDef def = t.def;
                defWealth.TryGetValue(def, out float w);
                defWealth[def] = w + value;
                defCount.TryGetValue(def, out int c);
                defCount[def] = c + t.stackCount;
            }
            tmpThings.Clear();

            cachedMap = map;
            cachedTick = Find.TickManager.TicksGame;
        }

        // Filled in by Task 3.
        internal static readonly Dictionary<ThingCategoryDef, float> CategoryCache =
            new Dictionary<ThingCategoryDef, float>();
    }
}
