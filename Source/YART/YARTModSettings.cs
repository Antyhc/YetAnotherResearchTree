using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class YARTModSettings : ModSettings
    {
        // 드롭다운에 별도 병합 그래프로 노출
        public List<ResearchPreset> tabPresets = new List<ResearchPreset>();

        // 키: 탭은 "t:"+defName, 프리셋은 "p:"+id
        public List<string> favorites = new List<string>();

        public bool replaceVanillaResearchTab = true;

        // 벤치 탭을 하나의 통합 그래프로 표시. 연구 창에서 토글
        public bool unifiedBenchView = true;

        // 프리셋 그래프에서 같은 탭 연구끼리 모음
        public bool groupClusterByTab = true;

        public bool gridBackground = true;

        // ResearchNodeIconExtension 오버라이드도 이 토글을 따름
        public bool showNodeSourceIcon = true;

        // off면 바닐라처럼 패널에만 표시
        public bool includeHiddenPrerequisites = false;

        // 선행이 더 높은 시대인 연구를 배치된 시대 색으로 칠함
        public bool unifyEraColorToEffective = false;

        // 좌측 패널 해금 목록을 아이콘+라벨 2열로. off면 아이콘만
        public bool unlockedContentExpanded = false;

        // 호버 시 무관한 노드를 어둡게 함
        public bool focusHighlightDimming = false;

        public bool completionLetterInsteadOfDialog = true;

        // SRR 감지 시 연구 큐 조작 차단
        public bool semiRandomCompatEnabled = true;

        // 누적 연구 포인트를 상단 큐 바에 표시
        public bool storedResearchCompatEnabled = true;

        // MP 세션에선 무시
        public bool pauseGameWhenOpen = false;

        // 색상 커스터마이징
        public string activeColorPreset = ColorPalettes.DefaultId;
        public List<Color> eraColors;
        public Color prereqMetColor = Color.green;
        public Color prereqUnmetColor = ColorLibrary.RedReadable;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref replaceVanillaResearchTab, "replaceVanillaResearchTab", true);
            Scribe_Values.Look(ref unifiedBenchView,          "unifiedBenchView",          true);
            Scribe_Values.Look(ref groupClusterByTab,         "groupClusterByTab",         true);
            Scribe_Values.Look(ref gridBackground,            "gridBackground",            true);
            Scribe_Values.Look(ref showNodeSourceIcon,        "showNodeSourceIcon",        true);
            Scribe_Values.Look(ref includeHiddenPrerequisites, "includeHiddenPrerequisites", false);
            Scribe_Values.Look(ref unifyEraColorToEffective,  "unifyEraColorToEffective",  false);
            Scribe_Values.Look(ref unlockedContentExpanded,   "unlockedContentExpanded",   false);
            Scribe_Values.Look(ref focusHighlightDimming,     "focusHighlightDimming",     false);
            Scribe_Values.Look(ref completionLetterInsteadOfDialog, "completionLetterInsteadOfDialog", true);
            Scribe_Values.Look(ref semiRandomCompatEnabled,    "semiRandomCompatEnabled",    true);
            Scribe_Values.Look(ref storedResearchCompatEnabled, "storedResearchCompatEnabled", true);
            Scribe_Values.Look(ref pauseGameWhenOpen,          "pauseGameWhenOpen",          false);
            Scribe_Values.Look(ref activeColorPreset, "activeColorPreset", ColorPalettes.DefaultId);
            Scribe_Collections.Look(ref eraColors, "eraColors", LookMode.Value);
            Scribe_Values.Look(ref prereqMetColor, "prereqMetColor", Color.green);
            Scribe_Values.Look(ref prereqUnmetColor, "prereqUnmetColor", ColorLibrary.RedReadable);
            Scribe_Collections.Look(ref tabPresets, "tabPresets", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && tabPresets == null)
                tabPresets = new List<ResearchPreset>();
            Scribe_Collections.Look(ref favorites, "favorites", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars && favorites == null)
                favorites = new List<string>();
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                EnsureColorsInitialized();
            // Constraints 반영은 YARTMod ctor와 UI 변경 핸들러에서 한다
        }

        private void EnsureColorsInitialized()
        {
            if (eraColors == null || eraColors.Count != ColorPalette.EraCount)
                eraColors = new List<Color>(ColorPalettes.Get(ColorPalettes.DefaultId).era);
        }

        public void ApplyColors()
        {
            EnsureColorsInitialized();
            ActiveColors.Apply(new ColorPalette
            {
                era = eraColors.ToArray(),
                prereqMet = prereqMetColor,
                prereqUnmet = prereqUnmetColor,
            });
        }

        private void ApplyPreset(string id)
        {
            var p = ColorPalettes.Get(id);
            eraColors = new List<Color>(p.era);
            prereqMetColor = p.prereqMet;
            prereqUnmetColor = p.prereqUnmet;
            activeColorPreset = id;
            ApplyColors();
        }

        private void MarkCustomAndApply()
        {
            activeColorPreset = ColorPalettes.CustomId;
            ApplyColors();
        }

        private void SetEraColor(int idx, Color c) { eraColors[idx] = c;      MarkCustomAndApply(); }
        private void SetPrereqMet(Color c)         { prereqMetColor = c;      MarkCustomAndApply(); }
        private void SetPrereqUnmet(Color c)       { prereqUnmetColor = c;    MarkCustomAndApply(); }

        private static readonly string[] SectionKeys =
        {
            "YART_Settings_Section_Display",
            "YART_Settings_Section_Colors",
            "YART_Settings_Section_Behavior",
            "YART_Settings_Section_Compat",
        };

        private int selectedSection;

        private const float NavWidth   = 190f;
        private const float NavRowH    = 38f;
        private const float ColumnGap  = 16f;

        private static void Hint(Listing_Standard listing, string text)
        {
            listing.Label("<color=#888888>" + text + "</color>");
        }

        public void DoSettingsWindowContents(Rect inRect)
        {
            Rect navRect     = new Rect(inRect.x, inRect.y, NavWidth, inRect.height);
            Rect contentRect = new Rect(navRect.xMax + ColumnGap, inRect.y,
                                        inRect.width - NavWidth - ColumnGap, inRect.height);

            DrawNav(navRect);

            Widgets.DrawLineVertical(navRect.xMax + ColumnGap / 2f, inRect.y + 4f, inRect.height - 8f);

            var listing = new Listing_Standard();
            listing.Begin(contentRect);

            using (Temporary.Font(GameFont.Medium))
                listing.Label(SectionKeys[selectedSection].Translate());
            listing.GapLine(4f);

            switch (selectedSection)
            {
                case 0: DrawDisplaySection(listing);     break;
                case 1: DrawColorsSection(listing);      break;
                case 2: DrawBehaviorSection(listing);    break;
                case 3: DrawCompatSection(listing);      break;
            }

            listing.End();
        }

        private void DrawNav(Rect rect)
        {
            float y = rect.y;
            for (int i = 0; i < SectionKeys.Length; i++)
            {
                Rect row = new Rect(rect.x, y, rect.width, NavRowH);

                if (i == selectedSection)
                    Widgets.DrawHighlightSelected(row);
                else if (Mouse.IsOver(row))
                    Widgets.DrawHighlight(row);

                Rect labelRect = new Rect(row.x + 12f, row.y, row.width - 14f, row.height);
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                    Widgets.Label(labelRect, SectionKeys[i].Translate());

                if (Widgets.ButtonInvisible(row) && selectedSection != i)
                {
                    selectedSection = i;
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }

                y += NavRowH + 2f;
            }
        }

        private void DrawDisplaySection(Listing_Standard listing)
        {
            bool replace = replaceVanillaResearchTab;
            listing.CheckboxLabeled("YART_Settings_ReplaceTab".Translate(), ref replace,
                "YART_Settings_ReplaceTabDesc".Translate());
            if (replace != replaceVanillaResearchTab)
            {
                replaceVanillaResearchTab = replace;
                VanillaTabReplacer.Apply(replace);
            }

            listing.CheckboxLabeled("YART_Settings_GridBackground".Translate(), ref gridBackground,
                "YART_Settings_GridBackgroundDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_ShowSourceIcon".Translate(), ref showNodeSourceIcon,
                "YART_Settings_ShowSourceIconDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_GroupClusterByTab".Translate(), ref groupClusterByTab,
                "YART_Settings_GroupClusterByTabDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_HiddenPrereqs".Translate(), ref includeHiddenPrerequisites,
                "YART_Settings_HiddenPrereqsDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_EraColor".Translate(), ref unifyEraColorToEffective,
                "YART_Settings_EraColorDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_FocusDimming".Translate(), ref focusHighlightDimming,
                "YART_Settings_FocusDimmingDesc".Translate());

            listing.GapLine(8f);
            DrawRebuildControls(listing);
        }

        // 그룹 클러스터링, 숨은 선행은 재생성해야 반영됨
        private void DrawRebuildControls(Listing_Standard listing)
        {
            bool? appliedGroup = GraphBuildPipeline.AppliedGroupClusterByTab;
            bool? builtHidden = ResearchGraph.Instance?.BuiltWithHiddenPrereqs;
            bool rebuildNeeded = (appliedGroup != null && appliedGroup != groupClusterByTab)
                              || (builtHidden != null && builtHidden != includeHiddenPrerequisites);

            if (rebuildNeeded)
            {
                using (Temporary.Font(GameFont.Medium))
                    listing.Label("<color=#FFB84D>" + "YART_Settings_RebuildNeeded".Translate() + "</color>");
                listing.Gap(4f);
            }

            bool rebuildClicked = false;
            using (Temporary.Color(rebuildNeeded ? new Color(1f, 0.8f, 0.35f) : GUI.color))
                rebuildClicked = listing.ButtonText("YART_Settings_Rebuild".Translate());
            if (rebuildClicked)
            {
                GraphBuildPipeline.RebuildNonBlocking();
            }
            Hint(listing, "YART_Settings_RebuildDesc".Translate());

            if (!Prefs.DevMode) return;

            listing.Gap();
            if (listing.ButtonText("YART_Settings_ExportGraph".Translate()))
            {
                LayoutExport.WriteToDesktop();
            }
            Hint(listing, "YART_Settings_ExportGraphDesc".Translate());
        }

        private void DrawColorsSection(Listing_Standard listing)
        {
            EnsureColorsInitialized();

            Rect presetRow = listing.GetRect(32f);
            int n = ColorPalettes.SelectableIds.Length;
            const float gap = 8f;
            float bw = (presetRow.width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                string id = ColorPalettes.SelectableIds[i];
                Rect br = new Rect(presetRow.x + i * (bw + gap), presetRow.y, bw, presetRow.height);
                if (activeColorPreset == id)
                    Widgets.DrawHighlightSelected(br);
                if (Widgets.ButtonText(br, ("YART_Settings_ColorPreset_" + id).Translate()))
                    ApplyPreset(id);
            }
            listing.Gap(10f);

            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                listing.Label("YART_Settings_ColorEraHeader".Translate());
            ColorPalette def = ColorPalettes.Get(ColorPalettes.DefaultId);
            for (int i = 0; i < ActiveColors.EraOrder.Length; i++)
            {
                int idx = i;
                ColorRow(listing, ActiveColors.EraOrder[i].ToStringHuman().CapitalizeFirst(),
                    eraColors[i], def.era[i], c => SetEraColor(idx, c));
            }

            listing.Gap(8f);

            // 선행(테크프린트) 충족/미충족
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                listing.Label("YART_Settings_ColorPrereqHeader".Translate());
            ColorRow(listing, "YART_Settings_ColorPrereqMet".Translate(),
                prereqMetColor, def.prereqMet, SetPrereqMet);
            ColorRow(listing, "YART_Settings_ColorPrereqUnmet".Translate(),
                prereqUnmetColor, def.prereqUnmet, SetPrereqUnmet);
        }

        private void ColorRow(Listing_Standard listing, string label, Color current, Color def, System.Action<Color> onSet)
        {
            Rect row = listing.GetRect(28f);
            Widgets.DrawHighlightIfMouseover(row);

            Rect swatch = new Rect(row.x + 2f, row.y + 3f, 42f, 22f);
            Widgets.DrawBoxSolid(swatch, current);
            using (Temporary.Color(new Color(1f, 1f, 1f, 0.35f)))
                Widgets.DrawBox(swatch);

            Rect labelRect = new Rect(swatch.xMax + 12f, row.y, row.width - swatch.width - 14f, row.height);
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
                Widgets.Label(labelRect, label);

            if (Widgets.ButtonInvisible(row))
                Find.WindowStack.Add(new Dialog_YARTColorPicker(current, def, onSet));

            listing.Gap(2f);
        }

        private void DrawBehaviorSection(Listing_Standard listing)
        {
            listing.CheckboxLabeled("YART_Settings_CompletionLetter".Translate(), ref completionLetterInsteadOfDialog,
                "YART_Settings_CompletionLetterDesc".Translate());

            listing.CheckboxLabeled("YART_Settings_PauseOnOpen".Translate(), ref pauseGameWhenOpen,
                "YART_Settings_PauseOnOpenDesc".Translate());
        }

        private void DrawCompatSection(Listing_Standard listing)
        {
            listing.CheckboxLabeled("YART_Settings_SemiRandomCompat".Translate(), ref semiRandomCompatEnabled,
                "YART_Settings_SemiRandomCompatDesc".Translate());
            Hint(listing, (SemiRandomResearchCompat.Detected
                ? "YART_Settings_SemiRandomDetected"
                : "YART_Settings_SemiRandomNotDetected").Translate());

            listing.Gap(8f);

            listing.CheckboxLabeled("YART_Settings_StoredResearchCompat".Translate(), ref storedResearchCompatEnabled,
                "YART_Settings_StoredResearchCompatDesc".Translate());
            Hint(listing, (StoredResearchCompat.Detected
                ? "YART_Settings_StoredResearchDetected"
                : "YART_Settings_StoredResearchNotDetected").Translate());
        }

    }
}
