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
            // No .Translate() here, and no translated value stored in a static field anywhere in
            // this mod.
            //
            // The reason is NOT that the language is unloaded at this point -- it is loaded.
            // PlayDataLoader.LoadAllPlayData calls LanguageDatabase.InitAllMetadata() (line 100 of
            // the 1.6 decompile) and activeLanguage.InjectIntoData_AfterImpliedDefs() (line 333)
            // well before StaticConstructorOnStartupUtility.CallAll() (line 346), so a .Translate()
            // here would resolve correctly today.
            //
            // The hazard is the opposite one: a static constructor runs once per process, so
            // anything resolved here is frozen at that language forever. Switching language in the
            // options menu reloads the language database but does not re-run this constructor, and
            // the player would keep seeing the old language until they restart the game.
            //
            // Resolve keys at call time instead -- see TooltipText.
            var harmony = new Harmony("kalas.wealthreadout");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log.Message("[Wealth Readout] Patches applied.");
        }
    }
}
