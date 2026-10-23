using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace YART.Data
{
    /// <summary>여러 연구 탭을 하나의 병합 그래프로 묶는 사용자 탭 그룹</summary>
    public class ResearchPreset : IExposable
    {
        public string Id;
        public string Name;
        public List<string> TabDefNames = new List<string>();

        public bool IncludeAllBenchTabs;

        public ResearchPreset() { }

        public static ResearchPreset AllTabs => new ResearchPreset
        {
            Id = GraphKey.AllTabsId,
            Name = "YART_AllTabs".Translate(),
            TabDefNames = new List<string>(),
            IncludeAllBenchTabs = true,
        };

        public ResearchPreset(string name, IEnumerable<string> tabDefNames)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = name;
            TabDefNames = new List<string>(tabDefNames);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id");
            Scribe_Values.Look(ref Name, "name");
            Scribe_Collections.Look(ref TabDefNames, "tabDefNames", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && TabDefNames == null)
                TabDefNames = new List<string>();
        }

        /// <summary>로드되지 않은 탭은 제외</summary>
        public List<ResearchTabDef> ResolveTabs()
        {
            var result = new List<ResearchTabDef>();
            if (TabDefNames == null) return result;
            foreach (var defName in TabDefNames)
            {
                var tab = DefDatabase<ResearchTabDef>.GetNamedSilentFail(defName);
                if (tab != null) result.Add(tab);
            }
            return result;
        }
    }
}
