using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Data;
using YART.Utils;
using YART.Rendering;
using YART.Compat;

namespace YART
{
    // 노드 렌더링과 외부 후행 팝업
    public partial class MainTabWindow_YART
    {
        private const int RichCardStripMax = 5;

        /// <summary>상태/해금/비용은 항상 실노드 기준으로 읽어야 함</summary>
        private static ResearchNode RealNodeOf(ResearchNode node) => node.IsProxy ? node.OriginalNode : node;

        private float GetUnfocusMultiplier(ResearchNode node)
        {
            if (hoveredNode != null)
            {
                if (focusedNodes.Contains(node)) return 1f;
                if (!YARTMod.Settings.focusHighlightDimming) return 1f; // 기본: 디밍 없이 경로만 강조
                return Mathf.Lerp(1f, Constraints.UnfocusedNodeOpacity, focusAmount);
            }
            if (matchedDefs.Count > 0 && !matchedDefs.Contains(node.Def))
            {
                return Constraints.UnfocusedNodeOpacity;
            }
            return 1f;
        }

        private float FocusHighlightAmount(ResearchNode node)
        {
            if (hoveredNode == null || node == hoveredNode) return 0f;
            return focusedNodes.Contains(node) ? focusAmount : 0f;
        }

        private static float GetRichCardLerp(float zoom)
        {
            return Mathf.InverseLerp(Constraints.RichCardZoomStart, Constraints.RichCardZoomFull, zoom);
        }

        private static Rect RepIconRect(Rect nodeRectScreen, float zoom)
        {
            float sz = (Constraints.NodeSize.y - 8f) * zoom; // 약 42*zoom, 노드 높이를 거의 채움
            return new Rect(nodeRectScreen.x + 4f * zoom, nodeRectScreen.center.y - sz / 2f, sz, sz);
        }

        private static float RepInset(float zoom) => (Constraints.NodeSize.y + 2f) * zoom;

        private static void GetRichCardIconMetrics(Rect nodeRectScreen, float zoom, bool hasRep,
            out float iconSize, out float iconGap, out float iconX, out float iconY)
        {
            iconSize = 17f * zoom;
            iconGap = 4f * zoom;
            iconY = nodeRectScreen.y + 26f * zoom;
            iconX = nodeRectScreen.x + 6f * zoom + (hasRep ? RepInset(zoom) : 0f);
        }

        private static string UnlockTipFor(Def d)
        {
            string tip = d.LabelCap;
            if (!d.description.NullOrEmpty()) tip += "\n" + d.description;
            return tip;
        }

        private static string OverflowUnlockTip(List<Def> unlocked, int startIndex)
        {
            var sb = new System.Text.StringBuilder();
            for (int j = startIndex; j < unlocked.Count; j++)
            {
                if (j > startIndex) sb.Append('\n');
                sb.Append(unlocked[j].LabelCap);
            }
            return sb.ToString();
        }

        private static void DrawTinyLabel(Rect rect, string text, Color color, TextAnchor anchor)
        {
            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(anchor))
            using (Temporary.Color(color))
            {
                Widgets.Label(rect, text);
            }
        }

        private void DrawNodeBackground(ResearchNode node, Vector2 offset, float zoom)
        {
            if (!visibleRect.Overlaps(node.Rect)) return;

            // 더미는 엣지의 중간 구간이라 연결 리본만 그림
            if (node.IsDummy)
            {
                DrawDummyConnector(node, offset, zoom);
                return;
            }

            if (zoom < 0.2f) return; // 저줌 LOD

            Rect nodeRectScreen = GetNodeScreenRect(node, offset, zoom);
            var state = node.State;
            Color eraColor = node.EraAccentColor;
            Color borderColor = eraColor;
            ResolveNodeSurface(state, eraColor, out Color bgColor, out float nodeAlpha, out float borderAlpha);

            // 호버 디밍은 설정에 따라(기본 off) 페이드, 검색 디밍은 항상 즉시
            float unfocusMul = GetUnfocusMultiplier(node);
            if (unfocusMul < 1f)
            {
                nodeAlpha *= unfocusMul;
                borderAlpha *= unfocusMul;
            }

            float corner = Constraints.NodeCornerRadius * Mathf.Max(0.7f, zoom);
            bool isHovered = !IsMouseOverBlockingElement() && nodeRectScreen.Contains(Event.current.mousePosition);

            if (isHovered)
            {
                var tipNode = node;
                bool tipShowTab = CurrentKey.IsPreset;
                TooltipHandler.TipRegion(nodeRectScreen,
                    new TipSignal(() => BuildNodeTooltip(tipNode, tipShowTab), tipNode.Def.GetHashCode()));
            }

            // 선행/후행 강조는 디밍 off에서도 보이게 글로우/테두리에 반영
            float focusHL = FocusHighlightAmount(node);
            // 잠긴 노드는 반투명이라 글로우가 비쳐 배경색이 과하게 변함. 강조 중엔 불투명하게
            if (focusHL > 0f && state == ResearchNodeState.Locked)
            {
                nodeAlpha = Mathf.Lerp(nodeAlpha, 1f, focusHL);
                borderAlpha = Mathf.Lerp(borderAlpha, 1f, focusHL);
            }

            // 1. 글로우
            float glowIntensity = NodeGlowIntensity(state, isHovered, focusHL);
            if (glowIntensity > 0f)
            {
                float expand = 10f * Mathf.Max(0.7f, zoom);
                Rect glowRect = nodeRectScreen.ExpandedBy(expand);
                Color glowColor = GenColor.WithAlpha(borderColor, glowIntensity * nodeAlpha);
                GLTexturedQuadBatcher.QueueNineSlice(Assets.GlowRadial, glowRect, glowColor,
                    cornerScreen: expand + corner, cornerUV: 0.5f);
            }

            // 2. 패널 채움. 프록시는 옅게
            float fillAlpha = bgColor.a * nodeAlpha;
            if (node.IsProxy) fillAlpha *= Constraints.ProxyGhostFillFactor;
            GLTexturedQuadBatcher.QueueNineSlice(Assets.NodePanel, nodeRectScreen,
                bgColor.WithAlpha(fillAlpha), corner, Assets.PanelCornerUV);

            // 3. 테두리. 텍스처가 3px/12px 비율이라 두께는 corner/4, 프록시는 점선
            Color finalBorderColor = borderColor.WithAlpha(borderColor.a * borderAlpha);
            if (focusHL > 0f)
            {
                // 흰색 쪽으로 끌고 알파를 채워 잠긴 후행 노드의 옅은 테두리도 또렷하게
                Color whitened = Color.Lerp(borderColor, Constraints.EdgeHighlight, Constraints.FocusNodeBorderWhiten * focusHL);
                finalBorderColor = whitened.WithAlpha(Mathf.Max(finalBorderColor.a, focusHL));
            }
            if (node.IsProxy)
            {
                DrawDashedBorder(nodeRectScreen, finalBorderColor, zoom, corner);
            }
            else
            {
                GLTexturedQuadBatcher.QueueNineSlice(Assets.NodePanelBorder, nodeRectScreen,
                    finalBorderColor, corner, Assets.PanelCornerUV);
            }

            // 4. 진행바. 연구 중이 아니어도 진행이 있으면 표시
            if (state != ResearchNodeState.Completed && node.Progress > 0f)
            {
                DrawNodeProgressBar(node, nodeRectScreen, zoom, nodeAlpha, eraColor);
            }
        }

        /// <summary>Locked도 테두리는 시대 색(무채색은 안 보임), 알파만 낮춤</summary>
        private static void ResolveNodeSurface(ResearchNodeState state, Color eraColor,
            out Color bgColor, out float nodeAlpha, out float borderAlpha)
        {
            Color glassBase = new Color(0.045f, 0.075f, 0.125f, 0.82f);
            nodeAlpha = 1f;
            borderAlpha = 1f;

            switch (state)
            {
                case ResearchNodeState.Completed:
                    bgColor = new Color(eraColor.r, eraColor.g, eraColor.b, 0.14f);
                    break;
                case ResearchNodeState.InProgress:
                case ResearchNodeState.Available:
                    bgColor = glassBase;
                    break;
                default: // Locked
                    bgColor = Color.Lerp(glassBase, eraColor, 0.08f);
                    borderAlpha = 0.45f;
                    nodeAlpha = 0.75f;
                    break;
            }
        }

        private static float NodeGlowIntensity(ResearchNodeState state, bool isHovered, float focusHL)
        {
            float glow = 0f;
            if (isHovered) glow = 0.45f;
            else if (state == ResearchNodeState.InProgress) glow = 0.55f;
            else if (state == ResearchNodeState.Completed) glow = 0.22f;
            if (focusHL > 0f) glow = Mathf.Max(glow, Constraints.FocusNodeGlow * focusHL);
            return glow;
        }

        private static void DrawNodeProgressBar(ResearchNode node, Rect nodeRectScreen, float zoom, float nodeAlpha, Color eraColor)
        {
            float barHeight = 4f * zoom;
            float yPos = nodeRectScreen.yMax - barHeight - (3f * zoom);
            float barWidth = nodeRectScreen.width - (10f * zoom);
            float xPos = nodeRectScreen.x + (5f * zoom);

            GLSolidQuadBatcher.QueueQuad(new Rect(xPos, yPos, barWidth, barHeight), new Color(0f, 0f, 0f, 0.5f * nodeAlpha));
            GLSolidQuadBatcher.QueueQuad(new Rect(xPos, yPos, barWidth * Mathf.Clamp01(node.Progress), barHeight),
                eraColor.WithAlpha(eraColor.a * nodeAlpha));
        }

        /// <summary>엣지 본선과 같은 색/강조 규칙</summary>
        private void DrawDummyConnector(ResearchNode node, Vector2 offset, float zoom)
        {
            Vector2 startScreen = (node.InputAnchor * zoom) + offset;
            Vector2 endScreen = (node.OutputAnchor * zoom) + offset;

            Color edgeColor = GetEdgeColor(node);
            int highlight = GetEdgeHighlightState(node, node);
            if (highlight > 0)
            {
                edgeColor = Color.Lerp(edgeColor, Constraints.EdgeHighlight, EdgeHighlightBlend());
            }
            else if (highlight < 0)
            {
                edgeColor = edgeColor.WithAlpha(edgeColor.a * EdgeUnfocusAlpha());
            }

            float width = Mathf.Max(Constraints.EdgeLineMinWidth, Constraints.EdgeLineWidth * zoom);
            EdgeRenderer.DrawStraightRibbon(startScreen, endScreen, edgeColor, edgeColor, width);
        }

        // 프록시 노드 용
        private static void DrawDashedBorder(Rect rect, Color color, float zoom, float cornerRadius)
        {
            float scale = Mathf.Max(0.7f, zoom);
            float dash = Constraints.ProxyDashLength * scale;
            float gap = Constraints.ProxyDashGap * scale;
            float th = Mathf.Max(1f, Constraints.ProxyDashThickness * scale);

            // 9-slice 테두리처럼 rect 안쪽에 그리려고 중심선을 두께 절반만큼 안으로
            float half = th * 0.5f;
            Rect r = rect.ContractedBy(half);
            float rc = Mathf.Clamp(cornerRadius - half, 0f, Mathf.Min(r.width, r.height) * 0.5f);

            BuildRoundedRectPath(r, rc);
            float total = dashCumLen[dashCumLen.Count - 1];
            if (total <= 0f) return;

            // 둘레를 (dash+gap)의 정수배로 맞춰 시작점 이음매를 없앰
            int count = Mathf.Max(1, Mathf.RoundToInt(total / (dash + gap)));
            float step = total / count;
            dash = Mathf.Min(dash, step * 0.65f);

            for (int i = 0; i < count; i++)
            {
                EmitDashStrip(i * step, i * step + dash, half, color);
            }
        }

        private static readonly List<Vector2> dashPath = new List<Vector2>(32);
        private static readonly List<float> dashCumLen = new List<float>(32);

        private static void BuildRoundedRectPath(Rect r, float rc)
        {
            dashPath.Clear();
            dashCumLen.Clear();

            if (rc <= 0.5f)
            {
                dashPath.Add(new Vector2(r.x, r.y));
                dashPath.Add(new Vector2(r.xMax, r.y));
                dashPath.Add(new Vector2(r.xMax, r.yMax));
                dashPath.Add(new Vector2(r.x, r.yMax));
                dashPath.Add(new Vector2(r.x, r.y));
            }
            else
            {
                const int arcSegs = 4;
                AddCornerArc(new Vector2(r.xMax - rc, r.y + rc), rc, -90f, 0f, arcSegs);   // 우상
                AddCornerArc(new Vector2(r.xMax - rc, r.yMax - rc), rc, 0f, 90f, arcSegs); // 우하
                AddCornerArc(new Vector2(r.x + rc, r.yMax - rc), rc, 90f, 180f, arcSegs);  // 좌하
                AddCornerArc(new Vector2(r.x + rc, r.y + rc), rc, 180f, 270f, arcSegs);    // 좌상
                dashPath.Add(dashPath[0]);
            }

            dashCumLen.Add(0f);
            for (int i = 1; i < dashPath.Count; i++)
            {
                dashCumLen.Add(dashCumLen[i - 1] + Vector2.Distance(dashPath[i - 1], dashPath[i]));
            }
        }

        private static void AddCornerArc(Vector2 center, float radius, float fromDeg, float toDeg, int segs)
        {
            for (int i = 0; i <= segs; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(fromDeg, toDeg, (float)i / segs);
                dashPath.Add(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
        }

        private static void EmitDashStrip(float s0, float s1, float half, Color color)
        {
            int last = dashPath.Count - 1;
            int i = 0;
            while (i < last - 1 && dashCumLen[i + 1] <= s0) i++;

            Vector2 prev = PointOnDashPath(i, s0);
            float s = s0;
            while (s < s1 && i < last)
            {
                float segEnd = dashCumLen[i + 1];
                float next = Mathf.Min(segEnd, s1);
                Vector2 cur = PointOnDashPath(i, next);
                Vector2 dir = cur - prev;
                float len = dir.magnitude;
                if (len > 0.001f)
                {
                    dir /= len;
                    Vector2 nrm = new Vector2(-dir.y, dir.x) * half;
                    GLSolidQuadBatcher.QueueFreeQuad(prev - nrm, cur - nrm, cur + nrm, prev + nrm, color);
                }
                prev = cur;
                s = next;
                if (s >= segEnd) i++;
            }
        }

        private static Vector2 PointOnDashPath(int seg, float s)
        {
            float segLen = dashCumLen[seg + 1] - dashCumLen[seg];
            float t = segLen > 0f ? (s - dashCumLen[seg]) / segLen : 0f;
            return Vector2.LerpUnclamped(dashPath[seg], dashPath[seg + 1], Mathf.Clamp01(t));
        }

        private void DrawNodeForeground(ResearchNode node, Vector2 offset, float zoom)
        {
            if (node.IsDummy) return;
            if (!visibleRect.Overlaps(node.Rect)) return;

            Rect nodeRectScreen = GetNodeScreenRect(node, offset, zoom);

            var state = node.State;
            Color textColor = Color.white;
            float nodeAlpha = 1f;

            if (state == ResearchNodeState.Completed) textColor = node.EraAccentColor;
            else if (state == ResearchNodeState.Locked)
            {
                textColor = new Color(0.6f, 0.65f, 0.72f, 1f);
                nodeAlpha = 0.75f; // 배경의 Locked 알파와 일치
            }

            float unfocusMul = GetUnfocusMultiplier(node);
            if (unfocusMul < 1f)
            {
                nodeAlpha *= unfocusMul;
                textColor.a *= nodeAlpha;
            }

            GUI.color = textColor;

            // IMGUI 폰트는 3단계 고정이라 저줌 라벨은 매트릭스 스케일로 축소
            if (zoom < Constraints.NodeTextMinZoom)
            {
                DrawScaledNodeLabel(node, nodeRectScreen, zoom);
                return;
            }

            // 대표 해금 아이콘이 있으면 라벨/리치 스트립을 그만큼 좌측 인셋
            var realForRep = RealNodeOf(node);
            bool hasRep = !node.IsHidden && realForRep.UnlockedDefs.Count > 0;
            float richLerp = GetRichCardLerp(zoom);

            Text.Anchor = hasRep ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            Widgets.Label(ComputeNodeLabelRect(node, nodeRectScreen, zoom, hasRep, richLerp), node.Label);
            Text.Anchor = TextAnchor.MiddleCenter; // 루프 공유 상태 복원

            // 리치 카드가 안 보이는 줌에서도 무엇을 해금하는지 보여줌
            if (hasRep)
            {
                DrawRepresentativeIcon(realForRep, nodeRectScreen, zoom, nodeAlpha, textColor);
            }

            DrawNodeModIcon(node, nodeRectScreen, zoom, nodeAlpha, textColor);

            if (richLerp > 0.05f && !node.IsHidden)
            {
                DrawRichCardContent(node, nodeRectScreen, zoom, nodeAlpha * richLerp);
            }
            else
            {
                DrawTechprintLowZoomMarker(node, nodeRectScreen, zoom, nodeAlpha);
            }

            DrawQueueBadge(node, nodeRectScreen, zoom, nodeAlpha);
            DrawExternalDependentsBadge(node, nodeRectScreen, zoom, nodeAlpha);
        }

        private static Rect ComputeNodeLabelRect(ResearchNode node, Rect nodeRectScreen, float zoom, bool hasRep, float richLerp)
        {
            if (zoom < 0.5f) return nodeRectScreen;

            float repInset = hasRep ? RepInset(zoom) : 0f;
            // 진행바 자리 양보. 조건은 배경의 진행바와 동일
            float bottomPadding = (node.State != ResearchNodeState.Completed && node.Progress > 0f) ? 6f * zoom : 0f;
            Rect textRect = new Rect(
                nodeRectScreen.x + 4f * zoom + repInset,
                nodeRectScreen.y,
                nodeRectScreen.width - 8f * zoom - repInset,
                nodeRectScreen.height - bottomPadding);

            if (richLerp > 0.05f)
            {
                // 리치 카드에선 라벨을 상단에 고정
                textRect.height = 22f * zoom;
                textRect.y += 3f * zoom;
            }
            return textRect;
        }

        private static readonly System.Text.StringBuilder nodeTooltipSb = new System.Text.StringBuilder(512);

        /// <summary>TipSignal textGetter라 실제 표시 직전에만 실행됨</summary>
        private static string BuildNodeTooltip(ResearchNode node, bool showTabLine = false)
        {
            var real = RealNodeOf(node);
            if (real.IsHidden) return real.Label;

            var sb = nodeTooltipSb;
            sb.Length = 0;
            sb.AppendLine(real.Def.LabelCap);

            if (!real.Def.description.NullOrEmpty())
            {
                sb.AppendLine();
                sb.AppendLine(real.Def.description);
            }

            sb.AppendLine();
            if (real.Def.IsFinished)
            {
                sb.Append((string)"YART_Finished".Translate());
            }
            else
            {
                // 바닐라와 같은 표기, 테크레벨 계수 반영
                sb.Append((string)"YART_Progress".Translate(real.Def.ProgressApparentString, real.Def.CostApparent.ToString("F0")));
            }

            if (real.State == ResearchNodeState.Locked)
            {
                real.GetLockedReasons(lockedReasonsBuffer);
                if (lockedReasonsBuffer.Count > 0)
                {
                    sb.AppendLine().Append((string)"YART_LockedReasons".Translate());
                    foreach (var reason in lockedReasonsBuffer)
                    {
                        sb.AppendLine().Append("  • ").Append(reason);
                    }
                }
            }

            // 통합 뷰에서 소속 탭 한 줄
            if (showTabLine && !node.IsProxy && !node.IsDummy && real.Tab != null)
            {
                sb.AppendLine().Append((string)"YART_Tab".Translate(real.Tab.LabelCap));
            }

            if (real.State == ResearchNodeState.Available && !SemiRandomResearchCompat.Active)
            {
                sb.AppendLine().AppendLine();
                sb.AppendLine((string)"YART_TipQueueShift".Translate());
                sb.AppendLine((string)"YART_TipQueueAlt".Translate());
                sb.Append((string)"YART_TipQueueCtrl".Translate());
            }

            return sb.ToString();
        }

        private static void DrawScaledNodeLabel(ResearchNode node, Rect nodeRectScreen, float zoom)
        {
            float scale = zoom / Constraints.NodeTextMinZoom;
            Vector2 pivot = nodeRectScreen.center;
            // 스케일 후 노드 rect에 맞도록 1/scale로 부풀림
            Rect inflated = new Rect(
                pivot.x - nodeRectScreen.width / (2f * scale),
                pivot.y - nodeRectScreen.height / (2f * scale),
                nodeRectScreen.width / scale,
                nodeRectScreen.height / scale);

            Matrix4x4 prevMatrix = GUI.matrix;
#if RW17
            // 1.7 UnityStubs의 ScaleAroundPivot은 피벗을 물리 픽셀로 받으므로 매트릭스로 변환해 넘김
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), prevMatrix.MultiplyVector(pivot));
#else
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), pivot);
#endif
            Widgets.Label(inflated, node.Label);
            GUI.matrix = prevMatrix;
        }

        /// <summary>역방향 프록시로 그래프를 채우는 대신 배지와 호버 팝업으로 노출. 미발견 후행도 카운트</summary>
        private void DrawExternalDependentsBadge(ResearchNode node, Rect nodeRectScreen, float zoom, float nodeAlpha)
        {
            if (zoom < 0.5f) return;
            if (node.IsProxy) return; // 원본 노드에만

            var ext = node.ExternalChildren;
            if (ext == null || ext.Count == 0) return;

            float size = Mathf.Max(14f, 18f * zoom);
            Rect badgeRect = new Rect(nodeRectScreen.xMax - size * 0.7f, nodeRectScreen.y - size * 0.4f, size, size);

            Color badgeColor = new Color(0.55f, 0.62f, 0.78f);
            Widgets.DrawBoxSolid(badgeRect, new Color(0.05f, 0.05f, 0.08f, 0.95f * nodeAlpha));
            GUIDrawingUtilities.DrawBorderLines(badgeRect, badgeColor.WithAlpha(nodeAlpha), 1f);

            var prevFont = Text.Font;
            var prevColor = GUI.color;
            Text.Font = GameFont.Tiny;
            GUI.color = badgeColor.WithAlpha(nodeAlpha);
            Widgets.Label(badgeRect, "+" + ext.Count);
            Text.Font = prevFont;
            GUI.color = prevColor;

            // 팝업은 맨 위에 와야 해서 후보만 기록하고 프레임 마지막에 그림
            if (Mouse.IsOver(badgeRect))
            {
                externalListCandidate = node;
                externalListCandidateAnchor = badgeRect;
            }
        }

        /// <summary>배지나 팝업 위에 마우스가 있는 동안 유지. 미발견 연구는 점프 불가</summary>
        private void DrawExternalDependentsPopup()
        {
            UpdateExternalListTarget();
            if (externalListNode == null)
            {
                externalListRect = Rect.zero;
                return;
            }

            var ext = externalListNode.ExternalChildren;
            if (ext == null || ext.Count == 0)
            {
                externalListNode = null;
                externalListRect = Rect.zero;
                return;
            }

            const int pageSize = 8;
            const float rowH = 26f;
            const float headerH = 22f;
            int pages = Mathf.CeilToInt((float)ext.Count / pageSize);
            externalListPage = Mathf.Clamp(externalListPage, 0, pages - 1);
            int start = externalListPage * pageSize;
            int rows = Mathf.Min(pageSize, ext.Count - start);
            float footerH = pages > 1 ? 26f : 0f;

            float width = 300f;
            float listH = 6f + headerH + rows * rowH + footerH + 6f;
            float x = Mathf.Min(externalListAnchor.xMax + 4f, canvasRect.xMax - width - 4f);
            float yPos = Mathf.Clamp(externalListAnchor.y, canvasRect.y, canvasRect.yMax - listH - 4f);
            externalListRect = new Rect(x, yPos, width, listH);

            Widgets.DrawBoxSolid(externalListRect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(externalListRect, Constraints.PanelBorder, 1f);

            float curY = externalListRect.y + 6f;

            DrawTinyLabel(new Rect(externalListRect.x + 8f, curY, externalListRect.width - 16f, headerH),
                "YART_RequiredByExternal".Translate(ext.Count), new Color(0.55f, 0.62f, 0.78f), TextAnchor.MiddleLeft);
            curY += headerH;

            for (int i = start; i < start + rows; i++)
            {
                Rect rowRect = new Rect(externalListRect.x + 2f, curY, externalListRect.width - 4f, rowH);
                if (DrawExternalDependentRow(rowRect, ext[i])) return; // 점프 시 팝업 종료
                curY += rowH;
            }

            if (pages > 1)
            {
                DrawExternalListPagination(curY, pages);
            }
        }

        private void UpdateExternalListTarget()
        {
            if (externalListCandidate != null)
            {
                if (externalListNode != externalListCandidate) externalListPage = 0;
                externalListNode = externalListCandidate;
                externalListAnchor = externalListCandidateAnchor;
                externalListCandidate = null;
                return;
            }

            if (externalListNode == null) return;

            // 배지와 팝업 사이를 지날 때 깜빡이지 않게 6px 여유
            Vector2 mouse = Event.current.mousePosition;
            if (!externalListRect.ExpandedBy(6f).Contains(mouse)
                && !externalListAnchor.ExpandedBy(6f).Contains(mouse))
            {
                externalListNode = null;
            }
        }

        /// <summary>클릭으로 점프했으면 true</summary>
        private bool DrawExternalDependentRow(Rect rowRect, ResearchNode child)
        {
            bool hidden = child.IsHidden;
            if (!hidden && Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);

            Rect dotRect = new Rect(rowRect.x + 6f, rowRect.y + rowRect.height / 2f - 3f, 6f, 6f);
            Widgets.DrawBoxSolid(dotRect, hidden ? new Color(0.4f, 0.4f, 0.45f) : child.EraAccentColor);

            // child는 실노드
            string tabLabel = child.Key.Channel.IsBench && child.Key.Tab != null
                ? (string)child.Key.Tab.LabelCap
                : child.Key.Channel.Label;

            DrawTinyLabel(new Rect(rowRect.x + 18f, rowRect.y, rowRect.width - 110f, rowRect.height),
                child.Label.Truncate(rowRect.width - 110f),
                hidden ? new Color(0.5f, 0.5f, 0.55f) : Color.white, TextAnchor.MiddleLeft);
            DrawTinyLabel(new Rect(rowRect.x, rowRect.y, rowRect.width - 6f, rowRect.height),
                tabLabel.Truncate(86f), new Color(0.5f, 0.55f, 0.65f), TextAnchor.MiddleRight);

            if (hidden || !Widgets.ButtonInvisible(rowRect)) return false;

            // 좌측 패널 이전(<) 버튼으로 돌아오도록 배지 주인을 먼저 히스토리에 넣음
            if (!history.HasCurrent || history.Current != externalListNode)
            {
                history.Push(externalListNode);
            }
            JumpToNode(child);
            externalListNode = null;
            externalListRect = Rect.zero;
            return true;
        }

        private void DrawExternalListPagination(float curY, int pages)
        {
            const float btnSize = 22f;
            Rect prevRect = new Rect(externalListRect.x + 8f, curY + 2f, btnSize, btnSize);
            Rect nextRect = new Rect(externalListRect.xMax - 8f - btnSize, curY + 2f, btnSize, btnSize);
            if (DrawNavButton(prevRect, "<", externalListPage > 0)) externalListPage--;
            if (DrawNavButton(nextRect, ">", externalListPage < pages - 1)) externalListPage++;

            DrawTinyLabel(new Rect(prevRect.xMax, curY + 2f, nextRect.x - prevRect.xMax, btnSize),
                $"{externalListPage + 1} / {pages}", new Color(0.6f, 0.65f, 0.75f), TextAnchor.MiddleCenter);
        }

        private void DrawRichCardContent(ResearchNode node, Rect nodeRectScreen, float zoom, float alpha)
        {
            var realNode = RealNodeOf(node);
            var unlocked = realNode.UnlockedDefs;

            var prevColor = GUI.color;
            var prevFont = Text.Font;
            var prevAnchor = Text.Anchor;

            DrawRichCardUnlockStrip(unlocked, nodeRectScreen, zoom, alpha);
            DrawRichCardCostRow(realNode, nodeRectScreen, zoom, alpha);

            // 루프 공유 상태 복원
            GUI.color = prevColor;
            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
        }

        /// <summary>대표(index 0)는 따로 그리므로 1번부터</summary>
        private static void DrawRichCardUnlockStrip(List<Def> unlocked, Rect nodeRectScreen, float zoom, float alpha)
        {
            bool hasRep = unlocked.Count > 0;
            GetRichCardIconMetrics(nodeRectScreen, zoom, hasRep, out float iconSize, out float iconGap, out float iconX, out float iconY);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            int shown = 0;
            for (int i = 1; i < unlocked.Count && shown < RichCardStripMax; i++)
            {
                Rect iconRect = new Rect(iconX + shown * (iconSize + iconGap), iconY, iconSize, iconSize);
                Widgets.DefIcon(iconRect, unlocked[i], null, 1f, null, drawPlaceholder: true);
                // 우클릭 InfoCard는 HandleNodeContextMenu에서 처리
                if (Mouse.IsOver(iconRect))
                {
                    TooltipHandler.TipRegion(iconRect, UnlockTipFor(unlocked[i]));
                }
                shown++;
            }

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;

            int afterRep = unlocked.Count - 1;
            if (afterRep > shown)
            {
                Rect moreRect = new Rect(iconX + shown * (iconSize + iconGap), iconY, 30f * zoom, iconSize);
                GUI.color = new Color(0.55f, 0.62f, 0.72f, alpha);
                Widgets.Label(moreRect, $"+{afterRep - shown}");
                if (Mouse.IsOver(moreRect))
                {
                    TooltipHandler.TipRegion(moreRect, OverflowUnlockTip(unlocked, 1 + shown));
                }
            }
            else if (unlocked.Count == 0)
            {
                Rect noneRect = new Rect(iconX, iconY, nodeRectScreen.width - 12f * zoom, iconSize);
                GUI.color = new Color(0.45f, 0.52f, 0.62f, alpha * 0.8f);
                Widgets.Label(noneRect, "-");
            }
        }

        /// <summary>오른쪽부터 비용, 테크프린트 수, 아이콘. 바닐라 DrawBottomRow와 같은 묶음</summary>
        private static void DrawRichCardCostRow(ResearchNode realNode, Rect nodeRectScreen, float zoom, float alpha)
        {
            float rowY = nodeRectScreen.yMax - 22f * zoom;
            float rowH = 19f * zoom;
            float right = nodeRectScreen.xMax - 6f * zoom;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleRight;

            // CostApparent = baseCost(또는 knowledgeCost) * 테크레벨 계수
            string costStr = realNode.Def.CostApparent.ToString("N0");
            if (realNode.State != ResearchNodeState.Completed && realNode.Progress > 0f)
            {
                costStr = $"{realNode.Def.ProgressApparentString}/{costStr}";
            }
            float costW = Text.CalcSize(costStr).x;
            GUI.color = new Color(0.72f, 0.79f, 0.88f, alpha);
            Widgets.Label(new Rect(right - costW, rowY, costW, rowH), costStr);
            right -= costW + 8f * zoom;

            if (realNode.Def.TechprintCount <= 0) return;

            // 아이콘은 바닐라처럼 흰색 틴트
            bool met = realNode.Def.TechprintRequirementMet;
            string tpStr = realNode.Def.TechprintsApplied + "/" + realNode.Def.TechprintCount;
            float tpW = Text.CalcSize(tpStr).x;
            GUI.color = (met ? ActiveColors.PrereqMet : ActiveColors.PrereqUnmet).WithAlpha(alpha);
            Widgets.Label(new Rect(right - tpW, rowY, tpW, rowH), tpStr);
            right -= tpW + 2f * zoom;

            float iconSz = 15f * zoom;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(right - iconSz, rowY + (rowH - iconSz) / 2f, iconSz, iconSz),
                Assets.IconTechprint, ScaleMode.ScaleToFit);
        }

        private void DrawRepresentativeIcon(ResearchNode realNode, Rect nodeRectScreen, float zoom, float alpha, Color restoreColor)
        {
            var unlocked = realNode.UnlockedDefs;
            if (unlocked.Count == 0) return;

            Rect r = RepIconRect(nodeRectScreen, zoom);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Widgets.DefIcon(r, unlocked[0], null, 1f, null, drawPlaceholder: true);
            GUI.color = restoreColor;
            if (Mouse.IsOver(r))
            {
                TooltipHandler.TipRegion(r, UnlockTipFor(unlocked[0]));
            }
        }

        // Core/DLC는 ExpansionDef.Icon, 그 외 모드는 ModMetaData.Icon
        private static readonly Dictionary<string, Texture2D> modIconCache = new Dictionary<string, Texture2D>();
        private static Dictionary<string, ExpansionDef> expansionByMod; // 키는 소문자 packageId

        private static Texture2D GetModIcon(ModContentPack mod)
        {
            if (mod == null) return null;
            string pid = mod.PackageId;
            if (string.IsNullOrEmpty(pid)) return null;
            pid = pid.ToLowerInvariant();

            if (modIconCache.TryGetValue(pid, out var cached)) return cached;

            if (expansionByMod == null)
            {
                expansionByMod = new Dictionary<string, ExpansionDef>();
                foreach (var exp in DefDatabase<ExpansionDef>.AllDefsListForReading)
                {
                    if (!string.IsNullOrEmpty(exp.linkedMod))
                        expansionByMod[exp.linkedMod.ToLowerInvariant()] = exp;
                }
            }

            Texture2D tex = expansionByMod.TryGetValue(pid, out var expansion)
                ? expansion.Icon
                : mod.ModMetaData?.Icon;
            modIconCache[pid] = tex; // null도 캐시해 재조회 방지
            return tex;
        }

        private void DrawNodeModIcon(ResearchNode node, Rect nodeRectScreen, float zoom, float alpha, Color restoreColor)
        {
            if (!YARTMod.Settings.showNodeSourceIcon) return;
            if (node.IsHidden) return; // 미발견 연구는 출처 노출 안 함

            var def = RealNodeOf(node)?.Def;
            var icon = def?.GetModExtension<ResearchNodeExtension>()?.Icon ?? GetModIcon(node.SourceMod);
            if (icon == null) return;

            Rect r = ModIconSlot(node, nodeRectScreen, zoom);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(r, icon, ScaleMode.ScaleToFit);
            GUI.color = restoreColor;
        }

        /// <summary>테크프린트 마커도 이 슬롯을 기준으로 배치</summary>
        private Rect ModIconSlot(ResearchNode node, Rect nodeRectScreen, float zoom)
        {
            float sz = 18f * zoom;
            float rightPad = 5f * zoom;
            if (HasQueueBadge(node, zoom)) rightPad += Mathf.Max(18f, 26f * zoom) * 0.7f;
            return new Rect(nodeRectScreen.xMax - sz - rightPad, nodeRectScreen.center.y - sz / 2f, sz, sz);
        }

        /// <summary>리치 카드가 없는 줌에서 테크프린트 충족 여부를 색으로 표시</summary>
        private void DrawTechprintLowZoomMarker(ResearchNode node, Rect nodeRectScreen, float zoom, float alpha)
        {
            if (node.IsHidden) return;
            var def = RealNodeOf(node).Def;
            if (def == null || def.TechprintCount <= 0) return;

            Rect modSlot = ModIconSlot(node, nodeRectScreen, zoom);
            float markSz = 13f * zoom;
            Rect r = new Rect(modSlot.center.x - markSz / 2f, modSlot.yMax + 2f * zoom, markSz, markSz);

            var prevColor = GUI.color;
            GUI.color = (def.TechprintRequirementMet ? ActiveColors.PrereqMet : ActiveColors.PrereqUnmet).WithAlpha(alpha);
            GUI.DrawTexture(r, Assets.IconTechprint, ScaleMode.ScaleToFit);
            GUI.color = prevColor;
        }

        /// <summary>DrawQueueBadge와 판정이 같아야 함</summary>
        private bool HasQueueBadge(ResearchNode node, float zoom)
        {
            if (zoom < 0.5f) return false;
            var qm = ResearchQueueManager.Instance;
            if (qm == null) return false;
            return node.State == ResearchNodeState.InProgress || qm.GetQueuePosition(RealNodeOf(node).Def) > 0;
        }

        private void DrawQueueBadge(ResearchNode node, Rect nodeRectScreen, float zoom, float nodeAlpha)
        {
            if (zoom < 0.5f) return;

            var queueMgr = ResearchQueueManager.Instance;
            if (queueMgr == null) return;

            bool isCurrent = node.State == ResearchNodeState.InProgress;
            int pos = queueMgr.GetQueuePosition(RealNodeOf(node).Def);
            if (!isCurrent && pos < 0) return;

            float size = Mathf.Max(18f, 26f * zoom);
            Rect badgeRect = new Rect(nodeRectScreen.xMax - size * 0.55f, nodeRectScreen.center.y - size / 2f, size, size);

            Color accent = node.EraAccentColor;
            Widgets.DrawBoxSolid(badgeRect, new Color(0.05f, 0.05f, 0.08f, 0.95f * nodeAlpha));
            GUIDrawingUtilities.DrawBorderLines(badgeRect, accent.WithAlpha(accent.a * nodeAlpha), 1.5f);

            if (isCurrent)
            {
                GUIDrawingUtilities.DrawIcon(badgeRect.ContractedBy(size * 0.22f), Assets.IconPlay, accent.WithAlpha(nodeAlpha));
            }
            else
            {
                var prevFont = Text.Font;
                var prevColor = GUI.color;
                Text.Font = zoom >= 0.7f ? GameFont.Small : GameFont.Tiny;
                GUI.color = accent.WithAlpha(nodeAlpha);
                Widgets.Label(badgeRect, pos.ToString());
                Text.Font = prevFont;
                GUI.color = prevColor;
            }
        }
    }
}
