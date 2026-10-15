using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;
using YART.Data;
using YART.Utils;

namespace YART
{
    public partial class MainTabWindow_YART : MainTabWindow
    {
        private readonly HistoryNavigator<ResearchNode> history = new HistoryNavigator<ResearchNode>();
        private readonly List<Rect> interactionExcludeRects = new List<Rect>();

        // Viewport State
        private Vector2 scrollPosition = new Vector2(500f, 500f);
        private float zoomLevel = Constraints.DefaultZoom;

        private Vector2 leftPanelScrollPosition = Vector2.zero;
        private Rect visibleRect;
        private Rect canvasRect;
        private float eraLabelTopOffset;
        private float lastKeyPanTime;
        private ResearchProjectDef pendingOpenDef; // 외부(InfoCard 등)에서 특정 연구로 열어달라는 요청
        private string pendingSwitchPresetId;      // 프리셋 생성/편집 후 비동기 빌드 완료 시 자동 전환 대상

        // Drag / Click State.
        private Vector2 leftMouseDownPos;
        private bool leftMouseDownOnCanvas;
        private bool isDraggingCanvas;
        private bool clickPending;
        private Vector2 rightMouseDownPos;
        private bool rightMouseDownOnCanvas;
        private bool rightClickPending;

        // Graph Selection State (그래프별 뷰포트 저장/복원)
        // selectedTab: 벤치 채널에서 마지막으로 보던 탭 (다른 채널로 가도 기억)
        private ResearchChannel selectedChannelField;
        private ResearchChannel SelectedChannel
        {
            get { return selectedChannelField ?? (selectedChannelField = ChannelRegistry.Bench); }
            set { selectedChannelField = value; }
        }
        private ResearchTabDef selectedTab; // null = Main (GraphKey 생성자가 보정)
        private readonly Dictionary<GraphKey, Vector2> graphScrollPositions = new Dictionary<GraphKey, Vector2>();
        private readonly Dictionary<GraphKey, float> graphZoomLevels = new Dictionary<GraphKey, float>();

        // DoWindowContents 진입 후 설정에 따른 통합 뷰 자동 전환을 한 번만 실행하는 단발 가드
        private bool unifiedViewApplied;

        // 현재 보고 있는 프리셋(탭 그룹 또는 내장 통합). null이면 일반 탭 뷰.
        private string currentPresetId;

        /// <summary>현재 캔버스에 표시 중인 서브그래프 키</summary>
        private GraphKey CurrentKey => currentPresetId != null
            ? GraphKey.ForPreset(currentPresetId)
            : new GraphKey(SelectedChannel, selectedTab);

        /// <summary>현재 통합 벤치 뷰(모든 탭 = 내장 프리셋)를 보고 있는가.</summary>
        private bool ViewingAllTabs => currentPresetId == GraphKey.AllTabsId;

        // Queue Bar State
        private Vector2 queueBarScrollPosition = Vector2.zero;
        private ResearchProjectDef queueBarHoveredDef;
        private ResearchProjectDef draggedQueueDef;
        private ResearchProjectDef queueCardMouseDownDef;
        private Vector2 queueCardMouseDownPos;

        // Focus Mode State
        private ResearchNode hoveredNode = null;
        private readonly HashSet<ResearchNode> focusedNodes = new HashSet<ResearchNode>();
        // 호버 포커스 페이드값 (0=비활성, 1=포커스).
        private float focusAmount = 0f;
        private float lastFocusFadeTime = 0f;

        // External Dependents Popup (외부 후행 배지 호버 목록)
        private ResearchNode externalListNode;        // 현재 팝업 대상 (null = 닫힘)
        private Rect externalListRect;                // 직전 프레임 팝업 영역 (유지 판정 + 캔버스 입력 차단)
        private Rect externalListAnchor;              // 대상 배지 rect
        private int externalListPage;
        private ResearchNode externalListCandidate;   // 이번 프레임 배지 호버 (배지 draw에서 기록)
        private Rect externalListCandidateAnchor;

        // Camera Focus Animation (FocusOnNode 부드러운 이동)
        private bool scrollAnimating;
        private Vector2 scrollAnimStart;
        private Vector2 scrollAnimTarget;
        private float scrollAnimStartTime;

        // 인게임 Rebuild 후 노드 참조 무효화 감지 — Generation이 변하면 스테일 참조 초기화
        private int lastSeenGeneration;

        // 이 창이 게임을 일시정지시켰는지
        private bool pausedByYart;

        public override void PreOpen()
        {
            base.PreOpen();

            preventCameraMotion = true;

            MaybePauseGame();

            // PreOpen에서도 설정하지만 SetInitialSizeAndPosition에서도 설정하여 확실하게 함
            this.windowRect.x = 0;
            this.windowRect.y = 0;
            this.windowRect.width = UI.screenWidth;
            this.windowRect.height = UI.screenHeight - Constraints.BottomTabHeight;

            // 창이 열릴 때 현재 제너레이션을 기준으로 삼는다.
            lastSeenGeneration = GraphBuildPipeline.Generation;

            bool viewReady = GraphBuildPipeline.GraphReady;
            if (viewReady) SaveCurrentView();

            // 통합 뷰 상태 초기화 — DoWindowContents 첫 진입 시 설정 기반 자동 전환을 수행한다.
            currentPresetId = null;
            unifiedViewApplied = false;

            if (viewReady) RestoreView(CurrentKey);

            // World Tech Level 등으로 가시성 필터 레벨이 바뀌었으면(주로 게임 진입 1회) 그래프를
            // 백그라운드로 다시 빌드한다. 필터는 정적이라 게임당 거의 1회만 트리거된다.
            if (WorldTechLevelCompat.NeedsRebuild)
                GraphBuildPipeline.RebuildNonBlocking();

            // 백그라운드 빌드 미완이어도 블로킹하지 않는다 — DoWindowContents가 준비될 때까지
            // 안내 라벨을 그리고, 창은 평소처럼 닫을 수 있다. 실패/미시작 시에만 동기 폴백.
            GraphBuildPipeline.EnsureBuiltNonBlocking();
        }

        private void MaybePauseGame()
        {
            pausedByYart = false;
            if (!YARTMod.Settings.pauseGameWhenOpen) return;
            if (MultiplayerCompat.InMultiplayer) return;

            var tm = Find.TickManager;
            if (tm == null || tm.CurTimeSpeed == TimeSpeed.Paused) return;

            tm.Pause();
            pausedByYart = true;
        }

        public override void PostClose()
        {
            base.PostClose();

            if (!pausedByYart) return;
            pausedByYart = false;

            var tm = Find.TickManager;
            if (tm != null && tm.CurTimeSpeed == TimeSpeed.Paused)
                tm.TogglePaused();
        }

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            this.windowRect = new Rect(0, 0, UI.screenWidth, UI.screenHeight - Constraints.BottomTabHeight);
        }

        /// <summary>그래프가 빌드되지 않는 동안 표시</summary>
        private static void DrawGraphBuildingNotice(Rect inRect)
        {
            using (Temporary.Anchor(TextAnchor.MiddleCenter))
            {
                using (Temporary.Font(GameFont.Medium))
                {
                    int dots = 1 + (int)(Time.realtimeSinceStartup * 2f) % 3;
                    GUI.color = new Color(1f, 1f, 1f, 0.75f);
                    Widgets.Label(inRect, "YART_Building".Translate() + new string('.', dots));
                }
                using (Temporary.Font(GameFont.Small))
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.45f);
                    var hintRect = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height);
                    Widgets.Label(hintRect, "YART_BuildingHint".Translate());
                }
                GUI.color = Color.white;
            }
        }

        /// <summary>한 프레임의 HUD/패널 배치. 모두 inRect 기준으로 미리 계산해 둔다.</summary>
        private struct FrameLayout
        {
            public Rect? QueueBar;       // null = 큐 매니저 없음 (큐 바 미표시)
            public Rect TopControls;
            public Rect? LeftPanel;      // null = 선택 노드 없음
            public Rect Chips;
            public Rect Favorites;
            public Rect Zoom;
            public Rect VanillaSwitch;
            public Rect Settings;
        }

        public override void DoWindowContents(Rect inRect)
        {
            using (Temporary.Anchor(TextAnchor.UpperLeft))
            using (Temporary.Font(GameFont.Small))
            {
                if (!GraphBuildPipeline.GraphReady)
                {
                    DrawGraphBuildingNotice(inRect);
                    return;
                }

                ResetStaleStateOnRebuild();
                ApplyUnifiedViewOnce();
                ApplyPendingPresetSwitch();
                ApplyPendingOpenDef();

                HandleHistoryMouseButtons();
                HandleGlobalKeys();

                canvasRect = inRect; // 전체 화면을 캔버스로 사용
                var layout = ComputeFrameLayout(inRect);
                eraLabelTopOffset = layout.QueueBar != null ? Constraints.QueueBarHeight : 52f;
                RebuildInteractionExcludeRects(layout);

                DrawFrame(inRect, layout);

                // 보조 마우스 버튼은 게임 카메라로 새지 않게 여기서 소비 (창 입력 격리)
                if ((Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
                    && Event.current.button >= 2)
                {
                    Event.current.Use();
                }
            }
        }

        /// <summary>인게임 Rebuild(Generation 변화) 후 무효가 된 노드 참조를 버린다.</summary>
        private void ResetStaleStateOnRebuild()
        {
            if (GraphBuildPipeline.Generation == lastSeenGeneration) return;

            history.Clear();
            focusedNodes.Clear();
            hoveredNode = null;
            externalListNode = null;
            externalListCandidate = null;
            lastSeenGeneration = GraphBuildPipeline.Generation;
        }

        /// <summary>설정에 따른 통합 뷰 자동 전환 - 창을 연 뒤 한 번만 수행한다.</summary>
        private void ApplyUnifiedViewOnce()
        {
            if (unifiedViewApplied) return;

            bool wantUnified = YARTMod.Settings.unifiedBenchView
                               && SelectedChannel.IsBench
                               && GetVisibleStandardTabs().Count >= 2;
            if (!wantUnified)
            {
                unifiedViewApplied = true;
                return;
            }

            // 빌드가 실패하면 다음 프레임에 재시도 (플래그를 세우지 않음)
            if (!EnsureUnifiedBenchGraph()) return;

            SwitchGraph(GraphKey.UnifiedBench, playSound: false);
            unifiedViewApplied = true;
        }

        /// <summary>프리셋 생성/편집 후 비동기 빌드가 끝나면 그 프리셋 뷰로 자동 전환.</summary>
        private void ApplyPendingPresetSwitch()
        {
            if (pendingSwitchPresetId == null) return;

            var presetKey = GraphKey.ForPreset(pendingSwitchPresetId);
            if (ResearchGraph.Instance.GetSubGraph(presetKey) != null)
            {
                SwitchGraph(presetKey, playSound: false);
                pendingSwitchPresetId = null;
            }
            else if (TabPresetManager.ById(pendingSwitchPresetId) == null)
            {
                pendingSwitchPresetId = null; // 삭제됐거나 사라진 프리셋 — 포기
            }
        }

        /// <summary>외부(InfoCard 등)에서 요청한 연구로 점프.</summary>
        private void ApplyPendingOpenDef()
        {
            if (pendingOpenDef == null) return;

            var node = GetNodeOnCurrentGraph(pendingOpenDef);
            if (node == null) ResearchGraph.Instance.AllNodes.TryGetValue(pendingOpenDef, out node);
            if (node != null) JumpToNode(node);
            pendingOpenDef = null;
        }

        /// <summary>마우스 뒤로(3)/앞으로(4) 버튼 = 선택 히스토리 이동. 두 버튼 모두 여기서 소비한다.</summary>
        private void HandleHistoryMouseButtons()
        {
            if (Event.current.type != EventType.MouseDown) return;
            int button = Event.current.button;
            if (button != 3 && button != 4) return;

            bool moved = false;
            if (button == 3 && history.CanUndo) { history.Undo(); moved = true; }
            else if (button == 4 && history.CanRedo) { history.Redo(); moved = true; }

            if (moved)
            {
                leftPanelScrollPosition = Vector2.zero;
                NavigateToHistoryNode(history.Current);
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
            Event.current.Use();
        }

        /// <summary>Ctrl+F = 검색창 포커스, Home = 전체 보기.</summary>
        private void HandleGlobalKeys()
        {
            if (Event.current.type != EventType.KeyDown) return;

            if (Event.current.control && Event.current.keyCode == KeyCode.F)
            {
                GUI.FocusControl(SearchFieldName);
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.Home && !SearchFieldFocused)
            {
                ZoomToFit();
                SoundDefOf.Click.PlayOneShotOnCamera();
                Event.current.Use();
            }
        }

        private FrameLayout ComputeFrameLayout(Rect inRect)
        {
            var layout = new FrameLayout
            {
                QueueBar = ResearchQueueManager.Instance != null
                    ? new Rect(0f, 0f, inRect.width, Constraints.QueueBarHeight)
                    : (Rect?)null,
                TopControls = ComputeTopLeftControlsRect(),
                Chips = ComputeTrackChipsRect(inRect),
                Favorites = ComputeFavoritesRect(inRect),
            };

            if (history.HasCurrent)
            {
                float width = Mathf.Max(300f, inRect.width * 0.25f);
                layout.LeftPanel = new Rect(0f, Constraints.QueueBarHeight, width,
                    inRect.height - Constraints.QueueBarHeight);
            }

            // 좌측 패널 오른쪽에 붙는 하단 줌 라벨 + 아이콘 버튼 두 개
            layout.Zoom = new Rect((layout.LeftPanel?.xMax ?? 0f) + 16f, inRect.yMax - 40f, 80f, 24f);
            layout.VanillaSwitch = new Rect(layout.Zoom.xMax + 6f, layout.Zoom.y, 24f, 24f);
            layout.Settings = new Rect(layout.VanillaSwitch.xMax + 6f, layout.Zoom.y, 24f, 24f);
            return layout;
        }

        /// <summary>캔버스 입력을 가로막는 UI 영역 목록을 이번 프레임용으로 다시 만든다.</summary>
        private void RebuildInteractionExcludeRects(FrameLayout layout)
        {
            interactionExcludeRects.Clear();
            interactionExcludeRects.Add(layout.TopControls);
            if (layout.QueueBar != null) interactionExcludeRects.Add(layout.QueueBar.Value);
            // 드롭다운/팝업은 직전 프레임 위치 — IMGUI 특성상 1프레임 지연은 무해
            if (searchResultsRect.height > 0f) interactionExcludeRects.Add(searchResultsRect);
            if (externalListRect.height > 0f) interactionExcludeRects.Add(externalListRect);
            if (layout.LeftPanel != null) interactionExcludeRects.Add(layout.LeftPanel.Value);
            if (layout.Chips.height > 0f) interactionExcludeRects.Add(layout.Chips);
            if (layout.Favorites.height > 0f) interactionExcludeRects.Add(layout.Favorites);
            interactionExcludeRects.Add(layout.Zoom);
            interactionExcludeRects.Add(layout.VanillaSwitch);
            interactionExcludeRects.Add(layout.Settings);
        }

        /// <summary>그리기 순서: 캔버스 → 큐 바 → 좌측 패널 → 플로팅 HUD → 팝업 → 오버레이.</summary>
        private void DrawFrame(Rect inRect, FrameLayout layout)
        {
            DoGraphCanvas(canvasRect);

            if (layout.QueueBar != null) DoQueueBar(layout.QueueBar.Value, layout.TopControls.width);
            else queueBarHoveredDef = null;

            HandleSearchDropdownScroll();

            if (layout.LeftPanel != null) DoLeftRect(layout.LeftPanel.Value);

            DoTopLeftControls();
            DoTrackChipsColumn(layout.Chips);
            DoFavoritesList(layout.Favorites);
            DoZoomIndicator(layout.Zoom);
            DoVanillaSwitchButton(layout.VanillaSwitch);
            DoSettingsButton(layout.Settings);

            DrawExternalDependentsPopup();
            DrawPerfOverlay(inRect);
        }

        private void SaveCurrentView()
        {
            graphScrollPositions[CurrentKey] = scrollAnimating ? scrollAnimTarget : scrollPosition;
            graphZoomLevels[CurrentKey] = zoomLevel;
        }

        private void RestoreView(GraphKey key)
        {
            scrollPosition = graphScrollPositions.TryGetValue(key, out var s) ? s : new Vector2(500f, 500f);
            zoomLevel = graphZoomLevels.TryGetValue(key, out var z) ? z : Constraints.DefaultZoom;
            scrollAnimating = false;
        }

        private void SwitchGraph(GraphKey key, bool playSound = true)
        {
            if (key.Equals(CurrentKey)) return;

            SaveCurrentView();
            scrollAnimating = false;

            currentPresetId = key.IsPreset ? key.PresetId : null;
            SelectedChannel = key.Channel;
            if (!key.IsPreset && key.Channel.IsBench) selectedTab = key.Tab;

            RestoreView(key);

            hoveredNode = null;
            focusedNodes.Clear();
            focusAmount = 0f;
            queueBarHoveredDef = null;
            draggedQueueDef = null;
            if (playSound) SoundDefOf.Click.PlayOneShotOnCamera();
        }

        /// <summary>
        /// 외부(바닐라 InfoCard 하이퍼링크 등)에서 특정 연구로 YART를 열어달라는 요청.
        /// </summary>
        public void RequestOpenAt(ResearchProjectDef def)
        {
            pendingOpenDef = def;
        }

        // ── 프리셋 에디터(Dialog_PresetEditor)용 공개 API ───────────────────────────
        /// <summary>현재 표시 중인 그래프 키 (에디터가 프리뷰 전 복원 지점을 기억하는 용도).</summary>
        public GraphKey CurrentGraphKey => CurrentKey;

        /// <summary>에디터의 눈(프리뷰) 또는 복원에서 캔버스를 특정 키로 즉시 전환 (그래프 준비 시에만).</summary>
        public void PreviewGraphKey(GraphKey key)
        {
            if (GraphBuildPipeline.GraphReady) SwitchGraph(key, playSound: false);
        }

        /// <summary>프리셋 생성/편집 후 비동기 빌드가 끝나면 그 프리셋 뷰로 자동 전환하도록 예약.</summary>
        public void RequestPresetSwitch(string presetId)
        {
            pendingSwitchPresetId = presetId;
        }

        /// <summary>삭제 등으로 현재 프리셋 뷰가 무효가 되면 첫 일반 탭으로 폴백.</summary>
        public void FallbackFromPresetIfViewing(string presetId)
        {
            if (currentPresetId != presetId) return;
            var tabs = GetVisibleStandardTabs();
            SwitchGraph(tabs.Count > 0
                ? new GraphKey(ChannelRegistry.Bench, tabs[0])
                : new GraphKey(ChannelRegistry.Bench), playSound: false);
        }

        /// <summary>
        /// def를 현재 선택된 캔버스에서 표현하는 노드(원본 또는 프록시)를 반환합니다. 없으면 null.
        /// </summary>
        private ResearchNode GetNodeOnCurrentGraph(ResearchProjectDef def)
        {
            var graph = ResearchGraph.Instance;
            if (def == null || !graph.Initialized) return null;
            return graph.GetNodeForGraph(def, CurrentKey);
        }

        private bool IsMouseOverBlockingElement()
        {
            Vector2 mousePos = Event.current.mousePosition;
            for (int i = 0; i < interactionExcludeRects.Count; i++)
            {
                if (interactionExcludeRects[i].Contains(mousePos)) return true;
            }
            return false;
        }
    }
}
