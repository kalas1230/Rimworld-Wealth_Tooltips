using System.Reflection;
using HarmonyLib;
using Verse;

namespace WealthReadout
{
    // The mod's entire entry point. There is deliberately no Mod subclass and no ModSettings:
    // the mod must be addable to and removable from any save with no trace, and ModSettings is
    // one of the things banned by that rule (see HANDOVER.md, rule 1).
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            // No .Translate() here or anywhere else in a static constructor: translation tables
            // are not loaded when [StaticConstructorOnStartup] runs, and a key resolved this
            // early is silently frozen as its raw key string for the rest of the session.
            var harmony = new Harmony("kalas.wealthreadout");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[Wealth Readout] Patches applied.");
        }
    }
}
