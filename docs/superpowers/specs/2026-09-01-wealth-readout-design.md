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
240 stored · 70 unstored
```

- **Categories sum their whole subtree.** Hovering `Foods` covers `Meals` covers `Simple meal`.
  Hovering a parent answers the parent's question.
- **The share is of total colony wealth**, not of items — so it is never inflated, and a player
  sweeping rows that read 2–4% can see for themselves that most of their wealth is elsewhere.
- **The stored/unstored line exists because the readout and wealth disagree** (see "The counting
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

1. **Unstored stock.** Steel dumped on the ground is wealth the readout does not count. This is why
   the tooltip carries a stored/unstored split rather than a single number.
2. **Whole classes of thing.** Weapons, apparel, art and furniture are not `CountAsResource` and have
   no row in the readout at all — in a mature colony, usually the majority of wealth.

**Decision: the tooltip reports true wealth, not readout wealth.** The number must describe the
wealth the storyteller actually reads, or acting on it does nothing. The stored/unstored line
reconciles it with the count printed beside it.

**Decision: nothing is added to cover point 2.** A footer line summarising unlisted wealth was
designed and dropped. The percentage already carries the honesty, since its denominator is total
colony wealth. Dropping it also removed the design's worst correctness trap — see "Rejected".

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
buckets by `ThingDef` and splits stored from unstored.

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

Default staleness interval: **1000 ticks**, configurable. `WealthWatcher` itself runs the same pass
every 5000, so this is roughly 5x vanilla's wealth cost — but only while the player is actually
hovering. **This default is a guess and must be profiled on a large late-game map before release.**

### Settings

- Staleness interval (ticks).
- Show the stored/unstored line (on by default).

Anything further waits until the tooltip has been used in play.

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

1. **The staleness default is unprofiled.** 1000 ticks is a guess. Measure the rebuild on a large
   late-game map with a full stockpile before trusting it.
2. **`IsInAnyStorage` and deep-storage mods.** The stored/unstored split depends on what counts as
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
2. **Unstored detection.** Spawn a stack outside any stockpile; the row count must not move and the
   tooltip's unstored figure must.
3. **Category summing.** A parent's silver must equal the sum of its children's, across a subtree
   with at least two levels of nesting.
4. **Zero-count rows.** Confirm no tooltip is registered for a category that rendered nothing.
5. **Both readout modes**, toggled via `Prefs.ResourceReadoutCategorized`.
6. **Performance.** Frame time while sweeping the full tree on a late-game map, with the index warm
   and cold.
