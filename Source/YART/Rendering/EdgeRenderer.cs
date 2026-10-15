using System.Collections.Generic;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART.Rendering
{
    /// <summary>
    /// 연구 노드 사이의 엣지들을 그래디언트 베지어 곡선으로 렌더링한다
    /// </summary>
    public static class EdgeRenderer
    {
        private static readonly Dictionary<(Vector2, Vector2, int, float, float), Vector2[]> bezierCache = new Dictionary<(Vector2, Vector2, int, float, float), Vector2[]>();
        private const int MaxCacheSize = 500;
        private static int cacheAccessCount = 0;

        /// <summary>
        /// 두 점을 잇는 그래디언트 베지어 곡선을 그린다.
        /// crossover는 수직 이동이 일어나는 통로 내 위치 (0=출발 직후, 1=도착 직전, 0.5=대칭).
        /// </summary>
        public static void DrawGradientBezier(Vector2 start, Vector2 end, Color startColor, Color endColor, float width, float zoom, float alpha = 1f, float crossover = 0.5f)
        {
            float dist = Vector2.Distance(start, end);
            if (dist < 1f) return;

            startColor = GenColor.WithAlpha(startColor, startColor.a * alpha);
            endColor = GenColor.WithAlpha(endColor, endColor.a * alpha);

            if (IsStraightLod(dist, zoom))
            {
                DrawStraightRibbon(start, end, startColor, endColor, width);
                return;
            }

            var (p1, p2) = ComputeControlPoints(start, end, zoom, crossover);
            Vector2[] points = GetBezierPoints(start, p1, p2, end, CalculateSegmentCount(dist, zoom));
            EmitRibbon(points, points.Length, startColor, endColor, width);
        }

        // 초저줌이거나 아주 짧은 엣지는 곡선 대신 직선으로 (LOD)
        private static bool IsStraightLod(float dist, float zoom) => zoom < 0.3f || dist < 30f;

        /// <summary>
        /// 베지어 제어점 계산. 고팬아웃 노드의 엣지들이 통로 내 서로 다른 X에서 교차하도록
        /// crossover로 핸들 길이를 분산한다 (staggered crossover).
        ///
        /// 불변식 — X 단조성(start.x ≤ p1.x ≤ p2.x ≤ end.x, 역방향이면 반전):
        /// 두 핸들 합이 cap(=0.9·xDist)을 넘으면 제어 다각형이 역전되므로 비례 축소한다.
        /// 축소 후 minOffset 미달은 허용 — X 단조성이 최소 핸들 길이보다 우선한다.
        /// </summary>
        private static (Vector2 p1, Vector2 p2) ComputeControlPoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dx = end.x - start.x;
            float xDist = Mathf.Abs(dx);

            // xDist≈0 방어: 수직/제자리 엣지는 제어점을 끝점에 붙인다 (NaN/0분할 없음)
            if (xDist < 1e-3f)
            {
                return (start, end);
            }

            float minOffset = 30f * zoom;
            float cap = 0.9f * xDist;

            float startOffset = Mathf.Max(cap * crossover, minOffset);
            float endOffset   = Mathf.Max(cap * (1f - crossover), minOffset);

            float sum = startOffset + endOffset;
            if (sum > cap)
            {
                float scale = cap / sum;
                startOffset *= scale;
                endOffset   *= scale;
            }

            // 부호 있는 방향으로 핸들 적용 (역방향 엣지도 정확히 처리)
            float sign = dx > 0f ? 1f : -1f;
            return (start + new Vector2(startOffset * sign, 0f), end - new Vector2(endOffset * sign, 0f));
        }

        /// <summary>
        /// 두 점을 잇는 단일 세그먼트 리본
        /// </summary>
        public static void DrawStraightRibbon(Vector2 start, Vector2 end, Color startColor, Color endColor, float width)
        {
            straightBuffer[0] = start;
            straightBuffer[1] = end;
            EmitRibbon(straightBuffer, 2, startColor, endColor, width);
        }

        private static readonly Vector2[] straightBuffer = new Vector2[2];

        private static void EmitRibbon(Vector2[] pts, int count, Color startColor, Color endColor, float width)
        {
            if (count < 2) return;
            float half = width * 0.5f;

            var uvBottom = new Vector2(0.5f, 0f);
            var uvTop = new Vector2(0.5f, 1f);

            Vector2 prevDir = (pts[1] - pts[0]).normalized;
            Vector2 prevOff = new Vector2(-prevDir.y, prevDir.x) * half;
            Vector2 prevPt = pts[0];
            Color prevColor = startColor;

            for (int i = 1; i < count; i++)
            {
                Vector2 dir = (pts[i] - pts[i - 1]).normalized;
                Vector2 normal = new Vector2(-dir.y, dir.x);

                Vector2 off;
                if (i < count - 1)
                {
                    // 접합점: 이웃 접선 평균의 법선으로 마이터. 급각에서 길이 폭주 방지를 위해 2배 클램프
                    Vector2 m = (dir + (pts[i + 1] - pts[i]).normalized).normalized;
                    Vector2 mN = new Vector2(-m.y, m.x);
                    float dot = Vector2.Dot(mN, normal);
                    off = mN * (half * (dot > 0.5f ? 1f / dot : 2f));
                }
                else
                {
                    off = normal * half;
                }

                float t = i / (float)(count - 1);
                Color color = Color.Lerp(startColor, endColor, t);

                GLTexturedQuadBatcher.QueueFreeQuad(Assets.EdgeLine,
                    prevPt - prevOff, prevPt + prevOff, pts[i] + off, pts[i] - off,
                    uvBottom, uvTop, uvTop, uvBottom,
                    prevColor, prevColor, color, color);

                prevPt = pts[i];
                prevOff = off;
                prevColor = color;
            }
        }

        /// <summary>
        /// 엣지 본선과 동일한 곡선의 폴리라인 점들을 반환. (체인 펄스 경로 구성용)
        /// 직선 LOD 구간(초저줌/짧은 거리)이면 null — 호출자가 끝점만 이으면 본선과 일치한다.
        /// </summary>
        public static Vector2[] GetCurvePoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dist = Vector2.Distance(start, end);
            if (IsStraightLod(dist, zoom)) return null;

            var (p1, p2) = ComputeControlPoints(start, end, zoom, crossover);
            return GetBezierPoints(start, p1, p2, end, CalculateSegmentCount(dist, zoom));
        }

        private static Vector2[] tailBuffer = new Vector2[256];
        private static readonly List<float> chainArcLengths = new List<float>(160);

        /// <summary>
        /// 실제 노드 사이의 전체 경로(더미 체인 포함) 폴리라인 위를 흐르는 단일 에너지 펄스.
        /// 월드 길이 기준 등속으로 이동하고, 도착 후 PulseArriveDelay만큼 쉬었다가 다시 출발한다.
        /// </summary>
        /// <param name="pts">스크린 좌표 폴리라인 (체인 전체)</param>
        public static void DrawChainPulse(List<Vector2> pts, Color color, float zoom, float alpha = 1f)
        {
            int n = pts.Count;
            if (n < 2 || alpha <= 0.01f) return;

            float total = FillArcLengths(pts);
            if (total < 8f) return;

            // 등속 주행 + 도착 딜레이. 월드 길이는 줌과 무관해 줌 변경 시 위상이 튀지 않는다.
            float worldLen = total / Mathf.Max(zoom, 0.01f);
            float travelTime = worldLen / Constraints.PulseSpeed;
            float cycleTime = travelTime + Constraints.PulseArriveDelay;
            float tCycle = Time.realtimeSinceStartup % cycleTime;
            if (tCycle >= travelTime) return; // 도착 후 대기 구간

            float headArc = (tCycle / travelTime) * total;
            float tailArc = Mathf.Max(0f, headArc - Constraints.PulseTailLength * total);

            // 꼬리 리본 (꼬리→머리로 갈수록 진하게). 균일 샘플링은 곡선 구간을 현(직선)으로
            // 잇는 아티팩트를 만들므로, 폴리라인의 실제 정점들을 그대로 따라간다.
            Color dim = new Color(color.r, color.g, color.b, 0f);
            Color headColor = color;
            headColor.a *= alpha;

            if (tailBuffer.Length < n + 2) tailBuffer = new Vector2[Mathf.NextPowerOfTwo(n + 2)];
            int m = 0;
            tailBuffer[m++] = PointAtArc(pts, chainArcLengths, tailArc);
            for (int i = 0; i < n; i++)
            {
                float a = chainArcLengths[i];
                if (a > tailArc && a < headArc) tailBuffer[m++] = pts[i];
            }
            tailBuffer[m++] = PointAtArc(pts, chainArcLengths, headArc);

            float tailWidth = Mathf.Max(Constraints.EdgeLineMinWidth, Constraints.EdgeLineWidth * zoom) * 1.5f;
            EmitRibbon(tailBuffer, m, dim, headColor, tailWidth);

            // 헤드: 글로우 스프라이트
            Vector2 head = tailBuffer[m - 1];
            float glowSize = Mathf.Max(10f, 16f * zoom);
            var glowRect = new Rect(head.x - glowSize / 2f, head.y - glowSize / 2f, glowSize, glowSize);
            Color glowColor = color;
            glowColor.a = 0.9f * alpha;
            GLTexturedQuadBatcher.QueueQuad(Assets.GlowRadial, glowRect, glowColor);
        }

        /// <summary>
        /// chainArcLengths를 pts의 누적 호 길이로 채우고 전체 길이를 반환.
        /// </summary>
        private static float FillArcLengths(List<Vector2> pts)
        {
            chainArcLengths.Clear();
            chainArcLengths.Add(0f);
            float total = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                total += Vector2.Distance(pts[i - 1], pts[i]);
                chainArcLengths.Add(total);
            }
            return total;
        }

        /// <summary>
        /// 폴리라인 위 호 길이 arc 지점의 좌표를 반환. (cum = 누적 호 길이, pts와 같은 개수)
        /// </summary>
        private static Vector2 PointAtArc(List<Vector2> pts, List<float> cum, float arc)
        {
            int idx = cum.BinarySearch(arc);
            if (idx < 0) idx = ~idx;
            if (idx <= 0) return pts[0];
            if (idx >= pts.Count) return pts[pts.Count - 1];

            float segLen = cum[idx] - cum[idx - 1];
            float f = segLen > 0.0001f ? (arc - cum[idx - 1]) / segLen : 0f;
            return Vector2.Lerp(pts[idx - 1], pts[idx], f);
        }

        public static int CalculateSegmentCount(float distance, float zoom)
        {
            float density;
            if (zoom < 0.5f)
                density = 25f;
            else if (zoom < 0.8f)
                density = 18f;
            else
                density = 12f;

            int segments = Mathf.CeilToInt(distance / density);
            return Mathf.Clamp(segments, 8, 40);
        }

        public static Vector2[] GetBezierPoints(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int segments)
        {
            Vector2 keyStart = new Vector2(Mathf.Round(p0.x), Mathf.Round(p0.y));
            Vector2 keyEnd = new Vector2(Mathf.Round(p3.x), Mathf.Round(p3.y));
            var cacheKey = (keyStart, keyEnd, segments, Mathf.Round(p1.x), Mathf.Round(p2.x));

            if (bezierCache.TryGetValue(cacheKey, out Vector2[] cached))
            {
                return cached;
            }

            Vector2[] points = new Vector2[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                points[i] = CubicBezierPoint(i / (float)segments, p0, p1, p2, p3);
            }

            // 100회 미스마다 캐시가 절반 이상 찼으면 통째로 비운다 (LRU 없이 단순 축출)
            cacheAccessCount++;
            if (cacheAccessCount > 100)
            {
                if (bezierCache.Count > MaxCacheSize / 2) bezierCache.Clear();
                cacheAccessCount = 0;
            }

            if (bezierCache.Count < MaxCacheSize)
            {
                bezierCache[cacheKey] = points;
            }

            return points;
        }

        private static Vector2 CubicBezierPoint(float t, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            float u = 1 - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;

            Vector2 p = uuu * p0;
            p += 3 * uu * t * p1;
            p += 3 * u * tt * p2;
            p += ttt * p3;
            return p;
        }

        public static void ClearCache()
        {
            bezierCache.Clear();
            cacheAccessCount = 0;
        }
    }
}
