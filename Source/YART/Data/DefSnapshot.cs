using System.Collections.Generic;
using RimWorld;
using Verse;
using YART.Compat;

namespace YART.Data
{
    // 백그라운드 빌드 중 `Collection was modified` 오류를 막으려고 미리 복사해 둔다
    public static class DefSnapshot
    {
        public sealed class ResearchInput
        {
            public List<ResearchSpec> Specs;
            public List<ResearchProjectDef> Defs;
            public Dictionary<TopoKey, GraphKey> Keys;

            public GraphKey KeyOf(TopoKey key) => key.IsPreset ? GraphKey.ForPreset(key.PresetId) : Keys[key];
        }

        private sealed class Snapshot
        {
            public ResearchInput Research;
            public Dictionary<ResearchProjectDef, List<Def>> ReverseUnlocks;
        }

        private static readonly List<Def> Empty = new List<Def>();
        private static volatile Snapshot current;

        public static void Capture()
        {
            var reverse = new Dictionary<ResearchProjectDef, List<Def>>();

            // 바닐라 UnlockedDefs와 같은 순서
            foreach (var thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                LinkAll(reverse, thingDef.researchPrerequisites, thingDef);
                LinkAll(reverse, thingDef.plant?.sowResearchPrerequisites, thingDef);
            }
            foreach (var terrainDef in DefDatabase<TerrainDef>.AllDefsListForReading)
            {
                LinkAll(reverse, terrainDef.researchPrerequisites, terrainDef);
            }
            foreach (var recipeDef in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                Link(reverse, recipeDef.researchPrerequisite, recipeDef);
                LinkAll(reverse, recipeDef.researchPrerequisites, recipeDef);
            }

            current = new Snapshot
            {
                Research = CaptureResearch(),
                ReverseUnlocks = reverse,
            };
        }

        private static ResearchInput CaptureResearch()
        {
            var input = new ResearchInput
            {
                Specs = new List<ResearchSpec>(),
                Defs = new List<ResearchProjectDef>(),
                Keys = new Dictionary<TopoKey, GraphKey>(),
            };

            foreach (var def in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
            {
                if (!WorldTechLevelCompat.ShouldShow(def)) continue;

                var key = GraphKey.For(def);
                var spec = new ResearchSpec
                {
                    Id = def.defName,
                    Label = def.label.CapitalizeFirst(),
                    TechLevel = (int)def.techLevel,
                    ChannelId = key.Channel.Id,
                    ChannelIsBench = key.Channel.IsBench,
                    TabId = key.Tab?.defName,
                    SourceTab = def.tab?.defName,
                    Prereqs = Ids(def.prerequisites),
                    HiddenPrereqs = Ids(def.hiddenPrerequisites),
                };
                input.Specs.Add(spec);
                input.Defs.Add(def);
                input.Keys[TopoKey.Of(spec)] = key;
            }
            // 필터가 바뀌면 PreOpen에서 리빌드하도록 기록
            WorldTechLevelCompat.MarkBuilt();

            return input;
        }

        private static string[] Ids(List<ResearchProjectDef> defs)
        {
            if (defs == null) return null;
            var ids = new List<string>(defs.Count);
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i] != null) ids.Add(defs[i].defName);
            }
            return ids.ToArray();
        }

        private static Snapshot Current
        {
            get
            {
                var snap = current;
                if (snap == null)
                {
                    Capture();
                    snap = current;
                }
                return snap;
            }
        }

        public static ResearchInput Research => Current.Research;

        public static List<Def> ReverseUnlocksOf(ResearchProjectDef def)
        {
            return Current.ReverseUnlocks.TryGetValue(def, out var list) ? list : Empty;
        }

        private static void LinkAll(Dictionary<ResearchProjectDef, List<Def>> reverse, List<ResearchProjectDef> prereqs, Def unlocked)
        {
            if (prereqs == null) return;
            for (int i = 0; i < prereqs.Count; i++) Link(reverse, prereqs[i], unlocked);
        }

        private static void Link(Dictionary<ResearchProjectDef, List<Def>> reverse, ResearchProjectDef prereq, Def unlocked)
        {
            if (prereq == null) return;
            if (!reverse.TryGetValue(prereq, out var list))
            {
                list = new List<Def>();
                reverse[prereq] = list;
            }
            list.Add(unlocked);
        }
    }
}
