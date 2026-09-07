using System;
using Autodesk.Navisworks.Api;

namespace ABSwitchBack.Navisworks
{
    /// <summary>
    /// The single place where Navisworks API version differences live, mirroring
    /// RevitCompat on the Revit side.
    ///
    /// VariantData gained its 64-bit and unsigned accessors in the 2026 release:
    ///
    ///   Navisworks 2024, 2025 : IsInt32 / ToInt32 only
    ///   Navisworks 2026, 2027 : also IsInt64, IsNat32, IsNat64 and their readers
    ///
    /// Calling the newer members unconditionally is why builds for 2024 and 2025 were
    /// impossible before - the code simply would not compile against them.
    ///
    /// Nothing is lost on the older releases: a property that holds a 64-bit or unsigned
    /// value there still reads correctly through ToDisplayString, which every release has.
    /// </summary>
    internal static class NavisCompat
    {
        /// <summary>
        /// Reads a positive numeric value from a property, using whatever accessors the
        /// release provides. Returns false when the value is not numeric, in which case
        /// the caller falls back to parsing the display string.
        /// </summary>
        public static bool TryReadNumeric(VariantData value, out long result)
        {
            result = 0;
            if (value == null) return false;

            if (value.IsInt32)
            {
                result = value.ToInt32();
                return result > 0;
            }

#if NAVIS2026_OR_LATER
            if (value.IsInt64)
            {
                result = value.ToInt64();
                return result > 0;
            }

            if (value.IsNat32)
            {
                result = value.ToNat32();
                return result > 0;
            }

            if (value.IsNat64)
            {
                ulong raw = value.ToNat64();
                if (raw > long.MaxValue) return false;
                result = (long)raw;
                return result > 0;
            }
#endif

            if (value.IsDouble || value.IsAnyDouble)
            {
                double d = value.ToAnyDouble();
                if (Math.Abs(d - Math.Round(d)) > 0.0001) return false;
                result = (long)Math.Round(d);
                return result > 0;
            }

            return false;
        }
    }
}
