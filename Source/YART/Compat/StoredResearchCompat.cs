using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace YART.Compat
{
    [StaticConstructorOnStartup]
    public static class StoredResearchCompat
    {
        private const string PackageId = "leoltron.storedresearch";
        public static bool Detected { get; }
        public static bool Active => Detected && (YARTMod.Settings?.storedResearchCompatEnabled ?? true);
        public static Texture2D Icon { get; }

        private static readonly Type compType;                 // StoredResearchWorldComponent
        private static readonly PropertyInfo totalStoredProp;  // float TotalResearchStored
        private static readonly FieldInfo universalField;      // StoredResearch StoredUniversalPoints
        private static readonly FieldInfo pointsDictField;     // Dictionary<string, StoredResearch>
        private static readonly FieldInfo knowledgeField;      // KnowledgeManager Knowledge
        private static readonly PropertyInfo allKnowledgeProp; // IReadOnlyCollection<StoredKnowledge>
        private static readonly PropertyInfo srValueProp;      // StoredResearch.Value (float)
        private static readonly PropertyInfo srBenchProp;      // StoredResearch.Bench (ThingDef)
        private static readonly PropertyInfo srFacilitiesProp; // StoredResearch.Facilities (List<ThingDef>)
        private static readonly FieldInfo skCategoryField;     // StoredKnowledge.KnowledgeCategory
        private static readonly FieldInfo skValueField;        // StoredKnowledge.Value (float)
        private static readonly MethodInfo openDialogMethod;   // StoredResearchInfoDialog.OpenResearch()

        private static World cachedWorld;
        private static WorldComponent cachedComp;

        private const float CacheInterval = 0.5f;
        private static float cachedTotal;
        private static float totalCachedAt = -1f;
        private static string cachedTooltip;
        private static float tooltipCachedAt = -1f;

        static StoredResearchCompat()
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, ignorePostfix: true) == null) return;

            try
            {
                T CheckNull<T>(T obj, string name) where T : class
                    => obj ?? throw new MissingMemberException($"'{name}' not found");

                compType = CheckNull(GenTypes.GetTypeInAnyAssembly("Leoltron.StoredResearch.StoredResearchWorldComponent"), "StoredResearchWorldComponent");
                var srType = CheckNull(GenTypes.GetTypeInAnyAssembly("Leoltron.StoredResearch.StoredResearch"), "StoredResearch");
                var kmType = CheckNull(GenTypes.GetTypeInAnyAssembly("Leoltron.StoredResearch.KnowledgeManager"), "KnowledgeManager");
                var skType = CheckNull(GenTypes.GetTypeInAnyAssembly("Leoltron.StoredResearch.StoredKnowledge"), "StoredKnowledge");
                var dialogType = CheckNull(GenTypes.GetTypeInAnyAssembly("Leoltron.StoredResearch.StoredResearchInfoDialog"), "StoredResearchInfoDialog");

                totalStoredProp  = CheckNull(compType.GetProperty("TotalResearchStored"), "TotalResearchStored");
                universalField   = CheckNull(compType.GetField("StoredUniversalPoints"), "StoredUniversalPoints");
                pointsDictField  = CheckNull(compType.GetField("StoredPointsByBenchAndFacilitiesDefsName"), "StoredPointsByBenchAndFacilitiesDefsName");
                knowledgeField   = CheckNull(compType.GetField("Knowledge"), "Knowledge");
                allKnowledgeProp = CheckNull(kmType.GetProperty("AllStoredKnowledge"), "AllStoredKnowledge");
                srValueProp      = CheckNull(srType.GetProperty("Value"), "StoredResearch.Value");
                srBenchProp      = CheckNull(srType.GetProperty("Bench"), "StoredResearch.Bench");
                srFacilitiesProp = CheckNull(srType.GetProperty("Facilities"), "StoredResearch.Facilities");
                skCategoryField  = CheckNull(skType.GetField("KnowledgeCategory"), "StoredKnowledge.KnowledgeCategory");
                skValueField     = CheckNull(skType.GetField("Value"), "StoredKnowledge.Value");
                openDialogMethod = CheckNull(dialogType.GetMethod("OpenResearch", BindingFlags.Public | BindingFlags.Static), "StoredResearchInfoDialog.OpenResearch");

                Icon = ContentFinder<Texture2D>.Get("StoredResearch/UI/Icons/StoredResearch", reportFailure: false);

                Detected = true;
                Log.Message("[YART] Stored Research detected");
            }
            catch (Exception e)
            {
                Log.Warning($"[YART] Stored Research detected, but preparing is failed: ({e.Message})");
            }
        }

        private static WorldComponent Component
        {
            get
            {
                var world = Find.World;
                if (world == null) return null;
                if (cachedWorld != world)
                {
                    cachedWorld = world;
                    cachedComp = world.GetComponent(compType);
                    totalCachedAt = -1f;
                    tooltipCachedAt = -1f;
                }
                return cachedComp;
            }
        }

        public static float TotalStored
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (now - totalCachedAt < CacheInterval) return cachedTotal;
                totalCachedAt = now;

                cachedTotal = 0f;
                try
                {
                    var comp = Component;
                    if (comp != null) cachedTotal = (float)totalStoredProp.GetValue(comp);
                }
                catch (Exception e)
                {
                    WarnOnce(e);
                }
                return cachedTotal;
            }
        }

        private static readonly StringBuilder sb = new StringBuilder(256);

        public static string BuildTooltip()
        {
            float now = Time.realtimeSinceStartup;
            if (cachedTooltip != null && now - tooltipCachedAt < CacheInterval) return cachedTooltip;
            tooltipCachedAt = now;

            sb.Clear();
            sb.Append("StoredResearchInfoDialog.Title".Translate().Colorize(ColoredText.TipSectionTitleColor));
            sb.Append(": ").Append(TotalStored.ToString("0.##"));

            try
            {
                var comp = Component;
                if (comp != null)
                {
                    var lines = new List<string>();

                    object universal = universalField.GetValue(comp);
                    float universalValue = universal != null ? (float)srValueProp.GetValue(universal) : 0f;
                    if (universalValue >= 0.01f)
                        lines.Add($"{"StoredResearchInfoDialog.UniversalPoints".Translate()} {universalValue:0.##}");

                    if (pointsDictField.GetValue(comp) is IDictionary dict)
                    {
                        foreach (object entry in dict.Values)
                        {
                            float value = (float)srValueProp.GetValue(entry);
                            if (value < 0.01f) continue;

                            var bench = srBenchProp.GetValue(entry) as ThingDef;
                            string label = bench?.LabelCap ?? "?";
                            if (srFacilitiesProp.GetValue(entry) is IList<ThingDef> facilities && facilities.Count > 0)
                                label += $" ({facilities.Select(f => f.LabelCap.Resolve()).ToCommaList()})";
                            lines.Add($"{label}: {value:0.##}");
                        }
                    }

                    if (knowledgeField.GetValue(comp) is object km
                        && allKnowledgeProp.GetValue(km) is IEnumerable knowledges)
                    {
                        foreach (object sk in knowledges)
                        {
                            float value = (float)skValueField.GetValue(sk);
                            if (value < 0.01f) continue;
                            var category = skCategoryField.GetValue(sk) as Def;
                            lines.Add($"{category?.LabelCap ?? "?"}: {value:0.##}");
                        }
                    }

                    if (lines.Count > 0)
                    {
                        sb.AppendLine().AppendLine();
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (i > 0) sb.AppendLine();
                            sb.Append("  ").Append(lines[i]);
                        }
                    }

                    sb.AppendLine().AppendLine();
                    sb.Append("ToolTip.ThisIsStoredResearch".Translate().Colorize(ColoredText.SubtleGrayColor));
                }
            }
            catch (Exception e)
            {
                WarnOnce(e);
            }

            cachedTooltip = sb.ToString();
            return cachedTooltip;
        }

        public static void OpenInfoDialog()
        {
            try
            {
                openDialogMethod?.Invoke(null, null);
            }
            catch (Exception e)
            {
                WarnOnce(e);
            }
        }

        private static bool warned;
        private static void WarnOnce(Exception e)
        {
            if (warned) return;
            warned = true;
            Log.Warning($"[YART] Stored Research compat: reading stored points failed. ({e.Message})");
        }
    }
}
