using System.Collections.Generic;
using Verse;

namespace YART.Data
{
    // `Collection was modified` 에러 방지를 위해서, 미리 복사돈을 떠둔다
    public static class DefSnapshot
    {
        private sealed class Snapshot
        {
            public List<ResearchProjectDef> ResearchProjects;
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
                ResearchProjects = new List<ResearchProjectDef>(DefDatabase<ResearchProjectDef>.AllDefsListForReading),
                ReverseUnlocks = reverse,
            };
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

        public static List<ResearchProjectDef> ResearchProjects => Current.ResearchProjects;

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
