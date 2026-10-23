using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;

namespace YART.Utils
{
    public static class UnlockedDefsUtility
    {
        public class UnlockedGroup
        {
            public List<ResearchProjectDef> AlsoNeeds;
            public List<Def> Defs;
        }

        public static List<Def> GetUnlockedDefs(this ResearchProjectDef proj)
        {
            var node = proj.HomeNode();
            return node != null ? node.UnlockedDefs : new List<Def>();
        }

        public static List<UnlockedGroup> GetUnlockedGroups(this ResearchProjectDef proj)
        {
            var node = proj.HomeNode();
            return node != null ? node.UnlockedDefsGrouped : new List<UnlockedGroup>();
        }

        public static ResearchNode HomeNode(this ResearchProjectDef proj)
        {
            var graph = ResearchGraph.Instance;
            if (proj != null && graph != null && graph.Initialized && graph.AllNodes.TryGetValue(proj, out var node))
                return node;
            return null;
        }

        public static List<UnlockedGroup> GroupUnlockedDefs(IEnumerable<Def> unlockedDefs, ResearchProjectDef self)
        {
            var groups = new List<UnlockedGroup>();
            if (unlockedDefs == null) return groups;

            foreach (var def in unlockedDefs)
            {
                var also = ResearchPrereqsOf(def)
                    .Where(r => r != null && r != self)
                    .Distinct()
                    .OrderBy(r => r.defName)
                    .ToList();

                var g = groups.FirstOrDefault(x => SameResearchSet(x.AlsoNeeds, also));
                if (g == null)
                {
                    g = new UnlockedGroup { AlsoNeeds = also, Defs = new List<Def>() };
                    groups.Add(g);
                }
                g.Defs.Add(def);
            }
            return groups.OrderBy(g => g.AlsoNeeds.Count).ToList();
        }

        public static IEnumerable<ResearchProjectDef> ResearchPrereqsOf(Def def)
        {
            switch (def)
            {
                case ThingDef t:
                    if (t.researchPrerequisites != null)
                        foreach (var r in t.researchPrerequisites) yield return r;
                    if (t.plant?.sowResearchPrerequisites != null)
                        foreach (var r in t.plant.sowResearchPrerequisites) yield return r;
                    break;
                case TerrainDef te:
                    if (te.researchPrerequisites != null)
                        foreach (var r in te.researchPrerequisites) yield return r;
                    break;
                case RecipeDef rc:
                    if (rc.researchPrerequisite != null) yield return rc.researchPrerequisite;
                    if (rc.researchPrerequisites != null)
                        foreach (var r in rc.researchPrerequisites) yield return r;
                    break;
            }
        }

        private static bool SameResearchSet(List<ResearchProjectDef> a, List<ResearchProjectDef> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static float Draw(float x, float cy, float width,
            List<UnlockedGroup> groups, bool expanded, float iconSize,
            Action<ResearchProjectDef> onResearchClick = null)
        {
            if (groups == null) return cy;
            foreach (var g in groups)
            {
                if (g.Defs.Count == 0) continue;
                if (g.AlsoNeeds.Count > 0)
                    cy = DrawGroupHeader(x, cy, width, g.AlsoNeeds, onResearchClick);
                cy = expanded
                    ? DrawList(x, cy, width, g.Defs)
                    : DrawIcons(x, cy, width, g.Defs, iconSize);
            }
            return cy;
        }

        public static float Measure(float width, List<UnlockedGroup> groups, bool expanded, float iconSize)
        {
            if (groups == null) return 0f;
            float h = 0f;
            foreach (var g in groups)
            {
                if (g.Defs.Count == 0) continue;
                if (g.AlsoNeeds.Count > 0)
                    h += MeasureGroupHeader(width, g.AlsoNeeds);
                h += expanded
                    ? MeasureList(g.Defs)
                    : MeasureIcons(width, g.Defs, iconSize);
            }
            return h;
        }

        private static float MeasureGroupHeader(float width, List<ResearchProjectDef> alsoNeeds)
            => LayoutGroupHeader(0f, 0f, width, alsoNeeds, draw: false, onResearchClick: null);

        private static float MeasureList(List<Def> defs)
        {
            const float rowH = 30f, rowGap = 2f;
            int rows = Mathf.CeilToInt(defs.Count / 2f);
            return rows * (rowH + rowGap);
        }

        private static float MeasureIcons(float width, List<Def> defs, float iconSize)
        {
            const float gap = 6f;
            int perRow = Mathf.FloorToInt((width + gap) / (iconSize + gap));
            if (perRow < 1) perRow = 1;
            int rows = Mathf.CeilToInt((float)defs.Count / perRow);
            return rows * (iconSize + gap);
        }

        private static float DrawGroupHeader(float x, float cy, float width,
            List<ResearchProjectDef> alsoNeeds, Action<ResearchProjectDef> onResearchClick)
            => LayoutGroupHeader(x, cy, width, alsoNeeds, draw: true, onResearchClick);

        /// <summary>Measure/Draw 공용 줄바꿈 배치. draw=false면 그리지 않고 헤더 아래 y만 반환</summary>
        private static float LayoutGroupHeader(float x, float cy, float width,
            List<ResearchProjectDef> alsoNeeds, bool draw, Action<ResearchProjectDef> onResearchClick)
        {
            const float lineH = 22f;
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            {
                float startX = x + 4f;
                float maxX = x + width;
                float curX = startX;
                float lineY = cy;

                // 접두사, 구분자처럼 줄바꿈 없이 이어붙이는 텍스트
                void PlaceText(string s)
                {
                    float w = Text.CalcSize(s).x;
                    if (draw)
                    {
                        using (Temporary.Color(Constraints.MutedText))
                            Widgets.Label(new Rect(curX, lineY, w, lineH), s);
                    }
                    curX += w;
                }

                PlaceText((string)"UnlockedWith".Translate() + " ");

                for (int i = 0; i < alsoNeeds.Count; i++)
                {
                    var research = alsoNeeds[i];
                    string text = research.LabelCap;
                    float tw = Text.CalcSize(text).x;

                    if (curX + tw > maxX && curX > startX)
                    {
                        curX = startX;
                        lineY += lineH;
                    }

                    if (draw) DrawResearchToken(new Rect(curX, lineY, tw, lineH), research, text, onResearchClick);
                    curX += tw;

                    if (i < alsoNeeds.Count - 1)
                        PlaceText(", ");
                }
                return lineY + lineH;
            }
        }

        private static void DrawResearchToken(Rect tokenRect, ResearchProjectDef research, string text,
            Action<ResearchProjectDef> onResearchClick)
        {
            if (onResearchClick != null && Mouse.IsOver(tokenRect)) Widgets.DrawHighlight(tokenRect);

            using (Temporary.Color(research.IsFinished ? ActiveColors.PrereqMet : ActiveColors.PrereqUnmet))
                Widgets.Label(tokenRect, text);

            if (onResearchClick != null && Widgets.ButtonInvisible(tokenRect))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                onResearchClick(research);
            }
        }

        private static float DrawList(float x, float cy, float width, List<Def> defs)
        {
            const float rowH = 30f, rowGap = 2f, colGap = 8f, cellIcon = 24f;
            float colW = (width - colGap) / 2f;
            for (int i = 0; i < defs.Count; i++)
            {
                var def = defs[i];
                int col = i % 2;
                float cellX = x + col * (colW + colGap);
                Rect cellRect = new Rect(cellX, cy, colW, rowH);

                Rect iconRect = new Rect(cellX, cy + (rowH - cellIcon) / 2f, cellIcon, cellIcon);
                Widgets.DefIcon(iconRect, def, null, 1f, null, drawPlaceholder: true);

                Rect labelRect = new Rect(iconRect.xMax + 6f, cy, colW - cellIcon - 6f, rowH);
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                    Widgets.Label(labelRect, ((string)def.LabelCap).Truncate(labelRect.width));

                HandleCell(cellRect, def);
                if (col == 1) cy += rowH + rowGap;
            }
            if (defs.Count % 2 == 1) cy += rowH + rowGap;
            return cy;
        }

        private static float DrawIcons(float x, float cy, float width, List<Def> defs, float iconSize)
        {
            const float gap = 6f;
            float curX = x, rowY = cy;
            foreach (var def in defs)
            {
                if (curX + iconSize > x + width) { curX = x; rowY += iconSize + gap; }
                HandleCell(new Rect(curX, rowY, iconSize, iconSize), def, drawIcon: true);
                curX += iconSize + gap;
            }
            return rowY + iconSize + gap;
        }

        private static void HandleCell(Rect r, Def def, bool drawIcon = false)
        {
            if (drawIcon) Widgets.DefIcon(r, def, null, 1f, null, drawPlaceholder: true);
            if (Mouse.IsOver(r))
            {
                Widgets.DrawHighlight(r);
                TooltipHandler.TipRegion(r, def.LabelCap);
            }
            if (Widgets.ButtonInvisible(r))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                Find.WindowStack.Add(new Dialog_InfoCard(def));
            }
        }
    }
}
