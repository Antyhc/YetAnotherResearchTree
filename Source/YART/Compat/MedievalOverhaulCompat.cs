using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace YART.Compat
{
    /// <summary>Medieval Overhaul의 schematic(설계도 책) 요구 조건을 반영</summary>
    [StaticConstructorOnStartup]
    public static class MedievalOverhaulCompat
    {
        private const string PackageId = "DankPyon.Medieval.Overhaul";
        private const string HarmonyId = "medievalOverhaul";

        public static bool Detected { get; }

        private static readonly Type extensionType;                 // RequiredSchematic : DefModExtension
        private static readonly FieldInfo schematicDefField;        // ThingDef schematicDef

        // MO의 CanStartNow Postfix가 실제로 붙었는지
        private static readonly bool enforced;

        private static readonly Dictionary<ResearchProjectDef, ThingDef> schematicByDef = new Dictionary<ResearchProjectDef, ThingDef>();

        private const float CacheInterval = 0.5f;
        private static readonly HashSet<ThingDef> booksNearBenches = new HashSet<ThingDef>();
        private static float booksCachedAt = float.NegativeInfinity;

        static MedievalOverhaulCompat()
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, ignorePostfix: true) == null) return;

            try
            {
                T CheckNull<T>(T obj, string name) where T : class
                    => obj ?? throw new MissingMemberException($"'{name}' not found");

                extensionType = CheckNull(GenTypes.GetTypeInAnyAssembly("MedievalOverhaul.RequiredSchematic"), "RequiredSchematic");
                schematicDefField = CheckNull(extensionType.GetField("schematicDef"), "schematicDef");
                enforced = IsCanStartNowPatched();

                Detected = true;
                Log.Message("[YART] Medieval Overhaul detected");
            }
            catch (Exception e)
            {
                Log.Warning($"[YART] Medieval Overhaul detected, but preparing is failed: ({e.Message})");
            }
        }

        private static bool IsCanStartNowPatched()
        {
            var getter = AccessTools.PropertyGetter(typeof(ResearchProjectDef), nameof(ResearchProjectDef.CanStartNow));
            var patches = Harmony.GetPatchInfo(getter);
            if (patches?.Postfixes == null) return false;
            foreach (var patch in patches.Postfixes)
            {
                if (patch.owner == HarmonyId) return true;
            }
            return false;
        }

        public static ThingDef RequiredSchematicFor(ResearchProjectDef def)
        {
            if (!Detected || def == null) return null;

            if (schematicByDef.TryGetValue(def, out var cached)) return cached;

            ThingDef schematic = null;
            var extensions = def.modExtensions;
            if (extensions != null)
            {
                for (int i = 0; i < extensions.Count; i++)
                {
                    if (extensionType.IsInstanceOfType(extensions[i]))
                    {
                        schematic = schematicDefField.GetValue(extensions[i]) as ThingDef;
                        break;
                    }
                }
            }
            schematicByDef[def] = schematic;
            return schematic;
        }

        public static bool SchematicRequirementMet(ResearchProjectDef def)
        {
            if (!Detected || !enforced) return true;

            var schematic = RequiredSchematicFor(def);
            if (schematic == null) return true;

            if (!UnityData.IsInMainThread) return true;
            if (Current.ProgramState != ProgramState.Playing) return true;

            return BooksNearBenches().Contains(schematic);
        }

        private static HashSet<ThingDef> BooksNearBenches()
        {
            float now = Time.realtimeSinceStartup;
            if (now - booksCachedAt < CacheInterval) return booksNearBenches;
            booksCachedAt = now;

            booksNearBenches.Clear();
            try
            {
                var maps = Find.Maps;
                if (maps == null) return booksNearBenches;

                for (int i = 0; i < maps.Count; i++)
                {
                    var buildings = maps[i].listerBuildings.allBuildingsColonist;
                    for (int j = 0; j < buildings.Count; j++)
                    {
                        if (!(buildings[j] is Building_ResearchBench bench)) continue;

                        var room = bench.GetRoom();
                        if (room == null) continue;

                        foreach (var bookcase in room.ContainedThings<Building_Bookcase>())
                        {
                            var books = bookcase.HeldBooks;
                            for (int k = 0; k < books.Count; k++)
                                booksNearBenches.Add(books[k].def);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                WarnOnce(e);
            }
            return booksNearBenches;
        }

        private static bool warned;
        private static void WarnOnce(Exception e)
        {
            if (warned) return;
            warned = true;
            Log.Warning($"[YART] Medieval Overhaul compat: schematic check failed. ({e.Message})");
        }
    }
}
