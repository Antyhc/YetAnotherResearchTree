using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using YART.Compat;
using YART.Utils;

namespace YART.Data
{
    public enum ResearchNodeState : byte
    {
        Locked,
        Available,
        InProgress,
        Completed
    }

    public class ResearchNode
    {
        public ResearchProjectDef Def { get; }

        public ResearchChannel Channel { get; }

        /// <summary>벤치 채널 전용, 그 외 null</summary>
        public ResearchTabDef Tab { get; }

        // 병합 그래프처럼 Channel/Tab으로 재구성할 수 없는 키용. null이면 Channel/Tab에서 계산
        private readonly GraphKey? _keyOverride;

        public GraphKey Key => _keyOverride.HasValue ? _keyOverride.Value : new GraphKey(Channel, Tab);

        public bool IsDummy { get; }

        public string Id
        {
            get
            {
                if (IsDummy) return $"Dummy_{GetHashCode()}";
                return IsProxy ? $"Proxy_{OriginalNode.Id}" : Def.defName;
            }
        }

        private static string _unknownLabel;
        private static string UnknownLabel => _unknownLabel ?? (_unknownLabel = string.Format("({0})", "UnknownResearch".Translate()));

        public string Label
        {
            get
            {
                if (IsDummy) return string.Empty;
                if (IsProxy) return OriginalNode.Label;
                if (IsHidden) return UnknownLabel;
                return Def.label.CapitalizeFirst();
            }
        }

        public bool IsProxy { get; }

        public ResearchNode OriginalNode { get; }

        public int Rank { get; set; }

        /// <summary>같은 Rank 안의 세로 순서</summary>
        public int VOrder { get; set; }

        public string GroupKey { get; set; }

        /// <summary>좌상단 기준</summary>
        public Vector2 Position { get; set; }
        public Rect Rect => new Rect(Position.x, Position.y, Constraints.NodeSize.x, Constraints.NodeSize.y);
        public Vector2 Center => new Vector2(Position.x + Constraints.NodeSize.x / 2f, Position.y + Constraints.NodeSize.y / 2f);

        public List<ResearchNode> Prerequisites { get; } = new List<ResearchNode>();

        private List<ResearchNode> _cachedAncestors;

        public List<ResearchNode> Ancestors => _cachedAncestors ?? (_cachedAncestors = CollectAncestors());

        private List<ResearchNode> CollectAncestors()
        {
            var ancestors = new HashSet<ResearchNode>();

            void Walk(ResearchNode curNode)
            {
                foreach (var prereq in curNode.Prerequisites.Where(prereq => ancestors.Add(prereq)))
                {
                    Walk(prereq);
                }
            }

            Walk(this);
            return ancestors.ToList();
        }

        public List<ResearchNode> Children { get; } = new List<ResearchNode>();

        // 좌측 패널용, 그래프 빌드에서 채움
        public List<ResearchNode> PanelPrerequisites { get; set; }
        public List<ResearchNode> PanelChildren { get; set; }

        // 다른 그래프(탭/채널)에 있는 후행 연구
        public List<ResearchNode> ExternalChildren { get; set; }


        private ResearchNodeState _cachedState = ResearchNodeState.Locked;
        private float _lastStateUpdateTime = float.NegativeInfinity;
        private int _lastStateVersion = -1;

        private static int _globalStateVersion;

        public static void InvalidateAllStates() => _globalStateVersion++;

        public ResearchNodeState State
        {
            get
            {
                if (IsDummy) return ResearchNodeState.Locked;
                if (IsProxy) return OriginalNode.State;

                // 레이아웃 빌드 스레드에서 CalculateState를 부르면 AssertMainThread에 걸림. 레이아웃은 State를 안 씀
                if (!UnityData.IsInMainThread) return _cachedState;

                if (_lastStateVersion != _globalStateVersion
                    || Time.realtimeSinceStartup - _lastStateUpdateTime > 0.5f)
                {
                    _cachedState = CalculateState();
                    _cachedHidden = Def.IsHidden;
                    _lastStateUpdateTime = Time.realtimeSinceStartup;
                    _lastStateVersion = _globalStateVersion;
                }
                return _cachedState;
            }
        }

        private bool _cachedHidden;

        /// <summary>아노말리 미발견 연구</summary>
        public bool IsHidden
        {
            get
            {
                if (IsDummy) return false;
                if (IsProxy) return OriginalNode.IsHidden;
                // 그래프 빌드(메인 메뉴 LongEvent) 중에 부르면 NRE
                if (Current.ProgramState != ProgramState.Playing) return false;
                _ = State;
                return _cachedHidden;
            }
        }

        private ResearchNodeState CalculateState()
        {
            if (Def.IsFinished) return ResearchNodeState.Completed;
            if (Find.ResearchManager.IsCurrentProject(Def)) return ResearchNodeState.InProgress;
            if (CanBeQueuedRecursively()) return ResearchNodeState.Available;
            return ResearchNodeState.Locked;
        }

        /// <summary>0 ~ 1</summary>
        public float Progress
        {
            get
            {
                if (IsDummy) return 0f;
                if (IsProxy) return OriginalNode.Progress;
                if (Def.IsFinished) return 1f;
                return Def.ProgressPercent;
            }
        }

        private TechLevel? _techLevelInternal;

        public TechLevel TechLevel
        {
            get
            {
                if (_techLevelInternal != null) return _techLevelInternal.Value;

                if (IsDummy) return TechLevel.Undefined;
                _techLevelInternal = IsProxy ? OriginalNode.TechLevel : Def.techLevel;
                return _techLevelInternal.Value;
            }
            set
            {
                _techLevelInternal = value;
            }
        }

        /// <summary>max(자신, 조상 전체). SugiyamaLayout이 채움</summary>
        public TechLevel? EffectiveTechLevelInternal { get; set; }

        private TechLevel ColorTechLevel =>
            YARTMod.Settings.unifyEraColorToEffective && EffectiveTechLevelInternal.HasValue
                ? EffectiveTechLevelInternal.Value
                : TechLevel;

        public Color EraAccentColor
        {
            get
            {
                if (IsDummy) return Constraints.EdgeDefault;
                if (IsProxy) return OriginalNode.EraAccentColor;

                return ActiveColors.Era(ColorTechLevel);
            }
        }

        public ModContentPack SourceMod => IsProxy ? OriginalNode.SourceMod : Def?.modContentPack;

        private List<Def> _cachedUnlockedDefs;

        public List<Def> UnlockedDefs => _cachedUnlockedDefs ?? (_cachedUnlockedDefs = BuildUnlockedDefs());

        private List<Def> BuildUnlockedDefs()
        {
            var unlocked = new List<Def>();
            if (IsDummy || IsProxy || Def == null) return unlocked;

            if (Def.UnlockedDefs != null) unlocked.AddRange(Def.UnlockedDefs);
            unlocked.AddRange(DefSnapshot.ReverseUnlocksOf(Def));

            return unlocked.Distinct().ToList();
        }

        private List<UnlockedDefsUtility.UnlockedGroup> _cachedUnlockedGroups;

        /// <summary>추가로 필요한 선행연구 집합별로 묶음, 조건 적은 그룹부터</summary>
        public List<UnlockedDefsUtility.UnlockedGroup> UnlockedDefsGrouped =>
            _cachedUnlockedGroups ?? (_cachedUnlockedGroups = UnlockedDefsUtility.GroupUnlockedDefs(UnlockedDefs, Def));

        /// <summary>키는 Channel/Tab에서 재계산</summary>
        public ResearchNode(ResearchProjectDef def, GraphKey key)
            : this(def, key.Channel, key.Tab, null)
        {
        }

        public ResearchNode(ResearchNode originalNode, GraphKey targetKey)
        {
            Def = originalNode.Def;
            Channel = targetKey.Channel;
            Tab = targetKey.Tab;
            IsProxy = true;
            OriginalNode = originalNode;
            IsDummy = false;
            _keyOverride = targetKey;
            GroupKey = originalNode.Def?.tab?.defName;
        }

        // 더미 노드
        private ResearchNode(GraphKey key, bool isDummy)
        {
            Def = null;
            Channel = key.Channel;
            Tab = key.Tab;
            IsProxy = false;
            OriginalNode = null;
            IsDummy = isDummy;
            _keyOverride = key;
        }

        public static ResearchNode CreateMergedCopy(ResearchNode original, GraphKey targetKey)
        {
            return new ResearchNode(original.Def, original.Channel, original.Tab, targetKey);
        }

        // keyOverride != null이면 병합 그래프용 복사본
        private ResearchNode(ResearchProjectDef def, ResearchChannel channel, ResearchTabDef tab, GraphKey? keyOverride)
        {
            Def = def;
            Channel = channel;
            Tab = tab;
            IsProxy = false;
            OriginalNode = null;
            IsDummy = false;
            _keyOverride = keyOverride;
            GroupKey = def?.tab?.defName; // 그룹 클러스터링 키, null이면 클러스터링 안 함
        }

        public bool CanBeQueuedRecursively(HashSet<ResearchProjectDef> visited = null)
        {
            if (IsDummy) return false;
            if (IsProxy) return OriginalNode.CanBeQueuedRecursively(visited);

            // 큐에 넣진 않지만 체인상으로는 유효
            if (Def.IsFinished) return true;

            if (!CanResearchIgnoringPrerequisites()) return false;

            if (visited == null) visited = new HashSet<ResearchProjectDef>();
            if (!visited.Add(Def)) return false; // 사이클

            var graph = ResearchGraph.Instance;
            if (graph == null || !graph.Initialized) return false;

            return PrerequisitesQueueable(Def.prerequisites, visited, graph)
                && PrerequisitesQueueable(Def.hiddenPrerequisites, visited, graph);
        }

        private static bool PrerequisitesQueueable(List<ResearchProjectDef> prereqDefs, HashSet<ResearchProjectDef> visited, ResearchGraph graph)
        {
            if (prereqDefs == null) return true;
            foreach (var prereqDef in prereqDefs)
            {
                if (prereqDef.IsFinished) continue;
                if (!graph.AllNodes.TryGetValue(prereqDef, out var prereqNode)) return false;
                if (!prereqNode.CanBeQueuedRecursively(visited)) return false;
            }
            return true;
        }

        private bool CanResearchIgnoringPrerequisites()
        {
            if (IsDummy) return false;
            if (IsProxy) return OriginalNode.CanResearchIgnoringPrerequisites();
            if (Def.IsFinished) return false;

            // 여러 모드가 CanStartNow에 Postfix를 걸기 때문에 쓸 수 있으면 그걸로 판별
            if (Def.PrerequisitesCompleted) return Def.CanStartNow;

            // Medieval Overhaul은 CanStartNow를 패치하므로 선행 미완료 경로에서도 도식 조건을 따로 봐야 함
            return Def.TechprintRequirementMet
                && (Def.requiredResearchBuilding == null || Def.PlayerHasAnyAppropriateResearchBench)
                && Def.PlayerMechanitorRequirementMet
                && Def.AnalyzedThingsRequirementsMet
                && !Def.IsHidden
                && Def.InspectionRequirementsMet
                && MedievalOverhaulCompat.SchematicRequirementMet(Def);
        }

        /// <summary>바닐라 MainTabWindow_Research.DrawStartButton의 lockedReasons와 같은 판정</summary>
        public void GetLockedReasons(List<string> outReasons)
        {
            outReasons.Clear();
            if (IsDummy) return;
            if (IsProxy) { OriginalNode.GetLockedReasons(outReasons); return; }
            if (Def.IsFinished) return;

            if (!Def.TechprintRequirementMet)
            {
                outReasons.Add("InsufficientTechprintsApplied".Translate(Def.TechprintsApplied, Def.TechprintCount));
            }
            if (!Def.InspectionRequirementsMet)
            {
                outReasons.Add("MissingGravEngineInspection".Translate());
            }
            if (!Def.PlayerMechanitorRequirementMet)
            {
                outReasons.Add("MissingRequiredMechanitor".Translate());
            }
            if (!Def.AnalyzedThingsRequirementsMet && Def.requiredAnalyzed != null)
            {
                foreach (var thing in Def.requiredAnalyzed)
                {
                    outReasons.Add("NotStudied".Translate(thing.LabelCap));
                }
            }
            if (Def.requiredResearchBuilding != null && !Def.PlayerHasAnyAppropriateResearchBench)
            {
                outReasons.Add("MissingRequiredResearchFacilities".Translate() + " (" + RequiredBenchDetail() + ")");
            }
            if (!MedievalOverhaulCompat.SchematicRequirementMet(Def))
            {
                var schematic = MedievalOverhaulCompat.RequiredSchematicFor(Def);
                outReasons.Add("DankPyon_RequiredSchematic".Translate() + ": " + schematic.LabelCap);
            }

            // 자체 조건은 충족. 선행 체인의 잠긴 연구를 안내
            if (outReasons.Count == 0 && State == ResearchNodeState.Locked)
            {
                AddLockedAncestorReasons(outReasons);
            }
        }

        /// <summary>CanBeResearchedAt은 연결된 활성 시설(복합분석기 등)까지 검사하므로 작업대와 시설을 둘 다 표시</summary>
        private string RequiredBenchDetail()
        {
            string detail = Def.requiredResearchBuilding.LabelCap;
            if (!Def.requiredResearchFacilities.NullOrEmpty())
            {
                detail += " + " + string.Join(", ", Def.requiredResearchFacilities.Select(f => f.label));
            }
            return detail;
        }

        private void AddLockedAncestorReasons(List<string> outReasons)
        {
            foreach (var ancestor in Ancestors)
            {
                if (ancestor.IsDummy || ancestor.Def == null || ancestor.Def.IsFinished) continue;
                if (!ancestor.CanResearchIgnoringPrerequisites())
                {
                    outReasons.Add($"Prerequisite locked: {ancestor.Label}");
                }
            }
        }

        public Vector2 InputAnchor => new Vector2(Position.x, Position.y + Constraints.NodeSize.y / 2f);
        public Vector2 OutputAnchor => new Vector2(Position.x + Constraints.NodeSize.x, Position.y + Constraints.NodeSize.y / 2f);

        // 엣지 포트 분산
        private static int globalPortVersion;
        private int portVersion = -1;
        private Dictionary<ResearchNode, (float y, float ratio)> outputPorts;
        private Dictionary<ResearchNode, (float y, float ratio)> inputPorts;

        public static void InvalidateAllPorts()
        {
            globalPortVersion++;
        }

        public Vector2 GetOutputAnchor(ResearchNode to)
        {
            if (IsDummy || to == null) return OutputAnchor;
            EnsurePorts();
            return outputPorts.TryGetValue(to, out var port)
                ? new Vector2(Position.x + Constraints.NodeSize.x, port.y)
                : OutputAnchor;
        }

        public Vector2 GetInputAnchor(ResearchNode from)
        {
            if (IsDummy || from == null) return InputAnchor;
            EnsurePorts();
            return inputPorts.TryGetValue(from, out var port)
                ? new Vector2(Position.x, port.y)
                : InputAnchor;
        }

        /// <summary>팬 안의 정규화 순번, 0=최상단 1=최하단</summary>
        public float GetOutputPortRatio(ResearchNode to)
        {
            if (IsDummy || to == null) return 0.5f;
            EnsurePorts();
            return outputPorts.TryGetValue(to, out var port) ? port.ratio : 0.5f;
        }

        /// <summary>팬 안의 정규화 순번, 0=최상단 1=최하단</summary>
        public float GetInputPortRatio(ResearchNode from)
        {
            if (IsDummy || from == null) return 0.5f;
            EnsurePorts();
            return inputPorts.TryGetValue(from, out var port) ? port.ratio : 0.5f;
        }

        private void EnsurePorts()
        {
            if (portVersion == globalPortVersion && outputPorts != null) return;
            portVersion = globalPortVersion;
            outputPorts = BuildPorts(Children, c => c.InputAnchor.y);
            inputPorts = BuildPorts(Prerequisites, p => p.OutputAnchor.y);
        }

        private Dictionary<ResearchNode, (float y, float ratio)> BuildPorts(List<ResearchNode> counterparts, Func<ResearchNode, float> sortKey)
        {
            var ports = new Dictionary<ResearchNode, (float y, float ratio)>(counterparts.Count);
            int n = counterparts.Count;
            float centerY = Position.y + Constraints.NodeSize.y / 2f;
            switch (n)
            {
                case 0: return ports;
                case 1:
                    ports[counterparts[0]] = (centerY, 0.5f);
                    return ports;
            }

            // 상대 Y 순으로 배치해야 같은 노드의 엣지끼리 교차하지 않음
            var sorted = counterparts.OrderBy(sortKey).ThenBy(c => c.Position.x).ToList();
            float span = Constraints.NodeSize.y - 2f * Constraints.EdgePortPadding;
            float spacing = Mathf.Min(Constraints.EdgePortMaxSpacing, span / (n - 1));
            float firstY = centerY - spacing * (n - 1) / 2f;
            for (int i = 0; i < sorted.Count; i++)
            {
                ports[sorted[i]] = (firstY + spacing * i, i / (float)(n - 1));
            }
            return ports;
        }

        public override string ToString()
        {
            return $"ResearchNode[{Id}, Graph={Key}, Rank={Rank}]";
        }

        public static ResearchNode Create(ResearchProjectDef def)
        {
            return new ResearchNode(def, GraphKey.For(def));
        }

        public static ResearchNode CreateProxy(ResearchNode originalNode, GraphKey targetKey)
        {
            return new ResearchNode(originalNode, targetKey);
        }

        public static ResearchNode CreateDummy(GraphKey key)
        {
            return new ResearchNode(key, true);
        }

        public static void CollectConnectedNodes(ResearchNode node, HashSet<ResearchNode> collected, bool upstream, Predicate<ResearchNode> filter = null, Predicate<ResearchNode> recurseCondition = null)
        {
            var nextNodes = upstream ? node.Prerequisites : node.Children;
            foreach (var nextNode in nextNodes)
            {
                if (filter != null && !filter(nextNode)) continue;
                if (!collected.Add(nextNode)) continue;
                if (recurseCondition == null || recurseCondition(nextNode))
                {
                    CollectConnectedNodes(nextNode, collected, upstream, filter, recurseCondition);
                }
            }
        }

        public static ResearchChannel DetermineChannel(ResearchProjectDef def)
        {
            return ChannelRegistry.Of(def);
        }
    }
}
