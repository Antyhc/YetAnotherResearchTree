using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Compat;

namespace YART.Data
{
    public class ResearchEdge
    {
        /// <summary>시작 노드 (선행 연구)</summary>
        public ResearchNode From { get; }

        /// <summary>끝 노드 (후속 연구)</summary>
        public ResearchNode To { get; }

        public ResearchEdge(ResearchNode from, ResearchNode to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// SugiyamaLayout.Calculate 가 수집하는 레이아웃 품질 지표.
    /// </summary>
    public class LayoutMetrics
    {
        /// <summary>ordering 종료 시점의 인접 레이어 원시 교차 수</summary>
        public int Crossings;

        /// <summary>NormalizeEdges 이전 실제-실제 의존 엣지의 rank span 합</summary>
        public int TotalEdgeSpan;

        /// <summary>더미 체인 Y 프로파일의 방향 전환 총 수</summary>
        public int BendCount;

        /// <summary>더미 체인 전체의 |delta Y| 절대합 (단위: px)</summary>
        public float VerticalVariation;

        /// <summary>ShapeChains 비수렴 체인 수</summary>
        public int NonConvergedChains;

        /// <summary>막힘으로 정당화되지 않는 더미 체인 방향 전환 수</summary>
        public int UnjustifiedBends;

        /// <summary>가장 높은 컬럼의 픽셀 범위 (max − min, 단위: px)</summary>
        public float MaxColumnHeight;

        /// <summary>예산 초과 컬럼의 제곱합 비용</summary>
        public float OverflowCost;

        /// <summary>비고립 노드를 포함하는 서로 다른 랭크 수 (= 레이아웃 가로 폭)</summary>
        public int UsedRankCount;

        /// <summary>
        /// Phase 4 전역 rank balancing의 목적함수.
        /// RankCost = OverflowCost + RankCostSpanWeight × TotalEdgeSpan + RankCostWidthWeight × UsedRankCount
        /// </summary>
        public float RankCost;
    }

    /// <summary>
    /// 특정 구역(Region)의 연구 서브 그래프를 관리합니다.
    /// </summary>
    public class ResearchSubGraph
    {
        /// <summary>이 서브 그래프의 식별 키 (트랙 + 탭)</summary>
        public GraphKey Key { get; }

        /// <summary>이 서브 그래프가 속한 병렬 연구 채널</summary>
        public ResearchChannel Channel => Key.Channel;

        /// <summary>이 서브 그래프의 바닐라 연구 탭 (벤치 채널 전용, 그 외 null)</summary>
        public ResearchTabDef Tab => Key.Tab;

        /// <summary>이 구역에 속한 모든 노드 (프록시 포함)</summary>
        public List<ResearchNode> Nodes { get; } = new List<ResearchNode>();

        /// <summary>이 구역 내의 모든 엣지</summary>
        public List<ResearchEdge> Edges { get; } = new List<ResearchEdge>();

        /// <summary>그래프의 전체 바운딩 박스 (렌더링 영역)</summary>
        public Rect BoundingBox { get; private set; }

        /// <summary>각 TechLevel이 시작되는 X 좌표 (구분선 그리기용)</summary>
        public Dictionary<TechLevel, float> TechLevelBoundaries { get; set; } = new Dictionary<TechLevel, float>();

        public LayoutMetrics Metrics { get; internal set; }

        public ResearchSubGraph(GraphKey key)
        {
            Key = key;
        }

        public void UpdateBoundingBox()
        {
            if (Nodes.Count == 0)
            {
                BoundingBox = Rect.zero;
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var node in Nodes)
            {
                var rect = node.Rect;
                minX = Mathf.Min(minX, rect.xMin);
                minY = Mathf.Min(minY, rect.yMin);
                maxX = Mathf.Max(maxX, rect.xMax);
                maxY = Mathf.Max(maxY, rect.yMax);
            }

            BoundingBox = new Rect(minX - Constraints.BoundingBoxPadding.x,
                minY - Constraints.BoundingBoxPadding.y,
                (maxX - minX) + 2 * Constraints.BoundingBoxPadding.x,
                (maxY - minY) + 2 * Constraints.BoundingBoxPadding.y);
        }

        public void AddNode(ResearchNode node)
        {
            if (!Nodes.Contains(node))
            {
                Nodes.Add(node);
            }
        }

        public void AddEdge(ResearchNode from, ResearchNode to)
        {
            var edge = new ResearchEdge(from, to);
            Edges.Add(edge);
        }
    }

    /// <summary>
    /// 전체 연구 그래프를 관리하는 메인 컨테이너입니다.
    /// </summary>
    public class ResearchGraph
    {
        private static ResearchGraph _instance;
        public static ResearchGraph Instance => _instance ?? (_instance = new ResearchGraph());

        public Dictionary<GraphKey, ResearchSubGraph> SubGraphs { get; private set; } = new Dictionary<GraphKey, ResearchSubGraph>();
        public Dictionary<ResearchProjectDef, ResearchNode> AllNodes { get; private set; } = new Dictionary<ResearchProjectDef, ResearchNode>();

        private readonly Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>> _proxyCache = new Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>>();
        private volatile bool _initialized = false;

        // 프리셋(통합 벤치 + 사용자 탭 그룹) 병합 그래프 — 프리셋 Id별 def→복사본 맵
        private readonly Dictionary<string, Dictionary<ResearchProjectDef, ResearchNode>> _presetNodeByDef
            = new Dictionary<string, Dictionary<ResearchProjectDef, ResearchNode>>();

        public bool Initialized => _initialized;

        public bool? BuiltWithHiddenPrereqs { get; private set; }

        private ResearchSubGraph GetOrAddSubGraph(GraphKey key)
        {
            if (!SubGraphs.TryGetValue(key, out var sub))
            {
                sub = new ResearchSubGraph(key);
                SubGraphs[key] = sub;
                _proxyCache[key] = new Dictionary<ResearchProjectDef, ResearchNode>();
            }
            return sub;
        }

        public void Build()
        {
            try
            {
                if (Prefs.DevMode) Log.Message("[YART] Building Research Graph...");
                Clear();

                CreateNodes();
                BuildConnections();
                RemoveCycles();
                RemoveRedundantPrerequisites();
                BuildPanelLists();

                _initialized = true;
                if (Prefs.DevMode) Log.Message($"[YART] Graph built. Total nodes: {AllNodes.Count}");
            }
            catch (Exception ex)
            {
                Log.Error($"[YART] Failed to build research graph: {ex}");
                _initialized = false;
            }
        }

        private void RemoveCycles()
        {
            foreach (var subGraph in SubGraphs.Values)
            {
                RemoveCyclesForSubGraph(subGraph);
            }
        }

        /// <summary>서브그래프 전 노드에서 DFS (비연결 컴포넌트 포함) — 백엣지를 끊는다.</summary>
        private static void RemoveCyclesForSubGraph(ResearchSubGraph subGraph)
        {
            var visited = new HashSet<ResearchNode>();
            var recursionStack = new HashSet<ResearchNode>();
            foreach (var node in subGraph.Nodes.ToList())
            {
                if (!visited.Contains(node))
                {
                    DetectAndRemoveCycle(node, visited, recursionStack, subGraph);
                }
            }
        }

        private static void DetectAndRemoveCycle(ResearchNode node, HashSet<ResearchNode> visited, HashSet<ResearchNode> recursionStack, ResearchSubGraph subGraph)
        {
            visited.Add(node);
            recursionStack.Add(node);

            foreach (var child in node.Children.ToList())
            {
                if (!visited.Contains(child))
                {
                    DetectAndRemoveCycle(child, visited, recursionStack, subGraph);
                    continue;
                }
                if (!recursionStack.Contains(child)) continue;

                Log.Warning($"[YART] Cycle detected: {node.Label} -> {child.Label}. Breaking connection.");
                node.Children.Remove(child);
                child.Prerequisites.Remove(node);
                RemoveEdge(subGraph, node, child);
            }

            recursionStack.Remove(node);
        }

        private static void RemoveEdge(ResearchSubGraph subGraph, ResearchNode from, ResearchNode to)
        {
            var edge = subGraph.Edges.FirstOrDefault(e => e.From == from && e.To == to);
            if (edge != null) subGraph.Edges.Remove(edge);
        }

        private void Clear()
        {
            AllNodes.Clear();
            SubGraphs.Clear();
            _proxyCache.Clear();
            _initialized = false;
            _presetNodeByDef.Clear();
        }

        private void CreateNodes()
        {
            var allDefs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            foreach (var def in allDefs)
            {
                // World Tech Level: 월드 테크레벨 초과 연구를 그래프에서 제외
                // (WTL이 바닐라 연구 목록에서 RemoveAll 하는 것과 동일 — 미설치 시 항상 true)
                if (!WorldTechLevelCompat.ShouldShow(def)) continue;

                var node = ResearchNode.Create(def);
                AllNodes[def] = node;

                GetOrAddSubGraph(node.Key).AddNode(node);
            }
            // 이번 빌드에 사용한 필터 시그니처를 기록 (필터 레벨이 바뀌면 PreOpen에서 리빌드)
            WorldTechLevelCompat.MarkBuilt();
        }

        private void BuildConnections()
        {
            bool includeHidden = YARTMod.Settings.includeHiddenPrerequisites;
            BuiltWithHiddenPrereqs = includeHidden;
            foreach (var node in AllNodes.Values)
            {
                var def = node.Def;

                ConnectPrereqList(node, def.prerequisites);
                if (includeHidden) ConnectPrereqList(node, def.hiddenPrerequisites);
            }
        }

        private void ConnectPrereqList(ResearchNode node, List<ResearchProjectDef> prereqDefs)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                if (AllNodes.TryGetValue(prereqDef, out var prereqNode))
                {
                    ConnectNodes(prereqNode, node);
                }
            }
        }

        private void ConnectNodes(ResearchNode from, ResearchNode to)
        {
            // 서브그래프(트랙+탭)가 다르면 대상 그래프에 프록시를 세워 연결 — 교차 트랙/교차 탭 동일 처리
            var source = from.Key.Equals(to.Key) ? from : GetOrCreateProxy(from, to.Key);
            ConnectWithinSubGraph(source, to, SubGraphs[to.Key]);
        }

        private static void ConnectWithinSubGraph(ResearchNode from, ResearchNode to, ResearchSubGraph subGraph)
        {
            if (to.Prerequisites.Contains(from)) return;
            from.Children.Add(to);
            to.Prerequisites.Add(from);
            subGraph.AddEdge(from, to);
        }

        /// <summary>
        /// A -> B, B -> C, A -> C 인 경우 A -> C 연결을 제거
        /// </summary>
        private void RemoveRedundantPrerequisites()
        {
            foreach (var subGraph in SubGraphs.Values)
            {
                RemoveRedundantPrerequisitesForSubGraph(subGraph);
            }
        }

        private void BuildPanelLists()
        {
            ResetPanelLists(AllNodes.Values);

            foreach (var node in AllNodes.Values)
            {
                AddPanelPrereqs(node, node.Def.prerequisites);
                AddPanelPrereqs(node, node.Def.hiddenPrerequisites);
            }

            foreach (var node in AllNodes.Values)
            {
                SortPanelLists(node);
                node.ExternalChildren = node.PanelChildren
                    .Where(c => !c.Key.Equals(node.Key) && HasSurvivingExternalEdge(node, c))
                    .ToList();
            }
        }

        private static void ResetPanelLists(IEnumerable<ResearchNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.PanelPrerequisites = new List<ResearchNode>();
                node.PanelChildren = new List<ResearchNode>();
            }
        }

        private static void SortPanelLists(ResearchNode node)
        {
            node.PanelPrerequisites = node.PanelPrerequisites.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();
            node.PanelChildren = node.PanelChildren.OrderBy(n => n.TechLevel).ThenBy(n => n.Label).ToList();
        }

        /// <summary>
        /// node → child(다른 그래프) 의존이 배지 목록에 표시되어야 하는지.
        /// </summary>
        private static bool HasSurvivingExternalEdge(ResearchNode node, ResearchNode child)
        {
            for (int i = 0; i < child.Prerequisites.Count; i++)
            {
                var p = child.Prerequisites[i];
                if (p.IsProxy && p.OriginalNode == node) return true;
            }
            return child.Def.prerequisites == null || !child.Def.prerequisites.Contains(node.Def);
        }

        private void AddPanelPrereqs(ResearchNode node, List<ResearchProjectDef> prereqDefs)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                if (!AllNodes.TryGetValue(prereqDef, out var prereqNode)) continue;
                LinkPanelPair(node, prereqNode, linkChild: true);
            }
        }

        /// <summary>중복 없이 양방향 패널 링크. linkChild=false면 선행 쪽만 (외부 노드는 후행 목록을 오염시키면 안 됨).</summary>
        private static void LinkPanelPair(ResearchNode node, ResearchNode prereqNode, bool linkChild)
        {
            if (!node.PanelPrerequisites.Contains(prereqNode)) node.PanelPrerequisites.Add(prereqNode);
            if (linkChild && !prereqNode.PanelChildren.Contains(node)) prereqNode.PanelChildren.Add(node);
        }

        private HashSet<ResearchNode> GetAllAncestors(ResearchNode node, HashSet<ResearchNode> visited = null)
        {
            if (visited == null) visited = new HashSet<ResearchNode>();

            foreach (var parent in node.Prerequisites)
            {
                if (visited.Add(parent))
                {
                    GetAllAncestors(parent, visited);
                }
            }
            return visited;
        }

        private ResearchNode GetOrCreateProxy(ResearchNode original, GraphKey targetKey)
        {
            GetOrAddSubGraph(targetKey); // 대상 서브그래프 + 프록시 캐시 존재 보장

            var cache = _proxyCache[targetKey];
            if (cache.TryGetValue(original.Def, out var existingProxy))
            {
                return existingProxy;
            }

            var proxy = ResearchNode.CreateProxy(original, targetKey);
            cache[original.Def] = proxy;
            SubGraphs[targetKey].AddNode(proxy);
            return proxy;
        }

        public ResearchNode GetNodeForGraph(ResearchProjectDef def, GraphKey key)
        {
            // 프리셋 병합 그래프(통합 벤치 포함): 프리셋 복사본 → 프리셋 프록시 캐시 순으로 조회
            if (key.IsPreset)
            {
                if (_presetNodeByDef.TryGetValue(key.PresetId, out var presetMap) && presetMap.TryGetValue(def, out var presetCopy))
                    return presetCopy;
                if (_proxyCache.TryGetValue(key, out var presetCache) && presetCache.TryGetValue(def, out var presetProxy))
                    return presetProxy;
                return null;
            }

            if (AllNodes.TryGetValue(def, out var realNode))
            {
                if (realNode.Key.Equals(key)) return realNode;

                if (_proxyCache.TryGetValue(key, out var cache) && cache.TryGetValue(def, out var proxy))
                {
                    return proxy;
                }
            }
            return null;
        }

        public ResearchSubGraph GetOrBuildUnifiedBench(bool runLayout = true)
        {
            return GetSubGraph(GraphKey.UnifiedBench) ?? BuildPreset(ResearchPreset.AllTabs, runLayout);
        }

        public ResearchSubGraph BuildPreset(ResearchPreset preset, bool runLayout = true)
        {
            if (!_initialized || preset == null) return null;

            // 소스 서브그래프 필터: 내장 통합=모든 벤치 탭, 사용자=선택 탭만. (다른 병합 그래프 소스는 제외)
            Func<GraphKey, bool> includeSource;
            if (preset.IncludeAllBenchTabs)
            {
                includeSource = src => src.Channel != null && src.Channel.IsBench && !src.IsPreset;
            }
            else
            {
                var tabs = preset.ResolveTabs();
                if (tabs.Count == 0) { RemovePresetSubGraph(preset.Id); return null; }
                var tabSet = new HashSet<ResearchTabDef>(tabs);
                includeSource = src => src.Channel != null && src.Channel.IsBench && !src.IsPreset
                    && tabSet.Contains(src.Tab);
            }

            var key = GraphKey.ForPreset(preset.Id);
            RemovePresetSubGraph(preset.Id); // 재빌드 시 이전 상태 제거

            try
            {
                var subGraph = BuildMergedSubGraphCore(key, includeSource, runLayout, out var presetNodeByDef);
                _presetNodeByDef[preset.Id] = presetNodeByDef;

                if (Prefs.DevMode) Log.Message($"[YART] Preset '{preset.Name}' subgraph built: {subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges");
                return subGraph;
            }
            catch (Exception ex)
            {
                Log.Error($"[YART] BuildPreset '{preset.Name}' failed, rolling back: {ex}");
                RemovePresetSubGraph(preset.Id);
                return null;
            }
        }

        public void RemovePresetSubGraph(string presetId)
        {
            if (presetId == null) return;
            var key = GraphKey.ForPreset(presetId);
            SubGraphs.Remove(key);
            _proxyCache.Remove(key);
            _presetNodeByDef.Remove(presetId);
        }

        private ResearchSubGraph BuildMergedSubGraphCore(
            GraphKey key,
            Func<GraphKey, bool> includeSource,
            bool runLayout,
            out Dictionary<ResearchProjectDef, ResearchNode> nodeByDef)
        {
            var subGraph = GetOrAddSubGraph(key);
            nodeByDef = new Dictionary<ResearchProjectDef, ResearchNode>();

            foreach (var kvp in SubGraphs)
            {
                if (kvp.Key.Equals(key)) continue;
                if (!includeSource(kvp.Key)) continue;

                foreach (var node in kvp.Value.Nodes)
                {
                    if (node.IsDummy || node.IsProxy) continue;

                    var copy = ResearchNode.CreateMergedCopy(node, key);
                    subGraph.AddNode(copy);
                    nodeByDef[node.Def] = copy;
                }
            }

            bool includeHidden = YARTMod.Settings.includeHiddenPrerequisites;
            foreach (var copy in nodeByDef.Values)
            {
                ConnectMergedPrereqList(copy, copy.Def.prerequisites, nodeByDef, key, subGraph);
                if (includeHidden)
                    ConnectMergedPrereqList(copy, copy.Def.hiddenPrerequisites, nodeByDef, key, subGraph);
            }

            RemoveCyclesForSubGraph(subGraph);
            RemoveRedundantPrerequisitesForSubGraph(subGraph);
            BuildPanelListsForUnified(nodeByDef, subGraph);

            if (runLayout)
            {
                new SugiyamaLayout().Calculate(subGraph);
                ResearchNode.InvalidateAllPorts();
            }

            return subGraph;
        }

        private void ConnectMergedPrereqList(ResearchNode copy, List<ResearchProjectDef> prereqDefs,
            Dictionary<ResearchProjectDef, ResearchNode> nodeByDef, GraphKey key, ResearchSubGraph subGraph)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                if (nodeByDef.TryGetValue(prereqDef, out var prereqCopy))
                {
                    ConnectWithinSubGraph(prereqCopy, copy, subGraph);
                }
                else if (AllNodes.TryGetValue(prereqDef, out var canonicalNode))
                {
                    ConnectWithinSubGraph(GetOrCreateProxy(canonicalNode, key), copy, subGraph);
                }
            }
        }

        /// <summary>
        /// Transitive Reduction을 적용한다.
        /// </summary>
        private void RemoveRedundantPrerequisitesForSubGraph(ResearchSubGraph subGraph)
        {
            foreach (var node in subGraph.Nodes)
            {
                if (node.Prerequisites.Count < 2) continue;

                var toRemove = new HashSet<ResearchNode>();
                var directPrereqs = node.Prerequisites.ToList();
                foreach (var p in directPrereqs)
                {
                    if (toRemove.Contains(p)) continue;

                    var ancestorsOfP = GetAllAncestors(p);
                    foreach (var ancestor in ancestorsOfP)
                    {
                        if (directPrereqs.Contains(ancestor))
                        {
                            toRemove.Add(ancestor);
                        }
                    }
                }

                foreach (var remove in toRemove)
                {
                    node.Prerequisites.Remove(remove);
                    remove.Children.Remove(node);
                    RemoveEdge(subGraph, remove, node);
                }
            }
        }

        private void BuildPanelListsForUnified(
            Dictionary<ResearchProjectDef, ResearchNode> unifiedNodeByDef,
            ResearchSubGraph subGraph)
        {
            ResetPanelLists(unifiedNodeByDef.Values);

            foreach (var copy in unifiedNodeByDef.Values)
            {
                AddPanelPrereqsForUnified(copy, copy.Def.prerequisites, unifiedNodeByDef);
                AddPanelPrereqsForUnified(copy, copy.Def.hiddenPrerequisites, unifiedNodeByDef);
            }

            foreach (var copy in unifiedNodeByDef.Values)
            {
                SortPanelLists(copy);
                copy.ExternalChildren = ExternalChildrenForMergedCopy(copy);
            }
        }

        /// <summary>병합 그래프 안에는 벤치 탭이 모두 들어와 있으므로, 외부 배지는 비-벤치 채널만 남긴다.</summary>
        private List<ResearchNode> ExternalChildrenForMergedCopy(ResearchNode copy)
        {
            if (!AllNodes.TryGetValue(copy.Def, out var canonical) || canonical.ExternalChildren == null)
            {
                return new List<ResearchNode>();
            }
            return canonical.ExternalChildren.Where(c => c.Channel != null && !c.Channel.IsBench).ToList();
        }

        private void AddPanelPrereqsForUnified(
            ResearchNode node,
            List<ResearchProjectDef> prereqDefs,
            Dictionary<ResearchProjectDef, ResearchNode> unifiedNodeByDef)
        {
            if (prereqDefs == null) return;
            foreach (var prereqDef in prereqDefs)
            {
                // 병합 복사본이 있으면 그것을, 없으면 정규 노드를 가리킨다 (후자는 후행 링크 없음)
                bool isMergedCopy = unifiedNodeByDef.TryGetValue(prereqDef, out var prereqNode);
                if (!isMergedCopy && !AllNodes.TryGetValue(prereqDef, out prereqNode)) continue;

                LinkPanelPair(node, prereqNode, linkChild: isMergedCopy);
            }
        }

        public ResearchSubGraph GetSubGraph(GraphKey key)
        {
            return SubGraphs.TryGetValue(key, out var graph) ? graph : null;
        }

        /// <summary>해당 채널의 서브그래프 중 하나라도 실제(더미/프록시 제외) 노드를 갖는가</summary>
        public bool ChannelHasRealNodes(ResearchChannel channel)
        {
            foreach (var kvp in SubGraphs)
            {
                if (kvp.Key.Channel != channel) continue;
                if (kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy)) return true;
            }
            return false;
        }
    }
}
