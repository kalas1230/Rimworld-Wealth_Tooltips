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

## Read this first: three traps

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
if (!activeTips.ContainsKey(tip.uniqueId)) { ... }
activeTips[tip.uniqueId].signal.text = tip.text;   // last call in the frame wins
```

Re-registering under vanilla's id therefore replaces vanilla's text. That works in categorized mode,
where the ids are knowable (`catDef.GetHashCode()`, `thingDef.shortHash`).

**It does not work in simple mode.** `ResourceReadout.DrawIcon` uses `TipSignal(TaggedString)`, whose
id is `text.GetHashCode()` — matching it means reconstructing vanilla's exact tooltip string. So the
mod uses `TooltipHandler.ClearTooltipsFrom(rect)` instead, uniformly in both modes: clear, then
register our own tip under our own id. No dependence on vanilla's id scheme anywhere.

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

## Open items

1. **Profile the staleness constant.** Highest priority. No setting exists to escape a bad value.
   Currently set to **5000 ticks** provisionally — the conservative end of the plan's decision
   rule, matching `WealthWatcher.MinCountInterval` so the rebuild is never more frequent than
   the pass vanilla already runs. This is a safe default, **not a measurement**: the
   *Profile index rebuild* debug action (added 2026-09-01) has not yet been run on a heavy
   map. When it is, lower to 1000 under ~5 ms/run or 2500 at 5–15 ms, and record the figure
   and the map it came from here.
2. **`IsInAnyStorage` vs deep-storage mods.** The stored/elsewhere split depends on what counts as
   storage; container-adding mods may make it misleading. Test against at least one.
3. **Rect reconstruction is coupled to vanilla's layout arithmetic.** If Ludeon changes indentation
   or line height in the readout, the tooltip region drifts off the row. Re-check `DoCategory`'s rect
   construction on every version bump.
4. **Conflict test against other readout mods.** Postfixes are friendly here, but verify rather than
   assume.
