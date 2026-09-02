# Wealth Readout — Handover

Why this mod is shaped the way it is. The spec at
`docs/superpowers/specs/2026-09-01-wealth-readout-design.md` says *what* to build. This file says
*why*, and records what was already tried and rejected so it is not rediscovered at cost.

Started 2026-09-01. No code exists yet.

**All vanilla code claims here were verified against an ILSpy decompile of 1.6
`Assembly-CSharp.dll`.** Where a claim is unverified, it says so.

---

## The two rules

These override features. A proposal that breaks either is out of scope no matter how good it is.

### 1. Never touch save data

The mod must be addable to and removable from **any** save at **any** time — no warning, no version
bump, no corruption, no "this save was created with mods that are no longer installed."

Concretely, this bans: defs, `GameComponent`, `MapComponent`, anything scribed, and `ModSettings`.
The mod is Harmony patches and nothing else. The wealth index is a plain in-memory cache, rebuilt on
demand and discarded with the session.

This is currently free — the design needs none of those things. **The rule exists so it stays free.**
The temptation arrives later, when someone wants to persist a cached breakdown across a save/load to
skip a rebuild, or store a per-player preference. Both are refusals.

### 2. Do not diverge from vanilla; stay minimal

The smallest patch surface that does the job, and no behaviour change beyond it.

The mod changes tooltip **text**. It adds no rows, no windows, no UI elements, no keybinds; it moves
nothing, resizes nothing, and its hover regions match vanilla's exactly. Four patches — two
prefix/postfix pairs on `Listing_ResourceReadout`, one postfix on `ResourceReadout.DrawIcon` — and no
transpilers anywhere.

This is also the conflict posture. Postfixes that only rewrite tooltip text let other readout mods
patch the same methods without either mod breaking the other. A transpiler would not.

---

## Read this first: four traps

**1. The wealth walk must be mirrored exactly, or the mod is silently wrong.**

`WealthWatcher.CalculateWealthItems` is the definition of what wealth *is* for the storyteller:
`GetAllThingsRecursively` over `HaulableEver`, filtered by `WealthWatcher.WealthItemsFilter` (which
is `public static` — reuse it, never reimplement it), skipping anything not
`SpawnedOrAnyParentSpawned` or whose `PositionHeld` is fogged, summing `MarketValue * stackCount`.

Change the request group, hand-roll the filter, or drop the fogged check, and the tooltip reports a
wealth the storyteller does not use. **Every test still passes and the numbers still look plausible.**
This is the easiest way to destroy the mod without noticing. Verification item 1 in the spec exists
solely to catch it: our index must equal `wealthWatcher.WealthItems` after a `ForceRecount` on the
same tick.

**2. The readout and wealth count different things — this is the whole design, not an edge case.**

`ResourceCounter` walks only `SlotGroup`s and only defs flagged `CountAsResource`. `WealthWatcher`
walks everything haulable, stored or not, including pawn inventories and containers.

So the row says 240 and the truth is 310, and whole categories — weapons, apparel, art, furniture —
have no row at all because they are not `CountAsResource`. Do not "fix" the row count to match. The
tooltip reports true wealth and names the gap; that is the decision.

**4. A readout row is not the head of its category subtree.**

`ResourceReadout` builds its top level from *every* category with `resourceReadoutRoot`, and both
the drawing and the counting then refuse to descend into another root:

```csharp
Listing_ResourceReadout.DoCategoryChildren:
    if (!childCategoryNode.catDef.resourceReadoutRoot) DoCategory(...);
ResourceCounter.GetCountIn:
    if (!cat.childCategories[j].resourceReadoutRoot) num += GetCountIn(...);
```

In Core, `Foods`, `FoodMeals` and `FoodRaw` are all three roots, and the latter two are children of
`Foods`. So the **Foods row covers only the defs sitting directly in Foods** — Biotech's baby food
and hemogen packs. Meals and raw food are separate rows, siblings rather than contents.

This shipped wrong: the rollups recursed into every child category, so a colony with 2,471 stored
under Foods was told **"46,642 elsewhere"** — roughly 49k of rice and meat swept in from rows that
Foods does not represent. The tooltip's own three numbers were describing three different scopes.

The rule: **a row's scope is its own `childThingDefs` plus its NON-root child categories,
transitively.** Wealth, total count and vanilla's stored count must all be taken over that one def
set. `WealthIndex.CategoryDefs` is that set; do not restate the rule anywhere else.

**3. "On hover" cannot mean "per frame."**

`TooltipHandler.TipRegion` is called on every Repaint while the mouse is over a row — a two-second
hover is roughly 120 calls. Computing wealth per call means 120 full-map walks. The index is built by
the *first* hover that needs it and cached against a tick-based staleness constant.

The saving grace: bucketing every def costs the same as bucketing one, because the walk visits every
thing either way. So one pass serves the entire tree.

---

## The decision trail

### The target UI was misidentified first, and it cost several rounds

"The categories on the left-hand side" is the **resource readout** (`RimWorld.ResourceReadout`) — the
always-visible top-left panel that becomes a category tree when `Prefs.ResourceReadoutCategorized` is
on. It is **not** the `ThingFilter` tree in stockpile and bill dialogs.

Both are `Listing_Tree` category trees with the same Foods → Meals → Simple meal shape, and a verbal
description does not distinguish them. The tells are "always open" and "can be changed to have
categories" — the latter is the readout's own options toggle.

If a future session designs against a RimWorld category tree, confirm which panel before writing
anything.

### Why the tooltip is replaced rather than appended

`TooltipHandler.TipRegion` keys on `uniqueId` and overwrites:

```csharp
if (!activeTips.ContainsKey(tip.uniqueId)) { ...; firstTriggerTime = now; }
activeTips[tip.uniqueId].signal.text = tip.text;   // last call in the frame wins
```

Re-registering under vanilla's id therefore replaces vanilla's text. All three patches do exactly
that and nothing else — see below for why the original approach was wrong.

### `ClearTooltipsFrom` is not rect-scoped — this bug cost a release

**Corrected 2026-09-01, after the first in-game test showed no tooltip at all on hover — not ours,
and not even vanilla's.**

The original design used `TooltipHandler.ClearTooltipsFrom(rect)` and then registered under our own
id, so that neither mode had to know vanilla's id scheme. That reasoning was built on the name and
the `Rect` parameter. The actual 1.6 implementation:

```csharp
public static void ClearTooltipsFrom(Rect rect)
{
    if (Event.current.type != EventType.Repaint || !Mouse.IsOver(rect)) return;
    foreach (var pair in activeTips)
        if (pair.Value.lastTriggerFrame == frame) dyingTips.Add(pair.Key);   // EVERY tip this frame
    ...
}
```

The rect is only a gate. What gets removed is **every tooltip registered anywhere this frame**. So
each Repaint: vanilla registered its tip, we deleted it, and our re-registration created a *new*
`ActiveTip` — and a new tip is stamped `firstTriggerTime = Time.realtimeSinceStartup`. The draw gate
is

```csharp
if (realtimeSinceStartup > value.firstTriggerTime + value.signal.delay) drawingTips.Add(value);
```

with `delay = 0.45f` on every `TipSignal` constructor in play. Resetting the stamp 60 times a second
means the 0.45 s threshold is never reached, so nothing ever drew.

**The fix:** never call `ClearTooltipsFrom`. Register under vanilla's own `uniqueId`, which
overwrites the text while leaving `firstTriggerTime` untouched.

In categorized mode the ids were already knowable and already correct (`catDef.GetHashCode()`,
`thingDef.shortHash`). Simple mode was the case the clear existed to avoid: `DrawIcon` uses
`TipSignal(TaggedString)`, whose id is the tagged string's hash. `ReadoutPatches.SimpleIconTipId`
reconstructs that string and hands it to `TipSignal` rather than hashing it locally — `TaggedString`
does not override `GetHashCode`, so the id comes from `ValueType.GetHashCode` and must not be
reimplemented.

**Residual risk, accepted:** if that reconstruction ever drifts from `DrawIcon`'s expression, the
ids stop matching and the player gets two stacked tooltips. That is visible rather than silent, and
the *Check simple-mode tooltip id* debug action compares the helper against a verbatim copy of
vanilla's expression across every resource def. Re-run it on every version bump.
**Last run 2026-09-02: PASS, 334 defs, 0 mismatched** (on a reduced modlist, not the full 40–50).

**The general lesson:** a vanilla method taking a `Rect` was assumed to be scoped to that rect
without reading it. Every other vanilla claim in this file was verified against the decompile; this
one was not, and it was the one that broke the mod.

### The percentage denominator is not `WealthTotal`

```
denominator = ourItemsTotal + (WealthTotal - WealthItems)
```

`WealthWatcher` recounts at most every 5000 ticks (`MinCountInterval`), so its `WealthItems` and our
fresh pass come from different moments. Using `WealthTotal` directly mixes a fresh numerator with a
stale denominator and produces category shares that do not add up. Taking only the
buildings/pawns/floors remainder from vanilla, and the items half from our own pass, is exact.

### Why there is no settings page

Dropped whole, after each proposed entry failed on its own terms. The full reasoning is in the spec's
"Rejected" section; the short form:

- **Percentage toggle** — removes information the user requested by hovering. Nobody touches it.
- **"Count unstored" toggle** — would let the player select a *wrong answer*. Rejected on principle.
  If the mismatch reads badly, fix the wording, never the counting.
- **Raid-point impact** — blocked concretely, not out of caution. Answering it honestly requires
  differencing the real threat curve, which requires substituting a fake wealth underneath
  `map.PlayerWealthForStoryteller`. That is the invasive hook the sibling **Perceived Wealth** mod is
  built around, and it breaks rule 2 here. Reimplementing the curve drifts from vanilla and from
  every difficulty mod. The feature belongs in a mod already paying that cost.
- **Staleness interval** — defensible, but cannot justify a page alone.

Consequence to accept: the staleness constant cannot be worked around by a user with a heavy modlist,
so **profiling it is mandatory before release**, not optional.

### Why "elsewhere", not "unstored"

The non-`SlotGroup` remainder includes pawn inventories, caravan packs and containers — not only
things lying on the ground. "Unstored" implies a pending hauling job that most of that remainder does
not have.

---

## Rejected — do not rediscover

**The "not listed" footer.** A line under the tree summarising wealth with no row
(`Not listed: 31,600 (68%)`), hoverable for a buildings / pawns / gear split.

It was designed, then dropped once the percentage was recognised as already carrying the honesty —
its denominator is total colony wealth, so a player sweeping rows that read 2–4% can see the bulk is
elsewhere.

Dropping it was structural, not cosmetic. The footer required "total minus the root-level rows that
actually rendered this frame," which meant a further patch on `ResourceReadout.ResourceReadoutOnGUI`,
a per-frame accumulation set, and a real correctness trap: **a `ThingDef` can belong to more than one
`ThingCategoryDef`**, so naive root-level summing double-counts and makes the remainder too small.

**Do not reintroduce the footer without solving the multi-category dedupe first.**

**Adding rows for weapons/apparel/art.** Changes what the vanilla panel is for — it is a *resource*
readout — and maximises conflict with other readout mods. Breaks rule 2.

**An inline silver figure on each row.** Vanilla already right-aligns a count there and the panel is
narrow. Not v1.

**Per-zone numbers.** An artefact of the misidentified UI. The readout is map-wide by construction.

---

## Parked as a separate mod

**Wealth in the Architect build menu** — hovering a building shows how much wealth placing it would
add, before you commit. Forward-looking rather than current-state, a different UI, a different patch
surface.

Explicitly not part of this mod. If built, it gets its own repo, matching how Perceived Wealth is
kept separate from the Varied Pawns mod.

---

## Why the test did not catch trap 4

The "Check category subtree summing" action compared the rollups against `cat.DescendantThingDefs`,
which recurses every child category — including `resourceReadoutRoot` ones. That is *our* traversal
assumption, not vanilla's. The check therefore restated the bug and agreed with it across a full
implementation cycle.

It now makes two comparisons, and the second is the one with teeth: `WealthIndex.StoredCountOf`
sums vanilla's own per-def counts over our def set, and must equal `ResourceCounter.GetCountIn`,
which vanilla computes by its own independent recursion. One side of that equation is not ours, so
a traversal that drifts fails immediately.

**The general lesson, and it is the same one as the `ClearTooltipsFrom` bug:** a check written from
the same understanding as the code tests nothing. Anchor every check to a vanilla-computed number.

## The debug harness was removed before release, 2026-09-02

`Source/DebugActions.cs` and its eight `[DebugAction]` entries are **gone**, deleted after the
verification pass below had already been run and recorded. Everything from here to the end of this
file that describes running an action is therefore a **historical record of a harness that no
longer exists**. The results stand; the instrument does not.

**Why.** Eight rows in the shared debug menu is a disproportionate footprint for a mod whose entire
product is tooltip text, and that menu is a commons — every mod's actions land in the same list.
Rule 2's minimal-surface posture covers the dev menu too.

**What went with it.** `WealthIndex.StoredCountOf`, `WealthIndex.CategoryDefs` and
`WealthIndex.ItemsTotal` had no other callers and were deleted in the same change. Nothing on the
tooltip path changed, and the assembly builds with 0 warnings.

**What that costs, stated plainly rather than minimised.** The `StoredCountOf` vs
`ResourceCounter.GetCountIn` comparison was the only check in the project anchored to a number
vanilla computes independently — the exact thing the section above calls "the one with teeth", and
the thing whose absence let trap 4 ship. That anchor is now gone. So is the rebuild profiler that
produced the 10.94/12.16 ms figures behind `StalenessTicks = 2500`; that constant is now a recorded
measurement with no way to re-take it.

**How to re-verify after a version bump** (open item 3, and any future change to the traversal
rule): restore the harness from git rather than writing a new one. It is preserved in full at
commit `f523343`, the last commit before this removal:

```
git show f523343:Source/DebugActions.cs > Source/DebugActions.cs
git show f523343:Source/WealthIndex.cs   # for StoredCountOf / CategoryDefs / ItemsTotal
```

Build, test in-game, then delete it again before staging a release. **Do not reimplement the checks
from scratch** — the restored file already encodes which comparisons have teeth and which are
self-agreeing, and rediscovering that distinction is precisely what cost a full implementation
cycle the first time.

The one check that survives without the harness is the simple-mode tooltip id: hover a resource
icon with `resourceReadoutCategorized` off, and one tooltip rather than two stacked means the id
still matches.

## Driving the debug actions — historical; two corrections from 2026-09-02

*The harness these describe was removed — see the section above. Kept because the two failure modes
below are properties of the GABS bridge and the RimWorld log cap, not of our actions, and they will
recur the moment the harness is restored for a version-bump re-check.*

The inherited recipe said: don't call `rimworld/search_debug_actions` (it walks vanilla's incident
nodes and NREs inside `Verse.DebugActionsIncidents.RitualSiegeWithSpecifics`, raising a blocking
GABS attention item); use `rimworld/list_debug_action_children` on `Actions` and filter locally.

**1. The listing route is not a safe alternative.** `list_debug_action_children` on `Actions` hits
the *same* `RitualSiegeWithSpecifics` NRE and raises the same blocking attention item — listing a
node calls `TrySetupChildren` on its children, which invokes that method. Observed at `Entry`.

What works is skipping discovery entirely: call `rimworld/execute_debug_action` with the literal
path `Actions\<label>`. The labels are in `Source/DebugActions.cs`; read them from there rather
than asking the game. The category is still not part of the path.

**2. A flood of load errors silences every action's report.** RimWorld's log cap
(`Reached max messages limit. Stopping logging to avoid spam.`) makes `Log.Message` a no-op for the
remainder of the session, so actions still execute and still return `success: true` while
`effects.logs` is empty and Player.log gains nothing. No bridge tool resets it; only a restart does.
A save loaded with a partial modlist reaches the cap during load, before any action runs.

So the empty-log rule has a second cause beyond startup timing: **check Player.log for the
`max messages limit` line before concluding an action produced no output.**

## Next steps, in order

Publishing sequence agreed 2026-09-02. Do these in order: several later steps depend on
artefacts the earlier ones produce.

1. **User reviews the code. — DONE 2026-09-02.** Reviewed by the owner, and the tree is committed.

   The working tree was uncommitted when this step was written; it no longer is. Six commits
   landed: the publish scaffolding, the packageId rename, the `StalenessTicks` measurement, the
   debug-harness removal, this document, and the staging script's stale harness pointer. The
   upload now corresponds to a commit someone can check out, so `build-release.ps1`'s
   dirty-tree warning is silent.

   Two changes to the repo itself came with it, and they are worth knowing before you push:

   - **The remote exists**: `origin` is `https://github.com/kalas1230/Rimworld-Wealth_Tooltips.git`.
     Nothing has been pushed yet. Step 3 below still stands — the *page* has to be created and
     `<url>` added to `About.xml`.
   - **Every commit SHA changed.** All 28 commits were rewritten to author and commit as
     `kalas1230 <gokalpxd@gmail.com>`; twelve had been made under a different identity. Any SHA
     written down elsewhere before 2026-09-02 is dead. The ones in this file were updated with
     it. Identity is now pinned for every repo under `Desktop\Rimworld-mod` by an `includeIf`
     in the global git config, so this cannot recur here.

2. **Run a last in-game test.** Every in-game verification in this file was performed under the
   old `kalas.wealthreadout` packageId and the old Harmony instance id. Both were renamed on
   2026-09-02, the assembly was rebuilt, and the game has not been launched since.

   That rename already produced one silent breakage: an illegal `--` inside an XML comment left
   `About.xml` unparseable, and RimWorld skips a mod whose `About.xml` will not parse, so it
   would have installed and done nothing. The only visible symptom was the release zip quietly
   falling back to a `-dev` filename because `modVersion` could not be read. A preflight parse
   check now guards that specific fault, but the general point stands: **a clean build says
   nothing about whether the game still loads and patches the mod under its new identity.**

   Launch once, confirm `[Wealth Readout] Patches applied.` in Player.log, and hover one readout
   row. Two minutes, and it covers the one thing no build-time check can.

3. **Create the GitHub page for the mod.** This repo has no remote. Two things depend on it and
   both must be done *before* step 4, because they change the file that gets shipped:
   - Add `<url>` to `About/About.xml`. RimWorld renders it as a clickable link in the mod info
     panel, and it is the only route back to source for a player who installed from a modpack.
   - Add the source and bug-report link to the end of the Workshop description
     (`docs/workshop-description.txt`), which mod managers and modpack listings show instead of
     the `<url>` field.

4. **Double check the Steam description and the PNGs, especially the in-game description shown
   on the mod page.** Three separate texts exist and they drift apart if edited singly:
   - `About/About.xml` `<description>` is the **in-game** mod list text.
   - `docs/workshop-description.txt` (below the `====` divider) is the **Workshop** listing.
   - `Release/upload/description-paste.txt` is generated from the file above. Paste that one, not
     the source file, and never a hand-copy: hand-copies of this text previously accumulated 22
     mojibake sequences in the sibling repo before the script took ownership of it.

   Known image issues, both open:
   - `About/Preview.png` is **374x638, portrait**. Steam renders the Workshop thumbnail
     landscape, so it will letterbox with large empty side bars. A purpose-made image around
     640x360 would present far better. The two screenshots are fine as *gallery* images and are
     kept at `docs/workshop-images/`.
   - `About/ModIcon.png` is absent (the mod list row icon). Cosmetic; the staging script warns
     rather than blocks.

5. **Double check that only the mod is in the folder that gets published.** Run
   `.\tools\build-release.ps1 -Check`. It re-derives the expected file set from the repo,
   hashes every staged file against its source, and fails on anything stale, missing or
   unexpected. It exists because a staged folder is a build output with an indefinite shelf life
   sitting in exactly the folder the uploader is pointed at, and `Release/` is gitignored, so
   nothing in git tracks whether it is current.

   Expected contents, and nothing else:

   ```
   About/About.xml   About/LoadFolders.xml   About/Preview.png
   Assemblies/WealthReadout.dll   Languages/English/Keyed/WealthReadout.xml   LICENSE
   ```

   Point the Steam uploader at `Release\Wealth Tooltips\`, never at the repo root.

   **Run once on 2026-09-02, and it failed as it should.** File set clean — 6 staged, 6
   expected, nothing missing, nothing extra, no dev content leaked. One file stale:
   `Assemblies\WealthReadout.dll`, staged at 24,064 bytes against a freshly built 10,752. That
   drop is the debug harness coming out, so the staged copy is a pre-removal build carrying all
   eight debug actions — exactly the "indefinite shelf life" failure this check exists for.

   **Do not restage yet.** The obvious response is the `-Build -Zip` the script suggests, and
   it is premature: `About.xml` has no `<url>` element until step 3, and that file ships, so
   anything staged now goes stale the moment the link is added. Restage after steps 3 and 4,
   then re-run this and expect green.

   One cosmetic artifact: the check's header reads `from commit 79ee0f7`, a SHA that no longer
   exists after the authorship rewrite. It is recorded staging metadata and regenerates on the
   next stage. Nothing to fix.

6. **Create the Steam page and publish.** After the first successful upload RimWorld writes
   `About/PublishedFileId.txt`. **Commit that file and never delete it.** It is what binds this
   folder to the Workshop item; without it the next upload creates a *second, duplicate* item
   instead of updating the first, and the original cannot be reclaimed.

7. **Create a GitHub release.** Tag it to match `About/About.xml`'s `<modVersion>` (currently
   1.0.0) and attach `Release/WealthTooltips-<version>.zip`, which
   `build-release.ps1 -Zip` produces. Bump `<modVersion>` on every subsequent Workshop update:
   RimWorld ignores the field, but mod managers display it and the zip is named from it.

8. **Cross-link the two mods, and put the GitHub link at the end of both descriptions.**
   Last, because it needs URLs that do not exist until the earlier steps are done: the Workshop
   item is only created in step 6, and the repo only gets a URL in step 3.

   - Add a link to **Wealth Tooltips** in **Varied Pawns**' description.
   - Add a link to **Varied Pawns** in **Wealth Tooltips**' description.
   - Add the GitHub link at the end of both.

   Edit the descriptions through each repo's `docs/workshop-description.txt` and re-run that
   repo's `tools/build-release.ps1`, then paste the regenerated
   `Release/upload/description-paste.txt`. Do not type the change straight into the Steam
   description box: the repo file is the source of truth, and editing only the live listing is
   exactly how the two copies drift apart. Both mods keep an in-game `About.xml` `<description>`
   as well, so decide per link whether it belongs in the Workshop text, the in-game text, or
   both, and change every copy you choose.

   Note that **Perceived Wealth** is a third, design-only sibling with no code and nothing
   published. It is not part of this cross-linking; do not advertise it.

## Open items

**Status after the 2026-09-02 session: 1, 2, 4 and 5 all closed. 3 is the only one left, and it
is a version-bump gate with nothing to do until Ludeon changes the readout's layout.**

1. **Profile the staleness constant. — CLOSED 2026-09-02.** `StalenessTicks` is now **2500**,
   set from measurement rather than caution.

   *Profile index rebuild* on the owner's heavy colony reported **10.94 ms/run**, then
   **12.16 ms/run** on a repeat, 20 runs each after a warm-up. Map: **250×250** — the largest
   vanilla size — at **1,099,095** total wealth, 58 active mods including Combat Extended,
   Adaptive Storage Framework, Neat Storage and Gravship Storage. The plan's rule maps 5–15 ms
   to 2500, and both samples sit well inside that band rather than near a boundary, so the
   choice does not hinge on which sample you take.

   At 2500 against vanilla's `MinCountInterval` of 5000, our index is now the fresher of the
   two halves of the percentage denominator; the comment on `Denominator` was updated to match,
   since it previously reasoned from the two intervals being equal.

   **The first attempt, earlier the same day, produced no figure and the reason is worth
   keeping.** The save was loaded with only part of its mod list. That threw ~9,978 load errors
   — thousands of dropped `ThingDef` references plus absent Combat Extended — which did two
   things at once: it removed things from the map, so any timing would have understated the
   real cost, and it exhausted RimWorld's log cap (`Reached max messages limit. Stopping
   logging to avoid spam.`), which makes `Log.Message` a no-op for the rest of the session. The
   action still ran and still returned `success: true` while reporting nothing. Reloading with
   the full list dropped the error count to 64 and both problems went away together.

   The lesson generalises past this item: **a degraded load can silence the instrument and
   corrupt the measurement at the same time, and the silence hides the corruption.** Check
   Player.log for the cap line before trusting — or disbelieving — any debug action's output.

2. **`IsInAnyStorage` vs deep-storage mods.** The stored/elsewhere split depends on what counts as
   storage; container-adding mods may make it misleading. Test against at least one.

   **First evidence, 2026-09-02 — encouraging, not yet conclusive.** Measured on the heavy save
   with Adaptive Storage Framework, Neat Storage, Neat Storage Fridge and Gravship Storage all
   active: steel `total=26726 stored=25326 elsewhere=1400` (5.2% elsewhere), raw resources
   `total=54677 stored=52571 elsewhere=2106` (3.9%). `rawDiff == elsewhere` in both, so nothing
   was clamped at zero.

   Those shares are what a tidy colony should look like, which says the deep containers *are*
   being recognised by `IsInAnyStorage` rather than dumping their contents into "elsewhere" —
   the specific failure this item was opened for.

   **CLOSED the same day, by two anchored perturbations.**

   - *Negative case.* 75 steel spawned on open ground moved `total` 26726 → 26801 and
     `elsewhere` 1400 → 1475, leaving `stored` at 25326. Exactly the placed quantity, in
     exactly the expected bucket.
   - *Positive case.* The owner destroyed a Gravship Storage container holding steel and the
     figures moved accordingly — so a deep container's contents are inside vanilla's stored
     count, which is the thing this item doubted.

   **The scope ruling that comes with it, and it bounds our responsibility:** our `stored` is
   `ResourceCounter`'s own number, which counts `SlotGroup`s. If a container-adding mod's
   contents did *not* move these figures, that mod is failing to register as a vanilla
   `SlotGroup` — a defect on its side of the contract, not a miscount on ours. We read vanilla;
   we cannot be liable for a mod that does not implement what vanilla defines. Do not add
   compensating logic for such a mod; it would break rule 2 and trap 1 together.
3. **Rect reconstruction is coupled to vanilla's layout arithmetic.** If Ludeon changes indentation
   or line height in the readout, the tooltip region drifts off the row. Re-check `DoCategory`'s rect
   construction on every version bump. The debug harness that used to back this re-check has since
   been deleted; restore it from commit `f523343` first — see "The debug harness was removed before
   release" for the procedure and for what it costs.
4. **Conflict test against other readout mods.** Postfixes are friendly here, but verify rather than
   assume. **CLOSED 2026-09-02** — tested with `visiblewealth.1trickpwnyta` active.

   - Both mods patched without either throwing: `[Wealth Readout] Patches applied.` on a clean
     load.
   - *Reconcile* still exact under conflict conditions: `ours=721120.40 vanilla=721120.40
     diff=0.00`.
   - **One tooltip, not two.** Verified visually, which is the only way this failure shows.
     Hovering the Raw resources row rendered a single tooltip reading
     `132,253 silver · 12.0% of colony wealth / 52571 stored · 2106 elsewhere`, matching the
     debug action's `total=54677 stored=52571 elsewhere=2106` exactly. A `get_ui_layout` capture
     showed one tooltip window, not a stacked pair.

   Postfix-only really is the friendly posture it was designed to be.

## Verification pass, 2026-09-02

All eight debug actions run against the heavy save described in item 1. Every check that anchors
to a vanilla-computed number passed exactly, which is the only kind of pass this file counts.

*These results were taken before the harness was deleted — see "The debug harness was removed
before release". They are the record of what passed, not a procedure you can re-run as written.*

| Action | Result |
| --- | --- |
| Reconcile index vs WealthWatcher | PASS — `ours=721120.30 vanilla=721120.30 diff=0.00` (trap 1) |
| Check category subtree summing | PASS — 330 checked, 0 internal, 0 vanilla-scope mismatches (trap 4) |
| Check share denominator | PASS — `721120.30 + 377974.40 = 1099095.00`, diff 0.00 vs vanilla |
| Check simple-mode tooltip id | PASS — 334 defs, 0 mismatched |
| Print sample tooltips | PASS |
| Report stored vs elsewhere (steel) | `26726 / 25326 stored / 1400 elsewhere`, wealth 50779.40 |
| Report stored vs elsewhere (category) | `54677 / 52571 stored / 2106 elsewhere` |
| Profile index rebuild | 10.94 then 12.16 ms/run — see item 1 |

The reconcile and denominator numbers agreeing to the cent on a 1.1M-wealth map with 58 mods is
the strongest evidence yet that the wealth walk mirrors vanilla's, since one side of each
comparison is computed by vanilla itself.

### Simple (non-categorized) mode, verified live

Previously only the *id* was checked statically. The owner switched
`resourceReadoutCategorized` off and confirmed the mod behaves correctly in simple mode: the
tooltip renders, with our text, and does not stack with vanilla's.

One observation worth pre-empting, because it reads as a bug and is not: **in simple mode the
tooltip appears when hovering the icon, not the count beside it.** That is vanilla's own hover
region. `ResourceReadout.DrawIcon` registers its tip on a 27×27 icon rect, and our postfix
re-registers on that same rect rather than widening it to the row. Widening is a one-line change
and is exactly the kind of "improvement" rule 2 refuses — it would be a UI change, and it would
make our hover target differ from every other readout mod's. Leave it.

### Cross-mod wealth comparisons: check freshness before calling it a conflict

A Visible Wealth figure was observed sitting exactly 75 steel below ours. It looked like a
scope disagreement — the natural reading being that one mod counts unstored items and the other
does not.

It was neither mod's bug. 75 steel had just been spawned while the game was **paused**. That
mod reads vanilla's cached `wealthWatcher` value, which recounts at most every
`MinCountInterval` (5000 ticks); ours rebuilds on demand. With no ticks elapsing, vanilla's
cache never refreshed, so the gap was precisely the perturbation. Letting the game run for a
while equalised the two.

Two things follow. First, **the mods agree once vanilla is fresh** — independent corroboration
that neither diverges from `WealthWatcher`. Second, this is the staleness the `Denominator`
design already reasons about, now observed rather than argued: a fresh numerator over a stale
vanilla figure produces exactly this kind of phantom discrepancy. Before investigating any
future cross-mod mismatch, force a recount (the *Reconcile* action calls
`wealthWatcher.ForceRecount()`) or let the game tick, and re-compare.

5. **Freshness asymmetry between `stored` and `elsewhere`. — CLOSED 2026-09-02, not reachable
   in practice. Nothing was changed, and nothing should be.**

   The asymmetry is real in code. `ResourceCounter.ShouldCount`, decompiled in full (the spec
   quoted only the call site):

   ```csharp
   private bool ShouldCount(Thing t)
   {
       if (t.IsNotFresh()) return false;
       if (t.SpawnedOrAnyParentSpawned && t.PositionHeld.Fogged(t.MapHeld)) return false;
       return true;
   }
   ```

   Two exclusions, and **the fogged one is symmetric** — our walk skips fogged things too. So rot
   is the sole asymmetry, which this file previously asserted without establishing.
   `IsNotFresh()` is `CompRottable` present and `Stage != RotStage.Fresh`. On the wealth side
   there is no freshness test, and `MarketValue`'s only `statParts` are `StatPart_Health` and a
   Biotech age part for pawns — **rot does not reduce market value**. So on paper a stack that
   stops being fresh loses its whole count from `stored` while keeping its whole wealth.

   **It does not survive to be observed.** `CompRottable`'s tick destroys it in the same pass
   that sees the stage change:

   ```csharp
   RotProgress += num * (float)delta;
   if (Stage == RotStage.Rotting && PropsRot.rotDestroys)
   {
       if (parent.IsInAnyStorage() && parent.SpawnedOrAnyParentSpawned)
           Messages.Message("MessageRottedAwayInStorage".Translate(...));
       parent.Destroy();
   }
   ```

   Count and wealth leave together, so there is no divergence to report. That depends on
   `rotDestroys`, whose default is `false`, so every rottable def was enumerated:

   | Scope | Rottable defs | `rotDestroys=true` | Not true |
   | --- | --- | --- | --- |
   | Core + all DLCs | 20 | 20 | **0** |
   | Installed mods (53,196 XML files scanned) | 820 | 820 | **0** |

   Nothing in vanilla or in the installed corpus relies on the default. The earlier claim here
   — "not small on a map with a lot of spoilage" — was **wrong**: spoilage removes count and
   wealth together.

   **Residual, stated honestly:** the comp acts on its own tick and `ResourceCounter` refreshes
   on another, so a stack can be Rotting-but-not-yet-destroyed for at most one comp-tick
   interval. A hover inside that window would show its count under `elsewhere`. It is seconds
   long, self-correcting, and cannot accumulate.

   **Reopen condition:** if any mod ships a `CountAsResource` def that is rottable with
   `rotDestroys=false`, this becomes reachable and the wording question returns. Re-run the def
   scan to check; it is a mechanical test, not a judgement call.

   **Two fixes were considered and both rejected.** Filtering `IsNotFresh` out of our walk breaks
   trap 1 — the storyteller prices rotting food at full value, so our number would stop being
   the number that drives raids. Computing `stored` ourselves from `IsInAnyStorage` is the more
   tempting one, since it would fix the asymmetry and the "elsewhere implies location" wording
   in one move; it is still wrong, because **`stored` must equal the count vanilla prints on the
   row a few pixels away.** A tooltip that contradicts the panel it explains is worse than the
   artifact it removes, and it would also dissolve the trap-4 anchor (`StoredCountOf` vs
   `GetCountIn`), leaving our traversal validated only against itself.
