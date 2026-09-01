# Wealth Readout — Design

Status: design agreed 2026-09-01. No code written.

Put colony wealth where the player already looks: the resource readout at the top-left of the
screen. Hovering a category or an item there shows what it contributes in silver and as a share of
colony wealth.

All vanilla claims in this document were verified against an ILSpy decompile of RimWorld 1.6
`Assembly-CSharp.dll`. Each is cited at the point it is used. Nothing here is from memory.

---

## The problem

Wealth drives raid difficulty, and players who want to manage it have to go looking for it. Every
existing mod answers the question in a dedicated window you have to open, read, and close.

| Mod | Where it answers |
| --- | --- |
| [WealthList](https://steamcommunity.com/sharedfiles/filedetails/?id=2900172649) | A new tab in the History menu |
| [Visible Wealth](https://steamcommunity.com/sharedfiles/filedetails/?id=3461137081) | Its own window; list or pie chart |
| [More Graphs](https://steamcommunity.com/sharedfiles/filedetails/?id=1966250412) | A graph tab, by category over time |
| [Wealth Display](https://steamcommunity.com/sharedfiles/filedetails/?id=2376329775) | Total wealth on the HUD |
| [WealthHere](https://steamcommunity.com/sharedfiles/filedetails/?id=2593103416) | Hovering a map cell |
| [Wealth Tweaks](https://www.nexusmods.com/rimworld/mods/694), [WealthCorrector](https://steamcommunity.com/sharedfiles/filedetails/?id=2976632810) | Rebalance wealth values; no display |

The gap is not "show wealth" — that is well covered. The gap is **wealth at the place you are
already looking**, with no window to open. The resource readout is on screen permanently and is
already the panel players use to ask "how much of X do I have." This mod makes it also answer "and
what is that costing me."

`WealthHere` is the nearest neighbour and is deliberately not overlapped: it answers for a map cell
under the cursor, not for a category of thing.

---

## What the player sees

The readout looks exactly like vanilla. Only the tooltips change.

Hovering any row replaces its tooltip with:

```
Foods
1,190 silver · 2.6% of colony wealth
240 stored · 70 elsewhere
```

- **Categories sum their whole subtree.** Hovering `Foods` covers `Meals` covers `Simple meal`.
  Hovering a parent answers the parent's question.
- **The share is of total colony wealth**, not of items — so it is never inflated, and a player
  sweeping rows that read 2–4% can see for themselves that most of their wealth is elsewhere.
- **The stored/elsewhere line exists because the readout and wealth disagree** (see "The counting
  mismatch"). Naming the gap is better than letting the player notice it and stop trusting the
  number.

Both readout modes are supported: the categorized tree (`Prefs.ResourceReadoutCategorized`) and the
flat simple list. They are drawn by different code and need separate patches — see "Three Harmony
patches". In simple mode there are no categories, so only per-item tooltips exist there.

---

## The counting mismatch

This is the central fact of the design and the source of most of its decisions.

**The readout and the wealth system count different things.**

`ResourceCounter.UpdateResourceCounts` walks only `SlotGroup`s — stockpiles and shelves — and only
admits defs flagged `CountAsResource`:

```csharp
List<SlotGroup> allGroupsListForReading = map.haulDestinationManager.AllGroupsListForReading;
for (int i = 0; i < allGroupsListForReading.Count; i++)
    foreach (Thing heldThing in allGroupsListForReading[i].HeldThings) {
        Thing innerIfMinified = heldThing.GetInnerIfMinified();
        if (innerIfMinified.def.CountAsResource && ShouldCount(innerIfMinified))
            countedAmounts[innerIfMinified.def] += innerIfMinified.stackCount;
    }
```

`WealthWatcher.CalculateWealthItems` walks every haulable on the map, stored or not, including
things held inside pawns and containers:

```csharp
ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
    tmpThings, allowUnreal: false, WealthItemsFilter);
for (int i = 0; i < tmpThings.Count; i++)
    if (tmpThings[i].SpawnedOrAnyParentSpawned && !tmpThings[i].PositionHeld.Fogged(map))
        num += tmpThings[i].MarketValue * (float)tmpThings[i].stackCount;
```

They diverge in two directions:

1. **Stock outside storage.** Steel on the ground, in a pawn's inventory, in a caravan pack or inside
   a container is wealth the readout does not count. This is why the tooltip carries a
   stored/elsewhere split rather than a single number — and why the second word is **"elsewhere",
   not "unstored"**. "Unstored" implies a pending hauling job; most of that remainder has none.
2. **Whole classes of thing.** Weapons, apparel, art and furniture are not `CountAsResource` and have
   no row in the readout at all — in a mature colony, usually the majority of wealth.

**Decision: the tooltip reports true wealth, not readout wealth.** The number must describe the
wealth the storyteller actually reads, or acting on it does nothing. The stored/elsewhere line
reconciles it with the count printed beside it.

**Decision: nothing is added to cover point 2.** A footer line summarising unlisted wealth was
designed and dropped. The percentage already carries the honesty, since its denominator is total
colony wealth. Dropping it also removed the design's worst correctness trap — see "Rejected".

---

## Constraints

These are not preferences. A change that breaks either one is out of scope by definition, however
good the feature is. Both are recorded with their reasoning in `HANDOVER.md`.

**1. Zero save footprint.** The mod must be addable to and removable from any save at any time, with
no warning, no version bump, and no corruption. Concretely: **no defs, no `GameComponent`, no
`MapComponent`, nothing scribed, no `ModSettings`.** The wealth index is a plain in-memory cache
rebuilt on demand and discarded with the session. The mod is Harmony patches and nothing else.

**2. Vanilla-shaped and minimal.** No divergence from vanilla behaviour, and the smallest patch
surface that does the job. The mod changes tooltip *text*. It adds no rows, no windows, no UI
elements, no keys; it moves nothing and resizes nothing; hover regions match vanilla's exactly. Four
patches (two prefix/postfix pairs and one standalone postfix), no transpilers.

Constraint 2 is also the best conflict posture available. Postfixes that only rewrite tooltip text
let other readout mods patch the same methods without either mod breaking the other.

---

## Design

### Three Harmony patches

The two readout modes are drawn by different code and are patched separately.

**Categorized mode.** Rows come from `Listing_ResourceReadout.DoCategory(node, nestLevel, openMask)`
and `DoThingDef(thingDef, nestLevel)`. Both already register a tooltip on mouseover:

```csharp
TooltipHandler.TipRegion(rect, new TipSignal(node.catDef.LabelCap, node.catDef.GetHashCode()));
TooltipHandler.TipRegion(rect, new TipSignal(() => thingDef.LabelCap + ": " + thingDef.description.CapitalizeFirst(), thingDef.shortHash));
```

**Simple mode.** Rows do not go through `Listing_ResourceReadout` at all. `DoReadoutSimple` calls
`DrawResourceSimple`, which calls `ResourceReadout.DrawIcon`, and that registers over the 27x27 icon
rect only — not the row — with no explicit id:

```csharp
TooltipHandler.TipRegion(rect, taggedString);   // TipSignal(TaggedString) => uniqueId = text.GetHashCode()
```

### Replacing the tooltip

`TooltipHandler.TipRegion` keys on `uniqueId` and **overwrites** rather than stacking:

```csharp
if (!activeTips.ContainsKey(tip.uniqueId)) { ... }
activeTips[tip.uniqueId].signal.text = tip.text;   // last call in the frame wins
```

Re-registering under vanilla's own id would therefore replace vanilla's text. That works in
categorized mode, where the ids are stable and knowable (`catDef.GetHashCode()`,
`thingDef.shortHash`). It does **not** work in simple mode, where the id is a hash of the tooltip
text and reproducing it means reconstructing that exact string.

So use the uniform mechanism instead. `TooltipHandler.ClearTooltipsFrom(rect)` drops every tip
triggered this frame while the mouse is over `rect`:

```csharp
public static void ClearTooltipsFrom(Rect rect) {
    if (Event.current.type != EventType.Repaint || !Mouse.IsOver(rect)) return;
    ...
}
```

Every patch therefore does the same two things: clear, then register our own tip under our own id.
No dependence on vanilla's id scheme, no stacking, no transpiler in any mode. The category tooltip
today is nothing but the category name, so nothing of value is lost.

> **SUPERSEDED 2026-09-01, after in-game testing.** This section is wrong and the mod shipped no
> tooltip at all because of it. `ClearTooltipsFrom` *removes the dictionary entry*, so the following
> `TipRegion` constructs a new `ActiveTip` stamped `firstTriggerTime = now` on every Repaint — and
> tips only draw once `realtimeSinceStartup > firstTriggerTime + delay`, with `delay = 0.45f`. The
> stamp never aged, so nothing ever drew, vanilla's included. The implementation now registers under
> vanilla's own `uniqueId` in all three patches and never clears; simple mode reconstructs vanilla's
> tagged string to obtain its id. See HANDOVER.md, "`ClearTooltipsFrom` is not rect-scoped".

### The patches

- **Prefix on `DoCategory` and `DoThingDef`** — record `curY` before the row draws.
- **Postfix on both** — reconstruct the row rect from the recorded `curY`, `nestLevel` and
  `lineHeight` (mirroring vanilla: `rect = new Rect(0f, curY, LabelWidth, lineHeight)` with
  `rect.xMin = XAtIndentLevel(nestLevel) + 18f`), then clear and re-register.
- **Postfix on `ResourceReadout.DrawIcon(x, y, thingDef)`** — rebuild the same
  `new Rect(x, y, 27f, 27f)` vanilla used, then clear and re-register. Note the hover target in
  simple mode is the icon only; matching vanilla's region is correct, not a limitation to fix.

Both listing methods early-return without drawing when their count is zero. **If `curY` did not move,
the row did not render and the postfix must do nothing** — otherwise it registers a tooltip for a row
that is not on screen.

### The wealth index

`WealthWatcher` exposes four floats and no per-def breakdown, so the mod builds its own.

The rebuild **mirrors `CalculateWealthItems` exactly**: `GetAllThingsRecursively` over
`HaulableEver`, filtered by `WealthWatcher.WealthItemsFilter` (which is `public static` and must be
reused, not reimplemented), skipping unspawned and fogged, summing `MarketValue * stackCount`. It
buckets by `ThingDef`, tracking wealth and total count per def.

**The "stored" count is not ours.** It is always vanilla's own `resourceCounter` value — the same
number the row prints a few pixels from the tooltip. Deriving it independently, say from
`IsInAnyStorage`, would let the tooltip contradict the row it is attached to, which reads as a bug
even when our figure is the more accurate one. "Elsewhere" is then `ourTotalCount - storedCount`,
clamped at zero: the two sources are not guaranteed to nest, because `ResourceCounter` unwraps
minified things via `GetInnerIfMinified` while the wealth walk values the minified container.

> Deviating from that walk anywhere — a different request group, a hand-rolled filter, dropping the
> fogged check — produces numbers that describe a wealth the storyteller does not use. The mod would
> still look correct and would be silently wrong.

Category totals mirror `ResourceCounter.GetCountIn`: sum own `childThingDefs`, then recurse
`childCategories`.

### The percentage denominator

```
denominator = ourItemsTotal + (wealthWatcher.WealthTotal - wealthWatcher.WealthItems)
```

The buildings/pawns/floors half comes from vanilla; the items half is ours. `WealthWatcher` recounts
at most every 5000 ticks (`MinCountInterval`), so its `WealthItems` and our fresh pass are from
different moments. Using `WealthTotal` directly would mix them and produce category shares that do
not add up.

### Laziness and caching

The index is built **on demand, by the first hover that needs it**, and rebuilt when older than a
staleness interval.

`TipRegion` is called on every Repaint while the mouse is over a row, so a hover held for two seconds
is roughly 120 calls. **Computing per call is not viable** — each would be a full-map walk.

Bucketing every def in the triggering pass costs the same as bucketing one, because the walk visits
every thing regardless. So the first hover pays for the whole index and every subsequent row is a
dictionary lookup.

Consequences:

- A player who never hovers the readout pays nothing. No MapComponent tick, no background work.
- A paused game rebuilds nothing, because staleness is measured in ticks.
- Sweeping down twenty rows costs one pass, not twenty.

Staleness interval: **1000 ticks**, a constant. `WealthWatcher` itself runs the same pass
every 5000, so this is roughly 5x vanilla's wealth cost — but only while the player is actually
hovering. **This default is a guess and must be profiled on a large late-game map before release.**

### No settings

The mod has **no settings page and no `ModSettings` class**. Nothing is configurable, including the
staleness interval, which is a constant.

That is a deliberate cost: it means the staleness default cannot be worked around by a user with a
400-mod list and a decade-old colony, so **profiling it is not optional** (risk 1). The alternative —
a settings page — was rejected along with everything that would have gone on it. See "Rejected".

---

## Rejected

**A footer line summarising unlisted wealth** (`Not listed: 31,600 (68%)`, hoverable for a
buildings / pawns / gear split). Dropped in favour of relying on the percentage.

Worth recording *why*, because the simplification was structural and not merely cosmetic. The footer
required computing "total minus the root-level rows that actually rendered this frame," which meant:
a further patch on `ResourceReadout.ResourceReadoutOnGUI`; a per-frame accumulation set; and a real
correctness trap, since **a `ThingDef` can belong to more than one `ThingCategoryDef`**, so naive
root-level summing double-counts and makes the remainder too small. Dropping the footer deleted all
three. Do not reintroduce it without solving the multi-category dedupe first.

**A settings page, and everything proposed for it.** Considered and dropped whole. Each entry failed
on its own terms, which is why the page had nothing left to hold:

- *A percentage toggle.* One clause of text in a tooltip that only appears when the player asks for
  it by hovering. No performance cost, no conflict surface. A setting that removes information the
  user just requested is a setting nobody touches.
- *A "count unstored" behaviour toggle.* **Rejected on principle, not on cost.** It would let the
  player switch the number from true wealth to a figure that matches the row but does not match what
  the storyteller reads — a setting whose purpose is to select a wrong answer. Every screenshot and
  bug report would then be ambiguous about which mode produced it. If the mismatch reads badly, fix
  the wording, never the counting.
- *Raid-point impact, defaulted off.* The most actionable feature considered, and the one with a
  concrete blocker. Threat points are not linear in wealth, so the only honest answer to "what does
  Foods cost me in raid points" is to evaluate the real curve at current wealth and at wealth minus
  that category, and difference them. But `StorytellerUtility.DefaultThreatPointsNow` reads wealth
  via `map.PlayerWealthForStoryteller`, which is the cached `WealthWatcher` — differencing it means
  temporarily substituting a fake wealth underneath the storyteller. That is an invasive hook into
  the highest-churn code in the game, and it is precisely what the sibling **Perceived Wealth** mod
  is built around. Reimplementing the curve instead guarantees drift from vanilla and from every
  difficulty mod. Both routes break constraint 2. The feature belongs in a mod already paying that
  cost.
- *The staleness interval.* Would have been the one defensible entry, but it cannot justify a
  settings page alone. It is a constant instead, which makes profiling it mandatory.

**Per-zone numbers.** Considered before the target UI was correctly identified. Meaningless here: the
resource readout is map-wide by construction.

**An inline silver figure on each row.** Vanilla already right-aligns a count on these rows, and the
readout is a narrow panel. Possible as a later setting; not v1.

---

## Out of scope

**Weapons, apparel and art cannot be hovered**, because they have no row in the readout. The mod
annotates what vanilla shows and is silent about the rest. This is a boundary, not an oversight.

**Buildings, pawns and terrain** are not items and are likewise unreachable. They still appear in the
denominator, which is what keeps the percentages honest.

**Adding rows for missing categories** was considered and rejected: it changes what the vanilla panel
is for — it is a *resource* readout — and maximises conflict with other readout mods.

### Parked as a separate mod

Wealth in the **Architect build menu**: hovering a building shows how much wealth placing it would
add, before you commit. Forward-looking rather than current-state, a different UI, a different patch
surface. Explicitly not folded into this mod. If built, it belongs in its own repo.

---

## Risks and open items

1. **The staleness constant is unprofiled, and there is no setting to escape it.** 1000 ticks is a
   guess. Because the mod ships no settings page, a bad value cannot be worked around by the user —
   it is a patch. Measure the rebuild on a large late-game map with a full stockpile before release.
   This is the single highest-priority open item.
2. **`IsInAnyStorage` and deep-storage mods.** The stored/elsewhere split depends on what counts as
   storage. Mods adding container buildings may make the split misleading. Check against at least one
   deep-storage mod.
3. **Other readout mods patch the same methods.** Postfixes are friendlier than transpilers here, but
   conflicts need testing rather than assuming.
4. **Rect reconstruction is coupled to vanilla's layout arithmetic.** If Ludeon changes indentation
   or line height in the readout, the tooltip region drifts from the row. A version bump should
   re-check `DoCategory`'s rect construction.
5. **`MarketValue` is a stat lookup per thing.** It is what vanilla does, so cost parity holds, but it
   is the bulk of the rebuild.

---

## Verification plan

Numbers, not impressions. A tooltip that looks plausible and is wrong is the failure mode.

1. **Reconciliation.** On a running colony, the sum of our per-def index must equal
   `wealthWatcher.WealthItems` after a `ForceRecount` on the same tick. Any drift means the walk was
   not mirrored correctly.
2. **Elsewhere detection.** Spawn a stack outside any stockpile; the row count must not move and the
   tooltip's unstored figure must.
3. **Category summing.** A parent's silver must equal the sum of its children's, across a subtree
   with at least two levels of nesting.
4. **Zero-count rows.** Confirm no tooltip is registered for a category that rendered nothing.
5. **Both readout modes**, toggled via `Prefs.ResourceReadoutCategorized`.
6. **Performance.** Frame time while sweeping the full tree on a late-game map, with the index warm
   and cold.
