using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;

namespace YART
{
    public partial class MainTabWindow_YART
    {
        private ResearchNode NodeAtMouse(ResearchSubGraph graph, Vector2 offset, float zoom,
            Vector2 mousePos, out Rect nodeRectScreen)
        {
            foreach (var node in graph.Nodes)
            {
                if (node.IsDummy) continue;
                if (!visibleRect.Overlaps(node.Rect)) continue;

                Rect screenRect = GetNodeScreenRect(node, offset, zoom);
                if (!screenRect.Contains(mousePos)) continue;

                nodeRectScreen = screenRect;
                return node;
            }

            nodeRectScreen = Rect.zero;
            return null;
        }

        private static void EnqueueByModifier(ResearchNode target)
        {
            var mgr = ResearchQueueManager.Instance;
            if (mgr == null || target.State != ResearchNodeState.Available || SemiRandomResearchCompat.Active)
            {
                SoundDefOf.ClickReject.PlayOneShotOnCamera();
                return;
            }

            // 사운드는 각 Enqueue 내부에서 냄
            if (Event.current.control) mgr.EnqueueWithChainExclusive(target.Def); // 체인이 닿는 채널을 비우고 이 체인만 남김
            else if (Event.current.alt) mgr.EnqueueWithChainToFront(target.Def); // 진행 중 연구도 밀어냄
            else mgr.EnqueueWithChain(target.Def);
        }

        private void HandleNodeInteraction(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // HandleInput이 드래그 없는 좌클릭으로 판별한 경우만 (MouseUp 시점)
            if (!clickPending) return;
            clickPending = false;
            if (IsMouseOverBlockingElement()) return;

            var node = NodeAtMouse(graph, offset, zoom, Event.current.mousePosition, out _);
            if (node != null)
            {
                HandleNodeClick(node);
                return;
            }

            if (searchResultsRect.height > 0f)
            {
                searchDropdownClosed = true;
                searchResultsRect = Rect.zero;
                GUI.FocusControl(null); // Help/자동완성 패널은 포커스가 있어야 열려 있으므로 해제해서 닫음
                Event.current.Use();
            }
            else if (history.HasCurrent)
            {
                history.Clear();
                Event.current.Use();
            }
        }

        private void HandleNodeClick(ResearchNode node)
        {
            var target = node.IsProxy ? node.OriginalNode : node;

            // 미발견 연구는 바닐라처럼 상호작용 불가
            if (target.IsHidden)
            {
                Event.current.Use();
                return;
            }

            if (Event.current.control || Event.current.alt || Event.current.shift)
            {
                EnqueueByModifier(target);
                Event.current.Use();
                return;
            }

            if (!history.HasCurrent || history.Current != target)
            {
                history.Push(target);
                leftPanelScrollPosition = Vector2.zero;
                SoundDefOf.Click.PlayOneShotOnCamera(); // 바닐라 연구 목록과 같은 소리
            }

            if (node.IsProxy)
            {
                SwitchGraph(target.Key, playSound: false); // 소리는 위의 Click 하나로
                FocusOnNode(target);
            }

            Event.current.Use();
        }

        /// <summary>좌측 패널 버튼과 같은 동작</summary>
        private void HandleNodeContextMenu(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            if (!rightClickPending) return;
            rightClickPending = false;
            if (IsMouseOverBlockingElement()) return;

            Vector2 mousePos = Event.current.mousePosition;
            var node = NodeAtMouse(graph, offset, zoom, mousePos, out Rect nodeRectScreen);
            if (node == null) return;

            var target = node.IsProxy ? node.OriginalNode : node;
            if (target.IsHidden) return; // 미발견 연구는 메뉴 없음

            // 해금 아이콘 위면 노드 메뉴보다 InfoCard 우선
            if (zoom >= Constraints.NodeTextMinZoom
                && TryHandleUnlockIconRightClick(target, nodeRectScreen, zoom, mousePos))
            {
                Event.current.Use();
                return;
            }

            Find.WindowStack.Add(new FloatMenu(BuildNodeContextOptions(node, target)));
            Event.current.Use();
        }

        private List<FloatMenuOption> BuildNodeContextOptions(ResearchNode node, ResearchNode target)
        {
            var def = target.Def;
            var queueMgr = ResearchQueueManager.Instance;
            var options = new List<FloatMenuOption>();

            if (target.State == ResearchNodeState.Available && def.CanStartNow && !SemiRandomResearchCompat.Active)
            {
                options.Add(new FloatMenuOption("YART_StartNow".Translate(), () => StartResearch(def)));
            }

            int queuePos = queueMgr?.GetQueuePosition(def) ?? -1;
            if (target.State == ResearchNodeState.InProgress && !SemiRandomResearchCompat.Active)
            {
                options.Add(new FloatMenuOption("YART_Stop".Translate(), () =>
                {
                    if (queueMgr != null) queueMgr.Remove(def);
                    else Find.ResearchManager.StopProject(def);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                }));
            }
            else if (queuePos > 0)
            {
                options.Add(new FloatMenuOption("YART_Unqueue".Translate(queuePos), () =>
                {
                    queueMgr.Remove(def);
                    SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                }));
            }
            else if (queueMgr != null && target.State == ResearchNodeState.Available && !SemiRandomResearchCompat.Active)
            {
                // Locked는 큐잉 불가. 좌측 패널, Shift+클릭과 같음
                int chainCount = ResearchQueueManager.CollectMissingChain(def).Count;
                options.Add(new FloatMenuOption(
                    chainCount > 1 ? "YART_QueueChain".Translate(chainCount) : "YART_Queue".Translate(),
                    () => queueMgr.EnqueueWithChain(def))); // 사운드는 EnqueueWithChain 내부
            }

            if (node.IsProxy)
            {
                options.Add(new FloatMenuOption("YART_GoToOriginal".Translate(), () => JumpToNode(target)));
            }
            options.Add(new FloatMenuOption("YART_ShowInfoCard".Translate(),
                () => Find.WindowStack.Add(new Dialog_InfoCard(def))));

            return options;
        }

        /// <summary>"+n" 위면 나머지 해금 목록 메뉴. 좌표는 GetRichCardIconMetrics로 렌더와 공유</summary>
        private bool TryHandleUnlockIconRightClick(ResearchNode realNode, Rect nodeRectScreen, float zoom, Vector2 mousePos)
        {
            var unlocked = realNode.UnlockedDefs;
            if (unlocked.Count == 0) return false;

            // 대표 아이콘. NodeTextMinZoom 이상에서만 보임
            if (RepIconRect(nodeRectScreen, zoom).Contains(mousePos))
            {
                Find.WindowStack.Add(new Dialog_InfoCard(unlocked[0]));
                return true;
            }

            // 리치 스트립은 대표를 뺀 index 1부터
            if (GetRichCardLerp(zoom) > 0f && unlocked.Count > 1)
            {
                GetRichCardIconMetrics(nodeRectScreen, zoom, hasRep: true,
                    out float iconSize, out float iconGap, out float iconX, out float iconY);
                int afterRep = unlocked.Count - 1;
                int shown = Mathf.Min(RichCardStripMax, afterRep);

                for (int k = 0; k < shown; k++)
                {
                    Rect iconRect = new Rect(iconX + k * (iconSize + iconGap), iconY, iconSize, iconSize);
                    if (iconRect.Contains(mousePos))
                    {
                        Find.WindowStack.Add(new Dialog_InfoCard(unlocked[1 + k]));
                        return true;
                    }
                }

                if (afterRep > shown)
                {
                    Rect moreRect = new Rect(iconX + shown * (iconSize + iconGap), iconY, 30f * zoom, iconSize);
                    if (moreRect.Contains(mousePos))
                    {
                        var opts = new List<FloatMenuOption>();
                        for (int j = 1 + shown; j < unlocked.Count; j++)
                        {
                            var local = unlocked[j];
                            opts.Add(new FloatMenuOption(local.LabelCap, () => Find.WindowStack.Add(new Dialog_InfoCard(local))));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                        return true;
                    }
                }
            }

            return false;
        }

        private void UpdateFocusMode(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // 저줌에선 성능 때문에 끔
            if (zoom < 0.4f)
            {
                hoveredNode = null;
                focusedNodes.Clear();
                return;
            }

            var newHoveredNode = queueBarHoveredDef != null ? GetNodeOnCurrentGraph(queueBarHoveredDef) : null;

            if (newHoveredNode == null && !IsMouseOverBlockingElement())
            {
                newHoveredNode = NodeAtMouse(graph, offset, zoom, Event.current.mousePosition, out _);
            }

            // 호버가 없으면 선택 노드 기준으로 하이라이트 유지
            if (newHoveredNode == null && history.HasCurrent)
            {
                newHoveredNode = GetNodeOnCurrentGraph(history.Current.Def);
            }

            if (newHoveredNode == hoveredNode) return;

            hoveredNode = newHoveredNode;
            focusedNodes.Clear();
            if (hoveredNode == null) return;

            focusedNodes.Add(hoveredNode);
            // 위로는 완료 여부와 무관하게 선행 체인 전부
            ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, true, filter: null);
            // 아래로는 직속 후행만. 더미는 통과
            ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, false, filter: null,
                recurseCondition: x => x.IsDummy);
        }
    }
}
