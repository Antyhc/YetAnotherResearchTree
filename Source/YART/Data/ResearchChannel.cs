using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Compat;

namespace YART.Data
{
    /// <summary>병렬로 진행되는 연구 채널</summary>
    public abstract class ResearchChannel
    {
        public abstract string Id { get; }
        public abstract string Label { get; }
        public abstract Color Color { get; }
        /// <summary>레지스트리 순서상 처음 매치된 채널이 이김</summary>
        public abstract bool Matches(ResearchProjectDef def);
        public abstract ResearchProjectDef CurrentProject { get; }
        /// <summary>idle 경고 톤이 다르고, 연구 탭 드롭다운은 벤치 전용</summary>
        public virtual bool IsBench => false;
        /// <summary>칩 툴팁에 덧붙일 문구</summary>
        public virtual string Tooltip => null;
    }

    public sealed class BenchChannel : ResearchChannel
    {
        public override string Id => TopoKey.BenchChannelId;
        // 바닐라 라벨이라 자동 현지화됨
        public override string Label => MainButtonDefOf.Research.LabelCap;
        public override Color Color => new Color(0.35f, 0.65f, 0.95f); // Blue
        public override bool IsBench => true;
        public override bool Matches(ResearchProjectDef def) => true;
        public override ResearchProjectDef CurrentProject => Find.ResearchManager.GetProject();
    }

    /// <summary>KnowledgeCategoryDef당 1개</summary>
    public sealed class KnowledgeChannel : ResearchChannel
    {
        public readonly KnowledgeCategoryDef Category;
        private readonly Color color;
        private readonly string label;
        private readonly string id; // GraphKey 해시가 자주 읽으므로 캐시해 할당 방지

        public KnowledgeChannel(KnowledgeCategoryDef category, int paletteIndex)
        {
            Category = category;
            color = PickColor(category, paletteIndex);
            // 바닐라 라벨 조합이라 자동 현지화됨 (예: "이상 현상: 기본")
            string anomalyPrefix = ResearchTabDefOf.Anomaly != null ? (string)ResearchTabDefOf.Anomaly.LabelCap : "Anomaly";
            label = anomalyPrefix + ": " + Category.LabelCap;
            id = "Knowledge_" + Category.defName;
        }

        private static Color PickColor(KnowledgeCategoryDef category, int paletteIndex)
        {
            if (category == KnowledgeCategoryDefOf.Basic) return new Color(0.85f, 0.35f, 0.35f);
            if (category == KnowledgeCategoryDefOf.Advanced) return new Color(0.75f, 0.25f, 0.55f);
            return Constraints.ChannelPalette[paletteIndex % Constraints.ChannelPalette.Length];
        }

        public override string Id => id;
        public override string Label => label;
        public override Color Color => color;
        public override bool Matches(ResearchProjectDef def) => def.knowledgeCategory == Category;
        public override ResearchProjectDef CurrentProject => Find.ResearchManager.GetProject(Category);
        public override string Tooltip => Category.overflowCategory != null
            ? $"Excess knowledge overflows to: {Category.overflowCategory.LabelCap}"
            : null;
    }

    public static class ChannelRegistry
    {
        private static List<ResearchChannel> all;
        private static ResearchChannel bench;
        private static readonly object initLock = new object();

        public static IReadOnlyList<ResearchChannel> All
        {
            get { EnsureInit(); return all; }
        }

        public static ResearchChannel Bench
        {
            get { EnsureInit(); return bench; }
        }

        private static void EnsureInit()
        {
            if (all != null) return;
            lock (initLock)
            {
                if (all != null) return;

                var list = new List<ResearchChannel>();
                int paletteIndex = 0;
                foreach (var category in DefDatabase<KnowledgeCategoryDef>.AllDefsListForReading)
                {
                    list.Add(new KnowledgeChannel(category, paletteIndex++));
                }
                if (GravshipCompat.IsVanillaGravshipExpandedLoaded.Value)
                {
                    list.Add(new GravshipChannel());
                }
                bench = new BenchChannel();
                list.Add(bench);

                if (Prefs.DevMode) Log.Message($"[YART] Channels: {string.Join(", ", list.Select(c => c.Id))}");
                all = list;
            }
        }

        public static ResearchChannel Of(ResearchProjectDef def)
        {
            EnsureInit();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Matches(def)) return all[i];
            }
            return bench;
        }

        /// <summary>모르는 Id면 Bench</summary>
        public static ResearchChannel ById(string id)
        {
            EnsureInit();
            if (string.IsNullOrEmpty(id)) return bench;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Id == id) return all[i];
            }
            return bench;
        }
    }
}
