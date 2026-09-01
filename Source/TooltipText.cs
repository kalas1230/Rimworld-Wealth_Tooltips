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

            // "N0", not ToStringMoney(). ToStringMoney renders "$1190" -- it already carries the
            // currency -- and the key's text names the unit as "silver", so using it would print
            // "$1190 silver". The approved design reads "1,190 silver", so the number is formatted
            // bare and the key supplies the unit.
            //
            // Rounded to whole units: fractions of a silver are noise at colony scale and the
            // readout panel is narrow. "N0" gives the reader's own thousands separator.
            sb.Append("WealthReadout.Line.Wealth".Translate(
                wealth.ToString("N0"),
                share.ToStringPercent("F1")));

            // The line is ALWAYS present; only its second half is dropped when nothing is
            // elsewhere. Three reasons the whole line is not suppressed instead:
            //
            // 1. Zero is ambiguous. ElsewhereCount clamps a negative difference to zero, so "0"
            //    also means "stored exceeded our total" -- the minified case, where ResourceCounter
            //    unwraps via GetInnerIfMinified while the wealth walk values the container.
            //    Suppressing the line hides that behind the same silence as a genuinely empty map.
            // 2. It flickered. The tooltip is rebuilt every Repaint, so one pawn picking up a stack
            //    added a line under a stationary cursor and hauling it took the line away again.
            // 3. The zero case is rarer than it looks. Our total counts everything haulable on the
            //    map -- ground, pawn inventories, containers -- against a stored count that sees
            //    only slot groups, so on a live colony something is nearly always loose.
            //
            // The bare "0 elsewhere" is still not printed: it is noise next to a row that already
            // shows the same stored figure a few pixels away.
            sb.Append('\n');
            sb.Append(elsewhereCount > 0
                ? "WealthReadout.Line.Split".Translate(
                    storedCount.ToStringCached(),
                    elsewhereCount.ToStringCached())
                : "WealthReadout.Line.StoredOnly".Translate(
                    storedCount.ToStringCached()));

            return sb.ToString();
        }
    }
}
