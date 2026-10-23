using System;
using System.Linq;
using Verse;

namespace YART.Compat
{
    /// <summary>Semi Random Research 사용 시 연구 큐 조작을 막는다</summary>
    [StaticConstructorOnStartup]
    public static class SemiRandomResearchCompat
    {
        private const string PackageIdMarker = "semirandom";

        public static bool Detected { get; }

        public static bool Active => Detected && (YARTMod.Settings?.semiRandomCompatEnabled ?? true);

        static SemiRandomResearchCompat()
        {
            Detected = LoadedModManager.RunningModsListForReading.Any(
                mod => mod.PackageId != null
                    && mod.PackageId.IndexOf(PackageIdMarker, StringComparison.OrdinalIgnoreCase) >= 0);

            if (Detected)
            {
                Log.Message("[YART] Semi Random Research detected.");
            }
        }
    }
}
