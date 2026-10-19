using System;

namespace N1MMMobileCounty.Core
{
    /// <summary>
    /// Turns a stream of per-fix county lookups into stable county-change events. A new county must be seen on
    /// several consecutive fixes before it is committed, so GPS jitter on a county line cannot make the display
    /// (or N1MM) flip-flop.
    /// </summary>
    public sealed class CountyTracker
    {
        private readonly int _confirmFixes;
        private string? _candidate;
        private int _candidateCount;

        public CountyTracker(int confirmFixes = 3)
        {
            _confirmFixes = Math.Max(1, confirmFixes);
        }

        /// <summary>The committed county, or null before the first confirmed fix. Outside the party's area is represented by an empty string.</summary>
        public string? Current { get; private set; }

        /// <summary>Forgets the committed county (used when the QSO party, and so the county list, changes).</summary>
        public void Reset()
        {
            Current = null;
            _candidate = null;
            _candidateCount = 0;
        }

        /// <summary>
        /// Feed one lookup result (a county abbreviation, or null when the fix is outside the area).
        /// Returns true when this fix caused <see cref="Current"/> to change.
        /// </summary>
        public bool Update(string? countyAbbreviation)
        {
            var seen = countyAbbreviation ?? string.Empty;
            if (seen == Current)
            {
                _candidate = null;
                _candidateCount = 0;
                return false;
            }

            if (seen == _candidate)
            {
                _candidateCount++;
            }
            else
            {
                _candidate = seen;
                _candidateCount = 1;
            }

            if (_candidateCount < _confirmFixes)
            {
                return false;
            }

            Current = seen;
            _candidate = null;
            _candidateCount = 0;
            return true;
        }
    }
}
