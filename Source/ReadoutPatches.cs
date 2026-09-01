using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WealthReadout
{
    // Shared helper for all four patches.
    public static class ReadoutPatches
    {
        // Re-register under VANILLA's uniqueId. Do not clear first -- see below.
        //
        // TooltipHandler.TipRegion overwrites rather than stacks, and critically it only assigns
        // firstTriggerTime when the id is NOT already present:
        //     if (!activeTips.ContainsKey(tip.uniqueId)) { ...; firstTriggerTime = now; }
        //     activeTips[tip.uniqueId].signal.text = tip.text;
        // So calling it under the id vanilla just used replaces the text while leaving the tip's
        // age intact. That matters because DrawActiveTips gates on age:
        //     if (realtimeSinceStartup > firstTriggerTime + signal.delay) drawingTips.Add(value);
        // and TipSignal's delay is 0.45s for every constructor we or vanilla use.
        //
        // The original design called TooltipHandler.ClearTooltipsFrom(rect) first, which was a bug:
        // despite the name and the Rect parameter, it uses the rect only as a Repaint/Mouse.IsOver
        // gate and then removes EVERY tip with lastTriggerFrame == frame -- every tooltip
        // registered anywhere this frame, vanilla's included. Re-registering afterwards therefore
        // created a brand-new ActiveTip with firstTriggerTime = now, on every Repaint, so the 0.45s
        // threshold was never reached and NO tooltip ever drew -- not ours, and not the vanilla one
        // we had just deleted. Verified against the 1.6 decompile of TooltipHandler and TipSignal.
        public static void ReplaceTip(Rect rect, string text, int vanillaUniqueId)
        {
            // Kept even though every caller already gates on ShouldBuildTipFor: this is the last
            // line of defence for the invariant TooltipHandler itself relies on, and it is two
            // comparisons.
            if (!ShouldBuildTipFor(rect)) return;

            TooltipHandler.TipRegion(rect, new TipSignal(text, vanillaUniqueId));
        }

        // Call this BEFORE looking anything up, not after.
        //
        // A postfix runs for every row that drew, on every GUI event the readout receives --
        // Layout, Repaint, MouseDown and the rest -- so a tree with dozens of visible rows runs its
        // body many times per frame. Only one of those rows is under the cursor, and only Repaint
        // can register a tooltip at all. Doing the wealth lookups and building the tooltip string
        // first and discarding them here would allocate a string per visible row per event, all but
        // one of them thrown away.
        public static bool ShouldBuildTipFor(Rect rect)
        {
            return Event.current.type == EventType.Repaint && Mouse.IsOver(rect);
        }

        // Vanilla's uniqueId for a simple-mode icon tip. ResourceReadout.DrawIcon registers
        //     TaggedString taggedString = thingDef.LabelCap + ": " + thingDef.description.CapitalizeFirst();
        //     TooltipHandler.TipRegion(rect, taggedString);
        // and the TipSignal(TaggedString) constructor sets uniqueId = text.GetHashCode(). Since
        // TaggedString does not override GetHashCode that resolves to ValueType.GetHashCode, so the
        // id is computed by handing the reconstructed string to TipSignal rather than hashed here.
        public static int SimpleIconTipId(ThingDef thingDef)
        {
            TaggedString vanillaText = thingDef.LabelCap + ": " + thingDef.description.CapitalizeFirst();
            return new TipSignal(vanillaText).uniqueId;
        }

        // Rebuilds the rect vanilla drew the row into. Mirrors Listing_ResourceReadout.DoCategory:
        //     Rect rect = new Rect(0f, curY, LabelWidth, lineHeight);
        //     rect.xMin = XAtIndentLevel(nestLevel) + 18f;
        //
        // Every member needed is publicly reachable, so no reflection: curY via Listing.CurHeight,
        // lineHeight and nestIndentWidth are public fields, XAtIndentLevel(i) is i * nestIndentWidth,
        // and Listing_ResourceReadout overrides LabelWidth => base.ColumnWidth, which is public.
        public static Rect RowRect(Listing_ResourceReadout listing, float yBefore, int nestLevel)
        {
            var rect = new Rect(0f, yBefore, listing.ColumnWidth, listing.lineHeight);
            rect.xMin = nestLevel * listing.nestIndentWidth + 18f;
            return rect;
        }
    }

    [HarmonyPatch(typeof(Listing_ResourceReadout), nameof(Listing_ResourceReadout.DoCategory))]
    public static class DoCategory_Patch
    {
        // curY is protected, but Listing.CurHeight is a public getter over it. Recorded before the
        // row draws so the postfix can rebuild the rect the row was drawn into.
        public static void Prefix(Listing_ResourceReadout __instance, out float __state)
        {
            __state = __instance.CurHeight;
        }

        public static void Postfix(Listing_ResourceReadout __instance, TreeNode_ThingCategory node,
                                   int nestLevel, float __state)
        {
            // DoCategory early-returns without drawing when GetCountIn is zero. If curY did not
            // move, no row exists and registering a tooltip would attach one to empty space.
            //
            // Note this is != and not <=: an open category recurses into its children before
            // returning, so CurHeight has moved by the whole subtree's height, not one line. The
            // rect is rebuilt from __state, which is where THIS row was drawn, so the children's
            // height does not matter.
            if (__instance.CurHeight == __state) return;

            // Rect first, then the gate, then the lookups. RowRect is three field reads and a
            // multiply; everything after the gate is a dictionary walk plus a string build, and on
            // all but one row per Repaint the answer would be discarded.
            Rect rect = ReadoutPatches.RowRect(__instance, __state, nestLevel);
            if (!ReadoutPatches.ShouldBuildTipFor(rect)) return;

            Map map = Find.CurrentMap;
            if (map == null) return;

            ThingCategoryDef cat = node.catDef;
            float wealth = WealthIndex.WealthOf(cat);
            int total = WealthIndex.TotalCountOf(cat);
            int stored = map.resourceCounter.GetCountIn(cat);

            string text = TooltipText.Build(
                cat.LabelCap,
                wealth,
                WealthIndex.ShareOf(wealth),
                stored,
                WealthIndex.ElsewhereCount(total, stored));

            ReadoutPatches.ReplaceTip(rect, text, cat.GetHashCode());
        }
    }

    [HarmonyPatch(typeof(Listing_ResourceReadout), "DoThingDef")]
    public static class DoThingDef_Patch
    {
        public static void Prefix(Listing_ResourceReadout __instance, out float __state)
        {
            __state = __instance.CurHeight;
        }

        public static void Postfix(Listing_ResourceReadout __instance, ThingDef thingDef,
                                   int nestLevel, float __state)
        {
            // DoThingDef early-returns when GetCount is zero. Same reasoning as above.
            if (__instance.CurHeight == __state) return;

            // Same ordering as DoCategory_Patch: cheap rect, gate, then the expensive work.
            Rect rect = ReadoutPatches.RowRect(__instance, __state, nestLevel);
            if (!ReadoutPatches.ShouldBuildTipFor(rect)) return;

            Map map = Find.CurrentMap;
            if (map == null) return;

            float wealth = WealthIndex.WealthOf(thingDef);
            // WealthIndex.TotalCountOf(ThingDef) does not exist -- it duplicated the pre-existing
            // CountOf(ThingDef) and was dropped in Task 4. CountOf is the map-wide per-def count.
            int total = WealthIndex.CountOf(thingDef);
            int stored = map.resourceCounter.GetCount(thingDef);

            string text = TooltipText.Build(
                thingDef.LabelCap,
                wealth,
                WealthIndex.ShareOf(wealth),
                stored,
                WealthIndex.ElsewhereCount(total, stored));

            ReadoutPatches.ReplaceTip(rect, text, thingDef.shortHash);
        }
    }

    // Simple (uncategorized) mode does not go through Listing_ResourceReadout at all:
    // DoReadoutSimple -> DrawResourceSimple -> DrawIcon. DrawIcon is private, hence the string name.
    //
    // Vanilla's tip region here covers the 27x27 icon only, not the whole row:
    //     Rect rect = new Rect(x, y, 27f, 27f);
    //     if (Mouse.IsOver(rect)) TooltipHandler.TipRegion(rect, taggedString);
    // We match that rect exactly rather than widening it to the row. Matching vanilla's hover
    // target is the correct behaviour, not a limitation to fix -- widening it would be a UI change,
    // which rule 2 forbids.
    [HarmonyPatch(typeof(ResourceReadout), "DrawIcon")]
    public static class DrawIcon_Patch
    {
        public static void Postfix(float x, float y, ThingDef thingDef)
        {
            // Same ordering as DoCategory_Patch/DoThingDef_Patch: cheap rect, gate, then the
            // expensive work. DrawIcon has no zero-count early return to mirror (it is only called
            // for defs DrawResourceSimple already decided to draw), so there is no __state/CurHeight
            // check here -- the rect is simply vanilla's fixed 27x27 icon rect at (x, y).
            var rect = new Rect(x, y, 27f, 27f);
            if (!ReadoutPatches.ShouldBuildTipFor(rect)) return;

            Map map = Find.CurrentMap;
            if (map == null) return;

            float wealth = WealthIndex.WealthOf(thingDef);
            // WealthIndex.TotalCountOf(ThingDef) does not exist -- see DoThingDef_Patch. CountOf is
            // the map-wide per-def count.
            int total = WealthIndex.CountOf(thingDef);
            int stored = map.resourceCounter.GetCount(thingDef);

            string text = TooltipText.Build(
                thingDef.LabelCap,
                wealth,
                WealthIndex.ShareOf(wealth),
                stored,
                WealthIndex.ElsewhereCount(total, stored));

            // Vanilla's tip here is TipRegion(rect, taggedString), so its uniqueId is whatever
            // TipSignal derives from that exact TaggedString -- not thingDef.shortHash, which is
            // what the categorized-mode rows use. Reconstruct vanilla's string and let TipSignal
            // compute the id from it rather than hashing it here: TaggedString does not override
            // GetHashCode, so the id comes out of ValueType.GetHashCode and is not ours to
            // reimplement. Building the string costs one concatenation on the single hovered icon.
            //
            // If this reconstruction ever drifts from DrawIcon's, the id stops matching and the
            // player sees two stacked tooltips rather than one replaced -- visible, not silent.
            // "Check simple-mode tooltip id" in DebugActions is the check for exactly that.
            ReadoutPatches.ReplaceTip(rect, text, ReadoutPatches.SimpleIconTipId(thingDef));
        }
    }
}
