using System.Collections.Generic;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;

namespace YART.Rendering
{
    public static class EdgeRenderer
    {
        private static readonly Dictionary<(Vector2, Vector2, int, float, float), Vector2[]> bezierCache = new Dictionary<(Vector2, Vector2, int, float, float), Vector2[]>();
        private const int MaxCacheSize = 500;
        private static int cacheAccessCount = 0;

        /// <summary>crossover는 수직 이동 위치 (0 출발 직후, 1 도착 직전, 0.5 대칭)</summary>
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

        private static bool IsStraightLod(float dist, float zoom) => zoom < 0.3f || dist < 30f;

        /// <summary>핸들 합이 cap을 넘으면 제어점 X 순서가 뒤집히므로 비례 축소. X 단조성이 minOffset보다 우선</summary>
        private static (Vector2 p1, Vector2 p2) ComputeControlPoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dx = end.x - start.x;
            float xDist = Mathf.Abs(dx);

            // 수직/제자리 엣지는 제어점을 끝점에 붙임
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

            // 역방향 엣지도 처리하도록 부호 적용
            float sign = dx > 0f ? 1f : -1f;
            return (start + new Vector2(startOffset * sign, 0f), end - new Vector2(endOffset * sign, 0f));
        }

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
                    // 마이터 조인. 급각에서 길이가 폭주하지 않게 2배로 클램프
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

        /// <summary>직선 LOD 구간이면 null. 이때 끝점만 이으면 본선과 일치</summary>
        public static Vector2[] GetCurvePoints(Vector2 start, Vector2 end, float zoom, float crossover)
        {
            float dist = Vector2.Distance(start, end);
            if (IsStraightLod(dist, zoom)) return null;

            var (p1, p2) = ComputeControlPoints(start, end, zoom, crossover);
            return GetBezierPoints(start, p1, p2, end, CalculateSegmentCount(dist, zoom));
        }

        private static Vector2[] tailBuffer = new Vector2[256];
        private static readonly List<float> chainArcLengths = new List<float>(160);

        /// <summary>pts는 스크린 좌표. 도착 후 PulseArriveDelay만큼 쉬었다 다시 출발</summary>
        public static void DrawChainPulse(List<Vector2> pts, Color color, float zoom, float alpha = 1f)
        {
            int n = pts.Count;
            if (n < 2 || alpha <= 0.01f) return;

            float total = FillArcLengths(pts);
            if (total < 8f) return;

            // 월드 길이 기준이라 줌을 바꿔도 위상이 튀지 않음
            float worldLen = total / Mathf.Max(zoom, 0.01f);
            float travelTime = worldLen / Constraints.PulseSpeed;
            float cycleTime = travelTime + Constraints.PulseArriveDelay;
            float tCycle = Time.realtimeSinceStartup % cycleTime;
            if (tCycle >= travelTime) return; // 도착 후 대기 구간

            float headArc = (tCycle / travelTime) * total;
            float tailArc = Mathf.Max(0f, headArc - Constraints.PulseTailLength * total);

            // 균일 샘플링은 곡선을 현으로 잘라먹으므로 폴리라인 정점을 그대로 따라감
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

            Vector2 head = tailBuffer[m - 1];
            float glowSize = Mathf.Max(10f, 16f * zoom);
            var glowRect = new Rect(head.x - glowSize / 2f, head.y - glowSize / 2f, glowSize, glowSize);
            Color glowColor = color;
            glowColor.a = 0.9f * alpha;
            GLTexturedQuadBatcher.QueueQuad(Assets.GlowRadial, glowRect, glowColor);
        }

        /// <summary>chainArcLengths를 채우고 전체 길이 반환</summary>
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

            // LRU 없이 통째로 비우는 단순 축출
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
