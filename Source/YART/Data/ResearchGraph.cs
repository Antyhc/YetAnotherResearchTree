using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace YART.Data
{
    public class ResearchEdge
    {
        public ResearchNode From { get; }

        public ResearchNode To { get; }

        public ResearchEdge(ResearchNode from, ResearchNode to)
        {
            From = from;
            To = to;
        }
    }

    /// <summary>SugiyamaLayout.Calculate가 채움</summary>
    public class LayoutMetrics
    {
        /// <summary>ordering 직후 인접 레이어 교차 수</summary>
        public int Crossings;

        /// <summary>NormalizeEdges 전, 실제 노드 사이 엣지의 rank span 합</summary>
        public int TotalEdgeSpan;

        /// <summary>더미 체인 Y의 방향 전환 수</summary>
        public int BendCount;

        /// <summary>더미 체인 전체의 |dY| 합, px</summary>
        public float VerticalVariation;

        /// <summary>ShapeChains 비수렴 체인 수</summary>
        public int NonConvergedChains;

        /// <summary>막힘 때문이 아닌 더미 체인 방향 전환 수</summary>
        public int UnjustifiedBends;

        /// <summary>가장 높은 컬럼의 max - min, px</summary>
        public float MaxColumnHeight;

        /// <summary>예산 초과 컬럼의 제곱합 비용</summary>
        public float OverflowCost;

        /// <summary>비고립 노드가 있는 랭크 수 (레이아웃 가로 폭)</summary>
        public int UsedRankCount;

        /// <summary>Phase 4 목적함수: OverflowCost + RankCostSpanWeight * TotalEdgeSpan + RankCostWidthWeight * UsedRankCount</summary>
        public float RankCost;
    }

    public class ResearchSubGraph
    {
        public GraphKey Key { get; }

        public ResearchChannel Channel => Key.Channel;

        /// <summary>벤치 채널 전용, 그 외 null</summary>
        public ResearchTabDef Tab => Key.Tab;

        /// <summary>프록시 포함</summary>
        public List<ResearchNode> Nodes { get; } = new List<ResearchNode>();

        public List<ResearchEdge> Edges { get; } = new List<ResearchEdge>();

        public Rect BoundingBox { get; private set; }

        /// <summary>TechLevel이 시작되는 X 좌표, 구분선용</summary>
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

    public class ResearchGraph
    {
        private static ResearchGraph _instance;
        public static ResearchGraph Instance => _instance ?? (_instance = new ResearchGraph());

        public Dictionary<GraphKey, ResearchSubGraph> SubGraphs { get; private set; } = new Dictionary<GraphKey, ResearchSubGraph>();
        public Dictionary<ResearchProjectDef, ResearchNode> AllNodes { get; private set; } = new Dictionary<ResearchProjectDef, ResearchNode>();

        private readonly Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>> _proxyCache = new Dictionary<GraphKey, Dictionary<ResearchProjectDef, ResearchNode>>();
        private volatile bool _initialized = false;

        // 프리셋 Id별 def -> 병합 복사본 (프리셋 = 통합 벤치 + 사용자 탭 그룹)
        private readonly Dictionary<string, Dictionary<ResearchProjectDef, ResearchNode>> _presetNodeByDef
            = new Dictionary<string, Dictionary<ResearchProjectDef, ResearchNode>>();

        public bool Initialized => _initialized;

        public bool? BuiltWithHiddenPrereqs { get; private set; }

        // 구성은 GraphTopology가 하고 여기선 그 결과를 게임 객체로 옮김
        private DefSnapshot.ResearchInput _input;
        private GraphTopology _topology;

        /// <summary>LayoutExport가 입력을 내보낼 때 씀</summary>
        public GraphTopology Topology => _topology;

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

                _input = DefSnapshot.Research;
                bool includeHidden = YARTMod.Settings.includeHiddenPrerequisites;
                BuiltWithHiddenPrereqs = includeHidden;
                _topology = GraphTopology.Build(_input.Specs, includeHidden, msg => Log.Warning("[YART] " + msg));

                MaterializeBase();
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

        private void Clear()
        {
            AllNodes.Clear();
            SubGraphs.Clear();
            _proxyCache.Clear();
            _initialized = false;
            _presetNodeByDef.Clear();
            _topology = null;
            _input = null;
        }

        /// <summary>삽입 순서(서브그래프는 첫 등장 순, AllNodes는 def 순, 나머지는 토폴로지 순)에 레이아웃, 패널 동점 정렬, 검색이 의존함</summary>
        private void MaterializeBase()
        {
            foreach (var tg in _topology.Graphs) GetOrAddSubGraph(_input.KeyOf(tg.Key));

            var nodeOf = new Dictionary<TopoNode, ResearchNode>();
            for (int i = 0; i < _input.Specs.Count; i++)
            {
                var tn = _topology.CanonicalOf(i);
                if (tn == null) continue;
                var node = ResearchNode.Create(_input.Defs[i]);
                AllNodes[node.Def] = node;
                nodeOf[tn] = node;
            }

            foreach (var tg in _topology.Graphs)
            {
                MaterializeGraph(tg, SubGraphs[_input.KeyOf(tg.Key)], nodeOf);
            }
        }

        /// <summary>nodeOf에 없는 노드(프록시, 병합 복사본)는 새로 만듦</summary>
        private void MaterializeGraph(TopoGraph tg, ResearchSubGraph subGraph, Dictionary<TopoNode, ResearchNode> nodeOf)
        {
            foreach (var tn in tg.Nodes)
            {
                if (!nodeOf.TryGetValue(tn, out var node))
                {
                    var canonicalNode = AllNodes[_input.Defs[tn.Spec]];
                    node = tn.IsProxy
                        ? GetOrCreateProxy(canonicalNode, subGraph.Key)
                        : ResearchNode.CreateMergedCopy(canonicalNode, subGraph.Key);
                    nodeOf[tn] = node;
                }
                subGraph.AddNode(node);
            }

            foreach (var tn in tg.Nodes)
            {
                var node = nodeOf[tn];
                foreach (var p in tn.Prerequisites) node.Prerequisites.Add(nodeOf[p]);
                foreach (var c in tn.Children) node.Children.Add(nodeOf[c]);
            }

            foreach (var e in tg.Edges)
            {
                subGraph.AddEdge(nodeOf[e.From], nodeOf[e.To]);
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

        /// <summary>node -> child(다른 그래프) 의존을 외부 배지에 표시할지</summary>
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

        /// <summary>linkChild=false면 선행 쪽만 연결. 외부 노드의 후행 목록을 오염시키면 안 됨</summary>
        private static void LinkPanelPair(ResearchNode node, ResearchNode prereqNode, bool linkChild)
        {
            if (!node.PanelPrerequisites.Contains(prereqNode)) node.PanelPrerequisites.Add(prereqNode);
            if (linkChild && !prereqNode.PanelChildren.Contains(node)) prereqNode.PanelChildren.Add(node);
        }

        private ResearchNode GetOrCreateProxy(ResearchNode original, GraphKey targetKey)
        {
            GetOrAddSubGraph(targetKey); // 서브그래프와 프록시 캐시 보장

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
            // 통합 벤치도 프리셋 그래프
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

            Func<TopoKey, bool> includeSource;
            if (preset.IncludeAllBenchTabs)
            {
                includeSource = src => src.IsBench && !src.IsPreset;
            }
            else
            {
                var tabs = preset.ResolveTabs();
                if (tabs.Count == 0) { RemovePresetSubGraph(preset.Id); return null; }
                var tabSet = new HashSet<string>(tabs.Select(t => t.defName));
                includeSource = src => src.IsBench && !src.IsPreset && tabSet.Contains(src.TabId);
            }

            var key = GraphKey.ForPreset(preset.Id);
            RemovePresetSubGraph(preset.Id);

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
            _topology?.RemoveGraph(TopoKey.ForPreset(presetId));
        }

        private ResearchSubGraph BuildMergedSubGraphCore(
            GraphKey key,
            Func<TopoKey, bool> includeSource,
            bool runLayout,
            out Dictionary<ResearchProjectDef, ResearchNode> nodeByDef)
        {
            bool includeHidden = YARTMod.Settings.includeHiddenPrerequisites;
            var tg = _topology.BuildMerged(TopoKey.ForPreset(key.PresetId), includeSource, includeHidden, out _);

            var subGraph = GetOrAddSubGraph(key);
            var nodeOf = new Dictionary<TopoNode, ResearchNode>();
            MaterializeGraph(tg, subGraph, nodeOf);

            nodeByDef = new Dictionary<ResearchProjectDef, ResearchNode>();
            foreach (var tn in tg.Nodes)
            {
                if (!tn.IsProxy) nodeByDef[_input.Defs[tn.Spec]] = nodeOf[tn];
            }

            BuildPanelListsForUnified(nodeByDef, subGraph);

            if (runLayout)
            {
                new SugiyamaLayout().Calculate(subGraph);
                ResearchNode.InvalidateAllPorts();
            }

            return subGraph;
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

        /// <summary>병합 그래프엔 벤치 탭이 다 들어 있으므로 외부 배지는 비벤치 채널만</summary>
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
                // 병합 복사본이 없으면 정규 노드를 가리키되 후행 링크는 안 걺
                bool isMergedCopy = unifiedNodeByDef.TryGetValue(prereqDef, out var prereqNode);
                if (!isMergedCopy && !AllNodes.TryGetValue(prereqDef, out prereqNode)) continue;

                LinkPanelPair(node, prereqNode, linkChild: isMergedCopy);
            }
        }

        public ResearchSubGraph GetSubGraph(GraphKey key)
        {
            return SubGraphs.TryGetValue(key, out var graph) ? graph : null;
        }

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
