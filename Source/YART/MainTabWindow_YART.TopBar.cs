using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;

namespace YART
{
    public partial class MainTabWindow_YART
    {
        private static List<ResearchChannel> GetVisibleChannels()
        {
            var result = new List<ResearchChannel> { ChannelRegistry.Bench };
            var graph = ResearchGraph.Instance;
            foreach (var channel in ChannelRegistry.All)
            {
                if (channel.IsBench) continue;
                if (graph.ChannelHasRealNodes(channel)) result.Add(channel);
            }
            return result;
        }

        private static List<ResearchTabDef> GetVisibleStandardTabs()
        {
            var result = new List<ResearchTabDef>();
            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                // 프리셋 키(통합 포함)는 Tab=null이므로 반드시 먼저 건너뜀
                if (kvp.Key.IsPreset) continue;
                if (!kvp.Key.Channel.IsBench) continue;
                if (!kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy)) continue;
                result.Add(kvp.Key.Tab);
            }
            result.SortBy(t => (int)t.index);
            return result;
        }

        /// <summary>프리셋 에디터가 선택지로 보여줄 탭 목록 (드롭다운과 동일).</summary>
        public List<ResearchTabDef> SelectableTabsForPreset() => GetVisibleStandardTabs();

        /// <summary>통합 벤치 서브그래프를 (필요하면 빌드해) 확보한다. 사용 가능하면 true.</summary>
        private static bool EnsureUnifiedBenchGraph()
        {
            GraphBuildPipeline.EnsureBuilt();
            ResearchGraph.Instance.GetOrBuildUnifiedBench();
            return ResearchGraph.Instance.GetSubGraph(GraphKey.UnifiedBench) != null;
        }

        /// <summary>어두운 배경 + 테두리 + 호버 하이라이트의 정사각 아이콘 버튼. 클릭되면 true.</summary>
        private static bool DrawIconButton(Rect rect, Texture2D icon, Color iconColor, float pad, string tooltip,
            float bgAlpha = 0.45f)
        {
            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, bgAlpha));
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);
            if (Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);
            GUIDrawingUtilities.DrawIcon(rect.ContractedBy(pad), icon, iconColor);
            TooltipHandler.TipRegion(rect, tooltip);
            return Widgets.ButtonInvisible(rect);
        }

        private Rect ComputeTopLeftControlsRect()
        {
            float row1 = 0f;
            if (SelectedChannel.IsBench && GetVisibleStandardTabs().Count >= 2)
            {
                row1 += Constraints.UnifiedBenchToggleWidth + 8f;
                if (!ViewingAllTabs)
                    row1 += Constraints.TabDropdownWidth + 6f + Constraints.TabPlusButtonSize + 8f;
            }
            float content = Mathf.Max(row1, Constraints.SearchBoxWidth);
            return new Rect(0f, 0f, 16f + content + 16f, Constraints.QueueBarHeight);
        }

        private void DoTopLeftControls()
        {
            const float x0 = 16f;
            const float y1 = 12f;
            const float searchH = 28f;

            bool hasTabRow = false;

            if (SelectedChannel.IsBench)
            {
                var visibleTabs = GetVisibleStandardTabs();

                // 일반 탭 뷰일 때만 selectedTab 유효성 보정. 프리셋(통합 포함) 뷰는 자체 서브그래프이므로 제외.
                if (!CurrentKey.IsPreset)
                {
                    var currentSub = ResearchGraph.Instance.GetSubGraph(CurrentKey);
                    bool currentTabValid = currentSub != null && currentSub.Nodes.Any(n => !n.IsDummy && !n.IsProxy);
                    if (!currentTabValid && visibleTabs.Count > 0)
                    {
                        SwitchGraph(new GraphKey(ChannelRegistry.Bench, visibleTabs[0]), playSound: false);
                    }
                }

                if (visibleTabs.Count >= 2)
                {
                    hasTabRow = true;
                    float x = x0;
                    Rect toggleRect = new Rect(x, y1, Constraints.UnifiedBenchToggleWidth, Constraints.TabDropdownHeight);
                    DrawUnifiedBenchToggle(toggleRect);
                    x += Constraints.UnifiedBenchToggleWidth + 8f;

                    if (!ViewingAllTabs)
                    {
                        Rect ddRect = new Rect(x, y1, Constraints.TabDropdownWidth, Constraints.TabDropdownHeight);
                        DrawTabDropdown(ddRect, visibleTabs);
                        x += Constraints.TabDropdownWidth + 6f;

                        Rect plusRect = new Rect(x, y1, Constraints.TabPlusButtonSize, Constraints.TabDropdownHeight);
                        DrawAddPresetButton(plusRect);
                    }
                }
            }

            // 2행: 검색창 (탭 행이 있으면 그 아래, 없으면 맨 위 자리)
            float searchY = hasTabRow ? y1 + Constraints.TabDropdownHeight + 6f : y1;
            Rect searchRect = new Rect(x0, searchY, Constraints.SearchBoxWidth, searchH);
            DoSearchBox(searchRect);
        }

        private Rect ComputeTrackChipsRect(Rect inRect)
        {
            var channels = GetVisibleChannels();
            if (channels.Count <= 1 && ResearchQueueManager.Instance == null) return Rect.zero;

            float height = channels.Count * (Constraints.TrackChipHeight + Constraints.TrackChipGap)
                           - Constraints.TrackChipGap;
            return new Rect(inRect.xMax - 16f - Constraints.TrackChipWidth,
                inRect.yMax - 16f - height, Constraints.TrackChipWidth, height);
        }

        private void DoTrackChipsColumn(Rect rect)
        {
            if (rect.height <= 0f) return;

            var channels = GetVisibleChannels();
            float y = rect.y;
            foreach (var channel in channels)
            {
                DrawTrackChip(new Rect(rect.x, y, rect.width, Constraints.TrackChipHeight), channel);
                y += Constraints.TrackChipHeight + Constraints.TrackChipGap;
            }
        }

        private void DoZoomIndicator(Rect zoomRect)
        {
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.75f, 0.82f, 0.92f, 0.95f)))
            {
                Widgets.Label(zoomRect, $"{zoomLevel * 100:F0}%");
            }

            TooltipHandler.TipRegion(zoomRect, "YART_ZoomTooltip".Translate());
            if (Widgets.ButtonInvisible(zoomRect) && zoomLevel != 1f)
            {
                Vector2 screenCenter = new Vector2(canvasRect.width, canvasRect.height) / 2f;
                Vector2 centerWorld = (screenCenter - scrollPosition) / zoomLevel;
                zoomLevel = 1f;
                scrollPosition = screenCenter - centerWorld * zoomLevel;
                scrollAnimating = false;
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
        }

        private static readonly Color HudIconColor = new Color(0.82f, 0.87f, 0.97f);

        private void DoVanillaSwitchButton(Rect rect)
        {
            if (DrawIconButton(rect, Assets.IconSwap, HudIconColor, 4f, "YART_SwitchToVanilla".Translate()))
            {
                VanillaTabReplacer.SwitchTo(useYart: false);
            }
        }

        private void DoSettingsButton(Rect rect)
        {
            if (DrawIconButton(rect, Assets.IconSettings, HudIconColor, 4f, "YART_OpenSettings".Translate()))
            {
                Find.WindowStack.Add(new Dialog_YARTSettings());
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
        }

        private const float TrackChipPad = 6f;

        private void DrawTrackChip(Rect rect, ResearchChannel channel)
        {
            bool selected = channel == SelectedChannel;
            Color trackColor = channel.Color;
            var queue = ResearchQueueManager.Instance?.GetQueue(channel);
            var current = ResearchQueueManager.GetCurrentProject(channel);
            int queueCount = queue?.Count ?? 0;

            Widgets.DrawBoxSolid(rect, selected ? GenColor.WithAlpha(trackColor, 0.15f) : new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, selected ? trackColor : Constraints.PanelBorder, selected ? 2f : 1f);
            if (!selected && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            // 1행: 채널명 (+ 큐 수) — 큐 배지가 있으면 nameRect 오른쪽을 줄여 겹침 방지
            Rect nameRect = new Rect(rect.x + TrackChipPad, rect.y + TrackChipPad - 1f,
                rect.width - TrackChipPad * 2 - (queueCount > 0 ? 30f : 0f), 18f);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(GenColor.WithAlpha(trackColor, 0.9f)))
            {
                Widgets.Label(nameRect, channel.Label);
            }

            if (queueCount > 0) DrawTrackChipQueuePill(rect, trackColor, queueCount);

            string tooltip = DrawTrackChipStatusRow(rect, channel, current, queue);

            // 진행바 (하단)
            if (current != null)
            {
                Rect track = new Rect(rect.x + 1f, rect.yMax - 4f, rect.width - 2f, 3f);
                Widgets.DrawBoxSolid(track, new Color(0f, 0f, 0f, 0.5f));
                Widgets.DrawBoxSolid(
                    new Rect(track.x, track.y, track.width * Mathf.Clamp01(current.ProgressPercent), track.height),
                    trackColor);
            }

            if (channel.Tooltip != null) tooltip = tooltip == null ? channel.Tooltip : tooltip + "\n\n" + channel.Tooltip;
            if (tooltip != null) TooltipHandler.TipRegion(rect, tooltip);

            // 프리셋 에디터가 열려 있는 동안에는 트랙(채널) 전환 금지 — 프리뷰 맥락 유지
            if (!Widgets.ButtonInvisible(rect) || Find.WindowStack.IsOpen<Dialog_PresetEditor>()) return;

            // 통합 뷰 ON + 벤치 칩 클릭 → 통합 키로 전환 (일반 탭 키로 돌아가지 않게).
            if (YARTMod.Settings.unifiedBenchView && channel.IsBench && EnsureUnifiedBenchGraph())
                SwitchGraph(GraphKey.UnifiedBench);
            else
                SwitchGraph(new GraphKey(channel, selectedTab));
        }

        /// <summary>칩 우상단의 둥근 큐 개수 배지.</summary>
        private static void DrawTrackChipQueuePill(Rect rect, Color trackColor, int queueCount)
        {
            using (Temporary.Font(GameFont.Small))
            {
                string countText = queueCount.ToString();
                const float pillH = 18f;
                float pillW = Mathf.Max(pillH, Text.CalcSize(countText).x + 10f);
                Rect pillRect = new Rect(rect.xMax - TrackChipPad - pillW, rect.y + TrackChipPad - 1f, pillW, pillH);

                // 둥근 pill 배경 (GUI.DrawTexture의 borderRadius 오버로드 — 게임 동봉 Unity 모듈에서 시그니처 확인됨)
                GUI.DrawTexture(pillRect, BaseContent.WhiteTex, ScaleMode.StretchToFill, true, 0f,
                    GenColor.WithAlpha(trackColor, 0.30f), 0f, pillH / 2f);

                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                using (Temporary.Color(new Color(0.92f, 0.95f, 1f)))
                {
                    Widgets.Label(pillRect, countText);
                }
            }
        }

        /// <summary>
        /// 칩 2행: 진행 중 연구 / 막힌 큐 머리(자물쇠) / 유휴. 상태 툴팁이 있으면 반환한다.
        /// </summary>
        private static string DrawTrackChipStatusRow(Rect rect, ResearchChannel channel,
            ResearchProjectDef current, IReadOnlyList<ResearchProjectDef> queue)
        {
            Rect statusRect = new Rect(rect.x + TrackChipPad, rect.y + 22f,
                rect.width - TrackChipPad * 2, rect.height - 28f);
            string statusText;
            Color statusColor;
            string tooltip = null;
            bool waiting = false;

            if (current != null)
            {
                statusText = current.LabelCap;
                statusColor = Color.white;
            }
            else if (queue != null && queue.Count > 0)
            {
                // 머리가 막혀 있음 (크로스트랙 선행, 벤치 부재 등)
                waiting = true;
                statusText = queue[0].LabelCap;
                statusColor = new Color(0.95f, 0.8f, 0.35f);
                tooltip = "YART_CannotStartYet".Translate(queue[0].LabelCap);
            }
            else
            {
                statusText = "YART_Idle".Translate();
                statusColor = channel.IsBench
                    ? new Color(0.95f, 0.6f, 0.4f) // 벤치 채널 유휴는 경고 톤
                    : new Color(0.5f, 0.5f, 0.55f);
            }

            if (waiting)
            {
                GUIDrawingUtilities.DrawIcon(new Rect(statusRect.x, statusRect.y + 1f, 12f, 12f), Assets.IconLock, statusColor);
                statusRect.x += 16f;
                statusRect.width -= 16f;
            }

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(statusColor))
            {
                Widgets.Label(statusRect, statusText.Truncate(statusRect.width));
            }

            return tooltip;
        }

        private void DrawUnifiedBenchToggle(Rect rect)
        {
            bool isOn = ViewingAllTabs;

            Widgets.DrawBoxSolid(rect, isOn ? GenColor.WithAlpha(SelectedChannel.Color, 0.15f) : new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, isOn ? SelectedChannel.Color : Constraints.PanelBorder, isOn ? 1.5f : 1f);
            if (!isOn && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            // 체크 아이콘 (ON) 또는 빈 박스 아이콘 영역 (OFF)
            float iconSize = 14f;
            Rect iconRect = new Rect(rect.x + 6f, rect.y + (rect.height - iconSize) / 2f, iconSize, iconSize);
            if (isOn)
            {
                GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconCheck, SelectedChannel.Color);
            }
            else
            {
                Widgets.DrawBoxSolid(iconRect, new Color(0.25f, 0.32f, 0.42f));
                GUIDrawingUtilities.DrawBorderLines(iconRect, Constraints.PanelBorder, 1f);
            }

            // 라벨
            Rect labelRect = new Rect(rect.x + iconSize + 10f, rect.y, rect.width - iconSize - 14f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(isOn ? SelectedChannel.Color : new Color(0.75f, 0.82f, 0.92f)))
            {
                Widgets.Label(labelRect, "YART_AllTabs".Translate());
            }

            TooltipHandler.TipRegion(rect, "YART_AllTabsTooltip".Translate());

            if (!Widgets.ButtonInvisible(rect)) return;

            if (isOn)
            {
                // OFF로: 마지막으로 보던 탭으로 복귀
                YARTMod.Settings.unifiedBenchView = false;
                YARTMod.Settings.Write();
                SwitchGraph(new GraphKey(ChannelRegistry.Bench, selectedTab));
            }
            else if (EnsureUnifiedBenchGraph())
            {
                // ON으로: 통합 서브그래프를 확보한 경우에만 설정을 켠다
                YARTMod.Settings.unifiedBenchView = true;
                YARTMod.Settings.Write();
                SwitchGraph(GraphKey.UnifiedBench);
            }
        }

        private void DrawTabDropdown(Rect rect, List<ResearchTabDef> tabs)
        {
            var currentTab = CurrentKey.IsPreset ? null : CurrentKey.Tab;
            ResearchPreset currentPreset = CurrentKey.IsPreset ? TabPresetManager.ById(CurrentKey.PresetId) : null;

            Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            Rect labelRect = new Rect(rect.x + 8f, rect.y, rect.width - 28f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(currentPreset != null ? new Color(0.7f, 0.85f, 1f) : Color.white))
            {
                string label = currentPreset != null ? currentPreset.Name
                    : (currentTab != null ? (string)currentTab.LabelCap : "—");
                Widgets.Label(labelRect, label.Truncate(labelRect.width));
            }

            Rect arrowRect = new Rect(rect.xMax - 20f, rect.y, 16f, rect.height);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            using (Temporary.Color(new Color(0.7f, 0.75f, 0.85f)))
            {
                Widgets.Label(arrowRect, "▼");
            }

            TooltipHandler.TipRegion(rect, "YART_TabDropdownTooltip".Translate());

            if (!Widgets.ButtonInvisible(rect)) return;

            Vector2 anchorScreen = GUIUtility.GUIToScreenPoint(new Vector2(rect.x, rect.yMax + 2f));
            Find.WindowStack.Add(new SearchableFloatMenu(anchorScreen, rect.width + 80f,
                BuildTabDropdownOptions(tabs, currentTab)));
        }

        /// <summary>드롭다운 항목: 위쪽에 프리셋(편집·삭제 버튼 포함), 아래쪽에 일반 연구 탭.</summary>
        private List<SearchableFloatMenu.Option> BuildTabDropdownOptions(
            List<ResearchTabDef> tabs, ResearchTabDef currentTab)
        {
            var options = new List<SearchableFloatMenu.Option>();

            foreach (var preset in TabPresetManager.Presets)
            {
                var local = preset;
                if (local.ResolveTabs().Count == 0) continue;

                var trailing = new List<SearchableFloatMenu.TrailingButton>
                {
                    new SearchableFloatMenu.TrailingButton(TexButton.Rename,
                        () => OpenPresetEditor(local), "YART_Preset_Edit".Translate()),
                    new SearchableFloatMenu.TrailingButton(TexButton.CloseXSmall,
                        () => ConfirmDeletePreset(local), "YART_Preset_Delete".Translate(),
                        new Color(0.95f, 0.55f, 0.5f)),
                };
                options.Add(new SearchableFloatMenu.Option(local.Name, () => SelectPreset(local),
                    selected: CurrentKey.IsPreset && CurrentKey.PresetId == local.Id,
                    trailing: trailing, leading: FavStar(Favorites.PresetKey(local.Id))));
            }

            foreach (var tab in tabs)
            {
                var localTab = tab;
                options.Add(new SearchableFloatMenu.Option(localTab.LabelCap,
                    () => SwitchGraph(new GraphKey(ChannelRegistry.Bench, localTab)),
                    selected: !CurrentKey.IsPreset && localTab == currentTab,
                    leading: FavStar(Favorites.TabKey(localTab))));
            }

            return options;
        }

        // ── 즐겨찾기 퀵리스트─────────────────────────────

        private Rect ComputeFavoritesRect(Rect inRect)
        {
            if (ViewingAllTabs) return Rect.zero;
            int n = Favorites.Resolve().Count;
            if (n == 0) return Rect.zero;

            const float rowH = 30f;
            float h = n * (rowH + 4f) - 4f;
            float w = Constraints.TrackChipWidth;
            return new Rect(inRect.xMax - 16f - w, Constraints.QueueBarHeight + 8f, w, h);
        }

        private void DoFavoritesList(Rect rect)
        {
            if (rect.height <= 0f) return;
            const float rowH = 30f;
            float y = rect.y;
            foreach (var fav in Favorites.Resolve())
            {
                DrawFavoriteButton(new Rect(rect.x, y, rect.width, rowH), fav);
                y += rowH + 4f;
            }
        }

        private void DrawFavoriteButton(Rect rect, Favorites.Entry fav)
        {
            bool selected = fav.Key.Equals(CurrentKey);
            Color accent = fav.Key.IsPreset ? new Color(0.7f, 0.85f, 1f) : ChannelRegistry.Bench.Color;

            Widgets.DrawBoxSolid(rect, selected ? GenColor.WithAlpha(accent, 0.15f) : new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(rect, selected ? accent : Constraints.PanelBorder, selected ? 1.5f : 1f);
            if (!selected && Mouse.IsOver(rect)) Widgets.DrawHighlight(rect);

            const float ic = 16f;
            Rect starRect = new Rect(rect.x + 7f, rect.y + (rect.height - ic) / 2f, ic, ic);
            GUIDrawingUtilities.DrawIcon(starRect, Assets.IconStar, Favorites.OnColor);

            Rect labelRect = new Rect(starRect.xMax + 7f, rect.y, rect.xMax - starRect.xMax - 13f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(selected ? accent : new Color(0.82f, 0.87f, 0.97f)))
            {
                Widgets.Label(labelRect, fav.Label.Truncate(labelRect.width));
            }

            if (Widgets.ButtonInvisible(rect)) SwitchToFavorite(fav.Key);
        }

        private void SwitchToFavorite(GraphKey key)
        {
            if (key.IsPreset)
            {
                var preset = TabPresetManager.ById(key.PresetId);
                if (preset != null) SelectPreset(preset);
            }
            else
            {
                SwitchGraph(key);
            }
        }

        private static SearchableFloatMenu.LeadingToggle FavStar(string favKey)
        {
            return new SearchableFloatMenu.LeadingToggle(
                () => Favorites.Has(favKey), () => Favorites.Toggle(favKey),
                Assets.IconStar, Assets.IconStarHollow, Favorites.OnColor, Favorites.OffColor);
        }

        private void DrawAddPresetButton(Rect rect)
        {
            if (DrawIconButton(rect, TexButton.Plus, new Color(0.75f, 0.85f, 0.95f), 8f,
                    "YART_Preset_New".Translate(), bgAlpha: 0.4f))
            {
                Find.WindowStack.Add(new Dialog_PresetEditor(this));
            }
        }

        private void SelectPreset(ResearchPreset preset)
        {
            var key = GraphKey.ForPreset(preset.Id);
            if (ResearchGraph.Instance.GetSubGraph(key) != null)
            {
                SwitchGraph(key);
            }
            else
            {
                GraphBuildPipeline.RebuildPresetNonBlocking(preset);
                RequestPresetSwitch(preset.Id);
            }
        }

        private void OpenPresetEditor(ResearchPreset preset)
        {
            Find.WindowStack.Add(new Dialog_PresetEditor(this, preset));
        }

        private void ConfirmDeletePreset(ResearchPreset preset)
        {
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "YART_Preset_DeleteConfirm".Translate(preset.Name),
                () =>
                {
                    TabPresetManager.Delete(preset.Id);
                    FallbackFromPresetIfViewing(preset.Id);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                },
                destructive: true));
        }
    }
}
