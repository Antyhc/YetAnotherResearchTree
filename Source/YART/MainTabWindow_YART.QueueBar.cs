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
    public partial class MainTabWindow_YART
    {
        private void DoQueueBar(Rect rect, float leftInset)
        {
            var queueMgr = ResearchQueueManager.Instance;
            if (queueMgr == null) return;
            var queue = queueMgr.GetQueue(SelectedChannel);

            Widgets.DrawBoxSolid(rect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(rect, Constraints.PanelBorder, 1f);
            // 상단 바라 채널 색 액센트는 아래 변에
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f),
                GenColor.WithAlpha(SelectedChannel.Color, 0.6f));

            queueBarHoveredDef = null;

            // 큐가 비어도 보이도록 먼저 그림
            float storedChipReserved = DrawStoredResearchChip(rect);

            if (queue.Count == 0 && draggedQueueDef == null)
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                using (Temporary.Color(new Color(0.45f, 0.5f, 0.6f)))
                {
                    Widgets.Label(new Rect(rect.x + leftInset + 12f, rect.y,
                            rect.width - leftInset - 24f - storedChipReserved, rect.height),
                        "YART_QueueEmpty".Translate());
                }
                return;
            }

            // 우측 고정 요소(DEV 패널, 비우기 버튼)만큼 스크롤 영역을 좁힘
            Rect scrollRect = rect;
            scrollRect.xMin += leftInset;
            scrollRect.width -= storedChipReserved;

            const float devWidth = 250f;
            bool hasDevPanel = Prefs.DevMode && queue.Count > 0;
            if (hasDevPanel)
            {
                scrollRect.width -= devWidth;
                DrawQueueDevControls(new Rect(rect.xMax - storedChipReserved - devWidth, rect.y, devWidth, rect.height),
                    queue[0]);
            }

            if (!SemiRandomResearchCompat.Active)
            {
                const float clearW = 60f;
                float clearRight = rect.xMax - storedChipReserved - (hasDevPanel ? devWidth : 0f);
                Rect clearRect = new Rect(clearRight - clearW - 8f, rect.y + 10f, clearW, rect.height - 24f);
                scrollRect.width -= (clearW + 16f);
                DrawQueueClearButton(clearRect, queueMgr, queue.Count);
            }

            float cardWidth = Constraints.QueueCardWidth;
            float gap = Constraints.QueueCardGap;
            Rect viewRect = new Rect(0f, 0f, queue.Count * (cardWidth + gap) + gap, rect.height - 18f);

            // 넘치면 휠로 가로 스크롤
            float maxScrollX = Mathf.Max(0f, viewRect.width - scrollRect.width);
            if (maxScrollX > 0f && Event.current.type == EventType.ScrollWheel
                && scrollRect.Contains(Event.current.mousePosition))
            {
                queueBarScrollPosition.x = Mathf.Clamp(
                    queueBarScrollPosition.x + Event.current.delta.y * 40f, 0f, maxScrollX);
                Event.current.Use();
            }

            Widgets.BeginScrollView(scrollRect, ref queueBarScrollPosition, viewRect);

            float x = gap;
            for (int i = 0; i < queue.Count; i++)
            {
                var def = queue[i];
                Rect cardRect = new Rect(x, 8f, cardWidth, rect.height - 26f);
                if (def != draggedQueueDef)
                {
                    DrawQueueCard(cardRect, def, i);
                }
                x += cardWidth + gap;
            }

            if (draggedQueueDef != null)
            {
                DrawDraggedQueueCard(rect, queueMgr, queue, cardWidth, gap);
            }
            else if (Event.current.rawType == EventType.MouseUp && Event.current.button == 0)
            {
                queueCardMouseDownDef = null;
            }

            Widgets.EndScrollView();
        }

        private void DrawQueueClearButton(Rect clearRect, ResearchQueueManager queueMgr, int queueCount)
        {
            if (DrawPanelButton(clearRect, "YART_Clear".Translate(), new Color(0.7f, 0.35f, 0.3f), enabled: true))
            {
                var channel = SelectedChannel;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "YART_ClearConfirm".Translate(queueCount),
                    () =>
                    {
                        queueMgr.ClearQueue(channel);
                        SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                    }));
            }
            TooltipHandler.TipRegion(clearRect, "YART_ClearTooltip".Translate());
        }

        /// <summary>놓는 순간 드롭 위치로 재정렬</summary>
        private void DrawDraggedQueueCard(Rect barRect, ResearchQueueManager queueMgr,
            IReadOnlyList<ResearchProjectDef> queue, float cardWidth, float gap)
        {
            int dragIndex = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i] == draggedQueueDef) { dragIndex = i; break; }
            }

            Rect dragRect = new Rect(Event.current.mousePosition.x - cardWidth / 2f, 8f, cardWidth, barRect.height - 26f);
            DrawQueueCard(dragRect, draggedQueueDef, dragIndex, dragging: true);

            if (Event.current.rawType != EventType.MouseUp || Event.current.button != 0) return;

            int target = Mathf.Clamp(
                Mathf.RoundToInt((Event.current.mousePosition.x - gap) / (cardWidth + gap)), 0, queue.Count);
            queueMgr.Reorder(SelectedChannel, draggedQueueDef, target);
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            draggedQueueDef = null;
            queueCardMouseDownDef = null;
        }

        /// <summary>쓴 가로폭(여백 포함)을 반환, 비활성이면 0</summary>
        private float DrawStoredResearchChip(Rect barRect)
        {
            if (!StoredResearchCompat.Active) return 0f;

            string label = StoredResearchCompat.TotalStored.ToString("F0");
            bool hasIcon = StoredResearchCompat.Icon != null;
            float chipW;
            using (Temporary.Font(GameFont.Small))
                chipW = 8f + (hasIcon ? 22f : 0f) + Text.CalcSize(label).x + 10f;

            Rect chipRect = new Rect(barRect.xMax - chipW - 8f, barRect.y + 10f, chipW, barRect.height - 24f);

            Widgets.DrawBoxSolid(chipRect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(chipRect, Constraints.PanelBorder, 1f);
            if (Mouse.IsOver(chipRect)) Widgets.DrawHighlight(chipRect);

            float x = chipRect.x + 8f;
            if (hasIcon)
            {
                const float isz = 18f;
                GUI.DrawTexture(new Rect(x, chipRect.center.y - isz / 2f, isz, isz), StoredResearchCompat.Icon);
                x += 22f;
            }
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(new Color(0.82f, 0.87f, 0.97f)))
            {
                Widgets.Label(new Rect(x, chipRect.y, chipRect.xMax - x, chipRect.height), label);
            }

            TooltipHandler.TipRegion(chipRect, new TipSignal(StoredResearchCompat.BuildTooltip, 84921733));
            if (Widgets.ButtonInvisible(chipRect))
            {
                StoredResearchCompat.OpenInfoDialog();
                SoundDefOf.Click.PlayOneShotOnCamera();
            }

            return chipW + 16f;
        }

        private static readonly (string label, float fraction)[] DevProgressSteps =
        {
            ("+25%", 0.25f), ("+50%", 0.5f), ("100%", 1f),
        };

        private void DrawQueueDevControls(Rect rect, ResearchProjectDef head)
        {
            Rect panel = rect.ContractedBy(0f, 8f);
            panel.xMax -= 8f;
            Widgets.DrawBoxSolid(panel, new Color(0.30f, 0.12f, 0.10f, 0.55f));
            GUIDrawingUtilities.DrawBorderLines(panel, new Color(0.85f, 0.45f, 0.30f, 0.7f), 1f);

            Rect inner = panel.ContractedBy(6f);

            Rect labelRect = new Rect(inner.x, inner.y, 34f, inner.height);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            using (Temporary.Color(new Color(0.95f, 0.6f, 0.45f)))
            {
                Widgets.Label(labelRect, "DEV");
            }

            const float btnGap = 4f;
            float btnWidth = (inner.width - labelRect.width - btnGap * 3) / 3f;
            float btnHeight = Mathf.Min(30f, inner.height);
            float btnY = inner.y + (inner.height - btnHeight) / 2f;
            float x = labelRect.xMax + btnGap;
            Color devBtnColor = new Color(0.85f, 0.5f, 0.3f);

            foreach (var step in DevProgressSteps)
            {
                if (DrawPanelButton(new Rect(x, btnY, btnWidth, btnHeight), step.label, devBtnColor, enabled: true))
                {
                    DevAddProgress(head, step.fraction);
                }
                x += btnWidth + btnGap;
            }

            TooltipHandler.TipRegion(panel, $"Dev: add progress to queue head\n{head.LabelCap}");
        }

        private static void DevAddProgress(ResearchProjectDef def, float fraction)
        {
            if (def.baseCost > 0f)
            {
                Find.ResearchManager.AddProgress(def, def.baseCost * fraction);
            }
            else if (ModsConfig.AnomalyActive && def.knowledgeCost > 0f)
            {
                Find.ResearchManager.ApplyKnowledge(def, def.knowledgeCost * fraction, out _);
            }
            ResearchNode.InvalidateAllStates();
        }

        private static void DrawMarqueeLabel(Rect rect, string text, Color color)
        {
            using (Temporary.Font(GameFont.Small))
            using (Temporary.Color(color))
            {
                Vector2 size = Text.CalcSize(text);
                if (size.x <= rect.width)
                {
                    using (Temporary.Anchor(TextAnchor.MiddleLeft))
                    {
                        Widgets.Label(rect, text);
                    }
                    return;
                }

                // 넘치면 그룹으로 클립하고 좌로 흘림. 끊김 없이 이어지게 두 벌 그림
                GUI.BeginGroup(rect);
                const float gap = 36f;
                const float speed = 35f; // px/s
                float period = size.x + gap;
                float offset = Mathf.Repeat(Time.realtimeSinceStartup * speed, period);
                using (Temporary.Anchor(TextAnchor.MiddleLeft))
                {
                    Widgets.Label(new Rect(-offset, 0f, size.x + 10f, rect.height), text);
                    Widgets.Label(new Rect(-offset + period, 0f, size.x + 10f, rect.height), text);
                }
                GUI.EndGroup();
            }
        }

        private void DrawQueueCard(Rect rect, ResearchProjectDef def, int index, bool dragging = false)
        {
            var queueMgr = ResearchQueueManager.Instance;
            bool isCurrent = Find.ResearchManager.IsCurrentProject(def);
            bool isBlockedHead = index == 0 && !isCurrent && !def.CanStartNow;

            Color accent = ResearchGraph.Instance.AllNodes.TryGetValue(def, out var node)
                ? node.EraAccentColor
                : Constraints.EdgeDefault;

            Color bg = isCurrent
                ? GenColor.WithAlpha(accent, 0.18f)
                : Color.Lerp(new Color(0.07f, 0.07f, 0.1f, 0.9f), accent, 0.13f);
            Widgets.DrawBoxSolid(rect, bg);
            GUIDrawingUtilities.DrawBorderLines(rect, isCurrent ? accent : GenColor.WithAlpha(accent, 0.55f), isCurrent ? 1.5f : 1f);

            if (!dragging && Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
                queueBarHoveredDef = def;
            }

            Rect indexRect = new Rect(rect.x + 4f, rect.y, 18f, rect.height);
            if (isCurrent)
            {
                Rect playRect = new Rect(indexRect.center.x - 7f, indexRect.center.y - 7f, 14f, 14f);
                GUIDrawingUtilities.DrawIcon(playRect, Assets.IconPlay, accent);
            }
            else
            {
                using (Temporary.Font(GameFont.Small))
                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                using (Temporary.Color(new Color(0.6f, 0.65f, 0.75f)))
                {
                    Widgets.Label(indexRect, (index + 1).ToString());
                }
            }

            if (isBlockedHead)
            {
                GUIDrawingUtilities.DrawIcon(new Rect(rect.x + 4f, rect.y + 2f, 12f, 12f), Assets.IconLock, new Color(0.95f, 0.8f, 0.35f));
                TooltipHandler.TipRegion(rect, "YART_CannotStartYet".Translate(def.LabelCap));
            }

            Rect labelRect = new Rect(rect.x + 24f, rect.y + 4f, rect.width - 44f, 24f);
            DrawMarqueeLabel(labelRect, def.LabelCap, Color.white);

            if (node != null) DrawQueueCardUnlockIcons(rect, node.UnlockedDefs, dragging);

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleRight))
            using (Temporary.Color(new Color(0.72f, 0.79f, 0.88f)))
            {
                Widgets.Label(new Rect(rect.x + 24f, rect.yMax - 25f, rect.width - 30f, 19f),
                    $"{def.ProgressApparentString} / {def.CostApparent:N0}");
            }

            float progress = def.ProgressPercent;
            Rect barTrack = new Rect(rect.x + 2f, rect.yMax - 5f, rect.width - 4f, 3f);
            Widgets.DrawBoxSolid(barTrack, new Color(0f, 0f, 0f, 0.5f));
            if (progress > 0f)
            {
                Widgets.DrawBoxSolid(new Rect(barTrack.x, barTrack.y, barTrack.width * Mathf.Clamp01(progress), barTrack.height), accent);
            }

            if (dragging) return;

            // SSR이면 closeRect가 zero라 아래 클릭 판별에서도 빠짐
            const float cs = 22f;
            Rect closeRect = SemiRandomResearchCompat.Active
                ? Rect.zero
                : new Rect(rect.xMax - cs - 3f, rect.y + 3f, cs, cs);
            if (!SemiRandomResearchCompat.Active && DrawQueueCardCloseButton(closeRect))
            {
                queueMgr?.Remove(def);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                return;
            }

            HandleQueueCardMouse(rect, closeRect, def);
        }

        private static bool DrawQueueCardCloseButton(Rect closeRect)
        {
            Widgets.DrawBoxSolid(closeRect, Mouse.IsOver(closeRect)
                ? new Color(0.92f, 0.24f, 0.24f, 0.97f)
                : new Color(0.62f, 0.16f, 0.16f, 0.9f));
            GUIDrawingUtilities.DrawBorderLines(closeRect, new Color(0.97f, 0.5f, 0.5f, 0.95f), 1f);
            GUIDrawingUtilities.DrawIcon(closeRect.ContractedBy(5f), TexButton.CloseXSmall, Color.white);
            return Widgets.ButtonInvisible(closeRect);
        }

        /// <summary>임계값을 넘겨 움직이면 드래그(재정렬), 아니면 클릭(노드 점프)</summary>
        private void HandleQueueCardMouse(Rect rect, Rect closeRect, ResearchProjectDef def)
        {
            var e = Event.current;
            if (e.button != 0) return;

            if (e.type == EventType.MouseDown
                && rect.Contains(e.mousePosition) && !closeRect.Contains(e.mousePosition))
            {
                queueCardMouseDownDef = def;
                queueCardMouseDownPos = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && queueCardMouseDownDef == def)
            {
                if ((e.mousePosition - queueCardMouseDownPos).sqrMagnitude >
                    Constraints.ClickDragThreshold * Constraints.ClickDragThreshold)
                {
                    draggedQueueDef = def;
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && queueCardMouseDownDef == def)
            {
                queueCardMouseDownDef = null;
                var jumpNode = GetNodeOnCurrentGraph(def);
                if (jumpNode == null) ResearchGraph.Instance.AllNodes.TryGetValue(def, out jumpNode);
                if (jumpNode != null) JumpToNode(jumpNode);
                e.Use();
            }
        }

        /// <summary>폭이 모자라면 마지막 칸을 "+n"으로 대체</summary>
        private static void DrawQueueCardUnlockIcons(Rect rect, List<Def> unlocked, bool dragging)
        {
            if (unlocked.Count == 0) return;

            const float isz = 18f, ig = 3f;
            float ix = rect.x + 24f, iy = rect.y + 34f;
            int maxFit = Mathf.Max(0, Mathf.FloorToInt((rect.xMax - 6f - ix + ig) / (isz + ig)));
            bool hasMore = unlocked.Count > maxFit;
            int shownIcons = hasMore ? Mathf.Max(0, maxFit - 1) : unlocked.Count;

            for (int u = 0; u < shownIcons; u++)
            {
                Rect iconRect = new Rect(ix + u * (isz + ig), iy, isz, isz);
                Widgets.DefIcon(iconRect, unlocked[u], null, 1f, null, drawPlaceholder: true);
                if (!dragging && Mouse.IsOver(iconRect)) TooltipHandler.TipRegion(iconRect, UnlockTipFor(unlocked[u]));
            }

            if (!hasMore) return;

            Rect moreRect = new Rect(ix + shownIcons * (isz + ig), iy, 24f, isz);
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(new Color(0.55f, 0.62f, 0.72f)))
            {
                Widgets.Label(moreRect, $"+{unlocked.Count - shownIcons}");
            }
            if (!dragging && Mouse.IsOver(moreRect)) TooltipHandler.TipRegion(moreRect, OverflowUnlockTip(unlocked, shownIcons));
        }
    }
}
