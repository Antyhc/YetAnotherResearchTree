using UnityEngine;

namespace YART.Data
{
    public static class Constraints
    {
        // Viewport
        public const float DefaultZoom = 1.1f;
        public const float MinZoom = 0.20f;
        public const float MaxZoom = 2.0f;
        public const float CameraFocusAnimDuration = 0.3f;

        // Layout & Rendering
        public static readonly Vector2 NodeSize = new Vector2(200f, 50f);
        public static readonly Vector2 NodeSpacing = new Vector2(50f, 28f);
        public static readonly Vector2 BoundingBoxPadding = new Vector2(50f, 50f);
        public static readonly Vector2 CullingMargin = new Vector2(0f, 0f);
        public const float BottomTabHeight = 35f;

        // Input
        // 이 이상 움직이면 클릭이 아니라 드래그
        public const float ClickDragThreshold = 5f;
        public const float KeyboardPanSpeed = 1100f;
        // 패닝해도 그래프가 화면에 이만큼은 남음
        public const float PanKeepVisible = 220f;

        // Track Chips
        public const float TrackChipWidth = 190f;
        public const float TrackChipHeight = 56f;
        public const float TrackChipGap = 8f;

        // Queue Bar
        public const float QueueBarHeight = 112f;
        public const float QueueCardWidth = 188f;
        public const float QueueCardGap = 8f;

        // Tab Dropdown
        public const float TabDropdownWidth = 150f;
        public const float TabDropdownHeight = 30f;
        public const float TabPlusButtonSize = 30f;

        // Unified Bench Toggle
        public const float UnifiedBenchToggleWidth = 90f;

        // Search Box
        public const float SearchBoxWidth = UnifiedBenchToggleWidth + 8f + TabDropdownWidth + 6f + TabPlusButtonSize;

        /// <summary>모드 지식 카테고리 채널용</summary>
        public static readonly Color[] ChannelPalette =
        {
            new Color(0.95f, 0.65f, 0.25f), // Orange
            new Color(0.55f, 0.80f, 0.30f), // Lime
            new Color(0.90f, 0.45f, 0.75f), // Pink
            new Color(0.45f, 0.55f, 0.95f), // Indigo
        };

        public static readonly Color BackgroundColor = new Color(0.043f, 0.110f, 0.200f);

        // Background Layers
        public const float GridMinorSpacing = 40f;
        public const float GridMajorSpacing = 200f;
        public static readonly Color GridMinorColor = new Color(1f, 1f, 1f, 0.018f);
        public static readonly Color GridMajorColor = new Color(1f, 1f, 1f, 0.035f);
        public static readonly Color GridDotColor = new Color(0.133f, 0.275f, 0.478f, 0.35f);
        public const float GridDotSize = 7f;
        public const float GridMinorFadeOutZoom = 0.6f; // 이 줌 미만에서 보조선 페이드 아웃
        public const float EraZoneTintAlpha = 0.09f;
        public const float NoiseAlpha = 0.05f;
        public const float NoiseTileScreenSize = 512f;

        // Proxy Node
        public const float ProxyGhostFillFactor = 0.45f;
        public const float ProxyDashLength = 8f;
        public const float ProxyDashGap = 5f;
        public const float ProxyDashThickness = 2f;

        // Node LOD
        public const float NodeTextMinZoom = 0.65f;   // 이 줌 미만에선 배지 숨기고 라벨은 축소 표시
        public const float RichCardZoomStart = 1.1f;  // 페이드 시작
        public const float RichCardZoomFull = 1.3f;   // 완전 전환
        public const float RichCardExtraHeight = 16f; // 월드 단위 세로 확장 (50 -> 66)
        public const float NodeCornerRadius = 7f;     // 화면 px

        // Edge Energy Pulse
        public const float PulseSpeed = 150f;         // 월드 px/초
        public const float PulseArriveDelay = 0.8f;   // 도착 후 다음 펄스까지, 초
        public const float PulseTailLength = 0.18f;   // 경로 대비 꼬리 길이 비율

        // Edge Colors
        public static readonly Color EdgeDefault = new Color(0.3f, 0.35f, 0.45f);
        public static readonly Color EdgeHighlight = new Color(1f, 1f, 1f);
        public const float EdgeEraDesaturate = 0.3f;

        // Edge Line Ribbon
        public const float EdgeLineWidth = 3f;
        public const float EdgeLineMinWidth = 2f;

        // Edge Ports
        public const float EdgePortMaxSpacing = 12f;
        public const float EdgePortPadding = 10f;

        // Staggered Crossover
        public const float EdgeCrossoverMin = 0.18f;
        public const float EdgeCrossoverMax = 0.82f;

        // Button Colors
        public static readonly Color ButtonDisabled = new Color(0.2f, 0.2f, 0.22f);
        public static readonly Color ButtonDisabledText = new Color(0.4f, 0.4f, 0.45f);
        public static readonly Color ButtonActive = new Color(0.18f, 0.45f, 0.65f);
        public static readonly Color ButtonActiveHover = new Color(0.22f, 0.55f, 0.75f);

        // Panel Colors
        public static readonly Color PanelBg = new Color(0.035f, 0.085f, 0.155f, 0.96f);
        public static readonly Color PanelBorder = new Color(0.20f, 0.30f, 0.42f);

        // Detail Panel, 완료 편지 공용
        public static readonly Color SectionUnlocks       = new Color(0.6f, 1f, 0.6f);
        public static readonly Color SectionPrerequisites = new Color(1f, 0.7f, 0.4f);
        public static readonly Color SectionFollowups     = new Color(0.4f, 0.8f, 1f);
        public static readonly Color MutedText            = new Color(0.5f, 0.5f, 0.55f);
        public static readonly Color ToggleInactive       = new Color(0.7f, 0.75f, 0.82f);
        public static readonly Color BodyText             = new Color(0.82f, 0.85f, 0.9f);

        // Tech Level Lines
        public static readonly Color TechLevelLineColor = new Color(0.22f, 0.32f, 0.46f);
        public static readonly Color TechLevelTextColor = new Color(0.55f, 0.63f, 0.75f);
        public const float EraLabelTopMargin = 8f;
        public const float EraLabelSidePadding = 10f;

        // Focus Mode
        public const float FocusedOpacity = 1.0f;
        public const float UnfocusedNodeOpacity = 0.3f;
        public const float UnfocusedEdgeOpacity = 0.15f;
        public const float FocusFadeDuration = 0.23f;
        public const float FocusNodeGlow = 0.03f;
        public const float FocusNodeBorderWhiten = 0.1f;

        // Layout Algorithm Configuration
        public const float LayoutTechLevelBoundaryOffset = 0.5f;
        public const float LayoutTechLevelGapMultiplier = 2.0f;
        public const int LayoutRankTightenMaxPasses = 16;
        public const float LayoutDummyNodeHeight = 20f;
        public const float LayoutDummySpacing = 10f;
        public const float LayoutDummyVisualPad = 4f;

        // 높이 제한 레이어링의 탭별 기본값. 통합 탭은 SugiyamaLayout.ComputeColumnBudget가 산정
        public static float LayoutMaxColumnRows = 9.5f;
        public static float LayoutMaxColumnHeight => LayoutMaxColumnRows * (NodeSize.y + NodeSpacing.y);
        // 통합 그래프 예산 = rank별 실노드 수의 이 백분위수(행). 높을수록 덜 평탄화되고 rank 높이가 커짐
        public static float LayoutUnifiedBudgetPercentile = 0.61f;
        // transpose/DP가 빠져나오지 못하는 local optimum 탈출용
        public const int LayoutSiftingMaxPasses = 4;
        // PickCandidate 가드
        public static int LayoutMaxEdgeSpan = 4;
        // 밴드 확장으로 늘릴 수 있는 랭크 수 합계
        public static int LayoutHeightLimitMaxExtraRanks = 1;
        // 통로 포화 컬럼에서도 남겨 두는 실노드 최소 행 수
        public static int LayoutHeightLimitMinRows = 4;

        // Crossing Minimization Configuration
        public const int LayoutOrderingMaxIterations = 24;
        public const int LayoutOrderingPatience = 5;
        public const int LayoutTransposeMaxSweeps = 8;
        public const float LayoutGroupCrossEdgeWeight = 0.15f;
        public const int LayoutGroupSearchMaxPasses = 8;
        public const int LayoutExactLayerMaxNodes = 10; // 부분집합 DP를 적용할 레이어 크기 상한
        // 멀티 스타트 선택 점수 = 교차 수 + 이 값 x 높이행수(estHeight/gridY)
        public static float LayoutOrderingHeightWeight = 0.55f;

        // Layout Metrics
        public const float MetricsRefViewportW = 1920f - 300f;
        public const float MetricsRefViewportH = 1080f - 35f;
        public const float MetricsBendEpsilon = 0.5f;

        // Rank Cost 가중치
        public static float RankCostSpanWeight  = 0.07f; // lambda, 엣지 스팬 항
        public static float RankCostWidthWeight = 0.66f; // mu, 사용 랭크 수 항

        // Chain Shaping Convergence (ShapeChains)
        public const int LayoutChainShapeMaxPasses = 32;
        public const float LayoutChainConvergeEps = 1f;
        public const float LayoutChainHashQuantum = 0.5f;

        // Gauss-Seidel 스윕 수
        public const int LayoutSpringPasses = 16;

        // 이웃 평균 대신 현재 Y에 머무르려는 정도
        public static float LayoutSpringSelfAnchor = 0.4f;

        // Tikhonov 중심 정규화 lambda_base, 0=비활성. barycenter 분모에만 더해 노드를 중심선(0)으로 당김
        // 높이 슬랙(현재높이/H_floor)에 비례하므로 높이가 이미 바닥인 그래프(통합)에선 효과 없음
        public static float LayoutSpringCenterAnchor = 0.02f;
    }
}
