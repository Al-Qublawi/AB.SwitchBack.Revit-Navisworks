using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.Navisworks.Api;
using ABSwitchBack.Core;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// Pulls a Revit ElementId out of a Navisworks item's properties.
    ///
    /// Exporters disagree about where the id lives. An NWC written by the Revit exporter
    /// puts it in a category called "Element ID" as a property named "Value", but IFC and
    /// third-party routes use names like "Revit Element ID" or plain "Id", and the id is
    /// often on a parent node rather than the geometry leaf that got clicked. So: search
    /// the clicked item first, then walk up its ancestors.
    /// </summary>
    internal static class ElementIdExtractor
    {
        /// <summary>Category names that mark a container holding the id (matched case-insensitively).</summary>
        private static readonly string[] IdCategoryHints =
        {
            "element id",
            "revit element id",
            "revit id"
        };

        /// <summary>Property names that hold the id itself.</summary>
        private static readonly string[] IdPropertyNames =
        {
            "element id",
            "elementid",
            "revit element id",
            "revitelementid",
            "revit id",
            "id"
        };

        /// <summary>
        /// Attempts to find a Revit element id on the item or one of its ancestors.
        /// Returns false with a human-readable reason when nothing usable is present.
        /// </summary>
        public static bool TryGetRevitElementId(ModelItem item, out long elementId, out string reason)
        {
            elementId = 0;
            reason = null;

            if (item == null)
            {
                reason = "No item was selected.";
                return false;
            }

            // Walk the clicked node then upward. Depth cap avoids pathological trees.
            ModelItem current = item;
            for (int depth = 0; current != null && depth < 16; depth++)
            {
                if (TryGetFromItem(current, out elementId))
                {
                    Log.Info("Element id " + elementId + " found on '" + SafeName(current) +
                             "' (" + depth + " level(s) above the clicked item).");
                    return true;
                }

                ModelItem parent = null;
                try { parent = current.Parent; }
                catch { }
                current = parent;
            }

            // Some exporters hang the id on a child of the picked node rather than on the
            // node itself. Only worth trying for a small container, where the answer is
            // unambiguous - never for a whole model file.
            if (!IsModelRoot(item) && TryGetFromDescendants(item, out elementId))
            {
                Log.Info("Element id " + elementId + " found on a child of '" + SafeName(item) + "'.");
                return true;
            }

            reason = BuildFailureReason(item);
            return false;
        }

        /// <summary>
        /// Bounded search of immediate descendants. Capped hard: if the node contains many
        /// items, any single id would be an arbitrary guess, so we decline instead.
        /// </summary>
        private static bool TryGetFromDescendants(ModelItem item, out long elementId)
        {
            elementId = 0;
            try
            {
                int examined = 0;
                foreach (ModelItem descendant in item.Descendants)
                {
                    if (++examined > 16) return false;
                    if (TryGetFromItem(descendant, out elementId)) return true;
                }
            }
            catch
            {
                // Enumeration can fail on odd model structures; treat as "not found".
            }
            return false;
        }

        /// <summary>True when this is a file/model node rather than an element.</summary>
        private static bool IsModelRoot(ModelItem item)
        {
            try { return item.Parent == null; }
            catch { return false; }
        }

        private static bool TryGetFromItem(ModelItem item, out long elementId)
        {
            elementId = 0;
            PropertyCategoryCollection categories;
            try { categories = item.PropertyCategories; }
            catch { return false; }
            if (categories == null) return false;

            // Pass 1 - a category that exists purely to carry the id, e.g. "Element ID".
            foreach (PropertyCategory category in categories)
            {
                if (!MatchesAny(DisplayOf(category), IdCategoryHints)) continue;

                foreach (DataProperty property in SafeProperties(category))
                {
                    string name = DisplayOf(property);
                    // Inside such a category the value is usually just called "Value".
                    if (!Equals(name, "value") && !MatchesAny(name, IdPropertyNames)) continue;
                    if (TryReadId(property, out elementId)) return true;
                }
            }

            // Pass 2 - any category, but the property must be explicitly named like an id.
            foreach (PropertyCategory category in categories)
            {
                foreach (DataProperty property in SafeProperties(category))
                {
                    if (!MatchesAny(DisplayOf(property), IdPropertyNames)) continue;
                    if (TryReadId(property, out elementId)) return true;
                }
            }

            return false;
        }

        private static bool TryReadId(DataProperty property, out long elementId)
        {
            elementId = 0;
            VariantData value;
            try { value = property.Value; }
            catch { return false; }
            if (value == null) return false;

            try
            {
                if (value.IsInt32) { elementId = value.ToInt32(); return elementId > 0; }
                if (value.IsInt64) { elementId = value.ToInt64(); return elementId > 0; }

                if (value.IsNat32) { elementId = value.ToNat32(); return elementId > 0; }
                if (value.IsNat64)
                {
                    ulong raw = value.ToNat64();
                    if (raw > long.MaxValue) return false;
                    elementId = (long)raw;
                    return elementId > 0;
                }

                if (value.IsDouble || value.IsAnyDouble)
                {
                    double d = value.ToAnyDouble();
                    if (Math.Abs(d - Math.Round(d)) > 0.0001) return false;
                    elementId = (long)Math.Round(d);
                    return elementId > 0;
                }

                // Strings: display strings, identifier strings, named constants.
                string text = value.ToDisplayString();
                return TryParseIdText(text, out elementId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parses "123456", " 123456 " and "Element ID: 123456". Rejects anything with no
        /// digits, GUID-shaped values, and multi-number strings that would be ambiguous.
        /// </summary>
        internal static bool TryParseIdText(string text, out long elementId)
        {
            elementId = 0;
            if (string.IsNullOrEmpty(text)) return false;

            text = text.Trim();
            if (text.Length == 0 || text.Length > 64) return false;
            if (text.IndexOf('-') >= 0 && text.Length > 20) return false; // GUID-like

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out elementId))
                return elementId > 0;

            // Fall back to a single embedded run of digits.
            var digits = new StringBuilder();
            int runs = 0;
            bool inRun = false;
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9')
                {
                    if (!inRun) { runs++; inRun = true; }
                    if (runs == 1) digits.Append(c);
                }
                else
                {
                    inRun = false;
                }
            }

            if (runs != 1 || digits.Length == 0) return false;
            return long.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out elementId)
                   && elementId > 0;
        }

        /// <summary>Lists what was actually available, so the user can see why it failed.</summary>
        private static string BuildFailureReason(ModelItem item)
        {
            var sb = new StringBuilder();

            // By far the most common cause: the pick resolved to the model file rather than
            // to an element, so say that plainly instead of blaming the export.
            if (IsModelRoot(item))
            {
                sb.AppendLine("The click selected the whole model file, not an element.");
                sb.AppendLine();
                sb.AppendLine("Usual causes:");
                sb.AppendLine("  - The trigger is set to Ctrl+Shift+Click. Navisworks reserves that");
                sb.AppendLine("    combination and expands the pick to the entire file. Use Ctrl+Click.");
                sb.AppendLine("  - Selection Resolution is set to File.");
                sb.AppendLine("    Set it to First Object or Geometry in Options > Interface > Selection.");
                return sb.ToString();
            }

            sb.Append("No Revit Element ID was found on the selected item or any of its parents.");

            try
            {
                var names = new List<string>();
                PropertyCategoryCollection categories = item.PropertyCategories;
                if (categories != null)
                {
                    foreach (PropertyCategory category in categories)
                    {
                        string display = DisplayOf(category);
                        if (!string.IsNullOrEmpty(display)) names.Add(display);
                        if (names.Count >= 12) break;
                    }
                }

                if (names.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append("Property tabs on this item: ");
                    sb.Append(string.Join(", ", names.ToArray()));
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append("SwitchBack looks for a tab named 'Element ID' or a property named " +
                              "'Element ID' / 'Revit Element ID' / 'Id'. Re-export the model from Revit " +
                              "with element properties enabled if none of these are present.");
                }
            }
            catch
            {
                // Diagnostics are best-effort only.
            }

            return sb.ToString();
        }

        private static IEnumerable<DataProperty> SafeProperties(PropertyCategory category)
        {
            DataPropertyCollection properties = null;
            try { properties = category.Properties; }
            catch { }
            if (properties == null) yield break;

            foreach (DataProperty p in properties)
            {
                if (p != null) yield return p;
            }
        }

        private static string DisplayOf(PropertyCategory category)
        {
            try
            {
                string d = category.DisplayName;
                if (!string.IsNullOrEmpty(d)) return d;
                return category.Name ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string DisplayOf(DataProperty property)
        {
            try
            {
                string d = property.DisplayName;
                if (!string.IsNullOrEmpty(d)) return d;
                return property.Name ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool Equals(string value, string expected)
        {
            return value != null && value.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesAny(string value, string[] candidates)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string normalised = value.Trim();
            foreach (string candidate in candidates)
            {
                if (normalised.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string SafeName(ModelItem item)
        {
            try { return item.DisplayName ?? item.ClassDisplayName ?? "item"; }
            catch { return "item"; }
        }
    }
}
