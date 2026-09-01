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
