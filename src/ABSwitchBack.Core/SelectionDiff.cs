using System.Collections.Generic;

namespace ABSwitchBack.Core
{
    /// <summary>
    /// Works out which single item a selection change was about.
    ///
    /// Navisworks reports only *that* the selection changed, never what changed, so the
    /// only way to identify the clicked element is to compare the selection before and
    /// after. Ctrl+click ADDS an unselected element and REMOVES an already selected one,
    /// and a one-item difference in either direction means the same thing: that is what
    /// the user clicked.
    ///
    /// Deliberately generic and free of any Autodesk type. This is the most subtle logic
    /// in the product - getting it wrong sends the WRONG element to Revit, silently - so
    /// it lives here where it can be tested directly with plain strings.
    /// </summary>
    public static class SelectionDiff
    {
        /// <summary>
        /// Returns the one item added, or failing that the one item removed.
        /// Returns null when the change is ambiguous: nothing changed, or more than one
        /// item moved in either direction (a Select All, a box selection, a clear).
        /// Guessing in those cases would be worse than doing nothing.
        /// </summary>
        public static T FindSingleChange<T>(HashSet<T> before, HashSet<T> after) where T : class
        {
            if (before == null || after == null) return null;

            T added = null;
            int addedCount = 0;
            foreach (T item in after)
            {
                if (before.Contains(item)) continue;
                added = item;
                if (++addedCount > 1) return null;
            }
            if (addedCount == 1) return added;

            T removed = null;
            int removedCount = 0;
            foreach (T item in before)
            {
                if (after.Contains(item)) continue;
                removed = item;
                if (++removedCount > 1) return null;
            }
            return removedCount == 1 ? removed : null;
        }
    }
}
