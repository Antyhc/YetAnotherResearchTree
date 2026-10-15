using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;

namespace YART
{
    // 캔버스 상호작용 의미론: 노드 클릭(선택/큐/프록시 점프), 호버 포커스 모드
    public partial class MainTabWindow_YART
    {
        /// <summary>
        /// 마우스 좌표 아래의 노드를 찾는다 (더미 제외, 컬링된 노드는 히트 대상 아님). 없으면 null.
        /// </summary>
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

            // 사운드는 각 Enqueue 내부(ResearchStart)에서 처리한다.
            if (Event.current.control) mgr.EnqueueWithChainExclusive(target.Def); // 체인이 닿는 채널을 비우고 이 체인만 남김
            else if (Event.current.alt) mgr.EnqueueWithChainToFront(target.Def);  // 진행 중 연구를 밀어내고 맨 앞에 삽입
            else mgr.EnqueueWithChain(target.Def);                                // 큐 끝에 추가 (미완료 선행 체인 포함)
        }

        private void HandleNodeInteraction(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // HandleInput에서 "드래그 없는 좌클릭"으로 판별된 경우에만 진입 (MouseUp 시점)
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
                GUI.FocusControl(null); // Help/자동완성 패널은 포커스 조건 — 외부 클릭 시 닫히도록 해제
                Event.current.Use();
            }
            else if (history.HasCurrent)
            {
                history.Clear();
                Event.current.Use();
            }
        }

        /// <summary>노드 좌클릭 처리. 프록시는 원본 노드를 대상으로 삼는다.</summary>
        private void HandleNodeClick(ResearchNode node)
        {
            var target = node.IsProxy ? node.OriginalNode : node;

            // 미발견(IsHidden) 연구는 바닐라처럼 상호작용 불가 (정보 마스킹)
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
                SoundDefOf.Click.PlayOneShotOnCamera(); // 노드 선택 (바닐라 연구 목록과 동일)
            }

            // 프록시 클릭 = 원본 노드가 속한 그래프로 전환 + 포커스
            if (node.IsProxy)
            {
                SwitchGraph(target.Key, playSound: false); // 노드 선택 Click으로 통일
                FocusOnNode(target);
            }

            Event.current.Use();
        }

        /// <summary>
        /// 노드 우클릭 컨텍스트 메뉴: Start now / Stop / Queue / Unqueue / 원본 그래프로 / 인포 카드.
        /// 좌측 패널 버튼과 동일한 의미론 (시작·중단은 바닐라 API 경유).
        /// </summary>
        private void HandleNodeContextMenu(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            if (!rightClickPending) return;
            rightClickPending = false;
            if (IsMouseOverBlockingElement()) return;

            Vector2 mousePos = Event.current.mousePosition;
            var node = NodeAtMouse(graph, offset, zoom, mousePos, out Rect nodeRectScreen);
            if (node == null) return;

            var target = node.IsProxy ? node.OriginalNode : node;
            if (target.IsHidden) return; // 미발견 연구는 메뉴 없음 (정보 마스킹)

            // 해금 아이콘 우클릭 = 해당 def InfoCard (대표 아이콘 또는 리치 스트립/"+n").
            // 노드 컨텍스트 메뉴보다 우선 — 아이콘 위가 아니면 아래로 흘러 일반 메뉴.
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
                // Available일 때만 큐잉 (Locked는 불가 — 좌측 패널·Shift+클릭과 동일)
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

        /// <summary>
        /// 리치카드 해금 아이콘 영역 우클릭 처리. 아이콘 위면 그 def의 InfoCard를,
        /// "+n" 오버플로 위면 전체 해금 목록 FloatMenu를 연다. 처리하면 true.
        /// 좌표 기하는 DrawRichCardContent와 공유(GetRichCardIconMetrics).
        /// </summary>
        private bool TryHandleUnlockIconRightClick(ResearchNode realNode, Rect nodeRectScreen, float zoom, Vector2 mousePos)
        {
            var unlocked = realNode.UnlockedDefs;
            if (unlocked.Count == 0) return false;

            // 대표 아이콘 (좌측, NodeTextMinZoom 이상에서 표시 — 렌더와 동일 좌표)
            if (RepIconRect(nodeRectScreen, zoom).Contains(mousePos))
            {
                Find.WindowStack.Add(new Dialog_InfoCard(unlocked[0]));
                return true;
            }

            // 리치 스트립 (리치 카드에서만; 대표 제외 index 1.. 최대 8개)
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

        /// <summary>
        /// 호버 대상에 맞춰 포커스 모드 상태를 갱신한다.
        /// </summary>
        private void UpdateFocusMode(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // 저줌에서는 성능을 위해 포커스 모드를 끈다
            if (zoom < 0.4f)
            {
                hoveredNode = null;
                focusedNodes.Clear();
                return;
            }

            var newHoveredNode = queueBarHoveredDef != null ? GetNodeOnCurrentGraph(queueBarHoveredDef) : null;

            // 패널 위에 마우스가 있으면 캔버스 호버로 치지 않는다
            if (newHoveredNode == null && !IsMouseOverBlockingElement())
            {
                newHoveredNode = NodeAtMouse(graph, offset, zoom, Event.current.mousePosition, out _);
            }

            // 호버가 없으면 선택 노드가 앵커 — 하이라이트가 계속 지속된다.
            if (newHoveredNode == null && history.HasCurrent)
            {
                newHoveredNode = GetNodeOnCurrentGraph(history.Current.Def);
            }

            if (newHoveredNode == hoveredNode) return; // 변한 게 없으면 집합 재구성 생략

            hoveredNode = newHoveredNode;
            focusedNodes.Clear();
            if (hoveredNode == null) return;

            focusedNodes.Add(hoveredNode);
            // 상류: 선행 체인 전부 (완료 여부 무관 — 전체 의존 경로를 보여준다)
            ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, true, filter: null);
            // 하류: 직속 후행만 (더미는 통과하되 첫 실노드에서 멈춤)
            ResearchNode.CollectConnectedNodes(hoveredNode, focusedNodes, false, filter: null,
                recurseCondition: x => x.IsDummy);
        }
    }
}
