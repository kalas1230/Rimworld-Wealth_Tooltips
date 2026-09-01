# Wealth Readout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hovering a category or item in RimWorld's top-left resource readout shows its wealth in silver, its share of colony wealth, and how much of it is stored versus elsewhere.

**Architecture:** Four Harmony patches that rewrite tooltip text and nothing else, backed by a static in-memory index that mirrors `WealthWatcher.CalculateWealthItems` and is rebuilt lazily by the first hover that needs it. No defs, no components, no settings, nothing scribed.

**Tech Stack:** C# 9 / net472, RimWorld 1.6 `Assembly-CSharp`, Harmony (`brrainz.harmony`), Unity IMGUI. Verification is via `LudeonTK` DebugActions inside a running game.

## Global Constraints

Copied from `docs/superpowers/specs/2026-09-01-wealth-readout-design.md` and `HANDOVER.md`. Every task's requirements implicitly include this section.

- **Zero save footprint.** No defs, no `GameComponent`, no `MapComponent`, nothing scribed, no `ModSettings`. If a task seems to need one, stop and escalate.
- **Vanilla-shaped and minimal.** The mod changes tooltip *text*. No rows, windows, UI elements or keybinds added; nothing moved or resized; hover regions identical to vanilla's. Postfixes and prefixes only — **no transpilers anywhere**.
- **Mirror the wealth walk exactly.** `GetAllThingsRecursively` over `ThingRequestGroup.HaulableEver`, filtered with `WealthWatcher.WealthItemsFilter` (reuse it — it is `public static`; never reimplement), skipping anything not `SpawnedOrAnyParentSpawned` or whose `PositionHeld` is fogged, summing `MarketValue * stackCount`. Deviating anywhere makes the mod silently wrong while every check still passes.
- **Staleness is a constant**, `StalenessTicks = 1000`. Not configurable.
- **Target 1.6 only.** `supportedVersions` and `LoadFolders.xml` must agree; do not advertise 1.5.
- **Never store a `.Translate()` result in a field that outlives the load sequence.** Resolve keys at call time, every time. Note the reason, because the commonly-stated one is wrong: the language *is* loaded by the time `[StaticConstructorOnStartup]` runs (`PlayDataLoader.LoadAllPlayData` calls `LanguageDatabase.InitAllMetadata()` at line 100 and `InjectIntoData_AfterImpliedDefs()` at line 333, before `StaticConstructorOnStartupUtility.CallAll()` at line 346). The real hazard is that a static constructor runs once per process, so a value resolved there is frozen at that language and survives the player switching language in the options menu.
- **Namespace/assembly:** `WealthReadout`. **packageId:** `kalas.wealthreadout`.
- **Comment style:** dense justification comments citing decompiled vanilla behaviour at the point of each non-obvious decision, matching the sibling Varied Pawns mod.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `About/About.xml` | Mod metadata, Harmony dependency, player-facing description |
| `About/LoadFolders.xml` | 1.6-only load mapping |
| `Source/WealthReadout.csproj` | net472 build, references to RimWorld and Harmony |
| `Source/HarmonyInit.cs` | `[StaticConstructorOnStartup]` entry point; `PatchAll` |
| `Source/WealthIndex.cs` | The per-def wealth pass, the cache, category rollup, denominator |
| `Source/TooltipText.cs` | Pure formatting: numbers in, tooltip string out |
| `Source/ReadoutPatches.cs` | The four Harmony patches |
| `Source/DebugActions.cs` | Dev-mode verification harness (the project's test suite) |
| `Languages/English/Keyed/WealthReadout.xml` | Translation keys |

**On testing:** this repo has no unit-test project, matching the sibling Varied Pawns mod, and that is a decision rather than a gap. The interesting code is `Map`-coupled — the only way to exercise the real path is inside a running game. `DebugActions.cs` is the test harness: each check computes numbers and logs a PASS/FAIL line. "Run the test" below means "launch RimWorld, open a colony, run the debug action, read the log."

---

### Task 1: A mod that loads

**Files:**
- Create: `About/About.xml`, `About/LoadFolders.xml`, `Source/WealthReadout.csproj`, `Source/HarmonyInit.cs`, `README.md`, `LICENSE`

**Interfaces:**
- Consumes: nothing.
- Produces: namespace `WealthReadout`; assembly builds to `Assemblies/WealthReadout.dll`.

- [ ] **Step 1: Create `About/About.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <packageId>kalas.wealthreadout</packageId>
  <name>Wealth Readout</name>
  <author>kalas</author>
  <modVersion>1.0.0</modVersion>
  <!-- 1.6 only, and LoadFolders.xml must say the same. supportedVersions is what actually gates
       loading; claiming a version this mod has never been run on is worse than not claiming it. -->
  <supportedVersions>
    <li>1.6</li>
  </supportedVersions>
  <description>Wealth drives raid difficulty, and finding out what is costing you means opening a window. This mod puts the answer where you are already looking.

Hover any category or item in the resource readout at the top left of the screen. The tooltip tells you what it is worth in silver, what share of your colony's wealth that is, and how much of it is stored versus sitting elsewhere.

The readout and the wealth system do not count the same things — the readout counts what is in your stockpiles, while wealth counts everything on the map including what is loose on the ground and carried by pawns. The tooltip reports true wealth, the number your storyteller actually reads, and shows the gap rather than hiding it.

Tooltips only. Nothing is added to the panel, nothing moves, and nothing is resized. Safe to add to any save and safe to remove from any save: the mod writes nothing into your save file. Requires Harmony.</description>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
      <displayName>Harmony</displayName>
      <steamWorkshopUrl>steam://url/CommunityFilePage/2009463077</steamWorkshopUrl>
      <downloadUrl>https://github.com/pardeike/HarmonyRimWorld/releases/latest</downloadUrl>
    </li>
  </modDependencies>
  <loadAfter>
    <li>brrainz.harmony</li>
  </loadAfter>
</ModMetaData>
```

- [ ] **Step 2: Create `About/LoadFolders.xml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- 1.6 only, matching About.xml's supportedVersions. Keep the two in step on every version bump. -->
<loadFolders>
  <v1.6>
    <li>/</li>
  </v1.6>
</loadFolders>
```

- [ ] **Step 3: Create `Source/WealthReadout.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <AssemblyName>WealthReadout</AssemblyName>
    <RootNamespace>WealthReadout</RootNamespace>
    <LangVersion>9.0</LangVersion>
    <OutputPath>..\Assemblies\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <PropertyGroup>
    <!-- Override on the command line with -p:RimWorldDir=... if your install path differs -->
    <RimWorldDir Condition="'$(RimWorldDir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\RimWorld</RimWorldDir>
    <HarmonyModDir Condition="'$(HarmonyModDir)' == ''">C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\Current</HarmonyModDir>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(RimWorldDir)\RimWorldWin64_Data\Managed\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(RimWorldDir)\RimWorldWin64_Data\Managed\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.IMGUIModule">
      <HintPath>$(RimWorldDir)\RimWorldWin64_Data\Managed\UnityEngine.IMGUIModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(HarmonyModDir)\Assemblies\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create `Source/HarmonyInit.cs`**

```csharp
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
```

- [ ] **Step 5: Create `README.md`**

```markdown
# Wealth Readout

A RimWorld 1.6 mod. Hover a category or item in the top-left resource readout to see what it
contributes to colony wealth, in silver and as a share of the total.

- Design: `docs/superpowers/specs/2026-09-01-wealth-readout-design.md`
- Why it is shaped this way, and what was already rejected: `HANDOVER.md`

## Building

```
dotnet build Source/WealthReadout.csproj
```

Override the install path if yours differs:

```
dotnet build Source/WealthReadout.csproj -p:RimWorldDir="D:\Steam\steamapps\common\RimWorld"
```

Output lands in `Assemblies/`.
```

- [ ] **Step 6: Create `LICENSE`**

Copy the MIT license text from the sibling repo at `../Rimworld-Pawn-variance-mod/LICENSE`, updating the project name if the text names one.

- [ ] **Step 7: Build**

Run: `dotnet build Source/WealthReadout.csproj`
Expected: `Build succeeded`, and `Assemblies/WealthReadout.dll` exists.

- [ ] **Step 8: Verify it loads in game**

Launch RimWorld with the mod enabled, above nothing and after Harmony. Load any colony.
Expected: `Player.log` contains `[Wealth Readout] Patches applied.` and no red errors.

**Do not trust an in-game check that only looks at the in-game log window** — it cannot see
mod-load errors at all. Read `Player.log` directly.

- [ ] **Step 9: Commit**

```bash
git add About Source README.md LICENSE
git commit -m "feat: scaffold the mod so it loads and patches"
```

---

### Task 2: The wealth index, reconciled against vanilla

The single highest-risk piece. If this walk does not match `WealthWatcher` exactly, every number the mod shows is wrong in a way that still looks plausible. The debug action written here is the check that catches it, so it is written first.

**Files:**
- Create: `Source/WealthIndex.cs`, `Source/DebugActions.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `WealthReadout.WealthIndex.EnsureFresh()` → `void`
  - `WealthReadout.WealthIndex.Rebuild(Map map)` → `void`
  - `WealthReadout.WealthIndex.ItemsTotal` → `float`
  - `WealthReadout.WealthIndex.WealthOf(ThingDef def)` → `float`
  - `WealthReadout.WealthIndex.CountOf(ThingDef def)` → `int`

- [ ] **Step 1: Write the failing test — the reconciliation debug action**

Create `Source/DebugActions.cs`:

```csharp
using LudeonTK;
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

            // Known and expected divergence: ForceRecount folds pocket maps (see its foreach over
            // Find.World.pocketMaps) into WealthItems, and our walk covers this map only. Run this
            // check on a colony with no pocket map, or expect ours < vanilla by exactly the pocket
            // map's item wealth.
            if (Find.World.pocketMaps.Count > 0)
            {
                Log.Warning("[Wealth Readout] This world has pocket maps; the reconciliation " +
                            "above is expected to differ. Re-run on a colony without them.");
            }
        }
    }
}
```

Add `using UnityEngine;` at the top for `Mathf`.

- [ ] **Step 2: Create the stub index so the test compiles and fails**

Create `Source/WealthIndex.cs`:

```csharp
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace WealthReadout
{
    public static class WealthIndex
    {
        private static readonly Dictionary<ThingDef, float> defWealth = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<ThingDef, int> defCount = new Dictionary<ThingDef, int>();
        private static float itemsTotal;

        public static float ItemsTotal => itemsTotal;

        public static float WealthOf(ThingDef def)
        {
            return defWealth.TryGetValue(def, out float v) ? v : 0f;
        }

        public static int CountOf(ThingDef def)
        {
            return defCount.TryGetValue(def, out int v) ? v : 0;
        }

        public static void EnsureFresh()
        {
        }

        public static void Rebuild(Map map)
        {
        }
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet build Source/WealthReadout.csproj`, launch RimWorld, load a colony with stockpiled
resources, open the debug menu (dev mode on), run **Wealth Readout → Reconcile index vs WealthWatcher**.

Expected: `Reconcile: ours=0.00 vanilla=<some large number> diff=-<large> -> FAIL`

- [ ] **Step 4: Implement the real walk**

Replace the body of `Source/WealthIndex.cs`:

```csharp
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
```

- [ ] **Step 5: Run the test to verify it passes**

Run the same debug action.
Expected: `Reconcile: ours=<n> vanilla=<n> diff=<|diff| < 1> -> PASS`

If it FAILs, do not adjust the tolerance. Compare the walk against the decompiled
`CalculateWealthItems` clause by clause.

- [ ] **Step 6: Commit**

```bash
git add Source/WealthIndex.cs Source/DebugActions.cs
git commit -m "feat: per-def wealth index mirroring WealthWatcher, with reconciliation check"
```

---

### Task 3: Category rollup and the share denominator

**Files:**
- Modify: `Source/WealthIndex.cs`
- Modify: `Source/DebugActions.cs`

**Interfaces:**
- Consumes: `WealthIndex.EnsureFresh()`, `WealthIndex.WealthOf(ThingDef)`, `WealthIndex.ItemsTotal`.
- Produces:
  - `WealthReadout.WealthIndex.WealthOf(ThingCategoryDef cat)` → `float`
  - `WealthReadout.WealthIndex.Denominator` → `float`
  - `WealthReadout.WealthIndex.ShareOf(float wealth)` → `float` (0–1)

- [ ] **Step 1: Write the failing test — subtree summing**

Add to `Source/DebugActions.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: build, launch, run **Check category subtree summing**.
Expected: a compile error, because `WealthOf(ThingCategoryDef)` and `Denominator` do not exist yet.

- [ ] **Step 3: Implement the rollup and denominator**

Replace the `CategoryCache` stub at the bottom of `Source/WealthIndex.cs` with:

```csharp
        private static readonly Dictionary<ThingCategoryDef, float> CategoryCache =
            new Dictionary<ThingCategoryDef, float>();

        internal static void ClearCategoryCache()
        {
            CategoryCache.Clear();
        }

        // Mirrors ResourceCounter.GetCountIn(ThingCategoryDef): own childThingDefs, then recurse
        // into childCategories. Memoised, because a deep category is otherwise re-walked on every
        // frame of a hover.
        //
        // Note what is deliberately NOT done here: sibling categories are never summed together.
        // A ThingDef can belong to more than one ThingCategoryDef, so each category's own total is
        // correct while a sum across siblings would double-count. This is exactly why the "not
        // listed" footer was cut from the design -- see HANDOVER.md.
        public static float WealthOf(ThingCategoryDef cat)
        {
            EnsureFresh();
            return WealthOfCategoryRaw(cat);
        }

        private static float WealthOfCategoryRaw(ThingCategoryDef cat)
        {
            if (CategoryCache.TryGetValue(cat, out float cached)) return cached;

            float sum = 0f;
            for (int i = 0; i < cat.childThingDefs.Count; i++)
            {
                defWealth.TryGetValue(cat.childThingDefs[i], out float v);
                sum += v;
            }
            for (int j = 0; j < cat.childCategories.Count; j++)
            {
                sum += WealthOfCategoryRaw(cat.childCategories[j]);
            }

            CategoryCache[cat] = sum;
            return sum;
        }

        // Not map.wealthWatcher.WealthTotal.
        //
        // WealthWatcher recounts at most every 5000 ticks, so its WealthItems and our pass come
        // from different moments. Taking the whole total from vanilla would put a fresh numerator
        // over a stale denominator and produce category shares that do not add up. Taking only the
        // buildings/pawns/floors remainder from vanilla, and the items half from our own pass, is
        // internally consistent.
        public static float Denominator
        {
            get
            {
                EnsureFresh();
                Map map = Find.CurrentMap;
                if (map == null) return 0f;

                WealthWatcher ww = map.wealthWatcher;
                float nonItems = ww.WealthTotal - ww.WealthItems;
                return itemsTotal + nonItems;
            }
        }

        // 0-1. Returns 0 rather than dividing by zero on a map with no wealth at all.
        public static float ShareOf(float wealth)
        {
            float denom = Denominator;
            return denom > 0f ? wealth / denom : 0f;
        }
```

In `Rebuild`, replace `CategoryCache.Clear();` with `ClearCategoryCache();`.

- [ ] **Step 4: Run to verify both pass**

Run: build, launch, run **Check category subtree summing** and **Check share denominator**.
Expected: `Category summing: <n> checked, 0 mismatched -> PASS` and `Denominator=... -> PASS`.

- [ ] **Step 5: Commit**

```bash
git add Source/WealthIndex.cs Source/DebugActions.cs
git commit -m "feat: category rollup and the drift-free share denominator"
```

---

### Task 4: Stored versus elsewhere

The stored figure comes from vanilla's `resourceCounter`, not from our own storage predicate. The row prints vanilla's count a few pixels from the tooltip, so any independently-derived stored count risks visibly contradicting it.

**Files:**
- Modify: `Source/WealthIndex.cs`
- Modify: `Source/DebugActions.cs`

**Interfaces:**
- Consumes: `WealthIndex.CountOf(ThingDef)`, `WealthIndex.WealthOf(ThingCategoryDef)`.
- Produces:
  - `WealthReadout.WealthIndex.TotalCountOf(ThingDef def)` → `int`
  - `WealthReadout.WealthIndex.TotalCountOf(ThingCategoryDef cat)` → `int`
  - `WealthReadout.WealthIndex.ElsewhereCount(int totalCount, int storedCount)` → `int`

- [ ] **Step 1: Write the failing test**

Add to `Source/DebugActions.cs`:

```csharp
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
            int total = WealthIndex.TotalCountOf(steel);
            int stored = map.resourceCounter.GetCount(steel);
            int elsewhere = WealthIndex.ElsewhereCount(total, stored);

            Log.Message($"[Wealth Readout] Steel: total={total} stored={stored} " +
                        $"elsewhere={elsewhere} wealth={WealthIndex.WealthOf(steel):F2}");
        }
```

- [ ] **Step 2: Run to verify it fails**

Expected: compile error — `TotalCountOf` and `ElsewhereCount` do not exist.

- [ ] **Step 3: Implement**

Add to `Source/WealthIndex.cs`:

```csharp
        public static int TotalCountOf(ThingDef def)
        {
            EnsureFresh();
            defCount.TryGetValue(def, out int v);
            return v;
        }

        public static int TotalCountOf(ThingCategoryDef cat)
        {
            EnsureFresh();
            int sum = 0;
            for (int i = 0; i < cat.childThingDefs.Count; i++)
            {
                defCount.TryGetValue(cat.childThingDefs[i], out int v);
                sum += v;
            }
            for (int j = 0; j < cat.childCategories.Count; j++)
            {
                sum += TotalCountOf(cat.childCategories[j]);
            }
            return sum;
        }

        // The stored figure is ALWAYS vanilla's own resourceCounter value -- the same number the
        // row prints a few pixels away. Deriving it independently (say from IsInAnyStorage) would
        // let the tooltip contradict the row it is attached to, which reads as a bug even when our
        // number is the more accurate one.
        //
        // Clamped at zero because the two sources are not guaranteed to nest: ResourceCounter
        // unwraps minified things via GetInnerIfMinified while the wealth walk values the minified
        // container, so a colony full of minified furniture can push stored above our total.
        public static int ElsewhereCount(int totalCount, int storedCount)
        {
            int diff = totalCount - storedCount;
            return diff > 0 ? diff : 0;
        }
```

- [ ] **Step 4: Run to verify it passes**

Run the debug action on a colony with steel in a stockpile.
Expected: `Steel: total=<n> stored=<n> elsewhere=0 wealth=<n>` with `total == stored`.

Then spawn 50 steel outside every stockpile and re-run.
Expected: `stored` unchanged, `elsewhere` risen by 50.

- [ ] **Step 5: Commit**

```bash
git add Source/WealthIndex.cs Source/DebugActions.cs
git commit -m "feat: stored/elsewhere split sourced from vanilla's own resource counter"
```

---

### Task 5: The tooltip text

**Files:**
- Create: `Source/TooltipText.cs`, `Languages/English/Keyed/WealthReadout.xml`
- Modify: `Source/DebugActions.cs`

**Interfaces:**
- Consumes: nothing — pure formatting, so it can be exercised without a map.
- Produces:
  - `WealthReadout.TooltipText.Build(string label, float wealth, float share, int storedCount, int elsewhereCount)` → `string`

- [ ] **Step 1: Create the translation keys**

Create `Languages/English/Keyed/WealthReadout.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <!-- {0} silver value, {1} share of colony wealth as a percentage -->
  <WealthReadout.Line.Wealth>{0} silver · {1} of colony wealth</WealthReadout.Line.Wealth>
  <!-- {0} count in stockpiles, {1} count anywhere else: on the ground, carried, in containers.
       "Elsewhere" and not "unstored" on purpose -- most of that remainder has no hauling job. -->
  <WealthReadout.Line.Split>{0} stored · {1} elsewhere</WealthReadout.Line.Split>
</LanguageData>
```

- [ ] **Step 2: Write the failing test**

Add to `Source/DebugActions.cs`:

```csharp
        // Formatting only. Runs without a map so it can be exercised from the main menu, which is
        // also the cheapest place to catch a missing translation key.
        [DebugAction(Category, "Print sample tooltips", allowedGameStates = AllowedGameStates.Entry)]
        private static void PrintSampleTooltips()
        {
            Log.Message("[Wealth Readout] Sample A:\n" +
                        TooltipText.Build("Foods", 1190f, 0.026f, 240, 70));
            Log.Message("[Wealth Readout] Sample B (nothing elsewhere):\n" +
                        TooltipText.Build("Plasteel", 1840f, 0.041f, 460, 0));
            Log.Message("[Wealth Readout] Sample C (zero wealth):\n" +
                        TooltipText.Build("Chocolate", 0f, 0f, 0, 0));
        }
```

- [ ] **Step 3: Run to verify it fails**

Expected: compile error — `TooltipText` does not exist.

- [ ] **Step 4: Implement**

Create `Source/TooltipText.cs`:

```csharp
using System.Text;
using Verse;

namespace WealthReadout
{
    // Pure formatting: numbers in, tooltip string out. No Map access and no lookups, so it can be
    // exercised from the main menu.
    public static class TooltipText
    {
        // Every .Translate() call happens here, at call time, and no translated value is ever
        // stored in a field. The language is in fact loaded well before [StaticConstructorOnStartup]
        // runs, so resolving early would look correct -- but a static constructor runs once per
        // process, so the value would be frozen at whatever language was active then and would
        // survive the player switching language in the options menu.
        public static string Build(string label, float wealth, float share,
                                   int storedCount, int elsewhereCount)
        {
            var sb = new StringBuilder();
            sb.Append(label);
            sb.Append('\n');

            // Silver is rounded to whole units: fractions of a silver are noise at colony scale,
            // and the panel is narrow. "N0" keeps the number bare so the key supplies the unit;
            // ToStringMoney would print "$1190 silver", since it already carries the currency.
            sb.Append("WealthReadout.Line.Wealth".Translate(
                wealth.ToString("N0"),
                share.ToStringPercent("F1")));

            // The split line is suppressed when there is nothing elsewhere, which is the common
            // case for a tidy colony. Printing "240 stored · 0 elsewhere" on every row would make
            // the interesting case harder to spot.
            if (elsewhereCount > 0)
            {
                sb.Append('\n');
                sb.Append("WealthReadout.Line.Split".Translate(
                    storedCount.ToStringCached(),
                    elsewhereCount.ToStringCached()));
            }

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: build, launch, and from the main menu run **Wealth Readout → Print sample tooltips**.

Expected in `Player.log`:

```
Sample A:
Foods
1,190 silver · 2.6% of colony wealth
240 stored · 70 elsewhere

Sample B (nothing elsewhere):
Plasteel
1,840 silver · 4.1% of colony wealth

Sample C (zero wealth):
Chocolate
0 silver · 0.0% of colony wealth
```

If a line reads `WealthReadout.Line.Wealth` instead of the text, a key resolved too early or the
Keyed file is not being loaded — check `LoadFolders.xml` before touching the code.

- [ ] **Step 6: Commit**

```bash
git add Source/TooltipText.cs Languages Source/DebugActions.cs
git commit -m "feat: tooltip text builder and English keys"
```

---

### Task 6: Categorized-mode patches

**Files:**
- Create: `Source/ReadoutPatches.cs`

**Interfaces:**
- Consumes: `WealthIndex.WealthOf(ThingDef)`, `WealthIndex.WealthOf(ThingCategoryDef)`, `WealthIndex.TotalCountOf(...)`, `WealthIndex.ShareOf(float)`, `WealthIndex.ElsewhereCount(int,int)`, `TooltipText.Build(...)`.
- Produces: `WealthReadout.ReadoutPatches.ReplaceTip(Rect rect, string text, int uniqueId)` → `void`, reused by Task 7.

- [ ] **Step 1: Implement the patches**

There is no separate failing-test step here: the deliverable is visual, and its check is step 2.

Create `Source/ReadoutPatches.cs`:

```csharp
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
            int total = WealthIndex.TotalCountOf(thingDef);
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
```

`DoThingDef` is private, so it is targeted by string name rather than `nameof`.

- [ ] **Step 2: Verify in game**

Build, launch, load a colony. Ensure **Options → Resource readout categorized** is ON.

Expected:
- Hovering `Materials` shows silver, a percentage, and a stored/elsewhere line if anything is loose.
- Hovering `Steel` under it shows steel's own figures.
- The parent's silver equals the sum of its visible children's.
- No tooltip appears over blank space below the tree.
- No errors in `Player.log`.

- [ ] **Step 3: Commit**

```bash
git add Source/ReadoutPatches.cs
git commit -m "feat: wealth tooltips on the categorized resource readout"
```

---

### Task 7: Simple-mode patch

**Files:**
- Modify: `Source/ReadoutPatches.cs`

**Interfaces:**
- Consumes: `ReadoutPatches.ReplaceTip(Rect, string, int)`, `TooltipText.Build(...)`, `WealthIndex.*`.
- Produces: nothing further.

- [ ] **Step 1: Implement**

Add to `Source/ReadoutPatches.cs`:

```csharp
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
            Map map = Find.CurrentMap;
            if (map == null) return;

            var rect = new Rect(x, y, 27f, 27f);

            float wealth = WealthIndex.WealthOf(thingDef);
            int total = WealthIndex.TotalCountOf(thingDef);
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
```

- [ ] **Step 2: Verify in game**

Build, launch, load a colony. Turn **Options → Resource readout categorized** OFF.

Expected:
- Hovering a resource *icon* shows the wealth tooltip.
- Hovering the number beside the icon shows nothing, exactly as in vanilla.
- Toggling the option back on still works (verification item 5).
- No errors in `Player.log`.

- [ ] **Step 3: Commit**

```bash
git add Source/ReadoutPatches.cs
git commit -m "feat: wealth tooltips in simple readout mode"
```

---

### Task 8: Profile the staleness constant and close the open items

The release gate. `StalenessTicks` ships as a constant with no user-side escape, so a bad value is a patch.

**Files:**
- Modify: `Source/DebugActions.cs`
- Modify: `Source/WealthIndex.cs` (only if profiling says the constant is wrong)
- Modify: `HANDOVER.md`

**Interfaces:**
- Consumes: `WealthIndex.Rebuild(Map)`.
- Produces: nothing consumed by other tasks.

- [ ] **Step 1: Add the profiling action**

Add to `Source/DebugActions.cs`:

```csharp
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
```

- [ ] **Step 2: Run it on a late-game map**

Load the largest, longest-running colony available — ideally one with a heavy modlist and full
stockpiles.

Run: **Wealth Readout → Profile index rebuild**. Record the ms/run figure.

Decision rule: a rebuild is one frame's hitch, and it happens at most once per `StalenessTicks`
while the player is hovering.
- Under ~5 ms: leave `StalenessTicks = 1000`.
- 5–15 ms: raise to `2500`.
- Over 15 ms: raise to `5000`, matching `WealthWatcher.MinCountInterval`, and record the measured
  figure in `HANDOVER.md`.

- [ ] **Step 3: Apply the decision**

If the constant changes, edit `StalenessTicks` in `Source/WealthIndex.cs` and update its comment to
cite the measured number and the map it came from. Replace the guess with evidence.

- [ ] **Step 4: Test against a deep-storage mod (open item 2)**

Enable a deep-storage mod, put resources in one of its containers, hover the row.

Expected: `stored` matches the row's printed count. If the container's contents are counted by
`ResourceCounter` they appear in `stored`; if not, they appear in `elsewhere`. Either is acceptable
so long as the tooltip does not contradict the row's own number. Record the observed behaviour in
`HANDOVER.md`.

- [ ] **Step 5: Test against another readout mod (open item 4)**

Enable a mod that patches the resource readout. Verify both mods' behaviour survives and
`Player.log` has no Harmony conflict warnings. Record the mod tested in `HANDOVER.md`.

- [ ] **Step 6: Update `HANDOVER.md`**

Move each closed item out of "Open items" and into a new "Closed gates" section with the evidence —
the measured number, the mods tested, the date. An open item closed without a recorded number is
not closed.

- [ ] **Step 7: Commit**

```bash
git add Source/DebugActions.cs Source/WealthIndex.cs HANDOVER.md
git commit -m "test: profile the rebuild and close the deep-storage and conflict gates"
```

---

## Notes for the implementer

**The one mistake that matters.** Task 2's walk is the mod. If it drifts from
`CalculateWealthItems`, every number is wrong and everything still looks fine. When the
reconciliation action fails, the answer is never to widen the tolerance.

**Two known divergences, both expected:**
- `ForceRecount` folds pocket maps into `WealthItems`; our walk covers the current map only. Reconcile
  on a colony without pocket maps.
- `ResourceCounter` unwraps minified things (`GetInnerIfMinified`) while the wealth walk values the
  minified container, so `stored` can exceed our total. `ElsewhereCount` clamps at zero for this.

**If you find yourself wanting a `MapComponent`, a def, or a settings page, stop.** All three are
banned by HANDOVER.md rule 1, and the design was built specifically to avoid needing them.
