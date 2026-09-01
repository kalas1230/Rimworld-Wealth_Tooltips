using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WealthReadout
{
    // Shared helper for all four patches.
    public static class ReadoutPatches
    {
        // Clear, then register our own tip under our own id.
        //
        // The alternative was re-registering under vanilla's uniqueId, which works because
        // TooltipHandler.TipRegion overwrites rather than stacks:
        //     if (!activeTips.ContainsKey(tip.uniqueId)) { ... }
        //     activeTips[tip.uniqueId].signal.text = tip.text;
        // But it only works where vanilla's id is knowable. In simple mode ResourceReadout.DrawIcon
        // uses TipSignal(TaggedString), whose id is text.GetHashCode() -- matching it would mean
        // reconstructing vanilla's exact string. ClearTooltipsFrom works identically in both modes,
        // so both use it and neither depends on vanilla's id scheme.
        public static void ReplaceTip(Rect rect, string text, int uniqueId)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!Mouse.IsOver(rect)) return;

            TooltipHandler.ClearTooltipsFrom(rect);
            TooltipHandler.TipRegion(rect, new TipSignal(text, uniqueId));
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

            ReadoutPatches.ReplaceTip(ReadoutPatches.RowRect(__instance, __state, nestLevel),
                                      text, cat.GetHashCode());
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

            ReadoutPatches.ReplaceTip(ReadoutPatches.RowRect(__instance, __state, nestLevel),
                                      text, thingDef.shortHash);
        }
    }
}
