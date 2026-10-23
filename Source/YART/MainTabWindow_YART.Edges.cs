using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Rendering;

namespace YART
{
    public partial class MainTabWindow_YART
    {
        private void DrawConnection(ResearchNode from, ResearchNode to, Vector2 offset, float zoom)
        {
            // 상대별 포트를 써서 고차수 노드의 엣지가 한 점에서 겹쳐 나가지 않게 함
            Vector2 startWorld = from.GetOutputAnchor(to);
            Vector2 endWorld = to.GetInputAnchor(from);

            if (!visibleRect.Overlaps(SegmentCullRect(startWorld, endWorld))) return;

            Vector2 startScreen = (startWorld * zoom) + offset;
            Vector2 endScreen = (endWorld * zoom) + offset;

            Color startColor = GetEdgeColor(from);
            Color endColor = GetEdgeColor(to);

            // 강조는 흰색 블렌드, 비강조는 알파(렌더러가 곡선 전체에 적용)
            float alpha = 1f;
            int highlight = GetEdgeHighlightState(from, to);
            if (highlight > 0)
            {
                float blend = EdgeHighlightBlend();
                startColor = Color.Lerp(startColor, Constraints.EdgeHighlight, blend);
                endColor = Color.Lerp(endColor, Constraints.EdgeHighlight, blend);
            }
            else if (highlight < 0)
            {
                alpha = EdgeUnfocusAlpha();
            }

            float width = Mathf.Max(Constraints.EdgeLineMinWidth, Constraints.EdgeLineWidth * zoom);

            // 펄스는 DrawActivePulses에서 체인 단위로 따로 그림
            EdgeRenderer.DrawGradientBezier(startScreen, endScreen, startColor, endColor, width, zoom, alpha, GetEdgeCrossover(from, to));
        }

        private static Rect SegmentCullRect(Vector2 startWorld, Vector2 endWorld)
        {
            const float margin = 150f;
            float minX = Mathf.Min(startWorld.x, endWorld.x);
            float maxX = Mathf.Max(startWorld.x, endWorld.x);
            float minY = Mathf.Min(startWorld.y, endWorld.y);
            float maxY = Mathf.Max(startWorld.y, endWorld.y);
            return new Rect(minX - margin, minY - margin, (maxX - minX) + margin * 2, (maxY - minY) + margin * 2);
        }

        /// <summary>포트 순번으로 수직 이동 X를 분산. 양 끝 팬 크기로 가중해 큰 팬 쪽이 우선</summary>
        private static float GetEdgeCrossover(ResearchNode from, ResearchNode to)
        {
            int fanOutWeight = from.Children.Count - 1;
            int fanInWeight = to.Prerequisites.Count - 1;
            if (fanOutWeight + fanInWeight <= 0) return 0.5f;

            float ratio = (from.GetOutputPortRatio(to) * fanOutWeight + to.GetInputPortRatio(from) * fanInWeight)
                          / (fanOutWeight + fanInWeight);
            return Mathf.Lerp(Constraints.EdgeCrossoverMin, Constraints.EdgeCrossoverMax, ratio);
        }

        private static readonly List<Vector2> chainPointsBuffer = new List<Vector2>(160);
        private static readonly List<ResearchNode> activeTargetsBuffer = new List<ResearchNode>(8);

        /// <summary>활성 경로(완료된 선행 -> 연구 중)마다 펄스 하나</summary>
        private void DrawActivePulses(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            foreach (var edge in graph.Edges)
            {
                if (edge.From.IsDummy) continue; // 체인의 머리 세그먼트에서만 시작
                if (edge.From.State != ResearchNodeState.Completed) continue;

                CollectActiveTargets(edge.To, activeTargetsBuffer);
                if (activeTargetsBuffer.Count == 0) continue;

                foreach (var target in activeTargetsBuffer)
                {
                    if (PulseSuppressed(edge.From, target)) continue;
                    if (!BuildPulsePolyline(edge.From, edge.To, target, offset, zoom)) continue;

                    Color pulseColor = Color.Lerp(GetEdgeColor(target), Color.white, 0.55f);
                    pulseColor.a = 1f;
                    EdgeRenderer.DrawChainPulse(chainPointsBuffer, pulseColor, zoom);
                }
            }
        }

        /// <summary>디밍 중엔 관련 (소스, 타깃) 쌍에만 펄스. 호버 디밍이 꺼져 있으면(기본) 억제 안 함</summary>
        private bool PulseSuppressed(ResearchNode source, ResearchNode target)
        {
            if (hoveredNode != null && YARTMod.Settings.focusHighlightDimming)
            {
                return !focusedNodes.Contains(source) || !focusedNodes.Contains(target);
            }
            if (hoveredNode == null && matchedDefs.Count > 0)
            {
                return !matchedDefs.Contains(source.Def) || !matchedDefs.Contains(target.Def);
            }
            return false;
        }

        /// <summary>타깃까지의 경로를 chainPointsBuffer에 스크린 좌표로 채움. 구간마다 본선과 같은 곡선</summary>
        private bool BuildPulsePolyline(ResearchNode source, ResearchNode firstHop, ResearchNode target, Vector2 offset, float zoom)
        {
            chainPointsBuffer.Clear();
            var prevNode = source;
            Vector2 prevOut = source.GetOutputAnchor(firstHop);
            chainPointsBuffer.Add((prevOut * zoom) + offset);

            var cur = firstHop;
            while (cur != null)
            {
                Vector2 inAnchor = cur.GetInputAnchor(prevNode);
                AppendCurve(chainPointsBuffer, (prevOut * zoom) + offset, (inAnchor * zoom) + offset,
                    zoom, GetEdgeCrossover(prevNode, cur));
                if (!cur.IsDummy) return true;

                // 더미 내부 직선 통로
                chainPointsBuffer.Add((cur.OutputAnchor * zoom) + offset);
                prevNode = cur;
                prevOut = cur.OutputAnchor;
                cur = NextHopToward(cur, target);
            }
            return false; // 방어용. 타깃은 같은 트렁크에서 모았으므로 정상이면 도달함
        }

        /// <summary>첫 더미 자식으로 전진해 레이아웃의 NextTrunkDown과 같은 트렁크를 걷는다</summary>
        private static void CollectActiveTargets(ResearchNode n, List<ResearchNode> result)
        {
            result.Clear();
            while (n != null)
            {
                if (!n.IsDummy)
                {
                    if (n.State == ResearchNodeState.InProgress) result.Add(n);
                    return;
                }
                ResearchNode nextTrunk = null;
                var children = n.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c.IsDummy) { if (nextTrunk == null) nextTrunk = c; }
                    else if (c.State == ResearchNodeState.InProgress) result.Add(c);
                }
                n = nextTrunk;
            }
        }

        /// <summary>자식에 타깃이 있으면 타깃, 없으면 다음 트렁크 더미</summary>
        private static ResearchNode NextHopToward(ResearchNode dummy, ResearchNode target)
        {
            ResearchNode nextTrunk = null;
            var children = dummy.Children;
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (ReferenceEquals(c, target)) return c;
                if (c.IsDummy && nextTrunk == null) nextTrunk = c; // NextTrunkDown과 같은 선택(첫 더미)
            }
            return nextTrunk;
        }

        /// <summary>첫 점은 직전 끝점과 같아서 생략</summary>
        private static void AppendCurve(List<Vector2> pts, Vector2 start, Vector2 end, float zoom, float crossover)
        {
            var curve = EdgeRenderer.GetCurvePoints(start, end, zoom, crossover);
            if (curve == null)
            {
                pts.Add(end); // 직선 LOD 구간
                return;
            }
            for (int i = 1; i < curve.Length; i++)
            {
                pts.Add(curve[i]);
            }
        }

        /// <summary>더미는 in-degree 1이라 위로는 단일 경로</summary>
        private static ResearchNode ResolveRealSource(ResearchNode n)
        {
            while (n != null && n.IsDummy)
            {
                n = n.Prerequisites.Count > 0 ? n.Prerequisites[0] : null;
            }
            return n;
        }

        /// <summary>엣지마다 매 프레임 호출됨. 호출자는 델리게이트를 캐시할 것(메서드 그룹 변환도 매번 할당)</summary>
        private static bool AnyRealTarget(ResearchNode n, Predicate<ResearchNode> pred)
        {
            while (n != null)
            {
                if (!n.IsDummy) return pred(n);
                ResearchNode nextTrunk = null;
                var children = n.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    if (c.IsDummy) { if (nextTrunk == null) nextTrunk = c; } // NextTrunkDown과 같은 선택(첫 더미)
                    else if (pred(c)) return true;
                }
                n = nextTrunk;
            }
            return false;
        }

        // AnyRealTarget용 캐시 (프레임당 할당 방지)
        private Predicate<ResearchNode> matchedDefsPred;

        private float EdgeHighlightBlend() => hoveredNode != null ? focusAmount : 1f;

        private float EdgeUnfocusAlpha()
        {
            if (hoveredNode != null)
            {
                if (!YARTMod.Settings.focusHighlightDimming) return 1f; // 기본값은 경로만 강조하고 디밍 안 함
                return Mathf.Lerp(1f, Constraints.UnfocusedEdgeOpacity, focusAmount);
            }
            return Constraints.UnfocusedEdgeOpacity;
        }

        /// <summary>1 = 강조, -1 = 디밍, 0 = 모드 꺼짐. 검색 매칭은 실노드에만 있어 더미 끝점은 실노드까지 따라감</summary>
        private int GetEdgeHighlightState(ResearchNode from, ResearchNode to)
        {
            if (hoveredNode != null)
            {
                // focusedNodes는 경로상 더미도 포함하므로 직접 판정
                return focusedNodes.Contains(from) && focusedNodes.Contains(to) ? 1 : -1;
            }
            if (matchedDefs.Count > 0)
            {
                if (matchedDefsPred == null) matchedDefsPred = n => n != null && matchedDefs.Contains(n.Def);
                var src = ResolveRealSource(from);
                return src != null && matchedDefs.Contains(src.Def) && AnyRealTarget(to, matchedDefsPred) ? 1 : -1;
            }
            return 0;
        }

        private readonly List<KeyValuePair<TechLevel, float>> eraBandsBuffer = new List<KeyValuePair<TechLevel, float>>(8);
        private void BuildEraBands(ResearchSubGraph graph)
        {
            eraBandsBuffer.Clear();
            if (graph?.TechLevelBoundaries == null) return;
            foreach (var kvp in graph.TechLevelBoundaries) eraBandsBuffer.Add(kvp);
            eraBandsBuffer.Sort((a, b) => a.Value.CompareTo(b.Value));
        }

        private TechLevel EraAtWorldX(float worldX)
        {
            if (eraBandsBuffer.Count == 0) return TechLevel.Undefined;
            TechLevel result = eraBandsBuffer[0].Key;
            for (int i = 0; i < eraBandsBuffer.Count; i++)
            {
                if (worldX >= eraBandsBuffer[i].Value) result = eraBandsBuffer[i].Key;
                else break;
            }
            return result;
        }

        private static Color TonedEra(Color era) =>
            Color.Lerp(era, Constraints.EdgeDefault, Constraints.EdgeEraDesaturate);

        private Color GetEdgeColor(ResearchNode node)
        {
            if (node.IsDummy)
            {
                var src = ResolveRealSource(node);
                if (src == null || src.State != ResearchNodeState.Completed)
                    return Constraints.EdgeDefault;

                TechLevel era = EraAtWorldX(node.Position.x);
                return era == TechLevel.Undefined ? TonedEra(src.EraAccentColor) : TonedEra(ActiveColors.Era(era));
            }

            switch (node.State)
            {
                case ResearchNodeState.Completed:
                    return TonedEra(node.EraAccentColor);
                default:
                    return Constraints.EdgeDefault;
            }
        }
    }
}
