using Autodesk.Revit.DB;

namespace ABSwitchBack.Revit
{
    /// <summary>
    /// The single place where the Revit API version differences live.
    ///
    /// Revit 2023 and earlier : ElementId(int),  ElementId.IntegerValue
    /// Revit 2024             : both int and long overloads exist
    /// Revit 2025 and later   : ElementId(int) and IntegerValue were REMOVED
    ///
    /// Everything else in this add-in is version-neutral.
    /// </summary>
    internal static class RevitCompat
    {
#if REVIT2024_OR_LATER
        public static ElementId ToElementId(long value)
        {
            return new ElementId(value);
        }

        public static long ToLong(ElementId id)
        {
            return id == null ? -1L : id.Value;
        }
#else
        public static ElementId ToElementId(long value)
        {
            // Pre-2024 ids are 32-bit; anything wider cannot exist in this model.
            if (value < int.MinValue || value > int.MaxValue) return ElementId.InvalidElementId;
            return new ElementId((int)value);
        }

        public static long ToLong(ElementId id)
        {
            return id == null ? -1L : id.IntegerValue;
        }
#endif
    }
}
