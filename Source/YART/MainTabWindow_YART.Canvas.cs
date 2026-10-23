using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;
using YART.Rendering;

namespace YART
{
    // 캔버스 배경, 그래프 렌더링, 카메라 팬/줌
    public partial class MainTabWindow_YART
    {
        private void DoGraphCanvas(Rect rect)
        {
            UpdateCameraAnimation(); // 입력/컬링 계산 전에
            HandleKeyboardPan();     // 마우스 위치와 무관
            HandleInput(rect);
            ClampScroll(rect);

            // World = (Screen - scroll) / zoom, 여기에 마진
            Vector2 minWorld = (Vector2.zero - scrollPosition) / zoomLevel;
            Vector2 maxWorld = (new Vector2(rect.width, rect.height) - scrollPosition) / zoomLevel;
            visibleRect = new Rect(minWorld.x - Constraints.CullingMargin.x, minWorld.y - Constraints.CullingMargin.y,
                (maxWorld.x - minWorld.x) + (Constraints.CullingMargin.x * 2),
                (maxWorld.y - minWorld.y) + (Constraints.CullingMargin.y * 2));

            SetBatcherClipRect(rect);

            try
            {
                Vector2 viewOffset = rect.position + scrollPosition;
                bool repaint = Event.current.type == EventType.Repaint;

                if (repaint)
                {
                    GUIScreenTransform.Capture();
                    DrawCanvasBackground(rect, viewOffset, zoomLevel);
                }

                PerfBeginDraw();
                DrawGraphContent(viewOffset, zoomLevel);
                PerfEndDraw();

                // 비네팅은 콘텐츠 위, 패널 아래
                if (repaint) GUI.DrawTexture(rect, Assets.Vignette, ScaleMode.StretchToFill);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"[YART] Error drawing graph: {ex}", 938472);
            }
            finally
            {
                SetBatcherClipRect(Rect.zero); // 클리핑 해제
            }
        }

        private static void SetBatcherClipRect(Rect rect)
        {
            GLLineBatcher.ClipRect = rect;
            GLSolidQuadBatcher.ClipRect = rect;
            GLTexturedQuadBatcher.ClipRect = rect;
        }

        private static List<KeyValuePair<TechLevel, float>> SortedEraBoundaries(ResearchSubGraph graph)
        {
            if (graph?.TechLevelBoundaries == null || graph.TechLevelBoundaries.Count == 0) return null;
            return graph.TechLevelBoundaries.OrderBy(kvp => kvp.Value).ToList();
        }

        /// <summary>그리드와 틴트는 월드 좌표에 고정</summary>
        private void DrawCanvasBackground(Rect rect, Vector2 offset, float zoom)
        {
            Widgets.DrawBoxSolid(rect, Constraints.BackgroundColor);

            DrawEraZoneTints(rect, offset, zoom);

            if (YARTMod.Settings.gridBackground)
            {
                DrawGrid(rect, offset, zoom);
            }

            // 노이즈 그레인
            using (Temporary.Color(new Color(1f, 1f, 1f, Constraints.NoiseAlpha)))
            {
                float ts = Constraints.NoiseTileScreenSize;
                Rect texCoords = new Rect(-offset.x * 0.5f / ts, offset.y * 0.5f / ts, rect.width / ts, rect.height / ts);
                GUI.DrawTextureWithTexCoords(rect, Assets.NoiseTile, texCoords);
            }
        }

        /// <summary>경계에서 오른쪽으로 페이드 아웃</summary>
        private void DrawEraZoneTints(Rect rect, Vector2 offset, float zoom)
        {
            var boundaries = SortedEraBoundaries(ResearchGraph.Instance.GetSubGraph(CurrentKey));
            if (boundaries == null) return;

            for (int i = 0; i < boundaries.Count; i++)
            {
                float zoneStartWorld = boundaries[i].Value;
                // 마지막 구역은 팬 중 일렁이지 않게 고정 폭
                float zoneEndWorld = i + 1 < boundaries.Count
                    ? boundaries[i + 1].Value
                    : zoneStartWorld + Constraints.GridMajorSpacing * 8f;
                if (zoneEndWorld < visibleRect.xMin || zoneStartWorld > visibleRect.xMax) continue;

                float x0 = (zoneStartWorld * zoom) + offset.x;
                float x1 = (zoneEndWorld * zoom) + offset.x;
                if (x1 <= x0) continue;

                Color era = ActiveColors.Era(boundaries[i].Key);
                GLSolidQuadBatcher.QueueGradientQuadH(
                    new Rect(x0, rect.y, x1 - x0, rect.height),
                    GenColor.WithAlpha(era, Constraints.EraZoneTintAlpha),
                    GenColor.WithAlpha(era, 0f));
            }
            GLSolidQuadBatcher.Flush();
        }

        private void DrawGrid(Rect rect, Vector2 offset, float zoom)
        {
            float minorAlpha = Mathf.InverseLerp(Constraints.GridMinorFadeOutZoom - 0.2f, Constraints.GridMinorFadeOutZoom, zoom);
            if (minorAlpha > 0f)
            {
                DrawGridLines(rect, offset, zoom, Constraints.GridMinorSpacing,
                    GenColor.WithAlpha(Constraints.GridMinorColor, Constraints.GridMinorColor.a * minorAlpha), skipMajorPositions: true);
            }
            DrawGridLines(rect, offset, zoom, Constraints.GridMajorSpacing, Constraints.GridMajorColor, skipMajorPositions: false);
            GLLineBatcher.Flush();

            if (zoom < 0.8f) return;

            float dotAlpha = Mathf.InverseLerp(0.8f, 1.1f, zoom);
            Color dotColor = GenColor.WithAlpha(Constraints.GridDotColor, Constraints.GridDotColor.a * dotAlpha);
            float size = Constraints.GridDotSize * zoom;
            float spacing = Constraints.GridMajorSpacing;
            float startX = Mathf.Floor(visibleRect.xMin / spacing) * spacing;
            float startY = Mathf.Floor(visibleRect.yMin / spacing) * spacing;
            for (float wx = startX; wx <= visibleRect.xMax; wx += spacing)
            {
                for (float wy = startY; wy <= visibleRect.yMax; wy += spacing)
                {
                    float sx = (wx * zoom) + offset.x;
                    float sy = (wy * zoom) + offset.y;
                    GLTexturedQuadBatcher.QueueQuad(Assets.GlowRadial,
                        new Rect(sx - size / 2f, sy - size / 2f, size, size), dotColor);
                }
            }
            GLTexturedQuadBatcher.Flush();
        }

        private void DrawGridLines(Rect rect, Vector2 offset, float zoom, float spacingWorld, Color color, bool skipMajorPositions)
        {
            if (color.a <= 0.004f) return;

            float startX = Mathf.Floor(visibleRect.xMin / spacingWorld) * spacingWorld;
            for (float wx = startX; wx <= visibleRect.xMax; wx += spacingWorld)
            {
                // 주선과 겹치는 보조선은 건너뜀
                if (skipMajorPositions && Mathf.Repeat(wx, Constraints.GridMajorSpacing) < 0.01f) continue;
                float sx = (wx * zoom) + offset.x;
                GLLineBatcher.QueueLine(new Vector2(sx, rect.y), new Vector2(sx, rect.yMax), color);
            }

            float startY = Mathf.Floor(visibleRect.yMin / spacingWorld) * spacingWorld;
            for (float wy = startY; wy <= visibleRect.yMax; wy += spacingWorld)
            {
                if (skipMajorPositions && Mathf.Repeat(wy, Constraints.GridMajorSpacing) < 0.01f) continue;
                float sy = (wy * zoom) + offset.y;
                GLLineBatcher.QueueLine(new Vector2(rect.x, sy), new Vector2(rect.xMax, sy), color);
            }
        }

        private void DrawGraphContent(Vector2 offset, float zoom)
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null) return;

            // 성능 측정용 반복
            int reps = perfStress > 1 ? perfStress : 1;
            for (int r = 0; r < reps; r++)
            {
                DrawSubGraph(graph, offset, zoom);
            }
        }

        private void DrawSubGraph(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            // clickPending은 MouseUp 패스에서 세팅되므로 Repaint 체크보다 앞에서 처리
            HandleNodeInteraction(graph, offset, zoom);
            HandleNodeContextMenu(graph, offset, zoom);

            // 이하 렌더링은 Repaint 패스에서만. Layout 패스에서 쌓은 GL 큐는 버려지는 낭비라
            // 이것만으로 프레임타임이 약 절반
            if (Event.current.type != EventType.Repaint) return;

            UpdateFocusMode(graph, offset, zoom);
            AdvanceFocusFade();

            DrawTechLevelLines(graph, offset, zoom);
            BuildEraBands(graph);

            // 엣지가 노드 뒤에 가려지게 먼저
            PerfSec(0); // 엣지 큐
            foreach (var edge in graph.Edges)
            {
                DrawConnection(edge.From, edge.To, offset, zoom);
            }
            PerfSecEnd();

            PerfSec(1); // 엣지 플러시
            GLLineBatcher.Flush();
            GLTexturedQuadBatcher.Flush();
            PerfSecEnd();

            PerfSec(2); // 배경 큐
            GLSolidQuadBatcher.Clear();
            foreach (var node in graph.Nodes)
            {
                DrawNodeBackground(node, offset, zoom);
            }
            PerfSecEnd();

            PerfSec(3); // 배경 플러시
            GLTexturedQuadBatcher.Flush();
            GLSolidQuadBatcher.Flush();
            PerfSecEnd();

            DrawActivePulses(graph, offset, zoom);
            GLTexturedQuadBatcher.Flush();

            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = zoom < 0.7f ? GameFont.Tiny : GameFont.Small;

            try
            {
                foreach (var node in graph.Nodes)
                {
                    DrawNodeForeground(node, offset, zoom);
                }
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }

            // DrawNodeBackground의 더미 커넥터 라인 등
            GLLineBatcher.Flush();
            PerfSecEnd();
        }

        private void AdvanceFocusFade()
        {
            float now = Time.realtimeSinceStartup;
            float dt = lastFocusFadeTime > 0f ? Mathf.Min(now - lastFocusFadeTime, 0.1f) : 0f;
            lastFocusFadeTime = now;
            focusAmount = Mathf.MoveTowards(focusAmount, hoveredNode != null ? 1f : 0f, dt / Constraints.FocusFadeDuration);
        }

        private void DrawTechLevelLines(ResearchSubGraph graph, Vector2 offset, float zoom)
        {
            var boundaries = SortedEraBoundaries(graph);
            if (boundaries == null) return;

            // 경계선은 세로로 줌/스크롤과 무관하게 캔버스 전체를 채움
            foreach (var kvp in boundaries)
            {
                float xWorld = kvp.Value;
                if (xWorld < visibleRect.xMin || xWorld > visibleRect.xMax) continue;

                float xScreen = (xWorld * zoom) + offset.x;
                GLLineBatcher.QueueLine(
                    new Vector2(xScreen, canvasRect.yMin),
                    new Vector2(xScreen, canvasRect.yMax),
                    Constraints.TechLevelLineColor);
            }

            float labelY = canvasRect.y + eraLabelTopOffset + Constraints.EraLabelTopMargin;

            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Color(Constraints.TechLevelTextColor))
            {
                for (int i = 0; i < boundaries.Count; i++)
                {
                    float zoneStart = (boundaries[i].Value * zoom) + offset.x;
                    float zoneEnd = i + 1 < boundaries.Count
                        ? (boundaries[i + 1].Value * zoom) + offset.x
                        : float.PositiveInfinity;

                    if (zoneEnd < canvasRect.xMin || zoneStart > canvasRect.xMax) continue;

                    string label = TechLevelLabel(boundaries[i].Key);
                    Vector2 size = Text.CalcSize(label);

                    float x = Mathf.Max(zoneStart + Constraints.EraLabelSidePadding,
                                        canvasRect.x + Constraints.EraLabelSidePadding);
                    x = Mathf.Min(x, zoneEnd - size.x - Constraints.EraLabelSidePadding);
                    if (x + size.x < canvasRect.xMin || x > canvasRect.xMax) continue;

                    Widgets.Label(new Rect(x, labelY, size.x + 4f, size.y), label);
                }
            }
        }

        /// <summary>리치 카드 확장이 반영되므로 렌더링과 히트테스트 모두 이걸 써야 함</summary>
        private static Rect GetNodeScreenRect(ResearchNode node, Vector2 offset, float zoom)
        {
            Rect world = node.Rect;
            float extra = Constraints.RichCardExtraHeight * GetRichCardLerp(zoom);
            if (extra > 0f)
            {
                world.y -= extra / 2f;
                world.height += extra;
            }

            return new Rect(
                Mathf.Round((world.x * zoom) + offset.x),
                Mathf.Round((world.y * zoom) + offset.y),
                Mathf.Round(world.width * zoom),
                Mathf.Round(world.height * zoom));
        }

        /// <summary>일시정지 중에도 움직이게 realtime dt, 프레임당 1회만 이동하게 Repaint에서만</summary>
        private void HandleKeyboardPan()
        {
            if (Event.current.type != EventType.Repaint) return;

            float now = Time.realtimeSinceStartup;
            float dt = now - lastKeyPanTime;
            lastKeyPanTime = now;

            string focused = GUI.GetNameOfFocusedControl();
            if (focused == "YARTSearchField" || focused == "YARTPresetName") return; // 텍스트 입력 중엔 WASD 팬 안 함
            // 첫 프레임이나 복귀 직후의 큰 dt는 순간이동이 되므로 무시
            if (dt <= 0f || dt > 0.2f) return;

            Vector2 dir = Vector2.zero;
            if (KeyBindingDefOf.MapDolly_Up.IsDown) dir.y += 1f;
            if (KeyBindingDefOf.MapDolly_Down.IsDown) dir.y -= 1f;
            if (KeyBindingDefOf.MapDolly_Left.IsDown) dir.x += 1f;
            if (KeyBindingDefOf.MapDolly_Right.IsDown) dir.x -= 1f;
            if (dir == Vector2.zero) return;

            scrollAnimating = false;
            scrollPosition += dir * (Constraints.KeyboardPanSpeed * dt);
        }

        private void HandleInput(Rect rect)
        {
            if (!Mouse.IsOver(rect)) return;

            if (IsMouseOverBlockingElement()) return;

            Event e = Event.current;

            if (e.type == EventType.ScrollWheel)
            {
                HandleWheelZoom(e, rect);
                e.Use();
            }

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    leftMouseDownPos = e.mousePosition;
                    leftMouseDownOnCanvas = true;
                    isDraggingCanvas = false;
                    break;

                // 휠 클릭 드래그는 항상 팬
                case EventType.MouseDrag when e.button == 2:
                    scrollAnimating = false;
                    scrollPosition += e.delta;
                    e.Use();
                    break;

                // 좌클릭 드래그는 노드 클릭과 구분하려고 임계값을 넘어야 팬
                case EventType.MouseDrag when e.button == 0 && leftMouseDownOnCanvas:
                    if (!isDraggingCanvas &&
                        (e.mousePosition - leftMouseDownPos).sqrMagnitude >
                        Constraints.ClickDragThreshold * Constraints.ClickDragThreshold)
                    {
                        isDraggingCanvas = true;
                    }

                    if (isDraggingCanvas)
                    {
                        scrollAnimating = false;
                        scrollPosition += e.delta;
                        e.Use();
                    }
                    break;

                case EventType.MouseUp when e.button == 0:
                    // 같은 OnGUI 패스의 HandleNodeInteraction에서 소비
                    if (leftMouseDownOnCanvas && !isDraggingCanvas)
                    {
                        clickPending = true;
                    }
                    leftMouseDownOnCanvas = false;
                    isDraggingCanvas = false;
                    break;

                case EventType.MouseDown when e.button == 1:
                    rightMouseDownPos = e.mousePosition;
                    rightMouseDownOnCanvas = true;
                    break;

                case EventType.MouseUp when e.button == 1:
                    // 같은 패스의 HandleNodeContextMenu에서 소비
                    if (rightMouseDownOnCanvas &&
                        (e.mousePosition - rightMouseDownPos).sqrMagnitude <=
                        Constraints.ClickDragThreshold * Constraints.ClickDragThreshold)
                    {
                        rightClickPending = true;
                    }
                    rightMouseDownOnCanvas = false;
                    break;
            }
        }

        private void HandleWheelZoom(Event e, Rect rect)
        {
            float oldZoom = zoomLevel;
            float newZoom = Mathf.Clamp(oldZoom + (-e.delta.y * 0.05f), Constraints.MinZoom, Constraints.MaxZoom);
            if (Mathf.Abs(newZoom - oldZoom) <= 0.001f) return;

            scrollAnimating = false;

            Vector2 mousePosRelative = e.mousePosition - rect.position;
            Vector2 mouseWorldPos = (mousePosRelative - scrollPosition) / oldZoom;
            zoomLevel = newZoom;
            scrollPosition = mousePosRelative - (mouseWorldPos * newZoom); // 커서 아래 월드 좌표 유지
        }

        private void FocusOnNode(ResearchNode node)
        {
            if (node == null || node.IsDummy) return;

            Vector2 screenCenter = new Vector2(canvasRect.width / 2f, canvasRect.height / 2f);
            StartScrollAnimation(screenCenter - (node.Center * zoomLevel));
        }

        private void StartScrollAnimation(Vector2 target)
        {
            scrollAnimStart = scrollPosition;
            scrollAnimTarget = target;
            scrollAnimStartTime = Time.realtimeSinceStartup;
            scrollAnimating = true;
        }

        /// <summary>유저가 직접 팬/줌하면 scrollAnimating을 꺼서 취소</summary>
        private void UpdateCameraAnimation()
        {
            if (!scrollAnimating) return;

            float t = (Time.realtimeSinceStartup - scrollAnimStartTime) / Constraints.CameraFocusAnimDuration;
            if (t >= 1f)
            {
                scrollPosition = scrollAnimTarget;
                scrollAnimating = false;
                return;
            }

            float eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);
            scrollPosition = Vector2.Lerp(scrollAnimStart, scrollAnimTarget, eased);
        }

        /// <summary>그래프를 최소 PanKeepVisible만큼 화면에 남김. 범위가 뒤집히면 중앙 고정</summary>
        private void ClampScroll(Rect rect)
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null) return;
            Rect bb = graph.BoundingBox;
            if (bb.width <= 0f || bb.height <= 0f) return;

            float vw = rect.width, vh = rect.height;
            float keepX = Mathf.Min(Constraints.PanKeepVisible, vw * 0.4f);
            float keepY = Mathf.Min(Constraints.PanKeepVisible, vh * 0.4f);

            float gx = bb.xMin * zoomLevel, gw = bb.width * zoomLevel;
            float gy = bb.yMin * zoomLevel, gh = bb.height * zoomLevel;

            // screenX = worldX * zoom + scroll.x. 우변은 keepX 이상, 좌변은 vw - keepX 이하
            float minX = keepX - gx - gw;
            float maxX = vw - keepX - gx;
            if (minX > maxX) { float m = (minX + maxX) * 0.5f; minX = maxX = m; }

            float minY = keepY - gy - gh;
            float maxY = vh - keepY - gy;
            if (minY > maxY) { float m = (minY + maxY) * 0.5f; minY = maxY = m; }

            scrollPosition.x = Mathf.Clamp(scrollPosition.x, minX, maxX);
            scrollPosition.y = Mathf.Clamp(scrollPosition.y, minY, maxY);
        }

        private void ZoomToFit()
        {
            var graph = ResearchGraph.Instance.GetSubGraph(CurrentKey);
            if (graph == null || graph.BoundingBox.width <= 0f || graph.BoundingBox.height <= 0f) return;

            Rect bb = graph.BoundingBox;
            float fit = Mathf.Min(canvasRect.width / bb.width, canvasRect.height / bb.height);
            zoomLevel = Mathf.Clamp(fit, Constraints.MinZoom, Constraints.MaxZoom);

            StartScrollAnimation(new Vector2(canvasRect.width / 2f, canvasRect.height / 2f) - (bb.center * zoomLevel));
        }

        /// <summary>프록시면 원본이 있는 그래프로 전환해서 포커스</summary>
        private void JumpToNode(ResearchNode node)
        {
            if (node == null || node.IsDummy) return;
            var realNode = RealNodeOf(node);
            if (realNode.IsHidden) return; // 미발견 연구로는 점프 불가

            if (!realNode.Key.Equals(CurrentKey))
            {
                SwitchGraph(realNode.Key, playSound: false); // 아래 Click과 소리가 겹치지 않게
            }
            FocusOnNode(realNode);
            SoundDefOf.Click.PlayOneShotOnCamera();
            if (!history.HasCurrent || history.Current != realNode)
            {
                history.Push(realNode);
                leftPanelScrollPosition = Vector2.zero;
            }
        }
    }
}
