using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LudeonTK;
using UnityEngine;
using Verse;
using YART.Data;

namespace YART
{
    [StaticConstructorOnStartup]
    public static class GraphBuildPipeline
    {
        private static volatile bool layoutCalculated = false;
        private static Task graphBuildTask;
        private static volatile bool graphReady;

        public static bool GraphReady => graphReady;

        public static ResearchSearchEngine SearchEngine { get; } = new ResearchSearchEngine();

        static GraphBuildPipeline()
        {
            ScheduleWarmedBackgroundBuild(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                BuildGraphAndLayout();
                LogElapsed(stopwatch, "Research graph built in background");
            }, "Background graph build failed");
        }

        private static void ScheduleWarmedBackgroundBuild(Action build, string failureMessage)
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                WarmUnlockedDefsCache();
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    DefSnapshot.Capture();
                    graphBuildTask = Task.Run(() =>
                    {
                        try { build(); }
                        catch (Exception ex) { Log.Error("[YART] " + failureMessage + ": " + ex); }
                    });
                });
            });
        }

        private static void RefreshGraphReady()
        {
            graphReady = ResearchGraph.Instance.Initialized && layoutCalculated && SearchEngine.IsBuilt;
        }

        private static void LogElapsed(Stopwatch stopwatch, string what, string suffix = "")
        {
            stopwatch.Stop();
            if (Prefs.DevMode)
                Log.Message($"[YART] {what} in {stopwatch.ElapsedMilliseconds} ms{suffix}");
        }


        private static void WarmUnlockedDefsCache()
        {
            // VFE Tribals 등이 UnlockedDefs getter를 패치해 첫 접근에 텍스처를 로드함
            // 워커 스레드에서 처음 접근하면 "different thread" 오류라 메인 스레드에서 미리 채운다
            var defs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                try { _ = defs[i].UnlockedDefs; }
                catch (Exception ex) { Log.Error("[YART] WarmUnlockedDefsCache failed for " + defs[i].defName + ": " + ex); }
            }
        }

        /// <summary>백그라운드 빌드 중이면 그냥 반환. 실패/미시작일 때만 동기 빌드</summary>
        public static void EnsureBuiltNonBlocking()
        {
            var task = graphBuildTask;
            if (!graphReady && (task == null || task.IsCompleted))
            {
                DefSnapshot.Capture();
                BuildGraphAndLayout();
            }
        }

        /// <summary>백그라운드 빌드 중이면 끝날 때까지 대기</summary>
        public static void EnsureBuilt()
        {
            var task = graphBuildTask;
            if (task != null && !task.IsCompleted)
            {
                var stopwatch = Stopwatch.StartNew();
                task.Wait();
                stopwatch.Stop();
                if (Prefs.DevMode && stopwatch.ElapsedMilliseconds > 1)
                {
                    Log.Message("[YART] Waited " + stopwatch.ElapsedMilliseconds + " ms for background graph build");
                }
            }

            // 백그라운드 실패/미시작 폴백. 예외는 태스크 안에서 삼키므로 상태로 판단
            if (!graphReady)
            {
                DefSnapshot.Capture();
                BuildGraphAndLayout();
            }
        }

        private static void BuildGraphAndLayout()
        {
            if (!ResearchGraph.Instance.Initialized)
            {
                ResearchGraph.Instance.Build();
                layoutCalculated = false;
            }

            if (!layoutCalculated)
            {
                RunLayoutAndSearch();
            }
            else if (!SearchEngine.IsBuilt)
            {
                SearchEngine.Build(ResearchGraph.Instance.AllNodes.Values);
            }

            RefreshGraphReady();
        }

        private static void RebuildEverything()
        {
            layoutCalculated = false;
            SearchEngine.Invalidate();
            ResearchGraph.Instance.Build();
            RunLayoutAndSearch();
            Rendering.EdgeRenderer.ClearCache();
            Generation++;
            RefreshGraphReady();
        }

        public static bool? AppliedGroupClusterByTab { get; private set; }

        private static void RunLayoutAndSearch()
        {
            var graph = ResearchGraph.Instance;
            LayoutExport.Begin();

            SugiyamaLayout.GroupClusteringEnabled = YARTMod.Settings.groupClusterByTab;
            AppliedGroupClusterByTab = SugiyamaLayout.GroupClusteringEnabled;

            // 1. 일반 탭 레이아웃
            Parallel.ForEach(graph.SubGraphs.Values.ToList(),
                subGraph => new SugiyamaLayout().Calculate(subGraph));

            // 2. 병합 서브그래프(통합 벤치, 프리셋)는 구성만
            ConstructMergedSubGraphs(graph);

            // 3. 병합 서브그래프 레이아웃
            var merged = graph.SubGraphs.Values.Where(sg => sg.Key.IsPreset).ToList();
            Parallel.ForEach(merged, subGraph => new SugiyamaLayout().Calculate(subGraph));

            ResearchNode.InvalidateAllPorts();
            layoutCalculated = true;
            SearchEngine.Build(graph.AllNodes.Values);
        }

        private static void ConstructMergedSubGraphs(ResearchGraph graph)
        {
            int benchTabCount = graph.SubGraphs.Count(kvp =>
                !kvp.Key.IsPreset && kvp.Key.Channel != null && kvp.Key.Channel.IsBench
                && kvp.Value.Nodes.Any(n => !n.IsDummy && !n.IsProxy));
            if (benchTabCount >= 2)
            {
                graph.GetOrBuildUnifiedBench(runLayout: false);
            }

            var presets = YARTMod.Settings?.tabPresets;
            if (presets != null)
            {
                foreach (var preset in presets)
                {
                    try { graph.BuildPreset(preset, runLayout: false); }
                    catch (Exception ex) { Log.Error($"[YART] Constructing preset '{preset?.Name}' failed: {ex}"); }
                }
            }
        }

        public static void RebuildPresetNonBlocking(ResearchPreset preset)
        {
            if (preset == null) return;

            graphReady = false;
            if (!ResearchGraph.Instance.Initialized) DefSnapshot.Capture();

            var prev = graphBuildTask;
            graphBuildTask = Task.Run(() =>
            {
                try
                {
                    if (prev != null && !prev.IsCompleted) prev.Wait();
                    if (!ResearchGraph.Instance.Initialized) BuildGraphAndLayout();

                    ResearchGraph.Instance.BuildPreset(preset);
                    Rendering.EdgeRenderer.ClearCache();
                    Generation++;
                    RefreshGraphReady();
                    if (Prefs.DevMode)
                        Log.Message($"[YART] Preset rebuilt in background (generation {Generation}): {preset.Name}");
                }
                catch (Exception ex)
                {
                    RefreshGraphReady();
                    Log.Error("[YART] RebuildPresetNonBlocking failed: " + ex);
                }
            });
        }

        public static int Generation { get; private set; }

        public static void RebuildNow()
        {
            try
            {
                // 진행 중인 백그라운드 빌드와의 레이스 방지
                var task = graphBuildTask;
                if (task != null && !task.IsCompleted)
                {
                    task.Wait();
                }

                var stopwatch = Stopwatch.StartNew();
                graphReady = false;
                DefSnapshot.Capture();
                RebuildEverything();
                LogElapsed(stopwatch, "RebuildNow completed", $" (generation {Generation})");
            }
            catch (Exception ex)
            {
                // graphReady가 false면 빌드 중 안내가 표시됨
                graphReady = false;
                layoutCalculated = false;
                Log.Error("[YART] RebuildNow failed: " + ex);
            }
        }

        /// <summary>언어 변경 등으로 플레이데이터를 재로드한 뒤 호출</summary>
        public static void OnPlayDataReloaded()
        {
            graphReady = false;
            layoutCalculated = false;
            SearchEngine.Invalidate(); // 언어 재감지는 메인 스레드에서

            ScheduleWarmedBackgroundBuild(() =>
            {
                ResearchGraph.Instance.Build();
                RunLayoutAndSearch();
                Rendering.EdgeRenderer.ClearCache();
                Generation++;
                RefreshGraphReady();
                if (Prefs.DevMode)
                    Log.Message("[YART] Rebuilt after play-data/language reload (generation " + Generation + ")");
            }, "Reload rebuild failed");
        }

        public static void RebuildNonBlocking()
        {
            graphReady = false;
            WarmUnlockedDefsCache();
            DefSnapshot.Capture();
            var prev = graphBuildTask;
            graphBuildTask = Task.Run(() =>
            {
                try
                {
                    if (prev != null && !prev.IsCompleted) prev.Wait();

                    var stopwatch = Stopwatch.StartNew(); // 이전 빌드 대기 시간은 제외
                    RebuildEverything();
                    LogElapsed(stopwatch, "Non-blocking rebuild done", $" (generation {Generation})");
                }
                catch (Exception ex)
                {
                    graphReady = false;
                    Log.Error("[YART] Non-blocking rebuild failed: " + ex);
                }
            });
        }

        [DebugAction("YART", "Column height dump", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpColumnHeights()
        {
            EnsureBuilt();

            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            float rowDummy = Constraints.LayoutDummyNodeHeight + Constraints.LayoutDummySpacing;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[YART] Column height dump (budget {Constraints.LayoutMaxColumnHeight:F0} px; rank: real + dummy = est px):");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var subGraph = kvp.Value;
                if (subGraph.Nodes.Count == 0) continue;

                var byRank = new SortedDictionary<int, (int real, int dummy)>();
                foreach (var node in subGraph.Nodes)
                {
                    byRank.TryGetValue(node.Rank, out var c);
                    if (node.IsDummy) c.dummy++;
                    else c.real++;
                    byRank[node.Rank] = c;
                }

                float maxPx = 0f;
                int maxRank = 0;
                sb.AppendLine($"=== {kvp.Key} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");
                foreach (var entry in byRank)
                {
                    float px = entry.Value.real * rowReal + entry.Value.dummy * rowDummy;
                    if (px > maxPx) { maxPx = px; maxRank = entry.Key; }
                    sb.AppendLine($"  rank {entry.Key,3}: {entry.Value.real,3} real + {entry.Value.dummy,3} dummy = {px,5:F0} px");
                }
                sb.AppendLine($"  max: rank {maxRank} = {maxPx:F0} px");
            }

            Log.Message(sb.ToString());
        }

        [DebugAction("YART", "Layout metrics dump", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpLayoutMetrics()
        {
            EnsureBuilt();

            var sb = new System.Text.StringBuilder();
            float dumpBudget = Constraints.LayoutMaxColumnHeight;
            float dumpRowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            sb.AppendLine($"[YART] Layout metrics dump (budget {dumpBudget:F0} px = {dumpBudget / dumpRowReal:F1} rows; λ={Constraints.RankCostSpanWeight} μ={Constraints.RankCostWidthWeight}):");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var subGraph = kvp.Value;
                var m = subGraph.Metrics;

                sb.AppendLine($"=== {kvp.Key} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");

                if (m == null)
                {
                    sb.AppendLine("  (no layout)");
                    continue;
                }

                var bb = subGraph.BoundingBox;
                float refW = Constraints.MetricsRefViewportW;
                float refH = Constraints.MetricsRefViewportH;
                float fitZoom = (bb.width > 0f && bb.height > 0f)
                    ? Mathf.Min(refW / bb.width, refH / bb.height)
                    : 0f;

                sb.AppendLine($"  crossings:          {m.Crossings}");
                sb.AppendLine($"  width x height:     {bb.width:F0} x {bb.height:F0} px");
                sb.AppendLine($"  fitZoom:            {fitZoom:F3}");
                sb.AppendLine($"  maxColumnHeight:    {m.MaxColumnHeight:F0} px");
                sb.AppendLine($"  totalEdgeSpan:      {m.TotalEdgeSpan}");
                sb.AppendLine($"  bendCount:          {m.BendCount}");
                sb.AppendLine($"  unjustifiedBends:   {m.UnjustifiedBends}");
                sb.AppendLine($"  verticalVariation:  {m.VerticalVariation:F0} px");
                sb.AppendLine($"  nonConvergedChains: {m.NonConvergedChains}");
                sb.AppendLine($"  overflowCost:       {m.OverflowCost:F2}  (단위: 행초과²)");
                sb.AppendLine($"  usedRankCount:      {m.UsedRankCount}");
                sb.AppendLine($"  rankCost:           {m.RankCost:F2}  (= overflow + λ·span + μ·width)");
            }

            Log.Message(sb.ToString());
        }

        [DebugAction("YART", "Dump graph structure", allowedGameStates = AllowedGameStates.Playing)]
        private static void DumpGraphStructure()
        {
            EnsureBuilt();

            Log.Message("[YART] ========== GRAPH STRUCTURE DUMP ==========");

            foreach (var kvp in ResearchGraph.Instance.SubGraphs)
            {
                var graphKey = kvp.Key;
                var subGraph = kvp.Value;

                Log.Message($"[YART] === SubGraph: {graphKey} ({subGraph.Nodes.Count} nodes, {subGraph.Edges.Count} edges) ===");

                foreach (var layer in subGraph.Nodes.GroupBy(n => n.Rank).OrderBy(g => g.Key))
                {
                    var ordered = layer.OrderBy(n => n.VOrder).ToList();
                    Log.Message($"[YART] --- Rank {layer.Key} ({ordered.Count} nodes) ---");

                    foreach (var node in ordered)
                    {
                        string prereqs = string.Join(", ", node.Prerequisites.Select(NodeRef));
                        string children = string.Join(", ", node.Children.Select(NodeRef));
                        string name = node.IsDummy ? "DUMMY" : node.Id;
                        Log.Message($"[YART]   {name}: Rank={node.Rank}, VOrder={node.VOrder}, Pos=({node.Position.x:F1},{node.Position.y:F1}) | In=[{prereqs}] Out=[{children}]");
                    }
                }
            }

            Log.Message("[YART] ========== END DUMP ==========");
        }

        private static string NodeRef(ResearchNode node)
            => node.IsDummy ? $"D({node.Rank},{node.VOrder})" : node.Id;
    }
}
