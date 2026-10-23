using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART
{
    public class SugiyamaLayout
    {
        private ResearchSubGraph graph;
        private Dictionary<ResearchNode, TechLevel> effectiveTechLevel;
        private Dictionary<int, List<ResearchNode>> isolatedNodesByRank;
        private HashSet<ResearchNode> isolatedNodesSet;
        private bool graphHasEras;
        private int lastNonConvergedChains; // ShapeChains 마지막 호출 기준
        private Dictionary<ResearchNode, int> subtreeWeight; // 하위 도달 노드 수 (center-out 시드용, lazy)
        private float columnBudget;

        /// <summary>같은 연구 탭 노드를 세로로 묶음. 병합 프리셋 그래프에만 적용</summary>
        public static bool GroupClusteringEnabled = false;
        private Dictionary<string, int> groupOrderIndex;

        private static float GridY => Constraints.NodeSize.y + Constraints.NodeSpacing.y;

        private static float CorridorRowY => Constraints.LayoutDummyNodeHeight + Constraints.LayoutDummySpacing;

        public void Calculate(ResearchSubGraph graph)
        {
            if (graph.Nodes.Count == 0) return;

            LayoutExport.Capture(graph); // 더미/랭크 생성 전에 떠야 함

            this.graph = graph;
            effectiveTechLevel = new Dictionary<ResearchNode, TechLevel>();
            isolatedNodesByRank = new Dictionary<int, List<ResearchNode>>();
            isolatedNodesSet = new HashSet<ResearchNode>();
            subtreeWeight = null;

            var realNodes = graph.Nodes.Where(n => !n.IsDummy && !n.IsProxy).ToList();
            if (realNodes.Count == 0) return;
            graphHasEras = realNodes.Any(n => n.TechLevel != TechLevel.Undefined);

            var topo = TopologicalOrder(realNodes);
            ComputeEffectiveTechLevels(topo);

            var bands = AssignRanks(topo);
            TightenRanks(topo, bands);
            columnBudget = ComputeColumnBudget(topo);
            BalanceRanks(topo, bands);
            PlaceProxies(bands);
            CompactRanks(bands);
            int metricsTotalEdgeSpan = ComputeTotalEdgeSpan(topo);
            NormalizeEdges();
            ExtractIsolated();
            var layers = BuildLayers(0);
            // 그룹 없이 먼저 돌린 순서를 EnforceGroupContiguity가 stable-sort로 보존해 그룹 내 시드로 씀
            int metricsCrossings = MinimizeCrossings(layers);

            if (GroupClusteringEnabled && graph.Key.IsPreset)
            {
                ApplyGroupClustering(layers);
                metricsCrossings = TotalCrossings(layers);
            }

            var rankX = AssignX(layers);
            AssignY(layers, rankX);
            if (UntangleChains(layers))
            {
                ShapeChains(layers);
                ResolveDummyRuns(layers);
            }

            PlaceIsolated(layers, rankX, bands);
            AlignTerminalLeavesToIncoming(layers); // 더미 셰이핑, 고립 배치가 끝난 뒤여야 함
            ValidateLayerSeparation(layers);

            graph.UpdateBoundingBox();

            graph.Metrics = ComputeLayoutMetrics(layers, metricsCrossings, metricsTotalEdgeSpan);
        }

        private bool InGraph(ResearchNode n) => n.Key.Equals(graph.Key);

        private IEnumerable<ResearchNode> RealPrereqs(ResearchNode n)
            => n.Prerequisites.Where(p => !p.IsDummy && !p.IsProxy && InGraph(p));

        private IEnumerable<ResearchNode> RealChildren(ResearchNode n)
            => n.Children.Where(c => !c.IsDummy && !c.IsProxy && InGraph(c));

        private IEnumerable<ResearchNode> Ups(ResearchNode n) => n.Prerequisites.Where(InGraph);

        private IEnumerable<ResearchNode> Downs(ResearchNode n) => n.Children.Where(InGraph);

        private ResearchNode NextTrunkDown(ResearchNode d) => Downs(d).FirstOrDefault(x => x.IsDummy);

        /// <summary>트렁크의 첫 더미. 더미 in-degree 1 불변식에 의존</summary>
        private ResearchNode ChainHeadOf(ResearchNode dummy)
        {
            var head = dummy;
            while (true)
            {
                var p = Ups(head).FirstOrDefault();
                if (p == null || !p.IsDummy) return head;
                head = p;
            }
        }

        private List<ResearchNode> TrunkTargets(List<ResearchNode> chain)
        {
            var targets = new List<ResearchNode>(2);
            foreach (var d in chain)
            {
                foreach (var c in Downs(d))
                {
                    if (!c.IsDummy) targets.Add(c);
                }
            }
            return targets;
        }

        private float TrunkDestY(List<ResearchNode> chain)
        {
            var ys = new List<float>(2);
            foreach (var d in chain)
            {
                foreach (var c in Downs(d))
                {
                    if (!c.IsDummy) ys.Add(c.Position.y);
                }
            }
            if (ys.Count == 0) return float.NaN;
            ys.Sort();
            int m = ys.Count / 2;
            return (ys.Count & 1) == 1 ? ys[m] : (ys[m - 1] + ys[m]) / 2f;
        }

        private static float NodeHeightOf(ResearchNode n)
            => n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;

        private static float SpacingOf(ResearchNode a, ResearchNode b)
            => a.IsDummy && b.IsDummy ? Constraints.LayoutDummySpacing : Constraints.NodeSpacing.y;

        /// <summary>SeparationOf에서 빼면 시각적 최소 간격이 되는 통로 클램프 완화량</summary>
        private static float RelaxOf(ResearchNode a, ResearchNode b)
            => Mathf.Max(0f, SpacingOf(a, b) - Constraints.LayoutDummyVisualPad);

        /// <summary>같은 레이어 인접 두 노드의 중심 간 최소 간격</summary>
        private static float SeparationOf(ResearchNode a, ResearchNode b)
            => (NodeHeightOf(a) + NodeHeightOf(b)) / 2f + SpacingOf(a, b);


        /// <summary>CompactRanks 이후, NormalizeEdges 이전에 호출해야 함</summary>
        private int ComputeTotalEdgeSpan(IReadOnlyList<ResearchNode> topo)
        {
            int span = 0;
            foreach (var n in topo)
            {
                foreach (var c in RealChildren(n))
                {
                    span += c.Rank - n.Rank; // 전방 엣지 불변식상 항상 양수
                }
            }
            return span;
        }

        private LayoutMetrics ComputeLayoutMetrics(
            List<List<ResearchNode>> layers,
            int crossings,
            int totalEdgeSpan)
        {
            try
            {
                var m = new LayoutMetrics();
                m.Crossings = crossings;
                m.TotalEdgeSpan = totalEdgeSpan;
                m.NonConvergedChains = lastNonConvergedChains;

                // 더미 체인별 Y 프로파일
                float bendEps = Constraints.MetricsBendEpsilon;
                var visitedChainHeads = new HashSet<ResearchNode>();
                foreach (var layer in layers)
                {
                    foreach (var node in layer)
                    {
                        if (node.IsDummy) continue;

                        foreach (var down in Downs(node))
                        {
                            if (!down.IsDummy) continue;

                            if (!visitedChainHeads.Add(down)) continue;

                            // Y 시퀀스: 소스 실노드, 트렁크 더미들, 첫 실타깃
                            float srcY = node.Position.y;
                            var dummyNodes = new List<ResearchNode>();
                            var cur = down;
                            while (cur != null)
                            {
                                dummyNodes.Add(cur);
                                cur = NextTrunkDown(cur);
                            }
                            int cn = dummyNodes.Count;

                            var chainY = new List<float>();
                            chainY.Add(srcY);
                            foreach (var dn in dummyNodes) chainY.Add(dn.Position.y);
                            var tail = down;
                            while (NextTrunkDown(tail) != null) tail = NextTrunkDown(tail);
                            var firstRealTarget = Downs(tail).FirstOrDefault(x => !x.IsDummy);
                            if (firstRealTarget != null) chainY.Add(firstRealTarget.Position.y);

                            int prevSign = 0;
                            for (int i = 1; i < chainY.Count; i++)
                            {
                                float dy = chainY[i] - chainY[i - 1];
                                m.VerticalVariation += Mathf.Abs(dy);
                                if (Mathf.Abs(dy) < bendEps) continue;
                                int sign = dy > 0 ? 1 : -1;
                                if (prevSign != 0 && sign != prevSign) m.BendCount++;
                                prevSign = sign;
                            }

                            // UnjustifiedBends: 이동 창(lo/hi)으로 설명되지 않는 방향 전환
                            if (cn < 2) continue;
                            float chainSrcY = srcY;
                            float chainDstY = firstRealTarget != null ? firstRealTarget.Position.y : float.NaN;
                            if (float.IsNaN(chainDstY)) continue;

                            float rawDirF = chainDstY - chainSrcY;
                            int chainDir = Mathf.Abs(rawDirF) < bendEps ? 0 : (rawDirF > 0f ? 1 : -1);

                            float monoFloor = chainSrcY;
                            float monoCeil  = chainSrcY;
                            int prevSignU = 0;
                            bool prevForced = false;
                            for (int j = 0; j < cn; j++)
                            {
                                var dn = dummyNodes[j];
                                var dnLayer = layers[dn.Rank];
                                int dnIdx = dn.VOrder;
                                float loJ = dnIdx > 0
                                    ? dnLayer[dnIdx - 1].Position.y + SeparationOf(dnLayer[dnIdx - 1], dn)
                                    : float.NegativeInfinity;
                                float hiJ = dnIdx + 1 < dnLayer.Count
                                    ? dnLayer[dnIdx + 1].Position.y - SeparationOf(dn, dnLayer[dnIdx + 1])
                                    : float.PositiveInfinity;

                                // 단조 추세 밖으로 강제됨: 창 역전, 또는 창이 추세 반대편에 있음
                                bool curForced = loJ > hiJ
                                    || (chainDir >= 0 && hiJ < monoFloor)
                                    || (chainDir <= 0 && loJ > monoCeil);

                                float dY = chainY[j + 1] - chainY[j]; // chainY[0]=srcY 이므로 j+1이 더미 Y
                                if (Mathf.Abs(dY) >= bendEps)
                                {
                                    int curSign = dY > 0f ? 1 : -1;
                                    if (prevSignU != 0 && curSign != prevSignU)
                                    {
                                        // 진입(curForced) 또는 직전 강제로부터의 복귀(prevForced)면 정당
                                        if (!curForced && !prevForced) m.UnjustifiedBends++;
                                    }
                                    prevSignU = curSign;
                                }
                                prevForced = curForced;

                                // 단조 방향으로만 갱신. 매번 리셋하면 복귀 굽힘을 오분류함
                                float placed = chainY[j + 1];
                                if (placed > monoFloor) monoFloor = placed;
                                if (placed < monoCeil)  monoCeil  = placed;
                            }
                        }
                    }
                }

                // 컬럼(랭크)별 실제 픽셀 범위
                var rankMin = new Dictionary<int, float>();
                var rankMax = new Dictionary<int, float>();
                foreach (var node in graph.Nodes)
                {
                    if (isolatedNodesSet.Contains(node)) continue;
                    float half = NodeHeightOf(node) / 2f; // Position.y는 박스 중심
                    float top = node.Position.y - half;
                    float bot = node.Position.y + half;
                    float cur2;
                    if (!rankMin.TryGetValue(node.Rank, out cur2) || top < cur2) rankMin[node.Rank] = top;
                    if (!rankMax.TryGetValue(node.Rank, out cur2) || bot > cur2) rankMax[node.Rank] = bot;
                }
                float maxColH = 0f;
                foreach (var kvp in rankMin)
                {
                    float h = rankMax[kvp.Key] - kvp.Value;
                    if (h > maxColH) maxColH = h;
                }
                m.MaxColumnHeight = maxColH;

                // RankCost 항. BalanceRanks 비용 함수와 같아야 함
                float budget = columnBudget;
                float rowReal = GridY;
                float overflowCost = 0f;
                foreach (var kvp2 in rankMin)
                {
                    float load = rankMax[kvp2.Key] - kvp2.Value;
                    float overPx = Mathf.Max(0f, load - budget);
                    float overRows = overPx / rowReal;
                    overflowCost += overRows * overRows;
                }
                m.OverflowCost = overflowCost;

                m.UsedRankCount = rankMin.Count; // rankMin은 고립 노드를 이미 제외함

                m.RankCost = m.OverflowCost
                    + Constraints.RankCostSpanWeight  * m.TotalEdgeSpan
                    + Constraints.RankCostWidthWeight * m.UsedRankCount;

                return m;
            }
            catch (Exception ex)
            {
                Log.Warning("[YART] LayoutMetrics 계측 중 예외 (레이아웃 동작에는 영향 없음): " + ex.Message);
                return null;
            }
        }

        private List<ResearchNode> TopologicalOrder(IReadOnlyList<ResearchNode> nodes)
        {
            var indeg = new Dictionary<ResearchNode, int>(nodes.Count);
            foreach (var n in nodes) indeg[n] = RealPrereqs(n).Count();

            var queue = new Queue<ResearchNode>(nodes.Where(n => indeg[n] == 0));
            var result = new List<ResearchNode>(nodes.Count);

            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                result.Add(v);
                foreach (var c in RealChildren(v))
                {
                    if (indeg.TryGetValue(c, out int d) && --indeg[c] == 0 && d > 0)
                        queue.Enqueue(c);
                }
            }

            // 사이클은 그래프 빌드에서 제거되지만 방어용
            if (result.Count < nodes.Count)
            {
                Log.Warning($"[YART] Layout: topological order incomplete ({result.Count}/{nodes.Count}) in {graph.Key}.");
                var seen = new HashSet<ResearchNode>(result);
                result.AddRange(nodes.Where(n => !seen.Contains(n)));
            }
            return result;
        }

        /// <summary>유효 TechLevel = max(자신, 모든 조상). 밴드 구간이 겹치지 않게 함</summary>
        private void ComputeEffectiveTechLevels(IEnumerable<ResearchNode> topo)
        {
            foreach (var v in topo)
            {
                var tl = v.TechLevel;
                foreach (var p in RealPrereqs(v))
                {
                    if (effectiveTechLevel.TryGetValue(p, out var ptl) && ptl > tl) tl = ptl;
                }
                effectiveTechLevel[v] = tl;
                v.EffectiveTechLevelInternal = tl; // 색을 유효 시대 구역에 맞추는 옵션이 참조
            }
        }

        /// <summary>TechLevel 밴드별 longest-path 랭크 할당</summary>
        private Dictionary<TechLevel, (int lo, int hi)> AssignRanks(IEnumerable<ResearchNode> topo)
        {
            var bands = new Dictionary<TechLevel, (int lo, int hi)>();
            int nextBase = 0;

            // GroupBy는 그룹 내 위상 순서를 보존함
            foreach (var band in topo.GroupBy(v => effectiveTechLevel[v]).OrderBy(g => g.Key))
            {
                int baseRank = nextBase;
                int maxRank = baseRank;

                foreach (var v in band)
                {
                    int r = baseRank;
                    foreach (var p in RealPrereqs(v))
                    {
                        if (p.Rank + 1 > r) r = p.Rank + 1;
                    }
                    v.Rank = r;
                    if (r > maxRank) maxRank = r;
                }

                bands[band.Key] = (baseRank, maxRank);
                nextBase = maxRank + 1;
            }
            return bands;
        }

        /// <summary>밴드와 선행/자식 제약 안에서 총 엣지 길이가 줄어드는 쪽으로 노드를 당김</summary>
        private void TightenRanks(IEnumerable<ResearchNode> topo, IReadOnlyDictionary<TechLevel, (int lo, int hi)> bands)
        {
            var reverseTopo = new List<ResearchNode>(topo);
            reverseTopo.Reverse();

            for (int pass = 0; pass < Constraints.LayoutRankTightenMaxPasses; pass++)
            {
                bool changed = false;

                foreach (var v in reverseTopo)
                {
                    var band = bands[effectiveTechLevel[v]];
                    int lo = band.lo, hi = band.hi;
                    int indeg = 0, outdeg = 0;

                    foreach (var p in RealPrereqs(v))
                    {
                        indeg++;
                        if (p.Rank + 1 > lo) lo = p.Rank + 1;
                    }
                    foreach (var c in RealChildren(v))
                    {
                        outdeg++;
                        if (c.Rank - 1 < hi) hi = c.Rank - 1;
                    }
                    if (hi < lo) continue;

                    int target;
                    if (outdeg > indeg) target = hi;
                    else if (indeg > outdeg) target = lo;
                    else target = Mathf.Clamp(v.Rank, lo, hi);

                    if (target != v.Rank)
                    {
                        v.Rank = target;
                        changed = true;
                    }
                }

                if (!changed) break;
            }
        }

        /// <summary>병합 그래프는 랭크별 실노드 수의 P75를 예산으로. 기본값은 너무 작아 limiter가 과하게 펼침</summary>
        private float ComputeColumnBudget(IReadOnlyList<ResearchNode> topo)
        {
            float def = Constraints.LayoutMaxColumnHeight;
            if (!graph.Key.IsPreset) return def;

            // limiter 이전의 자연 분포
            var counts = new Dictionary<int, int>();
            foreach (var v in topo)
            {
                counts.TryGetValue(v.Rank, out int c);
                counts[v.Rank] = c + 1;
            }
            if (counts.Count == 0) return def;

            var sorted = counts.Values.OrderBy(x => x).ToList();
            int idx = Mathf.Clamp((int)Math.Ceiling(sorted.Count * Constraints.LayoutUnifiedBudgetPercentile) - 1, 0, sorted.Count - 1);
            return Mathf.Max(def, sorted[idx] * GridY);
        }

        /// <summary>가장 늦은 실선행과의 스팬 기준. 캡 0은 비활성</summary>
        private bool ExceedsMaxEdgeSpan(ResearchNode v, int newRank)
        {
            int cap = Constraints.LayoutMaxEdgeSpan;
            if (cap <= 0) return false;
            int latestPrereqRank = -1;
            foreach (var p in RealPrereqs(v))
                if (p.Rank > latestPrereqRank) latestPrereqRank = p.Rank;
            return latestPrereqRank >= 0 && (newRank - latestPrereqRank) > cap;
        }

        /// <summary>예산을 넘는 컬럼의 슬랙 실노드를 오른쪽으로 민다. 통로 포화 컬럼은 MinRows에서 멈춤</summary>
        private void LimitColumnHeights(IReadOnlyList<ResearchNode> topo, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (bands.Count == 0) return;

            float rowReal = GridY;
            float rowCorridor = CorridorRowY;

            // 무한 루프 안전망. 이 상한을 넘으면 버그
            int rankCount = topo.Count == 0 ? 0 : topo.Max(v => v.Rank) + 1;
            int movesLeft = topo.Count * (rankCount + Constraints.LayoutHeightLimitMaxExtraRanks);
            int extraRanksLeft = Constraints.LayoutHeightLimitMaxExtraRanks;

            // 관통 통로는 소스당 1개(허브 버스). 고립 노드도 행으로 침
            float ColumnHeight(int r, out int rows, out int corridors)
            {
                rows = 0;
                corridors = 0;
                foreach (var v in topo)
                {
                    if (v.Rank == r)
                    {
                        rows++;
                    }
                    else if (v.Rank < r)
                    {
                        foreach (var c in RealChildren(v))
                        {
                            if (c.Rank > r) { corridors++; break; }
                        }
                    }
                }
                return rows * rowReal + corridors * rowCorridor;
            }

            // 우선순위: 새 통로 적은 순, 리프 우선, defName 서수(언어 무관 결정성)
            // 밴드 끝이면 자식 제약 생략. 자식은 다음 밴드라 랭크 삽입 후 r+2 이상이 됨
            ResearchNode PickCandidate(int r, bool atBandEdge)
            {
                ResearchNode best = null;
                int bestNew = int.MaxValue;
                bool bestLeaf = false;

                foreach (var v in topo)
                {
                    if (v.Rank != r) continue;

                    bool isLeaf = true;
                    if (atBandEdge)
                    {
                        isLeaf = !RealChildren(v).Any();
                    }
                    else
                    {
                        // 모든 실자식이 r+2 이상이어야 함
                        bool blocked = false;
                        foreach (var c in RealChildren(v))
                        {
                            isLeaf = false;
                            if (c.Rank < r + 2) { blocked = true; break; }
                        }
                        if (blocked) continue;
                    }

                    // 새로 r을 관통하게 되는 선행 수. 이미 관통 중인 선행은 버스를 공유하므로 제외
                    int newCorridors = 0;
                    foreach (var p in RealPrereqs(v))
                    {
                        bool spanning = false;
                        foreach (var c in RealChildren(p))
                        {
                            if (c.Rank > r) { spanning = true; break; }
                        }
                        if (!spanning) newCorridors++;
                    }
                    if (ExceedsMaxEdgeSpan(v, r + 1)) continue;

                    if (-rowReal + newCorridors * rowCorridor >= 0f) continue; // height(r)을 줄이지 못함

                    if (best == null
                        || newCorridors < bestNew
                        || (newCorridors == bestNew && ((isLeaf && !bestLeaf)
                            || (isLeaf == bestLeaf && string.CompareOrdinal(v.Id, best.Id) < 0))))
                    {
                        best = v;
                        bestNew = newCorridors;
                        bestLeaf = isLeaf;
                    }
                }
                return best;
            }

            // 밴드 확장으로 hi가 도중에 자라므로 매 반복 재조회
            bool warnedMoveCap = false;
            bool loggedCorridorFloor = false;
            foreach (var key in bands.Keys.OrderBy(k => k).ToList())
            {
                for (int r = bands[key].lo; r <= bands[key].hi; r++)
                {
                    while (movesLeft > 0)
                    {
                        int rows, corridors;
                        if (ColumnHeight(r, out rows, out corridors) <= columnBudget) break;

                        // 통로 포화 바닥: 통로가 예산을 다 먹은 컬럼. 더 밀면 밴드 끝에 좌초만 생김
                        if (rows <= Constraints.LayoutHeightLimitMinRows)
                        {
                            if (!loggedCorridorFloor && Prefs.DevMode
                                && corridors * rowCorridor >= columnBudget
                                                             - Constraints.LayoutHeightLimitMinRows * rowReal)
                            {
                                Log.Message($"[YART] Layout: rank {r} in {graph.Key} stays over budget — "
                                    + $"corridor-saturated ({corridors} corridors ≈ {corridors * rowCorridor:F0}px, "
                                    + $"{rows} real rows kept at floor {Constraints.LayoutHeightLimitMinRows}).");
                                loggedCorridorFloor = true;
                            }
                            break;
                        }

                        // 밴드 경계에서는 새 랭크를 삽입해야만 밀 수 있음
                        bool atBandEdge = r == bands[key].hi;
                        if (atBandEdge && extraRanksLeft <= 0) break;

                        var candidate = PickCandidate(r, atBandEdge);
                        if (candidate == null) break; // 소프트 예산이라 포기

                        if (atBandEdge)
                        {
                            InsertRankAfterBand(key, bands);
                            extraRanksLeft--;
                        }
                        candidate.Rank = r + 1;
                        movesLeft--;
                    }

                    // 이동 예산 소진은 비정상. 남은 초과 컬럼은 건너뜀
                    if (!warnedMoveCap && movesLeft <= 0 && ColumnHeight(r, out _, out _) > columnBudget)
                    {
                        Log.Warning($"[YART] Layout: height-limit move cap exhausted in {graph.Key} — remaining over-budget columns skipped.");
                        warnedMoveCap = true;
                    }
                }
            }

            if (Prefs.DevMode)
            {
                ValidateRankInvariants(topo, bands);
            }
        }

        /// <summary>LimitColumnHeights 결과를 시드로 +-1 greedy 정제</summary>
        private void BalanceRanks(IReadOnlyList<ResearchNode> topo, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (bands.Count == 0) return;

            // 1단계: LimitColumnHeights 시드
            LimitColumnHeights(topo, bands);

            float rowReal    = GridY;
            float rowCorridor = CorridorRowY;
            float budget     = columnBudget;
            float lambda     = Constraints.RankCostSpanWeight;
            float mu         = Constraints.RankCostWidthWeight;

            // limiter가 늘린 rankCount 기준
            int rankCount = topo.Count == 0 ? 0 : topo.Max(v => v.Rank) + 1;
            int movesLeft = topo.Count * rankCount;

            const float EPS = 1e-4f; // 이보다 작은 개선은 float 노이즈로 보고 무시

            // 증분 캐시, O(M*N^2) 재계산 회피
            var rowsArr = new int[rankCount];
            var corrArr = new int[rankCount];
            var diffBuf = new int[rankCount + 1];
            var maxCR = new Dictionary<ResearchNode, int>(topo.Count);

            void RebuildCaches()
            {
                System.Array.Clear(rowsArr, 0, rankCount);
                System.Array.Clear(diffBuf, 0, rankCount + 1);
                maxCR.Clear();
                foreach (var v in topo)
                {
                    rowsArr[v.Rank]++;
                    int mcr = v.Rank;
                    foreach (var c in RealChildren(v))
                        if (c.Rank > mcr) mcr = c.Rank;
                    maxCR[v] = mcr;
                    // 소스 v는 [v.Rank+1, mcr-1]을 관통 (허브 버스)
                    int lo2 = v.Rank + 1, hi2 = mcr - 1;
                    if (lo2 <= hi2) { diffBuf[lo2]++; diffBuf[hi2 + 1]--; }
                }
                int run = 0;
                for (int r = 0; r < rankCount; r++) { run += diffBuf[r]; corrArr[r] = run; }
            }

            // LimitColumnHeights.ColumnHeight와 같은 모델이어야 함
            float HeightOf(int rows, int corr) => rows * rowReal + corr * rowCorridor;
            float OverflowOf(float h)
            {
                float over = h - budget;
                if (over <= 0f) return 0f;
                float overRows = over / rowReal;
                return overRows * overRows;
            }
            // 소스가 r을 관통하는가
            int Contrib(int rank, int mcr, int r) => (rank < r && mcr > r) ? 1 : 0;

            // v를 oldR -> newR(oldR+-1)로 옮길 때의 RankCost 변화량, O(deg)
            // 통로 변화는 v 자신과 v의 부모에서만 생김
            float DeltaRankCost(ResearchNode v, int oldR, int newR)
            {
                int mcrV = maxCR[v]; // 자식은 그대로라 불변

                int dCorrOld = Contrib(newR, mcrV, oldR) - Contrib(oldR, mcrV, oldR);
                int dCorrNew = Contrib(newR, mcrV, newR) - Contrib(oldR, mcrV, newR);

                // v 이동으로 부모의 maxChildRank가 바뀔 수 있음
                foreach (var p in RealPrereqs(v))
                {
                    int mcrBefore = maxCR[p];
                    int mcrAfter;
                    if (oldR < mcrBefore)
                    {
                        mcrAfter = mcrBefore; // v가 p의 최대 자식이 아님
                    }
                    else
                    {
                        // v가 최대 자식이면 나머지 자식과 newR로 재계산
                        int m = newR;
                        foreach (var c in RealChildren(p))
                            if (!ReferenceEquals(c, v) && c.Rank > m) m = c.Rank;
                        mcrAfter = m;
                    }
                    dCorrOld += Contrib(p.Rank, mcrAfter, oldR) - Contrib(p.Rank, mcrBefore, oldR);
                    dCorrNew += Contrib(p.Rank, mcrAfter, newR) - Contrib(p.Rank, mcrBefore, newR);
                }

                // 결정성을 위해 연산 그룹핑 순서 고정
                float hOldBefore = HeightOf(rowsArr[oldR],     corrArr[oldR]);
                float hOldAfter  = HeightOf(rowsArr[oldR] - 1, corrArr[oldR] + dCorrOld);
                float hNewBefore = HeightOf(rowsArr[newR],     corrArr[newR]);
                float hNewAfter  = HeightOf(rowsArr[newR] + 1, corrArr[newR] + dCorrNew);

                float dOverflow = (OverflowOf(hOldAfter) + OverflowOf(hNewAfter))
                                - (OverflowOf(hOldBefore) + OverflowOf(hNewBefore));

                int dSpan = 0;
                foreach (var p in RealPrereqs(v)) dSpan += newR - oldR;
                foreach (var c in RealChildren(v)) dSpan -= newR - oldR;

                bool oldWillEmpty = rowsArr[oldR] == 1;
                bool newIsNew = rowsArr[newR] == 0;
                int dUsed = (newIsNew ? 1 : 0) - (oldWillEmpty ? 1 : 0);

                return dOverflow + lambda * dSpan + mu * dUsed;
            }

            // 2단계: +-1 greedy. 기존 랭크 안에서만 이동하고 랭크 삽입은 없음
            bool warnedMoveCap = false;
            bool improved = true;
            while (improved && movesLeft > 0)
            {
                RebuildCaches();
                improved = false;
                float bestDelta = -EPS; // 엄격히 음수인 이동만 채택
                ResearchNode bestNode = null;
                int bestOldR = -1, bestNewR = -1;

                // tie-break는 defName 서수 (언어 무관)
                foreach (var v in topo)
                {
                    int r = v.Rank;
                    var band = bands[effectiveTechLevel[v]];

                    int lo = band.lo;
                    int hi = band.hi;
                    foreach (var p in RealPrereqs(v))
                        if (p.Rank + 1 > lo) lo = p.Rank + 1;
                    foreach (var c in RealChildren(v))
                        if (c.Rank - 1 < hi) hi = c.Rank - 1;
                    if (hi < lo) continue;

                    // 왼쪽은 통로를 줄이는 방향이라 MinRows 가드 불필요
                    if (r - 1 >= lo)
                    {
                        int nr = r - 1;
                        float d = DeltaRankCost(v, r, nr);
                        if (d < bestDelta
                            || (d < bestDelta + EPS * 0.5f && bestNode != null
                                && (string.CompareOrdinal(v.Id, bestNode.Id) < 0
                                    || (string.CompareOrdinal(v.Id, bestNode.Id) == 0 && nr < bestNewR))))
                        {
                            bestDelta = d;
                            bestNode  = v;
                            bestOldR  = r;
                            bestNewR  = nr;
                            improved  = true;
                        }
                    }

                    if (r + 1 <= hi)
                    {
                        int nr = r + 1;

                        // limiter와 같은 정책. 시드만 캡하면 정제가 다시 흩뿌림
                        if (ExceedsMaxEdgeSpan(v, nr)) goto skipRight;

                        // 통로 포화 바닥
                        if (rowsArr[r] <= Constraints.LayoutHeightLimitMinRows) goto skipRight;

                        float d = DeltaRankCost(v, r, nr);
                        bool better = d < bestDelta;
                        if (!better && System.Math.Abs(d - bestDelta) < EPS * 0.5f && bestNode != null)
                        {
                            int cmp = string.CompareOrdinal(v.Id, bestNode.Id);
                            if (cmp < 0 || (cmp == 0 && nr < bestNewR)) better = true;
                        }
                        if (better)
                        {
                            bestDelta = d;
                            bestNode  = v;
                            bestOldR  = r;
                            bestNewR  = nr;
                            improved  = true;
                        }
                        skipRight:;
                    }
                }

                if (!improved || bestNode == null) break;

                bestNode.Rank = bestNewR;
                movesLeft--;

                if (movesLeft <= 0 && !warnedMoveCap)
                {
                    if (Prefs.DevMode)
                        Log.Warning($"[YART] BalanceRanks: refine move cap exhausted in {graph.Key} — remaining improvement skipped.");
                    warnedMoveCap = true;
                    break;
                }
            }

            if (Prefs.DevMode) ValidateRankInvariants(topo, bands);
        }

        /// <summary>밴드 hi 직후에 빈 랭크 삽입. 프록시는 아직 배치 전이라 제외</summary>
        private void InsertRankAfterBand(TechLevel key, Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            int hi = bands[key].hi;

            foreach (var n in graph.Nodes)
            {
                if (!n.IsProxy && !n.IsDummy && n.Rank > hi) n.Rank++;
            }
            foreach (var k in bands.Keys.ToList())
            {
                var b = bands[k];
                if (k == key) bands[k] = (b.lo, b.hi + 1);
                else if (b.lo > hi) bands[k] = (b.lo + 1, b.hi + 1);
            }
        }

        private void ValidateRankInvariants(IReadOnlyList<ResearchNode> topo, IReadOnlyDictionary<TechLevel, (int lo, int hi)> bands)
        {
            foreach (var v in topo)
            {
                var band = bands[effectiveTechLevel[v]];
                if (v.Rank < band.lo || v.Rank > band.hi)
                {
                    Log.Warning($"[YART] Layout: '{v.Label}' rank {v.Rank} outside band [{band.lo},{band.hi}] in {graph.Key}.");
                }
                foreach (var c in RealChildren(v))
                {
                    if (c.Rank <= v.Rank)
                    {
                        Log.Warning($"[YART] Layout: non-forward edge after height limit: '{v.Label}'({v.Rank}) -> '{c.Label}'({c.Rank}) in {graph.Key}.");
                    }
                }
            }

            int prevHi = -1;
            foreach (var k in bands.Keys.OrderBy(x => x))
            {
                var b = bands[k];
                if (b.lo <= prevHi)
                {
                    Log.Warning($"[YART] Layout: band {k} [{b.lo},{b.hi}] overlaps previous band (hi={prevHi}) in {graph.Key}.");
                }
                prevHi = b.hi;
            }
        }

        /// <summary>프록시(다른 트랙 선행의 대리 노드)를 원본 시대 구역 안에 배치</summary>
        private void PlaceProxies(Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            var proxies = graph.Nodes.Where(n => n.IsProxy).ToList();
            if (proxies.Count == 0) return;

            // 전용 컬럼을 넣는 이유: 한 컬럼에 시대가 섞이면 AssignX의 컬럼 대표 시대가 오염됨
            var placed = new HashSet<ResearchNode>();

            foreach (var eraGroup in proxies.GroupBy(p => p.TechLevel).OrderBy(g => g.Key))
            {
                var era = eraGroup.Key;
                bool hasBand = bands.TryGetValue(era, out var band);

                var needColumn = new List<ResearchNode>();
                foreach (var proxy in eraGroup)
                {
                    int minChild = MinChildRank(proxy);
                    if (minChild == int.MaxValue)
                    {
                        proxy.Rank = 0; // 자식 없는 프록시, 방어용
                        placed.Add(proxy);
                    }
                    else if (hasBand && minChild - 1 >= band.lo)
                    {
                        proxy.Rank = Mathf.Min(minChild - 1, band.hi);
                        placed.Add(proxy);
                    }
                    else
                    {
                        needColumn.Add(proxy);
                    }
                }
                if (needColumn.Count == 0) continue;

                int insertAt;
                if (hasBand)
                {
                    insertAt = band.lo;
                }
                else
                {
                    insertAt = int.MaxValue;
                    foreach (var kvp in bands)
                    {
                        if (kvp.Key > era && kvp.Value.lo < insertAt) insertAt = kvp.Value.lo;
                    }
                }

                bool inserted = false;
                foreach (var proxy in needColumn)
                {
                    int minChild = MinChildRank(proxy);
                    if (insertAt == int.MaxValue || minChild < insertAt)
                    {
                        // 자식 시대 <= 프록시 시대인 데이터 오류. 시대 구역은 포기하고 전방 엣지만 보장
                        proxy.Rank = minChild - 1;
                        placed.Add(proxy);
                        continue;
                    }

                    if (!inserted)
                    {
                        foreach (var n in graph.Nodes)
                        {
                            if ((!n.IsProxy || placed.Contains(n)) && n.Rank >= insertAt) n.Rank++;
                        }
                        foreach (var k in bands.Keys.ToList())
                        {
                            var b = bands[k];
                            if (b.lo >= insertAt) bands[k] = (b.lo + 1, b.hi + 1);
                        }
                        inserted = true;
                    }
                    proxy.Rank = insertAt;
                    placed.Add(proxy);
                }
            }
        }

        /// <summary>자식이 없으면 int.MaxValue</summary>
        private int MinChildRank(ResearchNode proxy)
        {
            int min = int.MaxValue;
            foreach (var c in proxy.Children.Where(InGraph))
            {
                if (c.Rank < min) min = c.Rank;
            }
            return min;
        }

        /// <summary>빈 랭크를 없애 0..K-1로 재매핑. 밴드 가장자리가 빈 랭크였을 수 있어 안쪽으로 클램프</summary>
        private void CompactRanks(Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            var used = graph.Nodes.Select(n => n.Rank).Distinct().OrderBy(r => r).ToList();
            var map = new Dictionary<int, int>(used.Count);
            for (int i = 0; i < used.Count; i++) map[used[i]] = i;

            foreach (var n in graph.Nodes) n.Rank = map[n.Rank];

            // used의 인덱스가 곧 새 랭크
            foreach (var k in bands.Keys.ToList())
            {
                var b = bands[k];
                int loIdx = used.BinarySearch(b.lo);
                if (loIdx < 0) loIdx = ~loIdx;     // lo 이상 첫 사용 랭크
                int hiIdx = used.BinarySearch(b.hi);
                if (hiIdx < 0) hiIdx = ~hiIdx - 1; // hi 이하 마지막 사용 랭크
                bands[k] = (loIdx, hiIdx);
            }
        }

        /// <summary>같은 From의 멀티랭크 엣지는 더미 트렁크 하나를 공유(허브 버스). 더미는 in 1, out 1 이상, 더미 자식 최대 1</summary>
        private void NormalizeEdges()
        {
            // 1패스. sourceOrder는 결정성용
            var multiBySource = new Dictionary<ResearchNode, List<ResearchEdge>>();
            var sourceOrder = new List<ResearchNode>();

            foreach (var edge in graph.Edges.ToList())
            {
                var from = edge.From;
                var to = edge.To;
                int rankDiff = to.Rank - from.Rank;

                if (rankDiff == 1) continue;

                if (rankDiff < 1)
                {
                    // 생기면 안 되지만 레이아웃 보호용으로 끊음
                    Log.Warning($"[YART] Layout: non-forward edge {from.Label} -> {to.Label} (diff={rankDiff}). Dropping.");
                    graph.Edges.Remove(edge);
                    from.Children.Remove(to);
                    to.Prerequisites.Remove(from);
                    continue;
                }

                if (!multiBySource.TryGetValue(from, out var list))
                {
                    list = new List<ResearchEdge>();
                    multiBySource[from] = list;
                    sourceOrder.Add(from);
                }
                list.Add(edge);
            }

            // 2패스: 원본 멀티랭크 엣지를 전부 공유 트렁크 경유로 대체
            foreach (var from in sourceOrder)
            {
                var edges = multiBySource[from];

                int maxToRank = from.Rank;
                foreach (var e in edges)
                {
                    if (e.To.Rank > maxToRank) maxToRank = e.To.Rank;

                    graph.Edges.Remove(e);
                    from.Children.Remove(e.To);
                    e.To.Prerequisites.Remove(from);
                }

                // from.Rank+1 .. maxToRank-1, 길이 >= 1
                var trunk = new ResearchNode[maxToRank - from.Rank - 1];
                var current = from;
                for (int r = from.Rank + 1; r < maxToRank; r++)
                {
                    var dummy = ResearchNode.CreateDummy(graph.Key);
                    dummy.Rank = r;
                    dummy.TechLevel = from.TechLevel;
                    dummy.GroupKey = from.GroupKey;

                    graph.AddNode(dummy);
                    Connect(current, dummy);
                    trunk[r - from.Rank - 1] = dummy;
                    current = dummy;
                }

                // 타깃 랭크 직전 더미에서 분기. rankDiff >= 2라 인덱스 >= 0
                foreach (var e in edges)
                {
                    Connect(trunk[e.To.Rank - from.Rank - 2], e.To);
                }
            }

            if (Prefs.DevMode) ValidateNormalizedEdges();
        }

        private void ValidateNormalizedEdges()
        {
            foreach (var n in graph.Nodes)
            {
                if (!n.IsDummy) continue;
                if (Ups(n).Count() != 1)
                {
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} has in-degree {Ups(n).Count()} (expected 1) in {graph.Key}.");
                }
                if (!Downs(n).Any())
                {
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} has no children in {graph.Key}.");
                }
                // NextTrunkDown 트렁크 워크가 유일하려면 더미 자식 <= 1
                int dummyChildren = Downs(n).Count(c => c.IsDummy);
                if (dummyChildren > 1)
                {
                    // 더미는 라벨이 없어 실소스까지 올라감
                    var src = Ups(n).FirstOrDefault();
                    while (src != null && src.IsDummy) src = Ups(src).FirstOrDefault();
                    Log.Warning($"[YART] Layout: dummy at rank {n.Rank} (source '{src?.Label}') has {dummyChildren} dummy children (expected <= 1) in {graph.Key}.");
                }
            }
            foreach (var e in graph.Edges)
            {
                if (e.To.Rank != e.From.Rank + 1)
                {
                    Log.Warning($"[YART] Layout: non-unit edge after normalize: '{e.From.Label}'({e.From.Rank}) -> '{e.To.Label}'({e.To.Rank}) in {graph.Key}.");
                }
            }
        }

        private void Connect(ResearchNode from, ResearchNode to)
        {
            from.Children.Add(to);
            to.Prerequisites.Add(from);
            graph.AddEdge(from, to);
        }


        /// <summary>DFS 방문 순으로 채워 서브트리를 군집화. variant 3은 무거운 허브를 중앙에 둬 팬아웃 상단 쏠림 방지</summary>
        private List<List<ResearchNode>> BuildLayers(int variant)
        {
            int rankCount = graph.Nodes.Max(n => n.Rank) + 1;
            var layers = new List<List<ResearchNode>>(rankCount);
            for (int i = 0; i < rankCount; i++) layers.Add(new List<ResearchNode>());

            var visited = new HashSet<ResearchNode>(isolatedNodesSet); // 고립 노드는 PlaceIsolated 몫
            var stack = new Stack<ResearchNode>();

            var sources = graph.Nodes.Where(n => !isolatedNodesSet.Contains(n) && !Ups(n).Any());
            List<ResearchNode> ordered;
            switch (variant)
            {
                case 1:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenByDescending(n => Downs(n).Count())
                        .ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
                case 2:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenByDescending(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
                case 3:
                {
                    var w = ComputeSubtreeWeights();
                    ordered = sources.GroupBy(n => n.Rank).OrderBy(g => g.Key)
                        .SelectMany(g => CenterOutByWeight(g, w)).ToList();
                    break;
                }
                default:
                    ordered = sources.OrderBy(n => n.Rank)
                        .ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
                    break;
            }

            foreach (var source in ordered)
            {
                stack.Push(source);
                while (stack.Count > 0)
                {
                    var v = stack.Pop();
                    if (!visited.Add(v)) continue;
                    layers[v.Rank].Add(v);

                    var kids = Downs(v).ToList();
                    if (variant == 1) kids.Sort((a, b) => Downs(b).Count().CompareTo(Downs(a).Count()));
                    else if (variant == 2) kids.Reverse();
                    else if (variant == 3) kids = CenterOutByWeight(kids, ComputeSubtreeWeights());
                    for (int i = kids.Count - 1; i >= 0; i--)
                    {
                        if (!visited.Contains(kids[i])) stack.Push(kids[i]);
                    }
                }
            }

            // 소스에서 도달 불가한 노드, 정상 DAG엔 없음
            foreach (var n in graph.Nodes)
            {
                if (visited.Add(n)) layers[n.Rank].Add(n);
            }

            SyncVOrder(layers);
            return layers;
        }

        private static void SyncVOrder(List<List<ResearchNode>> layers)
        {
            foreach (var layer in layers)
            {
                for (int i = 0; i < layer.Count; i++) layer[i].VOrder = i;
            }
        }

        /// <summary>자신 제외, 더미 포함. 모든 엣지가 1랭크 전진이라 Rank 내림차순 1패스로 충분</summary>
        private Dictionary<ResearchNode, int> ComputeSubtreeWeights()
        {
            if (subtreeWeight != null) return subtreeWeight;

            var weight = new Dictionary<ResearchNode, int>(graph.Nodes.Count);
            var desc = new Dictionary<ResearchNode, HashSet<ResearchNode>>(graph.Nodes.Count);
            foreach (var n in graph.Nodes.OrderByDescending(x => x.Rank))
            {
                var set = new HashSet<ResearchNode>();
                foreach (var c in Downs(n))
                {
                    set.Add(c);
                    if (desc.TryGetValue(c, out var cd)) set.UnionWith(cd);
                }
                desc[n] = set;
                weight[n] = set.Count;
            }

            subtreeWeight = weight;
            return weight;
        }

        /// <summary>무거운 항목을 가운데, 가벼운 항목을 위아래 바깥으로 교대 배치</summary>
        private List<ResearchNode> CenterOutByWeight(IEnumerable<ResearchNode> items, Dictionary<ResearchNode, int> weight)
        {
            var sortedDesc = items
                .OrderByDescending(n => weight.TryGetValue(n, out var w) ? w : 0)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();

            var dq = new LinkedList<ResearchNode>();
            for (int i = 0; i < sortedDesc.Count; i++)
            {
                if ((i & 1) == 0) dq.AddLast(sortedDesc[i]);
                else dq.AddFirst(sortedDesc[i]);
            }
            return new List<ResearchNode>(dq);
        }

        /// <summary>차수 0 노드를 median 정렬에서 빼 둔다. 배치는 PlaceIsolated</summary>
        private void ExtractIsolated()
        {
            foreach (var n in graph.Nodes)
            {
                if (n.IsDummy) continue;
                if (Ups(n).Any() || Downs(n).Any()) continue;

                isolatedNodesSet.Add(n);
                if (!isolatedNodesByRank.TryGetValue(n.Rank, out var list))
                {
                    list = new List<ResearchNode>();
                    isolatedNodesByRank[n.Rank] = list;
                }
                list.Add(n);
            }
            foreach (var list in isolatedNodesByRank.Values)
            {
                list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            }
        }

        private static readonly string[] OrderingVariantNames = { "dfs", "degree", "reverse", "weighted" };

        private float OrderingScore(List<List<ResearchNode>> layers, int crossings)
        {
            return crossings + Constraints.LayoutOrderingHeightWeight * (EstimateMinHeight(layers) / GridY);
        }

        private int MinimizeCrossings(List<List<ResearchNode>> layers)
        {
            float bestScore = float.MaxValue;
            List<ResearchNode[]> bestSnap = null;

            for (int variant = 0; variant < OrderingVariantNames.Length; variant++)
            {
                if (variant > 0) Restore(layers, Snapshot(BuildLayers(variant)));
                int cross = RunOrderingLoop(layers);
                float score = OrderingScore(layers, cross);
                if (score < bestScore) // 동점이면 앞 변형 유지 (결정성)
                {
                    bestScore = score;
                    bestSnap = Snapshot(layers);
                }
            }

            Restore(layers, bestSnap);

            ExactSmallLayerSweep(layers);

            // DP가 못 다루는 큰 레이어의 국소해 탈출
            return SiftingPass(layers);
        }

        /// <summary>수렴 판정은 교차 수, 스냅샷 채택은 OrderingScore 기준. 최선 스냅샷의 교차 수 반환</summary>
        private int RunOrderingLoop(List<List<ResearchNode>> layers)
        {
            int initCross = TotalCrossings(layers);
            // bestCross는 최선 스냅샷의 교차, bestCrossSoFar는 stale 판정용
            float bestScore = OrderingScore(layers, initCross);
            int bestCross = initCross;
            int bestCrossSoFar = initCross;
            var bestOrder = Snapshot(layers);
            int stale = 0;

            for (int iter = 0;
                 iter < Constraints.LayoutOrderingMaxIterations && bestCrossSoFar > 0 && stale < Constraints.LayoutOrderingPatience;
                 iter++)
            {
                WMedian(layers, forward: iter % 2 == 0);
                Transpose(layers);

                int crossings = TotalCrossings(layers);

                if (crossings < bestCrossSoFar)
                {
                    bestCrossSoFar = crossings;
                    stale = 0;
                }
                else
                {
                    stale++;
                }

                float score = OrderingScore(layers, crossings);
                if (score < bestScore) // 동점이면 앞 스냅샷 유지 (결정성)
                {
                    bestScore = score;
                    bestCross = crossings;
                    bestOrder = Snapshot(layers);
                }
            }

            Restore(layers, bestOrder);
            return bestCross;
        }

        /// <summary>작은 레이어를 부분집합 DP로 전수 최적화해 transpose의 국소해 탈출. OrderingScore가 나빠지면 롤백</summary>
        private void ExactSmallLayerSweep(List<List<ResearchNode>> layers)
        {
            bool improvedAny = true;
            int guard = 0;
            while (improvedAny && guard++ < 4)
            {
                improvedAny = false;
                foreach (var layer in layers)
                {
                    int n = layer.Count;
                    if (n < 3 || n > Constraints.LayoutExactLayerMaxNodes) continue; // 2개는 transpose가 이미 최적

                    var cost = new long[n, n];
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            if (i != j) cost[i, j] = PairCrossings(layer[i], layer[j]);
                        }
                    }

                    long current = 0;
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = i + 1; j < n; j++) current += cost[i, j];
                    }
                    if (current == 0) continue;

                    int full = 1 << n;
                    var dp = new long[full];
                    var pick = new int[full];
                    for (int s = 1; s < full; s++) dp[s] = long.MaxValue;

                    for (int s = 0; s < full; s++)
                    {
                        if (dp[s] == long.MaxValue) continue;
                        for (int v = 0; v < n; v++)
                        {
                            if ((s & (1 << v)) != 0) continue;
                            long add = 0;
                            for (int u = 0; u < n; u++)
                            {
                                if ((s & (1 << u)) != 0) add += cost[u, v];
                            }
                            int ns = s | (1 << v);
                            if (dp[s] + add < dp[ns])
                            {
                                dp[ns] = dp[s] + add;
                                pick[ns] = v;
                            }
                        }
                    }

                    if (dp[full - 1] >= current) continue;

                    float scoreBefore = OrderingScore(layers, TotalCrossings(layers));
                    var saved = CaptureLayerOrder(layer);

                    var order = new ResearchNode[n];
                    int cur = full - 1;
                    for (int slot = n - 1; slot >= 0; slot--)
                    {
                        int v = pick[cur];
                        order[slot] = layer[v];
                        cur &= ~(1 << v);
                    }
                    layer.Clear();
                    layer.AddRange(order);
                    for (int i = 0; i < n; i++) layer[i].VOrder = i;

                    float scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                    if (scoreAfter < scoreBefore)
                    {
                        improvedAny = true;
                    }
                    else
                    {
                        RestoreLayerOrder(layer, saved); // 교차는 줄었지만 높이 비용이 상쇄
                    }
                }
            }
        }

        private static (ResearchNode[] order, int[] vOrders) CaptureLayerOrder(List<ResearchNode> layer)
        {
            var order = layer.ToArray();
            var vOrders = new int[order.Length];
            for (int i = 0; i < order.Length; i++) vOrders[i] = order[i].VOrder;
            return (order, vOrders);
        }

        private static void RestoreLayerOrder(List<ResearchNode> layer, (ResearchNode[] order, int[] vOrders) saved)
        {
            layer.Clear();
            layer.AddRange(saved.order);
            for (int i = 0; i < layer.Count; i++) layer[i].VOrder = saved.vOrders[i];
        }

        private static List<ResearchNode[]> Snapshot(List<List<ResearchNode>> layers)
            => layers.Select(l => l.ToArray()).ToList();

        private static void Restore(List<List<ResearchNode>> layers, List<ResearchNode[]> snapshot)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                layers[i].Clear();
                layers[i].AddRange(snapshot[i]);
            }
            SyncVOrder(layers);
        }

        /// <summary>DOT의 wmedian. weighted는 median 보간을 뜻하며 엣지 가중치가 아님</summary>
        private void WMedian(List<List<ResearchNode>> layers, bool forward)
        {
            if (forward)
            {
                for (int r = 1; r < layers.Count; r++) ReorderByMedian(layers[r], useUp: true);
            }
            else
            {
                for (int r = layers.Count - 2; r >= 0; r--) ReorderByMedian(layers[r], useUp: false);
            }
        }

        private void ReorderByMedian(List<ResearchNode> layer, bool useUp)
            => ReorderRangeByMedian(layer, 0, layer.Count, useUp);

        /// <summary>[start, start+len) 구간만 안정 재정렬. 이웃 없는 노드는 제자리 고정</summary>
        private void ReorderRangeByMedian(List<ResearchNode> layer, int start, int len, bool useUp)
        {
            if (len < 2) return;

            var medians = new float[len];
            for (int i = 0; i < len; i++)
            {
                var positions = (useUp ? Ups(layer[start + i]) : Downs(layer[start + i]))
                    .Select(u => u.VOrder).ToList();
                positions.Sort();
                medians[i] = MedianValue(positions);
            }

            var slot = new ResearchNode[len];
            var movable = new List<int>(len);
            for (int i = 0; i < len; i++)
            {
                if (medians[i] < 0f) slot[i] = layer[start + i];
                else movable.Add(i);
            }

            var sorted = movable.OrderBy(i => medians[i]).ToList(); // OrderBy는 안정 정렬
            int next = 0;
            for (int i = 0; i < len; i++)
            {
                if (slot[i] == null) slot[i] = layer[start + sorted[next++]];
            }

            for (int i = 0; i < len; i++)
            {
                layer[start + i] = slot[i];
                slot[i].VOrder = start + i;
            }
        }

        /// <summary>DOT 논문의 median value: 짝수 차수일 때 좌우 밀도로 보간</summary>
        private static float MedianValue(List<int> positions)
        {
            int n = positions.Count;
            if (n == 0) return -1f;

            int m = n / 2;
            if ((n & 1) == 1) return positions[m];
            if (n == 2) return (positions[0] + positions[1]) / 2f;

            int left = positions[m - 1] - positions[0];
            int right = positions[n - 1] - positions[m];
            if (left + right == 0) return (positions[m - 1] + positions[m]) / 2f;
            return (positions[m - 1] * right + positions[m] * left) / (float)(left + right);
        }

        private void Transpose(List<List<ResearchNode>> layers)
        {
            bool improved = true;
            int sweep = 0;

            while (improved && sweep++ < Constraints.LayoutTransposeMaxSweeps)
            {
                improved = false;
                foreach (var layer in layers)
                {
                    for (int i = 0; i + 1 < layer.Count; i++)
                    {
                        var v = layer[i];
                        var w = layer[i + 1];
                        if (PairCrossings(w, v) < PairCrossings(v, w))
                        {
                            layer[i] = w;
                            layer[i + 1] = v;
                            w.VOrder = i;
                            v.VOrder = i + 1;
                            improved = true;
                        }
                    }
                }
            }
        }

        /// <summary>Vertex sifting으로 큰 레이어의 국소해 탈출. OrderingScore가 나빠지면 롤백</summary>
        private int SiftingPass(List<List<ResearchNode>> layers)
        {
            bool improvedAny = true;
            int guard = 0;
            while (improvedAny && guard++ < Constraints.LayoutSiftingMaxPasses)
            {
                improvedAny = false;
                foreach (var layer in layers)
                {
                    int n = layer.Count;
                    if (n < 3) continue; // 2개 이하는 transpose가 이미 최적

                    var saved = CaptureLayerOrder(layer);
                    float scoreBefore = OrderingScore(layers, TotalCrossings(layers));

                    if (!SiftLayer(layer)) continue;

                    float scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                    if (scoreAfter < scoreBefore - 1e-4f) improvedAny = true;
                    else RestoreLayerOrder(layer, saved);
                }
            }
            return TotalCrossings(layers);
        }

        private bool SiftLayer(List<ResearchNode> layer) => SiftRange(layer, 0, layer.Count);

        /// <summary>cost는 인접 레이어 VOrder만 보므로 구간을 섞어도 불변이라 한 번만 계산. 바뀌었으면 true</summary>
        private bool SiftRange(List<ResearchNode> layer, int lo, int hi)
        {
            int len = hi - lo;
            var nodes = new ResearchNode[len];
            for (int i = 0; i < len; i++) nodes[i] = layer[lo + i];
            var index = new Dictionary<ResearchNode, int>(len);
            for (int i = 0; i < len; i++) index[nodes[i]] = i;

            var cost = new int[len, len];
            for (int i = 0; i < len; i++)
                for (int j = 0; j < len; j++)
                    if (i != j) cost[i, j] = PairCrossings(nodes[i], nodes[j]);

            bool changed = false;
            var work = new List<ResearchNode>(len);
            for (int i = 0; i < len; i++) work.Add(nodes[i]);

            foreach (var v in nodes)
            {
                int vi = index[v];
                int curPos = work.IndexOf(v);

                var others = new List<ResearchNode>(len - 1);
                foreach (var x in work)
                    if (!ReferenceEquals(x, v)) others.Add(x);

                // p=0(맨 위)일 때 교차 = sum cost[v, other]
                long running = 0;
                for (int j = 0; j < others.Count; j++) running += cost[vi, index[others[j]]];
                long best = running;
                int bestP = 0;
                // p가 늘면 v가 others[p-1] 아래로 가서 그 쌍 기여가 cost[v,w]에서 cost[w,v]로 바뀜
                for (int p = 1; p <= others.Count; p++)
                {
                    int wi = index[others[p - 1]];
                    running += cost[wi, vi] - cost[vi, wi];
                    if (running < best) { best = running; bestP = p; }
                }

                if (bestP != curPos)
                {
                    work.Clear();
                    for (int j = 0; j <= others.Count; j++)
                    {
                        if (j == bestP) work.Add(v);
                        if (j < others.Count) work.Add(others[j]);
                    }
                    changed = true;
                }
            }

            if (changed)
                for (int i = 0; i < len; i++) { layer[lo + i] = work[i]; work[i].VOrder = lo + i; }
            return changed;
        }

        /// <summary>a가 b 위에 있을 때 두 노드 엣지 간 교차 수</summary>
        private int PairCrossings(ResearchNode a, ResearchNode b)
        {
            int crossings = 0;
            foreach (var pa in Ups(a))
            {
                foreach (var pb in Ups(b))
                {
                    if (pa.VOrder > pb.VOrder) crossings++;
                }
            }
            foreach (var ca in Downs(a))
            {
                foreach (var cb in Downs(b))
                {
                    if (ca.VOrder > cb.VOrder) crossings++;
                }
            }
            return crossings;
        }

        private int TotalCrossings(List<List<ResearchNode>> layers)
        {
            int total = 0;
            for (int r = 0; r + 1 < layers.Count; r++)
            {
                total += CountCrossings(layers[r], layers[r + 1]);
            }
            return total;
        }

        /// <summary>펜윅 트리 역전쌍 카운트, O(E log V)</summary>
        private int CountCrossings(List<ResearchNode> upper, List<ResearchNode> lower)
        {
            if (upper.Count == 0 || lower.Count == 0) return 0;

            var sequence = new List<int>();
            foreach (var w in lower)
            {
                var ps = Ups(w).Select(p => p.VOrder).ToList();
                ps.Sort();
                sequence.AddRange(ps);
            }
            if (sequence.Count < 2) return 0;

            var bit = new int[upper.Count + 1];
            int inversions = 0, seen = 0;

            foreach (var x in sequence)
            {
                int lessOrEqual = 0;
                for (int i = x + 1; i > 0; i -= i & (-i)) lessOrEqual += bit[i];
                inversions += seen - lessOrEqual;

                for (int i = x + 1; i <= upper.Count; i += i & (-i)) bit[i]++;
                seen++;
            }
            return inversions;
        }

        private static string GroupOf(ResearchNode n) => string.IsNullOrEmpty(n.GroupKey) ? "(none)" : n.GroupKey;

        /// <summary>같은 그룹 연속 블록의 끝 (exclusive)</summary>
        private static int GroupBlockEnd(List<ResearchNode> layer, int start)
        {
            string g = GroupOf(layer[start]);
            int end = start + 1;
            while (end < layer.Count && GroupOf(layer[end]) == g) end++;
            return end;
        }

        private void ApplyGroupClustering(List<List<ResearchNode>> layers)
        {
            ComputeGroupOrder();
            if (groupOrderIndex.Count <= 1) return;
            EnforceGroupContiguity(layers);
            GroupAwareOrderingLoop(layers);
            RefineGroupOrderBySearch(layers);
            GroupAwareOrderingLoop(layers);
            SyncVOrder(layers);
        }

        /// <summary>swap/insertion 국소탐색, 평가는 전체 교차 수</summary>
        private void RefineGroupOrderBySearch(List<List<ResearchNode>> layers)
        {
            var order = groupOrderIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            int m = order.Count;
            if (m < 3) return;

            // 그룹 순서만 바꿔 비교하려고 띠 내부 순서를 고정
            var localIndex = new Dictionary<ResearchNode, int>(graph.Nodes.Count);
            foreach (var layer in layers)
            {
                int i = 0;
                while (i < layer.Count)
                {
                    int end = GroupBlockEnd(layer, i);
                    for (int j = i; j < end; j++) localIndex[layer[j]] = j - i;
                    i = end;
                }
            }

            var slot = new Dictionary<string, int>(m);

            int Restack(List<string> perm)
            {
                for (int i = 0; i < m; i++) slot[perm[i]] = i;
                foreach (var layer in layers)
                {
                    layer.Sort((a, b) =>
                    {
                        int c = slot[GroupOf(a)].CompareTo(slot[GroupOf(b)]);
                        return c != 0 ? c : localIndex[a].CompareTo(localIndex[b]);
                    });
                    for (int i = 0; i < layer.Count; i++) layer[i].VOrder = i;
                }
                return TotalCrossings(layers);
            }

            int baseCross = Restack(order);
            var best = new List<string>(order);
            int bestCross = baseCross;

            bool improved = true;
            int pass = 0;
            while (improved && pass++ < Constraints.LayoutGroupSearchMaxPasses)
            {
                improved = false;

                for (int i = 0; i < m; i++)
                {
                    for (int j = i + 1; j < m; j++)
                    {
                        var trial = new List<string>(best);
                        var tmp = trial[i]; trial[i] = trial[j]; trial[j] = tmp;
                        int c = Restack(trial);
                        if (c < bestCross) { bestCross = c; best = trial; improved = true; }
                    }
                }

                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < m; j++)
                    {
                        if (j == i || j == i + 1) continue;
                        var trial = new List<string>(best);
                        var g = trial[i];
                        trial.RemoveAt(i);
                        trial.Insert(j > i ? j - 1 : j, g);
                        int c = Restack(trial);
                        if (c < bestCross) { bestCross = c; best = trial; improved = true; }
                    }
                }
            }

            Restack(best); // 레이어에 반영
            var newOrder = new Dictionary<string, int>(m);
            for (int i = 0; i < m; i++) newOrder[best[i]] = i;
            groupOrderIndex = newOrder;

            if (Prefs.DevMode)
                Log.Message($"[YART] group-order search: {baseCross} → {bestCross} ({m} groups)");
        }

        private void GroupAwareOrderingLoop(List<List<ResearchNode>> layers)
        {
            float bestScore = OrderingScore(layers, TotalCrossings(layers));
            var best = Snapshot(layers);
            int stale = 0;
            for (int iter = 0;
                 iter < Constraints.LayoutOrderingMaxIterations && stale < Constraints.LayoutOrderingPatience;
                 iter++)
            {
                GroupWMedian(layers, forward: iter % 2 == 0);
                GroupConstrainedTranspose(layers);

                int cr = TotalCrossings(layers);
                float sc = OrderingScore(layers, cr);
                if (sc < bestScore - 1e-4f) { bestScore = sc; best = Snapshot(layers); stale = 0; }
                else stale++;
            }
            Restore(layers, best);

            GroupConstrainedSifting(layers);
        }

        private void GroupConstrainedSifting(List<List<ResearchNode>> layers)
        {
            bool improvedAny = true;
            int guard = 0;
            while (improvedAny && guard++ < Constraints.LayoutSiftingMaxPasses)
            {
                improvedAny = false;
                foreach (var layer in layers)
                {
                    int n = layer.Count;
                    if (n < 3) continue;

                    int blockStart = 0;
                    while (blockStart < n)
                    {
                        int blockEnd = GroupBlockEnd(layer, blockStart);
                        if (blockEnd - blockStart >= 3)
                        {
                            var saved = CaptureLayerOrder(layer);
                            float scoreBefore = OrderingScore(layers, TotalCrossings(layers));

                            if (SiftRange(layer, blockStart, blockEnd))
                            {
                                float scoreAfter = OrderingScore(layers, TotalCrossings(layers));
                                if (scoreAfter < scoreBefore - 1e-4f) improvedAny = true;
                                else RestoreLayerOrder(layer, saved);
                            }
                        }
                        blockStart = blockEnd;
                    }
                }
            }
        }

        private void GroupWMedian(List<List<ResearchNode>> layers, bool forward)
        {
            if (forward)
                for (int r = 1; r < layers.Count; r++) ReorderWithinGroups(layers[r], useUp: true);
            else
                for (int r = layers.Count - 2; r >= 0; r--) ReorderWithinGroups(layers[r], useUp: false);
        }

        /// <summary>블록 경계는 그대로라 EnforceGroupContiguity의 띠 구조가 보존됨</summary>
        private void ReorderWithinGroups(List<ResearchNode> layer, bool useUp)
        {
            int n = layer.Count;
            if (n < 2) return;

            int blockStart = 0;
            while (blockStart < n)
            {
                int blockEnd = GroupBlockEnd(layer, blockStart);
                ReorderRangeByMedian(layer, blockStart, blockEnd - blockStart, useUp);
                blockStart = blockEnd;
            }
        }

        private void ComputeGroupOrder()
        {
            // cross-degree = 그룹을 넘나드는 엣지 수
            var deg = new Dictionary<string, int>();
            foreach (var n in graph.Nodes)
                if (!deg.ContainsKey(GroupOf(n))) deg[GroupOf(n)] = 0;
            foreach (var e in graph.Edges)
            {
                string gf = GroupOf(e.From), gt = GroupOf(e.To);
                if (gf == gt) continue;
                deg[gf]++; deg[gt]++;
            }

            // 허브를 중앙에, 나머지는 양쪽으로 교대
            var byDeg = deg.Keys.OrderByDescending(g => deg[g]).ThenBy(g => g, StringComparer.Ordinal).ToList();
            var dq = new LinkedList<string>();
            for (int r = 0; r < byDeg.Count; r++)
            {
                if ((r & 1) == 0) dq.AddLast(byDeg[r]);
                else dq.AddFirst(byDeg[r]);
            }

            var order = new Dictionary<string, int>(dq.Count);
            int p = 0;
            foreach (var g in dq) order[g] = p++;
            groupOrderIndex = order;

            if (Prefs.DevMode)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[YART] group order (centerdeg) top→bottom:");
                foreach (var kv in order.OrderBy(k => k.Value))
                    sb.Append($"\n  {kv.Value,2}: {kv.Key}  (crossDeg={deg[kv.Key]})");
                Log.Message(sb.ToString());
            }
        }

        private void EnforceGroupContiguity(List<List<ResearchNode>> layers)
        {
            foreach (var layer in layers)
            {
                if (layer.Count < 2) continue;
                // OrderBy는 안정 정렬이라 그룹 내 순서가 보존됨
                var sorted = layer.OrderBy(n => groupOrderIndex.TryGetValue(GroupOf(n), out int idx) ? idx : int.MaxValue).ToList();
                layer.Clear();
                layer.AddRange(sorted);
                for (int i = 0; i < layer.Count; i++) layer[i].VOrder = i;
            }
        }

        private void GroupConstrainedTranspose(List<List<ResearchNode>> layers)
        {
            bool improved = true;
            int sweep = 0;
            while (improved && sweep++ < Constraints.LayoutTransposeMaxSweeps)
            {
                improved = false;
                foreach (var layer in layers)
                {
                    for (int i = 0; i + 1 < layer.Count; i++)
                    {
                        var v = layer[i];
                        var w = layer[i + 1];
                        if (GroupOf(v) != GroupOf(w)) continue;
                        if (PairCrossings(w, v) < PairCrossings(v, w))
                        {
                            layer[i] = w;
                            layer[i + 1] = v;
                            w.VOrder = i;
                            v.VOrder = i + 1;
                            improved = true;
                        }
                    }
                }
            }
        }

        /// <summary>graph.TechLevelBoundaries도 채움</summary>
        private float[] AssignX(List<List<ResearchNode>> layers)
        {
            int rankCount = layers.Count;
            var rankX = new float[rankCount];

            graph.TechLevelBoundaries.Clear();

            float currentX = 0f;
            TechLevel lastTechLevel = TechLevel.Undefined;
            bool firstRank = true;

            for (int r = 0; r < rankCount; r++)
            {
                // 랭크 대표 시대 = 실노드 유효 TechLevel 최대값, 없으면 직전 값
                TechLevel tl = lastTechLevel;
                bool hasReal = false;
                foreach (var n in layers[r])
                {
                    if (n.IsDummy || n.IsProxy) continue;
                    hasReal = true;
                    if (effectiveTechLevel.TryGetValue(n, out var etl) && etl > tl) tl = etl;
                }
                // 프록시 전용 컬럼만 원본 시대를 반영. 시대 없는 그래프가 외부 선행 하나로 전부 칠해지지 않게
                if (!hasReal && graphHasEras)
                {
                    foreach (var n in layers[r])
                    {
                        if (!n.IsProxy) continue;
                        if (n.TechLevel > tl) tl = n.TechLevel;
                    }
                }
                // 밴드 시작 컬럼이 고립 노드뿐일 수 있음
                if (isolatedNodesByRank.TryGetValue(r, out var isolatedHere))
                {
                    foreach (var n in isolatedHere)
                    {
                        if (effectiveTechLevel.TryGetValue(n, out var etl) && etl > tl) tl = etl;
                    }
                }

                if (!firstRank && tl > lastTechLevel)
                {
                    float boundaryX = currentX + Constraints.NodeSpacing.x * Constraints.LayoutTechLevelBoundaryOffset;
                    graph.TechLevelBoundaries[tl] = boundaryX;
                    currentX += Constraints.NodeSpacing.x * Constraints.LayoutTechLevelGapMultiplier;
                }
                else
                {
                    currentX += Constraints.NodeSpacing.x;
                }

                if (firstRank)
                {
                    currentX = 0f;
                    // 시작 시대에도 구분선/라벨/틴트가 나오게 경계 등록
                    if (tl != TechLevel.Undefined)
                    {
                        graph.TechLevelBoundaries[tl] =
                            -Constraints.NodeSpacing.x * Constraints.LayoutTechLevelBoundaryOffset;
                    }
                }

                rankX[r] = currentX;
                currentX += Constraints.NodeSize.x;

                lastTechLevel = tl;
                firstRank = false;
            }

            return rankX;
        }

        private void AssignY(List<List<ResearchNode>> layers, float[] rankX)
        {
            float gridY = GridY;
            var finalY = SpringRelaxY(layers);

            // 실노드 기준 중앙 정렬. 더미는 좁은 통로 간격 유지를 위해 스냅하지 않음
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var node in graph.Nodes)
            {
                if (node.IsDummy || isolatedNodesSet.Contains(node)) continue;
                float y = finalY[node];
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            float mid = (minY <= maxY) ? (minY + maxY) / 2f : 0f;

            foreach (var node in graph.Nodes)
            {
                if (isolatedNodesSet.Contains(node)) continue;
                float y = finalY[node] - mid;
                // Mathf.Round는 은행가 반올림이라 x.5 경계의 두 노드가 한 행으로 합쳐짐
                float placed = node.IsDummy ? y : Mathf.Floor(y / gridY + 0.5f) * gridY;
                node.Position = new Vector2(rankX[node.Rank], placed);
            }

            // 1행 간격 두 실노드가 둘 다 반 칸 경계면 float 오차로 같은 행에 떨어지므로 레이어 순서대로 행을 엄격히 늘림
            foreach (var layer in layers)
            {
                float prevY = float.NegativeInfinity;
                foreach (var node in layer)
                {
                    if (node.IsDummy || isolatedNodesSet.Contains(node)) continue;
                    if (node.Position.y < prevY + gridY - 0.5f)
                        node.Position = new Vector2(node.Position.x, prevY + gridY);
                    prevY = node.Position.y;
                }
            }

            // 실노드는 그리드 고정, 더미만 이동
            ShapeChains(layers);
            ResolveDummyRuns(layers);
        }

        /// <summary>말단 실노드를 들어오는 이웃 쪽 그리드 행으로 당겨 스냅이 만든 끝단 꺾임을 없앤다. 빈 슬롯일 때만 이동</summary>
        private void AlignTerminalLeavesToIncoming(List<List<ResearchNode>> layers)
        {
            float gridY = GridY;

            // 2패스: 앞 패스에서 비워진 슬롯을 다음 패스가 씀
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var layer in layers)
                {
                    foreach (var v in layer)
                    {
                        if (v.IsDummy || v.IsProxy) continue;
                        if (Downs(v).Any()) continue;          // 나가는 엣지가 있으면 새 꺾임이 생김
                        if (isolatedNodesSet.Contains(v)) continue;

                        float sum = 0f; int deg = 0;
                        foreach (var u in Ups(v)) { sum += u.Position.y; deg++; }
                        if (deg == 0) continue;
                        float target = sum / deg;

                        float gridTarget = Mathf.Floor(target / gridY + 0.5f) * gridY;
                        if (Mathf.Abs(gridTarget - v.Position.y) <= 0.5f) continue;

                        // 리스트 순서를 가정하지 않고 Y로 위/아래 이웃을 찾음
                        float loBound = float.NegativeInfinity, hiBound = float.PositiveInfinity;
                        foreach (var u in layer)
                        {
                            if (ReferenceEquals(u, v)) continue;
                            float uy = u.Position.y;
                            if (uy <= v.Position.y) { float b = uy + SeparationOf(u, v); if (b > loBound) loBound = b; }
                            if (uy >= v.Position.y) { float b = uy - SeparationOf(v, u); if (b < hiBound) hiBound = b; }
                        }

                        if (gridTarget < loBound - 0.5f || gridTarget > hiBound + 0.5f) continue;
                        v.Position = new Vector2(v.Position.x, gridTarget);
                    }
                }
            }
        }

        /// <summary>바리센터 스프링 + PAV 투영으로 실노드와 더미의 Y 결정</summary>
        private Dictionary<ResearchNode, float> SpringRelaxY(List<List<ResearchNode>> layers)
        {
            float selfAnchor = Constraints.LayoutSpringSelfAnchor;
            int passes = Constraints.LayoutSpringPasses;
            // Tikhonov 중심 정규화. barycenter 분모에만 더해 노드를 0으로 당김
            // 엣지 항과 함께 최소화되므로 후처리 시프트와 달리 fault-line이 없음
            float centerAnchor = Constraints.LayoutSpringCenterAnchor;

            // 1단계: 레이어별 비겹침 초기 Y
            var finalY = new Dictionary<ResearchNode, float>(graph.Nodes.Count);
            foreach (var layer in layers)
            {
                float y = 0f;
                for (int i = 0; i < layer.Count; i++)
                {
                    if (i > 0) y += SeparationOf(layer[i - 1], layer[i]);
                    finalY[layer[i]] = y;
                }
            }

            // 2단계: Gauss-Seidel 스프링 + PAV 투영
            int maxLayerSize = 0;
            foreach (var layer in layers)
                if (layer.Count > maxLayerSize) maxLayerSize = layer.Count;

            var desired = new float[maxLayerSize];
            var prefix = new float[maxLayerSize];

            // 높이 하한 = 가장 빽빽한 레이어의 박스 스팬
            // centerAnchor를 하한 대비 슬랙에 비례시켜 여유 있는 그래프만 당김
            float hFloor = 0f;
            foreach (var layer in layers)
            {
                if (layer.Count == 0) continue;
                float span = 0f;
                for (int i = 1; i < layer.Count; i++) span += SeparationOf(layer[i - 1], layer[i]);
                float box = span + NodeHeightOf(layer[0]) / 2f + NodeHeightOf(layer[layer.Count - 1]) / 2f;
                if (box > hFloor) hFloor = box;
            }

            float CurHeight()
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var kv in finalY)
                {
                    float half = NodeHeightOf(kv.Key) / 2f;
                    if (kv.Value - half < lo) lo = kv.Value - half;
                    if (kv.Value + half > hi) hi = kv.Value + half;
                }
                return hi >= lo ? hi - lo : 0f;
            }

            for (int pass = 0; pass < passes; pass++)
            {
                bool forward = (pass % 2 == 0); // 방향 교대로 빨리 수렴
                int layerCount = layers.Count;

                float slackRatio = hFloor > 1e-3f ? Mathf.Clamp((CurHeight() - hFloor) / hFloor, 0f, 1f) : 0f;
                float lambdaEff = centerAnchor * slackRatio;

                for (int li = 0; li < layerCount; li++)
                {
                    int layerIdx = forward ? li : (layerCount - 1 - li);
                    var layer = layers[layerIdx];
                    int n = layer.Count;
                    if (n == 0) continue;

                    for (int i = 0; i < n; i++)
                    {
                        var v = layer[i];
                        float sum = finalY[v] * selfAnchor;
                        float weight = selfAnchor + lambdaEff;

                        // 그룹 모드에서 띠 횡단 엣지는 약하게 가중. 띠가 평평해지고 횡단 엣지만 길게 그려짐
                        bool grouped = GroupClusteringEnabled;
                        float crossW = Constraints.LayoutGroupCrossEdgeWeight;
                        string gv = grouped ? GroupOf(v) : null;
                        foreach (var u in Ups(v))
                        {
                            float ew = (!grouped || GroupOf(u) == gv) ? 1f : crossW;
                            sum += finalY[u] * ew;
                            weight += ew;
                        }
                        foreach (var u in Downs(v))
                        {
                            float ew = (!grouped || GroupOf(u) == gv) ? 1f : crossW;
                            sum += finalY[u] * ew;
                            weight += ew;
                        }

                        desired[i] = sum / weight;
                    }

                    // 가변 갭 prefix를 빼고 비감소 회귀 후 복원해 y[i+1] >= y[i] + sep 보장
                    prefix[0] = 0f;
                    for (int i = 1; i < n; i++)
                        prefix[i] = prefix[i - 1] + SeparationOf(layer[i - 1], layer[i]);

                    for (int i = 0; i < n; i++)
                        desired[i] -= prefix[i];

                    SpringPavProject(desired, n);

                    // 음수 Y도 정상 좌표. 0으로 클램프하면 레이어 간 정렬이 깨짐
                    for (int i = 0; i < n; i++)
                    {
                        finalY[layer[i]] = desired[i] + prefix[i];
                    }
                }
            }

            return finalY;
        }


        /// <summary>균일 가중 PAV(비감소 등온 회귀) in-place, O(n) amortized</summary>
        private static void SpringPavProject(float[] buf, int count)
        {
            var sStart  = new int[count];
            var sEnd    = new int[count];
            var sSum    = new float[count];
            var sWeight = new float[count];
            int top = 0;

            for (int i = 0; i < count; i++)
            {
                sStart[top]  = i;
                sEnd[top]    = i;
                sSum[top]    = buf[i];
                sWeight[top] = 1f;
                top++;

                while (top >= 2)
                {
                    float avgPrev = sSum[top - 2] / sWeight[top - 2];
                    float avgCur  = sSum[top - 1] / sWeight[top - 1];
                    if (avgPrev <= avgCur + 1e-6f) break;

                    sEnd[top - 2]    = sEnd[top - 1];
                    sSum[top - 2]   += sSum[top - 1];
                    sWeight[top - 2] += sWeight[top - 1];
                    top--;
                }
            }

            // 잔차는 음수일 수 있으니 클램프하지 않음
            for (int b = 0; b < top; b++)
            {
                float avg = sSum[b] / sWeight[b];
                for (int i = sStart[b]; i <= sEnd[b]; i++)
                    buf[i] = avg;
            }
        }

        /// <summary>박스가 실제로 겹칠 때만 경고. 스냅 오차 +-gridY/2는 허용</summary>
        private void ValidateLayerSeparation(List<List<ResearchNode>> layers)
        {
            if (!Prefs.DevMode) return;

            foreach (var layer in layers)
            {
                for (int i = 1; i < layer.Count; i++)
                {
                    float visualMin = (NodeHeightOf(layer[i - 1]) + NodeHeightOf(layer[i])) / 2f - 0.5f;
                    if (layer[i].Position.y - layer[i - 1].Position.y < visualMin)
                    {
                        Log.Warning($"[YART] Layout: overlap in rank {layer[i].Rank}: " +
                                    $"'{layer[i - 1].Label}' y={layer[i - 1].Position.y} / '{layer[i].Label}' y={layer[i].Position.y}");
                    }
                }
            }
        }

        /// <summary>실노드 1개 또는 더미 체인 전체. EstimateMinHeight용</summary>
        private class LayoutUnit
        {
            public readonly List<ResearchNode> Members = new List<ResearchNode>();
            public readonly List<(LayoutUnit unit, float sep)> Above = new List<(LayoutUnit, float)>();
            public readonly List<(LayoutUnit unit, float sep)> Below = new List<(LayoutUnit, float)>();
            public readonly List<LayoutUnit> Linked = new List<LayoutUnit>();
        }

        private List<LayoutUnit> BuildUnits(List<List<ResearchNode>> layers, out Dictionary<ResearchNode, LayoutUnit> unitOf)
        {
            unitOf = new Dictionary<ResearchNode, LayoutUnit>();
            var units = new List<LayoutUnit>();

            foreach (var layer in layers)
            {
                foreach (var node in layer)
                {
                    if (unitOf.ContainsKey(node)) continue;

                    var unit = new LayoutUnit();
                    if (node.IsDummy)
                    {
                        // 트렁크 전체가 한 유닛
                        var cur = ChainHeadOf(node);
                        while (cur != null)
                        {
                            unit.Members.Add(cur);
                            unitOf[cur] = unit;
                            cur = NextTrunkDown(cur);
                        }
                    }
                    else
                    {
                        unit.Members.Add(node);
                        unitOf[node] = unit;
                    }
                    units.Add(unit);
                }
            }

            foreach (var layer in layers)
            {
                for (int i = 1; i < layer.Count; i++)
                {
                    var ua = unitOf[layer[i - 1]];
                    var ub = unitOf[layer[i]];
                    if (ReferenceEquals(ua, ub)) continue;
                    float sep = SeparationOf(layer[i - 1], layer[i]);
                    ua.Below.Add((ub, sep));
                    ub.Above.Add((ua, sep));
                }

                foreach (var node in layer)
                {
                    var un = unitOf[node];
                    foreach (var child in Downs(node))
                    {
                        if (!unitOf.TryGetValue(child, out var uc) || ReferenceEquals(un, uc)) continue;
                        un.Linked.Add(uc);
                        uc.Linked.Add(un);
                    }
                }
            }
            return units;
        }

        /// <summary>현재 순서의 최소 배치 높이 추정. 사이클은 결정적으로 끊어 항상 비교 가능</summary>
        private float EstimateMinHeight(List<List<ResearchNode>> layers)
        {
            var units = BuildUnits(layers, out _);
            if (units.Count == 0) return 0f;

            // AssignY 이전이라 좌표가 없어 위상 순서로 스윕
            var yUp = SweepPackUp(CycleTolerantUnitOrder(units));
            float max = 0f;
            foreach (var v in yUp.Values)
            {
                if (v > max) max = v;
            }
            return max;
        }

        /// <summary>큐가 비면 units 인덱스가 가장 작은 유닛을 강제로 꺼내 사이클을 끊음</summary>
        private static List<LayoutUnit> CycleTolerantUnitOrder(List<LayoutUnit> units)
        {
            var indeg = new Dictionary<LayoutUnit, int>(units.Count);
            foreach (var u in units) indeg[u] = u.Above.Count;

            var order = new List<LayoutUnit>(units.Count);
            var ordered = new HashSet<LayoutUnit>();
            var ready = new Queue<LayoutUnit>();
            foreach (var u in units)
            {
                if (indeg[u] == 0) ready.Enqueue(u);
            }

            int cursor = 0;
            while (order.Count < units.Count)
            {
                LayoutUnit next = null;
                while (ready.Count > 0)
                {
                    // 강제로 꺼낸 유닛이 나중에 indeg 0으로 다시 들어올 수 있음
                    var c = ready.Dequeue();
                    if (!ordered.Contains(c)) { next = c; break; }
                }
                if (next == null)
                {
                    while (ordered.Contains(units[cursor])) cursor++;
                    next = units[cursor];
                }

                ordered.Add(next);
                order.Add(next);
                foreach (var (below, sep) in next.Below)
                {
                    if (--indeg[below] == 0 && !ordered.Contains(below)) ready.Enqueue(below);
                }
            }
            return order;
        }

        /// <summary>아직 처리 안 된 위쪽 파트너의 제약은 건너뜀</summary>
        private static Dictionary<LayoutUnit, float> SweepPackUp(List<LayoutUnit> order)
        {
            var yUp = new Dictionary<LayoutUnit, float>(order.Count);
            foreach (var u in order)
            {
                float v = 0f;
                foreach (var (above, sep) in u.Above)
                {
                    if (yUp.TryGetValue(above, out float ya) && ya + sep > v) v = ya + sep;
                }
                yUp[u] = v;
            }
            return yUp;
        }

        /// <summary>더미 트렁크를 머리부터 꼬리 순으로 수집</summary>
        private List<List<ResearchNode>> CollectChains()
        {
            var chains = new List<List<ResearchNode>>();
            var seen = new HashSet<ResearchNode>();
            foreach (var n in graph.Nodes)
            {
                if (!n.IsDummy || seen.Contains(n)) continue;

                var chain = new List<ResearchNode>();
                var cur = ChainHeadOf(n);
                while (cur != null)
                {
                    chain.Add(cur);
                    seen.Add(cur);
                    cur = NextTrunkDown(cur);
                }
                chains.Add(chain);
            }
            return chains;
        }

        /// <summary>UntangleChains의 스왑 단위. 단일 타깃이면 종착 실노드까지 포함</summary>
        private class Strip
        {
            public readonly List<ResearchNode> Members = new List<ResearchNode>(); // 연속 랭크 순
            public int StartRank;
            public float DestY; // 타깃들의 median 행, 첫 AssignY 기준
            public int EndRank => StartRank + Members.Count - 1;
            public ResearchNode MemberAt(int rank) => Members[rank - StartRank];
        }

        /// <summary>순서 단계가 남긴 목적지 행 역전을 인접 구간 스왑으로 푼다. 바뀌었으면 true</summary>
        private bool UntangleChains(List<List<ResearchNode>> layers)
        {
            var strips = new List<Strip>();
            foreach (var chain in CollectChains())
            {
                var targets = TrunkTargets(chain);
                if (targets.Count == 0) continue; // 종착 없는 체인, 방어용
                var strip = new Strip { StartRank = chain[0].Rank };
                strip.Members.AddRange(chain);
                if (targets.Count == 1)
                {
                    strip.Members.Add(targets[0]);
                    strip.DestY = targets[0].Position.y;
                }
                else
                {
                    // 분기 트렁크는 타깃을 고정하고 더미만 스왑
                    strip.DestY = TrunkDestY(chain);
                }
                strips.Add(strip);
            }
            if (strips.Count < 2) return false;

            // 한 체인이 여러 이웃을 연달아 지나칠 수 있어 넉넉히
            int maxPasses = Mathf.Min(16, strips.Count);
            bool changed = false;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                bool swappedThisPass = false;
                for (int i = 0; i < strips.Count; i++)
                {
                    for (int j = i + 1; j < strips.Count; j++)
                    {
                        if (TryUntanglePair(layers, strips[i], strips[j])) swappedThisPass = true;
                    }
                }
                if (swappedThisPass) changed = true;
                else break;
            }
            return changed;
        }

        /// <summary>역전된 인접 구간을 스왑해 보고 경계 교차나 통로 수용량이 나빠지면 되돌림</summary>
        private bool TryUntanglePair(List<List<ResearchNode>> layers, Strip a, Strip b)
        {
            int first = Mathf.Max(a.StartRank, b.StartRank);
            int last = Mathf.Min(a.EndRank, b.EndRank);
            if (last < first) return false;

            bool swapped = false;
            int r = first;
            while (r <= last)
            {
                var ma = a.MemberAt(r);
                var mb = b.MemberAt(r);
                int diff = mb.VOrder - ma.VOrder;
                if (ReferenceEquals(ma, mb) || (diff != 1 && diff != -1)) { r++; continue; }

                // 같은 방향으로 인접이 유지되는 연속 구간 [r, end)
                int end = r + 1;
                while (end <= last)
                {
                    var na = a.MemberAt(end);
                    var nb = b.MemberAt(end);
                    if (ReferenceEquals(na, nb) || nb.VOrder - na.VOrder != diff) break;
                    end++;
                }

                var upper = diff > 0 ? a : b;
                var lower = diff > 0 ? b : a;
                if (upper.DestY > lower.DestY + 0.5f) // 역전: 위 스트립이 더 아래 행으로 가려 함
                {
                    int beforeCross = BoundaryCrossings(layers, r, end - 1);
                    int beforeBad = InfeasibleRuns(layers, r, end - 1);
                    SwapRun(layers, a, b, r, end - 1);
                    // 수용량 없는 통로로 들어가면 겹침이 남음
                    if (BoundaryCrossings(layers, r, end - 1) > beforeCross
                        || InfeasibleRuns(layers, r, end - 1) > beforeBad)
                    {
                        SwapRun(layers, a, b, r, end - 1); // 되돌림
                    }
                    else
                    {
                        swapped = true;
                    }
                }
                r = end;
            }
            return swapped;
        }

        /// <summary>위아래가 실노드로 막혀 완화 간격으로도 안 들어가는 더미 런 수</summary>
        private int InfeasibleRuns(List<List<ResearchNode>> layers, int s, int e)
        {
            int bad = 0;
            for (int r = Mathf.Max(0, s); r <= Mathf.Min(layers.Count - 1, e); r++)
            {
                var layer = layers[r];
                int i = 0;
                while (i < layer.Count)
                {
                    if (!layer[i].IsDummy) { i++; continue; }
                    int start = i;
                    while (i < layer.Count && layer[i].IsDummy) i++;

                    var above = start > 0 ? layer[start - 1] : null;
                    var below = i < layer.Count ? layer[i] : null;
                    if (above == null || below == null) continue; // 한쪽이 열려 있으면 언제나 수용 가능

                    float need = SeparationOf(above, layer[start]) - RelaxOf(above, layer[start]);
                    for (int j = start + 1; j < i; j++)
                    {
                        need += SeparationOf(layer[j - 1], layer[j]) - RelaxOf(layer[j - 1], layer[j]);
                    }
                    need += SeparationOf(layer[i - 1], below) - RelaxOf(layer[i - 1], below);
                    if (above.Position.y + need > below.Position.y + 0.5f) bad++;
                }
            }
            return bad;
        }

        /// <summary>[s..e] 바깥 경계 포함</summary>
        private int BoundaryCrossings(List<List<ResearchNode>> layers, int s, int e)
        {
            int from = Mathf.Max(0, s - 1);
            int to = Mathf.Min(layers.Count - 2, e);
            int total = 0;
            for (int r = from; r <= to; r++)
            {
                total += CountCrossings(layers[r], layers[r + 1]);
            }
            return total;
        }

        private static void SwapRun(List<List<ResearchNode>> layers, Strip a, Strip b, int s, int e)
        {
            for (int r = s; r <= e; r++)
            {
                var ma = a.MemberAt(r);
                var mb = b.MemberAt(r);
                var layer = layers[r];
                int ia = ma.VOrder, ib = mb.VOrder;
                layer[ia] = mb;
                layer[ib] = ma;
                ma.VOrder = ib;
                mb.VOrder = ia;
            }
        }

        private int CountSeparationViolations(List<List<ResearchNode>> layers)
        {
            int count = 0;
            foreach (var layer in layers)
            {
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < SeparationOf(layer[i], layer[i + 1]) - 0.5f) count++;
                }
            }
            return count;
        }

        /// <summary>bend = 더미 Y 변화량의 부호 전환</summary>
        private int CountAllChainBends(List<List<ResearchNode>> chains)
        {
            float eps = Constraints.MetricsBendEpsilon;
            int total = 0;
            foreach (var chain in chains)
            {
                var src = Ups(chain[0]).FirstOrDefault();
                if (src == null) continue;
                float prev = src.Position.y;
                int prevSign = 0;
                foreach (var d in chain)
                {
                    float dy = d.Position.y - prev;
                    prev = d.Position.y;
                    if (Mathf.Abs(dy) < eps) continue;
                    int sign = dy > 0 ? 1 : -1;
                    if (prevSign != 0 && sign != prevSign) total++;
                    prevSign = sign;
                }
            }
            return total;
        }

        /// <summary>양자화한 더미 Y의 FNV-1a 해시, ShapeChains 사이클 감지용</summary>
        private static int HashDummyPositions(List<List<ResearchNode>> chains)
        {
            float q = Constraints.LayoutChainHashQuantum;
            uint hash = 2166136261u;
            foreach (var chain in chains)
            {
                foreach (var d in chain)
                {
                    int qi = (int)Mathf.Round(d.Position.y / q);
                    hash = (hash ^ (uint)qi) * 16777619u;
                }
            }
            return (int)hash;
        }

        private static float[] SnapshotDummyPositions(List<List<ResearchNode>> chains)
        {
            int total = 0;
            foreach (var chain in chains) total += chain.Count;
            var snap = new float[total];
            int idx = 0;
            foreach (var chain in chains)
                foreach (var d in chain)
                    snap[idx++] = d.Position.y;
            return snap;
        }

        private static void RestoreDummySnapshot(List<List<ResearchNode>> chains, float[] snap)
        {
            int idx = 0;
            foreach (var chain in chains)
                foreach (var d in chain)
                {
                    float y = snap[idx++];
                    if (y != d.Position.y)
                        d.Position = new Vector2(d.Position.x, y);
                }
        }

        /// <summary>스냅 후 더미 체인을 이동 창 안에서 최소 꺾임으로 맞춘다. 남은 분리 위반은 ResolveDummyRuns 몫</summary>
        private void ShapeChains(List<List<ResearchNode>> layers)
        {
            var chains = CollectChains();
            if (chains.Count == 0)
            {
                lastNonConvergedChains = 0;
                return;
            }

            int bestBend = CountAllChainBends(chains);
            float[] bestSnap = SnapshotDummyPositions(chains);
            var seenHashes = new HashSet<int>();
            seenHashes.Add(HashDummyPositions(chains));

            bool cycleDetected = false;
            int finalMovedCount = 0;

            for (int pass = 0; pass < Constraints.LayoutChainShapeMaxPasses; pass++)
            {
                bool forward = pass % 2 == 0;
                int movedCount = 0;
                for (int i = 0; i < chains.Count; i++)
                {
                    var chain = chains[forward ? i : chains.Count - 1 - i];
                    float delta = FitChain(chain, layers);
                    if (delta > Constraints.LayoutChainConvergeEps) movedCount++;
                }
                finalMovedCount = movedCount;

                // 동률이면 최초 상태 유지
                int curBend = CountAllChainBends(chains);
                if (curBend < bestBend)
                {
                    bestBend = curBend;
                    bestSnap = SnapshotDummyPositions(chains);
                }

                if (movedCount == 0) break;

                int h = HashDummyPositions(chains);
                if (!seenHashes.Add(h))
                {
                    cycleDetected = true;
                    break;
                }
            }

            // 사이클이나 패스 소진 때만 복원. 정상 수렴은 고정점이라 복원하면 오히려 회귀함
            bool abnormalExit = cycleDetected || finalMovedCount > 0;
            if (abnormalExit) RestoreDummySnapshot(chains, bestSnap);

            lastNonConvergedChains = (finalMovedCount == 0 && !cycleDetected) ? 0 : finalMovedCount;
        }

        /// <summary>움직인 멤버의 최대 |dY| 반환</summary>
        private float FitChain(List<ResearchNode> chain, List<List<ResearchNode>> layers)
        {
            int n = chain.Count;
            var src = Ups(chain[0]).FirstOrDefault();
            float dstY = TrunkDestY(chain);
            if (src == null || float.IsNaN(dstY)) return 0f; // 방어용
            float srcY = src.Position.y;

            // 이동 창 = 같은 레이어 위/아래 이웃과의 분리 제약
            var lo = new float[n];
            var hi = new float[n];
            for (int j = 0; j < n; j++)
            {
                var d = chain[j];
                var layer = layers[d.Rank];
                int idx = d.VOrder; // SyncVOrder로 레이어 인덱스와 일치
                lo[j] = idx > 0
                    ? layer[idx - 1].Position.y + SeparationOf(layer[idx - 1], d)
                    : float.NegativeInfinity;
                hi[j] = idx + 1 < layer.Count
                    ? layer[idx + 1].Position.y - SeparationOf(d, layer[idx + 1])
                    : float.PositiveInfinity;
            }

            // 뒤쪽은 도착 행이 창에 들어가는 만큼 도착 행에, 그 앞은 출발 행이 들어가는 만큼 출발 행에 맞춤
            int sufFrom = n;
            for (int j = n - 1; j >= 0 && lo[j] <= dstY && dstY <= hi[j]; j--) sufFrom = j;
            int preTo = 0;
            for (int j = 0; j < sufFrom && lo[j] <= srcY && srcY <= hi[j]; j++) preTo = j + 1;

            float prev = srcY;
            float maxDelta = 0f;
            for (int j = 0; j < n; j++)
            {
                float target;
                if (j >= sufFrom) target = dstY;
                else if (j < preTo) target = srcY;
                else target = prev;

                float y = lo[j] <= hi[j] ? Mathf.Clamp(target, lo[j], hi[j]) : (lo[j] + hi[j]) / 2f;
                prev = y;
                float delta = Mathf.Abs(y - chain[j].Position.y);
                if (delta > maxDelta) maxDelta = delta;
                if (y != chain[j].Position.y) chain[j].Position = new Vector2(chain[j].Position.x, y);
            }
            return maxDelta;
        }

        /// <summary>연속 더미 구간을 위아래 실노드 사이에 클램프. 안 되면 완화 간격으로 재시도</summary>
        private void ResolveDummyRuns(List<List<ResearchNode>> layers)
        {
            foreach (var layer in layers)
            {
                int i = 0;
                while (i < layer.Count)
                {
                    if (!layer[i].IsDummy) { i++; continue; }
                    int start = i;
                    while (i < layer.Count && layer[i].IsDummy) i++;

                    var above = start > 0 ? layer[start - 1] : null;
                    var below = i < layer.Count ? layer[i] : null;
                    if (!TryClampDummyRun(layer, start, i - start, above, below, relaxed: false))
                    {
                        TryClampDummyRun(layer, start, i - start, above, below, relaxed: true);
                    }
                }
            }
        }

        /// <summary>현재 위치를 최대한 유지하며 위반만 해소. 창이 역전되면 변경 없이 false</summary>
        private static bool TryClampDummyRun(List<ResearchNode> layer, int start, int count,
            ResearchNode above, ResearchNode below, bool relaxed)
        {
            float Sep(ResearchNode a, ResearchNode b)
                => SeparationOf(a, b) - (relaxed ? RelaxOf(a, b) : 0f);

            // 경계 실노드는 고정
            var lo = new float[count];
            var hi = new float[count];
            for (int j = 0; j < count; j++)
            {
                if (j == 0)
                    lo[j] = above != null ? above.Position.y + Sep(above, layer[start]) : float.NegativeInfinity;
                else
                    lo[j] = lo[j - 1] + Sep(layer[start + j - 1], layer[start + j]);
            }
            for (int j = count - 1; j >= 0; j--)
            {
                if (j == count - 1)
                    hi[j] = below != null ? below.Position.y - Sep(layer[start + j], below) : float.PositiveInfinity;
                else
                    hi[j] = hi[j + 1] - Sep(layer[start + j], layer[start + j + 1]);
            }
            for (int j = 0; j < count; j++)
            {
                if (lo[j] > hi[j]) return false;
            }

            float prevPlaced = 0f;
            for (int j = 0; j < count; j++)
            {
                var d = layer[start + j];
                float floorY = lo[j];
                if (j > 0)
                {
                    float sep = Sep(layer[start + j - 1], d);
                    if (prevPlaced + sep > floorY) floorY = prevPlaced + sep;
                }
                float yNew = Mathf.Clamp(d.Position.y, floorY, hi[j]);
                if (yNew != d.Position.y)
                {
                    d.Position = new Vector2(d.Position.x, yNew);
                }
                prevPlaced = yNew;
            }
            return true;
        }

        /// <summary>고립 노드를 같은 시대 밴드 컬럼들의 바닥에 분산. 자리가 없으면 홈 컬럼 바닥에 쌓음</summary>
        private void PlaceIsolated(List<List<ResearchNode>> layers, float[] rankX,
            Dictionary<TechLevel, (int lo, int hi)> bands)
        {
            if (isolatedNodesByRank.Count == 0) return;
            float gridY = GridY;

            // 잔여 예산은 픽셀 범위 기준, 도착 1개당 gridY로 근사 차감
            var remaining = new float[layers.Count];
            for (int r = 0; r < layers.Count; r++)
            {
                float extent = 0f;
                if (layers[r].Count > 0)
                {
                    float top = float.MaxValue, bottom = float.MinValue;
                    foreach (var n in layers[r])
                    {
                        float half = NodeHeightOf(n) / 2f;
                        if (n.Position.y - half < top) top = n.Position.y - half;
                        if (n.Position.y + half > bottom) bottom = n.Position.y + half;
                    }
                    extent = bottom - top;
                }
                remaining[r] = columnBudget - extent;
            }

            // 1) 컬럼 배정. 좌표는 2)에서 일괄 결정
            var arrivals = new Dictionary<int, List<ResearchNode>>();
            foreach (int home in isolatedNodesByRank.Keys.OrderBy(r => r))
            {
                foreach (var node in isolatedNodesByRank[home])
                {
                    // 프록시는 effectiveTechLevel에 없음
                    var era = effectiveTechLevel.TryGetValue(node, out var etl) ? etl : node.TechLevel;
                    int target = -1;
                    if (bands.TryGetValue(era, out var band))
                    {
                        // 잔여 예산이 가장 큰 컬럼에 두고 매번 깎아 고르게 분산. 동률이면 home에 가까운 쪽
                        float bestRem = float.NegativeInfinity;
                        for (int r = band.lo; r <= band.hi; r++)
                        {
                            if (!NoHigherEraOccupant(layers[r], era)) continue;
                            float rem = remaining[r];
                            if (rem > bestRem
                                || (rem == bestRem && (target < 0 || System.Math.Abs(r - home) < System.Math.Abs(target - home))))
                            {
                                bestRem = rem;
                                target = r;
                            }
                        }
                    }
                    if (target < 0) target = home; // 밴드가 없거나 다 막히면 홈 바닥에 쌓음

                    remaining[target] -= gridY;
                    if (!arrivals.TryGetValue(target, out var list))
                    {
                        list = new List<ResearchNode>();
                        arrivals[target] = list;
                    }
                    list.Add(node);
                }
            }

            // 2) 좌표 결정, 레이어 편입
            foreach (int rank in arrivals.Keys.OrderBy(r => r))
            {
                var nodes = arrivals[rank];
                var layer = layers[rank];

                float nextY;
                if (layer.Count > 0)
                {
                    // 더미 스냅 오차로 마지막 VOrder가 최하단이 아닐 수 있음
                    float bottom = float.MinValue;
                    foreach (var nn in layer)
                        if (nn.Position.y > bottom) bottom = nn.Position.y;
                    nextY = Mathf.Ceil(bottom / gridY) * gridY + gridY;
                }
                else
                {
                    nextY = Mathf.Floor(-(nodes.Count - 1) / 2f + 0.5f) * gridY;
                }

                foreach (var n in nodes)
                {
                    n.Rank = rank; // 홈과 다를 수 있으니 레이어 인덱스에 맞춤
                    n.Position = new Vector2(rankX[rank], nextY);
                    layer.Add(n);
                    nextY += gridY;
                }
            }
            SyncVOrder(layers);
        }

        /// <summary>era보다 높은 시대 점유자(주로 데이터 오류 프록시)가 있으면 false</summary>
        private bool NoHigherEraOccupant(List<ResearchNode> layer, TechLevel era)
        {
            foreach (var n in layer)
            {
                if (n.IsDummy) continue;
                TechLevel tl;
                if (n.IsProxy) tl = n.TechLevel;
                else if (effectiveTechLevel.TryGetValue(n, out var etl)) tl = etl;
                else tl = n.TechLevel;
                if (tl > era) return false;
            }
            return true;
        }

    }
}
