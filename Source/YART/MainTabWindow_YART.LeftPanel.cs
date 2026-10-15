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
    // 좌측 상세 패널: 선택 연구 정보, 잠금 사유, 액션 버튼, 해금/선행/후행 목록
    public partial class MainTabWindow_YART
    {
        private static readonly List<string> lockedReasonsBuffer = new List<string>(8);
        private static readonly List<ResearchNode> emptyNodeList = new List<ResearchNode>();

        // CalculateLeftPanelHeight 캐시 — Text.CalcHeight 다발 호출을 0.5초 주기로 제한
        // (상태 캐시와 동일 주기; 잠금 사유/진행 상태 변화 반영용)
        private ResearchNode panelHeightCacheNode;
        private float panelHeightCacheWidth;
        private float panelHeightCacheValue;
        private float panelHeightCacheTime = float.NegativeInfinity;

        private const float PanelPadding = 12f;

        /// <summary>
        /// TechLevel 표시 라벨. 바닐라 ToStringHuman은 Undefined에 대한 번역 키가 Enums.xml에 없어
        /// 번역에 실패한다(릴리스: 키 평문 노출). Undefined만 YART 키로 대체하고 나머지는 바닐라 라벨.
        /// </summary>
        private static string TechLevelLabel(TechLevel tl)
        {
            return tl == TechLevel.Undefined
                ? (string)"YART_TechLevelUndefined".Translate()
                : tl.ToStringHuman();
        }

        private void DoLeftRect(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, Constraints.PanelBg);
            if (!history.HasCurrent) return;

            var currentNode = history.Current;
            Color eraColor = currentNode.EraAccentColor;

            GUIDrawingUtilities.DrawTopAccentBar(rect, eraColor, 4f);
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);

            if (!DrawPanelNavButtons(rect, ref currentNode)) return; // 닫기(X) — 이번 프레임 종료

            const float headerHeight = 45f;
            // 실제 콘텐츠 폭 = viewRect(rect.width-20) - 16 — 높이 계산도 같은 폭을 써야 한다
            float contentWidth = rect.width - 36f;
            Rect viewRect = new Rect(0, 0, rect.width - 20, GetCachedPanelHeight(currentNode, contentWidth));
            Rect scrollRect = new Rect(rect.x, rect.y + headerHeight, rect.width, rect.height - headerHeight);

            Widgets.BeginScrollView(scrollRect, ref leftPanelScrollPosition, viewRect);
            DrawPanelBody(currentNode, eraColor, contentWidth);
            Widgets.EndScrollView();
        }

        /// <summary>패널 우상단 내비 버튼 (ⓘ / ＜ / ＞ / X). 닫혔으면 false — 호출자는 즉시 종료해야 한다.</summary>
        private bool DrawPanelNavButtons(Rect rect, ref ResearchNode currentNode)
        {
            const float btnSize = 24f;
            const float gap = 4f;
            float btnY = rect.y + 10f;
            float btnX = rect.xMax - btnSize - 8f;

            if (DrawNavButton(new Rect(btnX, btnY, btnSize, btnSize), "X", true))
            {
                history.Clear();
                return false;
            }
            btnX -= btnSize + gap;

            if (DrawNavButton(new Rect(btnX, btnY, btnSize, btnSize), ">", history.CanRedo))
            {
                history.Redo();
                leftPanelScrollPosition = Vector2.zero;
                currentNode = history.Current;
                NavigateToHistoryNode(currentNode);
            }
            btnX -= btnSize + gap;

            if (DrawNavButton(new Rect(btnX, btnY, btnSize, btnSize), "<", history.CanUndo))
            {
                history.Undo();
                leftPanelScrollPosition = Vector2.zero;
                currentNode = history.Current;
                NavigateToHistoryNode(currentNode);
            }
            btnX -= btnSize + gap;

            // Info card (ⓘ) — 바닐라 위젯, 클릭 시 Dialog_InfoCard를 스스로 연다
            Widgets.InfoCardButton(new Rect(btnX, btnY, btnSize, btnSize), currentNode.Def);
            return true;
        }

        private float GetCachedPanelHeight(ResearchNode node, float contentWidth)
        {
            if (panelHeightCacheNode != node || panelHeightCacheWidth != contentWidth
                || Time.realtimeSinceStartup - panelHeightCacheTime > 0.5f)
            {
                panelHeightCacheValue = CalculateLeftPanelHeight(node, contentWidth);
                panelHeightCacheNode = node;
                panelHeightCacheWidth = contentWidth;
                panelHeightCacheTime = Time.realtimeSinceStartup;
            }
            return panelHeightCacheValue;
        }

        /// <summary>스크롤 뷰 안의 본문. 각 구역은 소비한 높이를 반환하므로 y 누적 순서가 곧 배치 순서다.</summary>
        private void DrawPanelBody(ResearchNode node, Color eraColor, float contentWidth)
        {
            const float padding = PanelPadding;
            float y = 0f;

            y += DrawPanelTitle(node, eraColor, padding, y, contentWidth);
            y += DrawTechLevelBadge(node, eraColor, padding, y);
            y += DrawPanelDescription(node, padding, y, contentWidth);
            y += DrawPanelCost(node, padding, y, contentWidth);
            y += DrawCostFactorWarning(node.Def, padding, y, contentWidth);

            // 요구 작업대/시설 (완료 전 상시 표시 — 어떤 건물이 필요한지 계획에 필요)
            y += DrawRequirementsSection(node, padding, y, contentWidth);
            // Medieval Overhaul의 Schematic
            y += DrawSchematicSection(node, padding, y, contentWidth);
            // 선행 미완료는 큐로 해결되므로 사유에 포함되지 않는다
            y += DrawLockedReasonsSection(node, padding, y, contentWidth);
            y += DrawTechprintBlock(node, padding, y, contentWidth);
            y += DrawResearchActionButtons(node, padding, y, contentWidth);

            y += DrawDivider(padding, y, contentWidth);
            y += DrawUnlockedContentSection(node, padding, y, contentWidth);
            y += 8;

            y += DrawDivider(padding, y, contentWidth);
            y += DrawNodeCardSection("YART_Prerequisites".Translate(), node.PanelPrerequisites ?? emptyNodeList,
                Constraints.SectionPrerequisites, padding, y, contentWidth);
            y += 8;

            y += DrawDivider(padding, y, contentWidth);
            DrawNodeCardSection("YART_Unlocks".Translate(), node.PanelChildren ?? emptyNodeList,
                Constraints.SectionFollowups, padding, y, contentWidth);
        }

        /// <summary>연구 제목(시대 색) + 우측 출처 모드/DLC 아이콘.</summary>
        private float DrawPanelTitle(ResearchNode node, Color eraColor, float padding, float y, float contentWidth)
        {
            const float iconSz = 24f;
            var titleIcon = node.IsHidden ? null : GetModIcon(node.SourceMod);
            float titleW = contentWidth - (titleIcon != null ? iconSz + 6f : 0f);

            using (Temporary.Font(GameFont.Medium))
            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Color(eraColor))
            {
                Widgets.Label(new Rect(padding, y, titleW, 30), node.Label);
            }

            if (titleIcon != null)
            {
                Rect iconRect = new Rect(padding + contentWidth - iconSz, y + 3f, iconSz, iconSz);
                var prevColor = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(iconRect, titleIcon, ScaleMode.ScaleToFit);
                GUI.color = prevColor;
                if (Mouse.IsOver(iconRect) && node.SourceMod != null)
                {
                    TooltipHandler.TipRegion(iconRect, node.SourceMod.Name);
                }
            }
            return 32f;
        }

        private float DrawTechLevelBadge(ResearchNode node, Color eraColor, float padding, float y)
        {
            Rect badgeRect = new Rect(padding, y, 80, 20);
            Widgets.DrawBoxSolid(badgeRect, GenColor.WithAlpha(eraColor, 0.3f));
            GUIDrawingUtilities.DrawBorderLines(badgeRect, GenColor.WithAlpha(eraColor, 0.6f), 1f);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(badgeRect, TechLevelLabel(node.TechLevel));
            }
            return 28f;
        }

        private float DrawPanelDescription(ResearchNode node, float padding, float y, float contentWidth)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.8f, 0.8f, 0.85f)))
            {
                float descHeight = Text.CalcHeight(node.Def.description, contentWidth);
                Widgets.Label(new Rect(padding, y, contentWidth, descHeight), node.Def.description);
                return descHeight + 12;
            }
        }

        /// <summary>비용/진행률 — 바닐라와 동일하게 CostApparent(테크레벨 계수 반영). 진행 중이면 "123 / 700".</summary>
        private float DrawPanelCost(ResearchNode node, float padding, float y, float contentWidth)
        {
            using (Temporary.Font(GameFont.Small))
            {
                string costText = !node.Def.IsFinished && node.Progress > 0f
                    ? "YART_CostProgress".Translate(node.Def.ProgressApparentString, node.Def.CostApparent.ToString("F0"))
                    : "YART_Cost".Translate(node.Def.CostApparent.ToString("N0"));
                Widgets.Label(new Rect(padding, y, contentWidth, 22), costText);
            }
            return 28f;
        }

        /// <summary>테크레벨 차이로 비용이 부풀려진 경우의 경고 문구 (해당 없으면 null). 바닐라 번역 키 재사용.</summary>
        private static string CostFactorWarningText(ResearchProjectDef def)
        {
            if (def.IsFinished) return null;
            float costFactor = def.CostFactor(Faction.OfPlayer.def.techLevel);
            if (costFactor == 1f) return null;

            return "TechLevelTooLow".Translate(Faction.OfPlayer.def.techLevel.ToStringHuman(),
                       def.techLevel.ToStringHuman(), (1f / costFactor).ToStringPercent())
                   + " " + "ResearchCostComparison".Translate(
                       def.Cost.ToString("F0"), def.CostApparent.ToString("F0"));
        }

        private float DrawCostFactorWarning(ResearchProjectDef def, float padding, float y, float contentWidth)
        {
            string costWarn = CostFactorWarningText(def);
            if (costWarn == null) return 0f;

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Color(new Color(0.95f, 0.8f, 0.35f)))
            {
                float warnHeight = Text.CalcHeight(costWarn, contentWidth);
                Widgets.Label(new Rect(padding, y, contentWidth, warnHeight), costWarn);
                return warnHeight + 6;
            }
        }

        private float DrawLockedReasonsSection(ResearchNode node, float padding, float y, float contentWidth)
        {
            if (node.State != ResearchNodeState.Locked) return 0f;
            node.GetLockedReasons(lockedReasonsBuffer);
            if (lockedReasonsBuffer.Count == 0) return 0f;

            float startY = y;
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.95f, 0.5f, 0.45f)))
            {
                Widgets.Label(new Rect(padding, y, contentWidth, 22), "YART_LockedReasons".Translate());
                y += 22;
                foreach (var reason in lockedReasonsBuffer)
                {
                    string line = "  • " + reason;
                    float lineHeight = Text.CalcHeight(line, contentWidth);
                    Widgets.Label(new Rect(padding, y, contentWidth, lineHeight), line);
                    y += lineHeight + 2;
                }
            }
            return (y - startY) + 8;
        }

        /// <summary>청사진 미충족 시 구입처 목록 + (개발 모드) 즉시 적용 버튼.</summary>
        private float DrawTechprintBlock(ResearchNode node, float padding, float y, float contentWidth)
        {
            var def = node.Def;
            if (def.TechprintCount <= 0 || def.TechprintRequirementMet) return 0f;

            float startY = y;
            y += DrawTechprintSourcesSection(def, padding, y, contentWidth);

            if (Prefs.DevMode)
            {
                if (DrawPanelButton(new Rect(padding, y, contentWidth, 26), "Dev: Apply techprint",
                        new Color(0.85f, 0.5f, 0.3f), enabled: true))
                {
                    Find.ResearchManager.ApplyTechprint(def, null);
                    SoundDefOf.TechprintApplied.PlayOneShotOnCamera();
                    ResearchNode.InvalidateAllStates();
                }
                y += 30;
            }
            return (y - startY) + 4;
        }

        private float DrawUnlockedContentSection(ResearchNode node, float padding, float y, float contentWidth)
        {
            float startY = y;
            var unlockedDefs = node.UnlockedDefs;
            Rect headerRect = new Rect(padding, y, contentWidth, 25);
            y += DrawSectionHeader(headerRect, "Unlocks".Translate(), unlockedDefs.Count, Constraints.SectionUnlocks);

            // 2-column(아이콘+라벨) 토글 — 카운트 배지(우측 24px) 왼쪽에. 항목이 있을 때만.
            if (unlockedDefs.Count > 0)
            {
                DrawUnlockedViewToggle(new Rect(headerRect.xMax - 24f - 4f - 18f, headerRect.y + 2f, 18f, 18f));
            }

            if (unlockedDefs.Count == 0)
            {
                y += DrawNonePlaceholder(padding, y, contentWidth);
            }
            else
            {
                y = UnlockedDefsUtility.Draw(padding, y, contentWidth,
                    node.UnlockedDefsGrouped, YARTMod.Settings.unlockedContentExpanded, 32f,
                    NavigateToResearchDef);
            }
            return y - startY;
        }

        private void DrawUnlockedViewToggle(Rect toggleRect)
        {
            bool expanded = YARTMod.Settings.unlockedContentExpanded;
            if (Mouse.IsOver(toggleRect)) Widgets.DrawHighlight(toggleRect);
            GUIDrawingUtilities.DrawIcon(toggleRect, Assets.IconSwap,
                expanded ? Constraints.SectionUnlocks : Constraints.ToggleInactive);
            TooltipHandler.TipRegion(toggleRect, "YART_ToggleUnlockedView".Translate());
            if (Widgets.ButtonInvisible(toggleRect))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                YARTMod.Settings.unlockedContentExpanded = !expanded;
                YARTMod.Settings.Write();
            }
        }

        /// <summary>헤더 + 연구 카드 목록 (선행/후행 공용). 비어 있으면 "없음" 한 줄.</summary>
        private float DrawNodeCardSection(string title, List<ResearchNode> nodes, Color accentColor,
            float padding, float y, float contentWidth)
        {
            float startY = y;
            y += DrawSectionHeader(new Rect(padding, y, contentWidth, 25), title, nodes.Count, accentColor);

            if (nodes.Count == 0)
            {
                y += DrawNonePlaceholder(padding, y, contentWidth);
            }
            else
            {
                foreach (var n in nodes)
                {
                    y += DrawResearchCard(new Rect(padding, y, contentWidth, 36), n);
                }
            }
            return y - startY;
        }

        /// <summary>빈 섹션의 "없음" 한 줄. 반환값 = 소비 높이.</summary>
        private static float DrawNonePlaceholder(float padding, float y, float contentWidth)
        {
            using (Temporary.Color(Constraints.MutedText))
            using (Temporary.Font(GameFont.Small))
            {
                Widgets.Label(new Rect(padding + 8, y, contentWidth, 22), "YART_None".Translate());
            }
            return 26f;
        }

        /// <summary>구분선 한 줄. 반환값 = 소비 높이.</summary>
        private float DrawDivider(float padding, float y, float contentWidth)
        {
            DrawSectionDivider(new Rect(padding, y, contentWidth, 1));
            return 12f;
        }

        private void NavigateToResearchDef(ResearchProjectDef def)
        {
            if (def == null) return;
            var node = GetNodeOnCurrentGraph(def);
            if (node == null) ResearchGraph.Instance.AllNodes.TryGetValue(def, out node);
            JumpToNode(node);
        }

        /// <summary>
        /// 이력 내비게이션 공용: 노드가 다른 그래프 소속이면 전환 후 포커스.
        /// JumpToNode와 달리 history는 건드리지 않는다 (이력 이동 중이므로).
        /// 항목은 현재 뷰 기준으로 재해석한다 — 통합/탭 뷰를 전환한 뒤에도 현재 뷰에 머무르고,
        /// 현재 그래프에 그 def가 없을 때만 원래 그래프로 전환한다.
        /// </summary>
        private void NavigateToHistoryNode(ResearchNode node)
        {
            if (node == null) return;
            var resolved = GetNodeOnCurrentGraph(node.Def) ?? node;
            if (!resolved.Key.Equals(CurrentKey))
            {
                SwitchGraph(resolved.Key, playSound: false); // 나브 버튼/마우스 버튼이 자체 Click 재생
            }
            FocusOnNode(resolved);
        }

        /// <summary>
        /// 좌측 패널의 연구 액션 버튼 영역. 상태에 따라 [Start now]/[Queue]/[Unqueue]/[Stop] 조합을 그립니다.
        /// 반환값은 사용한 세로 높이 (CalculateLeftPanelHeight의 45와 일치해야 함).
        /// </summary>
        private float DrawResearchActionButtons(ResearchNode node, float padding, float y, float contentWidth)
        {
            const float rowHeight = 45f;
            const float btnHeight = 32f;
            var def = node.Def;
            var queueMgr = ResearchQueueManager.Instance;

            float btnWidth = (contentWidth - 8f) / 2f;
            Rect btnA = new Rect(padding, y, btnWidth, btnHeight);
            Rect btnB = new Rect(padding + btnWidth + 8f, y, btnWidth, btnHeight);
            Rect btnFull = new Rect(padding, y, contentWidth, btnHeight);

            bool isCurrent = Find.ResearchManager.IsCurrentProject(def);
            int queuePos = queueMgr?.GetQueuePosition(def) ?? -1;

            if (node.State == ResearchNodeState.Completed)
            {
                DrawPanelButton(btnFull, "YART_Completed".Translate(), Constraints.ButtonDisabled, enabled: false);
                return rowHeight;
            }

            if (isCurrent || node.State == ResearchNodeState.InProgress)
            {
                if (SemiRandomResearchCompat.Active)
                {
                    DrawPanelButton(btnFull, "YART_InProgress".Translate(), Constraints.ButtonActive, enabled: false);
                    TooltipHandler.TipRegion(btnFull, "YART_SemiRandomActiveDesc".Translate());
                    return rowHeight;
                }

                DrawPanelButton(btnA, "YART_InProgress".Translate(), Constraints.ButtonActive, enabled: false);
                if (DrawPanelButton(btnB, "YART_Stop".Translate(), new Color(0.7f, 0.3f, 0.3f), enabled: true))
                {
                    if (queueMgr != null) queueMgr.Remove(def);
                    else Find.ResearchManager.StopProject(def);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                }
                return rowHeight;
            }

            if (node.State == ResearchNodeState.Available)
            {
                // Semi Random Research 활성: 수동 시작/큐잉 불가 — 그 모드의 'Next Research' 탭에서 선택.
                if (SemiRandomResearchCompat.Active)
                {
                    DrawPanelButton(btnFull, "YART_SemiRandomActive".Translate(), Constraints.ButtonDisabled, enabled: false);
                    TooltipHandler.TipRegion(btnFull, "YART_SemiRandomActiveDesc".Translate());
                    return rowHeight;
                }

                if (queuePos > 0)
                {
                    if (DrawPanelButton(btnFull, "YART_Unqueue".Translate(queuePos), Constraints.ButtonDisabled, enabled: true))
                    {
                        queueMgr.Remove(def);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }
                    return rowHeight;
                }

                int chainCount = ResearchQueueManager.CollectMissingChain(def).Count;
                string label = chainCount > 1 ? "YART_QueueChain".Translate(chainCount) : "YART_Queue".Translate();
                if (queueMgr != null) TooltipHandler.TipRegion(btnFull, QueueModifierTip());
                if (DrawPanelButton(btnFull, label, Constraints.ButtonActive, enabled: queueMgr != null))
                {
                    EnqueueByModifier(queueMgr, def); // 노드 클릭과 동일: 기본=끝, Alt=앞, Ctrl=비우고 추가
                }
                return rowHeight;
            }

            DrawPanelButton(btnFull, "YART_Locked".Translate(), Constraints.ButtonDisabled, enabled: false);
            return rowHeight;
        }

        private static void EnqueueByModifier(ResearchQueueManager mgr, ResearchProjectDef def)
        {
            if (mgr == null) return;
            var e = Event.current;
            if (e.control) mgr.EnqueueWithChainExclusive(def);
            else if (e.alt) mgr.EnqueueWithChainToFront(def);
            else mgr.EnqueueWithChain(def);
        }

        private static string QueueModifierTip() => string.Concat(
            (string)"YART_TipQueueShift".Translate(), "\n",
            (string)"YART_TipQueueAlt".Translate(), "\n",
            (string)"YART_TipQueueCtrl".Translate());

        private bool DrawPanelButton(Rect rect, string label, Color color, bool enabled)
        {
            Widgets.DrawBoxSolid(rect, GenColor.WithAlpha(color, enabled ? 0.3f : 0.18f));
            GUIDrawingUtilities.DrawBorderLines(rect, GenColor.WithAlpha(color, enabled ? 1f : 0.5f), 1f);

            if (enabled && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            using (Temporary.Alpha(enabled ? 1f : 0.5f))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(rect, label);
            }

            return enabled && Widgets.ButtonInvisible(rect);
        }

        /// <summary>연구를 즉시 시작합니다. 큐 매니저를 통해 큐 머리로 올린 뒤 바닐라 API로 시작.</summary>
        private void StartResearch(ResearchProjectDef project)
        {
            var queueMgr = ResearchQueueManager.Instance;
            if (queueMgr != null)
            {
                queueMgr.StartNow(project);
                return;
            }

            // 폴백: 바닐라 MainTabWindow_Research.DoBeginResearch와 동일한 흐름
            SoundDefOf.ResearchStart.PlayOneShotOnCamera();
            Find.ResearchManager.SetCurrentProject(project);
            TutorSystem.Notify_Event("StartResearchProject");
            ResearchNode.InvalidateAllStates();
        }

        /// <summary>글래스모피즘 스타일 내비게이션 버튼. 클릭 시 Click 사운드를 자체 재생한다.</summary>
        private bool DrawNavButton(Rect rect, string label, bool enabled)
        {
            Color bgColor = enabled ? new Color(0.15f, 0.18f, 0.25f, 0.9f) : new Color(0.1f, 0.1f, 0.12f, 0.5f);
            Color borderColor = enabled ? Constraints.PanelBorder : new Color(0.15f, 0.15f, 0.18f);

            Widgets.DrawBoxSolid(rect, bgColor);
            GUIDrawingUtilities.DrawBorderLines(rect, borderColor, 1f);

            if (enabled && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            using (Temporary.Alpha(enabled ? 1f : 0.3f))
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(rect, label);
            }

            bool clicked = enabled && Widgets.ButtonInvisible(rect);
            if (clicked) SoundDefOf.Click.PlayOneShotOnCamera();
            return clicked;
        }

        /// <summary>섹션 헤더 + 우측 카운트 배지. 반환값 = 소비 높이.</summary>
        private float DrawSectionHeader(Rect rect, string title, int count, Color accentColor)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(accentColor))
            {
                Widgets.Label(rect, title);
            }

            Rect countRect = new Rect(rect.xMax - 24, rect.y + 2, 24, 18);
            Widgets.DrawBoxSolid(countRect, GenColor.WithAlpha(accentColor, 0.2f));
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                Widgets.Label(countRect, count.ToString());
            }

            return rect.height + 4;
        }

        private void DrawSectionDivider(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.2f, 0.25f, 0.35f, 0.5f));
        }

        /// <summary>연구 한 건을 카드로 (상태 아이콘 + 라벨 + 클릭 점프). 반환값 = 카드 높이 + 간격.</summary>
        private float DrawResearchCard(Rect rect, ResearchNode node)
        {
            Color eraColor = node.EraAccentColor;
            Color bgColor;
            Color textColor = Color.white;

            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    bgColor = new Color(0.15f, 0.25f, 0.15f, 0.7f);
                    break;
                case ResearchNodeState.Available:
                    bgColor = new Color(0.2f, 0.2f, 0.1f, 0.7f);
                    break;
                case ResearchNodeState.InProgress:
                    bgColor = new Color(0.1f, 0.2f, 0.25f, 0.7f);
                    break;
                default:
                    bgColor = new Color(0.12f, 0.12f, 0.15f, 0.6f);
                    textColor = new Color(0.6f, 0.6f, 0.65f);
                    break;
            }

            Widgets.DrawBoxSolid(rect, bgColor);

            bool isHovered = Mouse.IsOver(rect);
            if (isHovered)
            {
                Widgets.DrawHighlight(rect);
            }

            const float iconSize = 20f;
            Rect iconRect = new Rect(rect.x + 8, rect.center.y - iconSize / 2, iconSize, iconSize);
            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconCheck, GenColor.WithAlpha(eraColor, 0.9f));
                    break;
                case ResearchNodeState.Locked:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconLock, GenColor.WithAlpha(Constraints.ButtonDisabledText, 0.8f));
                    break;
                case ResearchNodeState.InProgress:
                    GUIDrawingUtilities.DrawIcon(iconRect, Assets.IconPlay, eraColor);
                    break;
                case ResearchNodeState.Available:
                default:
                    GUIDrawingUtilities.DrawIconPlaceholder(iconRect, GenColor.WithAlpha(eraColor, 0.6f));
                    break;
            }

            Rect labelRect = new Rect(rect.x + 34, rect.y, rect.width - 40, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(textColor))
            {
                Widgets.Label(labelRect, node.Label);
            }

            // 미발견 연구는 설명 마스킹 + 선택/점프 불가
            if (isHovered && !node.IsHidden)
            {
                TooltipHandler.TipRegion(rect, node.Def.description);
            }

            if (Widgets.ButtonInvisible(rect) && !node.IsHidden)
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                if (!history.HasCurrent || history.Current != node)
                {
                    history.Push(node);
                    leftPanelScrollPosition = Vector2.zero;
                }

                if (!node.Key.Equals(CurrentKey))
                {
                    SwitchGraph(node.Key, playSound: false); // 카드 클릭 Click으로 통일
                }
                FocusOnNode(node);
            }

            return rect.height + 4;
        }

        /// <summary>
        /// 요구 작업대/시설 섹션. 완료 전, 특수 작업대(requiredResearchBuilding)나 시설이 있을 때만 표시.
        /// 충족 여부(PlayerHasAnyAppropriateResearchBench)로 색을 칠하고, 각 항목은 InfoCard로 연결.
        /// 반환값은 사용한 세로 높이 (MeasureRequirementsHeight와 일치해야 함).
        /// </summary>
        private float DrawRequirementsSection(ResearchNode node, float padding, float y, float contentWidth)
        {
            var def = node.Def;
            if (def.IsFinished) return 0f;

            var bench = def.requiredResearchBuilding;
            var facilities = def.requiredResearchFacilities;
            bool hasFacilities = facilities != null && facilities.Count > 0;
            if (bench == null && !hasFacilities) return 0f;

            float startY = y;
            bool met = def.PlayerHasAnyAppropriateResearchBench;
            Color statusColor = RequirementStatusColor(met);

            y += DrawRequirementHeader(padding, y, contentWidth, "YART_Requires".Translate(), met, statusColor);

            if (bench != null)
            {
                y += DrawRequirementRow(new Rect(padding, y, contentWidth, 28f), bench, statusColor);
            }
            if (hasFacilities)
            {
                foreach (var f in facilities)
                {
                    y += DrawRequirementRow(new Rect(padding, y, contentWidth, 28f), f, statusColor);
                }
            }
            y += 6f;
            return y - startY;
        }

        private static Color RequirementStatusColor(bool met) =>
            met ? new Color(0.6f, 1f, 0.6f) : new Color(0.95f, 0.8f, 0.35f);

        /// <summary>요구 섹션 헤더 + 우측 상태 아이콘(체크/잠금). 반환값 = 소비 높이.</summary>
        private static float DrawRequirementHeader(float padding, float y, float contentWidth, string label, bool met, Color statusColor)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.7f, 0.78f, 0.9f)))
            {
                Widgets.Label(new Rect(padding, y, contentWidth - 20f, 22), label);
            }
            GUIDrawingUtilities.DrawIcon(new Rect(padding + contentWidth - 18f, y + 3f, 14f, 14f),
                met ? Assets.IconCheck : Assets.IconLock, statusColor);
            return 24f;
        }

        /// <summary>요구 작업대/시설 한 항목 (아이콘 + 라벨 + InfoCard 클릭). 반환값 = 행 높이+간격.</summary>
        private float DrawRequirementRow(Rect rect, ThingDef thing, Color labelColor)
        {
            bool hovered = Mouse.IsOver(rect);
            if (hovered) Widgets.DrawHighlight(rect);

            const float iconSize = 22f;
            Rect iconRect = new Rect(rect.x + 8f, rect.center.y - iconSize / 2f, iconSize, iconSize);
            Widgets.DefIcon(iconRect, thing, null, 1f, null, drawPlaceholder: true);

            Rect labelRect = new Rect(iconRect.xMax + 8f, rect.y, rect.width - iconSize - 24f, rect.height);
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(labelColor))
            {
                Widgets.Label(labelRect, thing.LabelCap);
            }

            if (hovered)
            {
                TooltipHandler.TipRegion(rect, thing.description.NullOrEmpty() ? (string)thing.LabelCap : thing.description);
            }
            if (Widgets.ButtonInvisible(rect))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                Find.WindowStack.Add(new Dialog_InfoCard(thing));
            }
            return rect.height + 4f;
        }

        /// <summary>DrawRequirementsSection의 높이 측정 (CalculateLeftPanelHeight 전용).</summary>
        private static float MeasureRequirementsHeight(ResearchNode node)
        {
            var def = node.Def;
            if (def.IsFinished) return 0f;
            var bench = def.requiredResearchBuilding;
            var facilities = def.requiredResearchFacilities;
            int rows = (bench != null ? 1 : 0) + (facilities != null ? facilities.Count : 0);
            if (rows == 0) return 0f;
            return 24f + rows * 32f + 6f; // 헤더 + 행(28+4) + 하단 여백
        }

        /// <summary>Medieval Overhaul 도식 요구 — DrawRequirementsSection과 동일 스타일의 1행 섹션.</summary>
        private float DrawSchematicSection(ResearchNode node, float padding, float y, float contentWidth)
        {
            var def = node.Def;
            if (def.IsFinished) return 0f;
            var schematic = MedievalOverhaulCompat.RequiredSchematicFor(def);
            if (schematic == null) return 0f;

            float startY = y;
            bool met = MedievalOverhaulCompat.SchematicRequirementMet(def);
            Color statusColor = RequirementStatusColor(met);

            y += DrawRequirementHeader(padding, y, contentWidth,
                "DankPyon_RequiredSchematic".Translate() + ":", met, statusColor);
            y += DrawRequirementRow(new Rect(padding, y, contentWidth, 28f), schematic, statusColor);
            y += 6f;
            return y - startY;
        }

        /// <summary>DrawSchematicSection의 높이 측정</summary>
        private static float MeasureSchematicHeight(ResearchNode node)
        {
            if (node.Def.IsFinished) return 0f;
            if (MedievalOverhaulCompat.RequiredSchematicFor(node.Def) == null) return 0f;
            return 24f + 32f + 6f; // 헤더 + 행(28+4) + 하단 여백
        }

        private float DrawTechprintSourcesSection(ResearchProjectDef def, float padding, float y, float contentWidth)
        {
            float startY = y;

            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(new Color(0.7f, 0.78f, 0.9f)))
            {
                Widgets.Label(new Rect(padding, y, contentWidth, 22), "ResearchTechprintsFromFactions".Translate());
            }
            y += 24f;

            const float rowH = 26f;
            foreach (var faction in TechprintFactions(def))
            {
                Rect iconRect = new Rect(padding + 6f, y + (rowH - 22f) / 2f, 22f, 22f);
                FactionUIUtility.DrawFactionIconWithTooltip(iconRect, faction);
                using (Temporary.Font(GameFont.Small))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(new Color(0.8f, 0.82f, 0.88f)))
                {
                    Widgets.Label(new Rect(iconRect.xMax + 6f, y, contentWidth - 34f, rowH), faction.Name);
                }
                y += rowH;
            }
            y += 4f;
            return y - startY;
        }

        /// <summary>DrawTechprintSourcesSection의 높이 측정 (CalculateLeftPanelHeight 전용).</summary>
        private static float MeasureTechprintSourcesHeight(ResearchProjectDef def)
        {
            float h = 24f; // "...FromFactions" 헤더
            foreach (var _ in TechprintFactions(def)) h += 26f; // 세력 행
            return h + 4f;
        }

        /// <summary>테크프린트를 보유/판매하는 세력 (바닐라 MainTabWindow_Research.DrawTechprintInfo와 동일 판정).</summary>
        private static IEnumerable<Faction> TechprintFactions(ResearchProjectDef def)
        {
            if (def.heldByFactionCategoryTags == null) yield break;
            foreach (var tag in def.heldByFactionCategoryTags)
            {
                foreach (var faction in Find.FactionManager.AllFactionsInViewOrder)
                {
                    if (faction.def.categoryTag == tag) yield return faction;
                }
            }
        }

        /// <summary>
        /// 좌측 패널의 전체 높이 계산 (스크롤용). DrawPanelBody의 각 구역과 1:1로 대응해야 한다.
        /// </summary>
        private float CalculateLeftPanelHeight(ResearchNode node, float contentWidth)
        {
            float height = 32   // 제목
                         + 28;  // 테크레벨 배지

            using (Temporary.Font(GameFont.Small))
            {
                height += Text.CalcHeight(node.Def.description, contentWidth) + 12;
            }

            height += 28; // 비용

            string costWarn = CostFactorWarningText(node.Def);
            if (costWarn != null)
            {
                using (Temporary.Font(GameFont.Tiny))
                {
                    height += Text.CalcHeight(costWarn, contentWidth) + 6;
                }
            }

            height += MeasureRequirementsHeight(node);
            height += MeasureSchematicHeight(node);

            // 잠금 사유 (DrawLockedReasonsSection과 동일 계산)
            if (node.State == ResearchNodeState.Locked)
            {
                node.GetLockedReasons(lockedReasonsBuffer);
                if (lockedReasonsBuffer.Count > 0)
                {
                    height += 22; // "Locked:" 헤더
                    using (Temporary.Font(GameFont.Small))
                    {
                        foreach (var reason in lockedReasonsBuffer)
                        {
                            height += Text.CalcHeight("  • " + reason, contentWidth) + 2;
                        }
                    }
                    height += 8;
                }
            }

            // 테크프린트 구입처 보충 (DrawTechprintBlock과 동일 조건/구성)
            if (node.Def.TechprintCount > 0 && !node.Def.TechprintRequirementMet)
            {
                height += MeasureTechprintSourcesHeight(node.Def);
                if (Prefs.DevMode) height += 30;
                height += 4;
            }

            height += 45; // 연구 버튼
            height += 12; // 구분선

            // 해금 콘텐츠
            height += 29; // 헤더
            height += node.UnlockedDefs.Count == 0
                ? 26
                : UnlockedDefsUtility.Measure(contentWidth, node.UnlockedDefsGrouped,
                    YARTMod.Settings.unlockedContentExpanded, 32f);
            height += 8;

            // 선행 연구
            height += 12 + 29;
            var prereqs = node.PanelPrerequisites ?? emptyNodeList;
            height += prereqs.Count > 0 ? prereqs.Count * 40 : 26;
            height += 8;

            // 후행 연구
            height += 12 + 29;
            var children = node.PanelChildren ?? emptyNodeList;
            height += children.Count > 0 ? children.Count * 40 : 26;

            height += 60; // 여유 공간
            return height;
        }
    }
}
