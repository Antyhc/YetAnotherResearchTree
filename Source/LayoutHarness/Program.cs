using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RimWorld;
using Verse;
using YART;
using YART.Data;

namespace LayoutHarness
{
    internal static partial class Program
    {
        private static int Main(string[] args)
        {
            var opt = ParseArgs(args);
            if (opt == null) { PrintUsage(); return 1; }

            if (opt.Params != null) ApplyParamsFile(opt.Params);
            if (opt.Tune != null) return RunTune(opt.Tune, opt.OutDir ?? Path.Combine(Path.GetTempPath(), "YART_tune") /* 창작마당 업로드에 섞이지 않게 모드 폴더 밖 */, opt.Generations, opt.Parallel, opt.Seed, opt.Sigma, opt.MinNodes, opt.Start, opt.Holdout);
            if (opt.Score != null) return RunScore(opt.Score, opt.Json, opt.MinNodes);
            if (opt.Rescore != null) return RunRescore(opt.Rescore, 15);

            if (!File.Exists(opt.InPath))
            {
                Console.Error.WriteLine($"입력 파일 없음: {opt.InPath}");
            }

            var file = LoadDump(opt.InPath);
            if (opt.Against != null) file.Graphs = LoadDump(opt.Against).Graphs; // 예: 생성기 spec과 인게임 덤프 비교
            if (opt.Verify) return Verify(file);

            // input 섹션이 있으면 게임과 같은 GraphTopology로 재구성. --raw면 export된 그래프 그대로
            List<DumpGraph> dump;
            if (file.Input != null && !opt.Raw) dump = BuildFromInput(file.Input);
            else if (file.Graphs != null) dump = file.Graphs;
            else { Console.Error.WriteLine("graphs/input 섹션이 모두 없음"); return 1; }
            if (opt.List)
            {
                Console.WriteLine($"{dump.Count} graphs in {opt.InPath}:");
                foreach (var g in dump)
                    Console.WriteLine($"  {g.Key,-32} nodes={g.Nodes.Count,-5} edges={g.Edges.Count}");
                return 0;
            }

            Prefs.DevMode = opt.DevMode;
            YART.SugiyamaLayout.GroupClusteringEnabled = opt.Group;

            // 탭별 그래프에서 defName -> 탭키 맵을 만들어 그룹키(출처 탭) 복원
            // picked도 포함해야 탭별 그래프를 골랐을 때 그룹 클러스터링 no-op이 검증됨
            var groupMap = new Dictionary<string, string>();
            foreach (var g in dump)
            {
                if (IsMergedKey(g.Key)) continue;
                foreach (var n in g.Nodes)
                    if (!groupMap.ContainsKey(n.Id)) groupMap[n.Id] = g.Key;
            }

            var picked = dump.FirstOrDefault(g => g.Key == opt.Graph)
                       ?? dump.FirstOrDefault(g => g.Key.IndexOf(opt.Graph, StringComparison.OrdinalIgnoreCase) >= 0);
            if (picked == null)
            {
                Console.Error.WriteLine($"그래프 '{opt.Graph}' 없음. --list 로 키 확인.");
                return 1;
            }

            var graph = BuildGraph(picked, groupMap);
            if (opt.Group)
            {
                int withGroup = graph.Nodes.Count(n => !n.IsDummy && !string.IsNullOrEmpty(n.GroupKey));
                int distinct = graph.Nodes.Where(n => !n.IsDummy && !string.IsNullOrEmpty(n.GroupKey)).Select(n => n.GroupKey).Distinct().Count();
                Console.WriteLine($"[group] clustering ON — {withGroup} nodes mapped into {distinct} groups");
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new YART.SugiyamaLayout().Calculate(graph);
            sw.Stop();

            Console.WriteLine($"=== {picked.Key}  ({sw.ElapsedMilliseconds} ms) ===");
            PrintMetrics(graph);
            if (opt.Dump) DumpRanks(graph);
            if (opt.Trace != null)
            {
                Console.WriteLine($"--- TRACE '{opt.Trace}' ---");
                foreach (var n in graph.Nodes.Where(x => !x.IsDummy && (((x.Label ?? "").IndexOf(opt.Trace, StringComparison.OrdinalIgnoreCase) >= 0) || ((x.Id ?? "").IndexOf(opt.Trace, StringComparison.OrdinalIgnoreCase) >= 0))))
                {
                    Console.WriteLine($"  [{n.Label}] rank={n.Rank} vo={n.VOrder} y={n.Position.y:F0}");

                    // 같은 레이어 전체. 빈 공간, 장애물 확인용
                    var myLayer = graph.Nodes.Where(z => z.Rank == n.Rank).OrderBy(z => z.VOrder).ToList();
                    Console.WriteLine($"      LAYER r{n.Rank} ({myLayer.Count} nodes):");
                    foreach (var z in myLayer)
                        Console.WriteLine($"        vo{z.VOrder,2} y{z.Position.y,6:F0} {(z.IsDummy ? "(D)" : z.Label)}");

                    // 선행 더미 체인을 실소스까지 거슬러 올라가며 Y와 위아래 이웃 출력
                    foreach (var p in n.Prerequisites)
                    {
                        var profIn = new System.Collections.Generic.List<string>();
                        var cur = p; var guard = 0;
                        while (cur != null && cur.IsDummy && guard++ < 60)
                        {
                            var lay = graph.Nodes.Where(z => z.Rank == cur.Rank).OrderBy(z => z.VOrder).ToList();
                            int idx = cur.VOrder;
                            string up = idx > 0 ? $"{lay[idx-1].Position.y:F0}" : "—";
                            string dn = idx+1 < lay.Count ? $"{lay[idx+1].Position.y:F0}" : "—";
                            profIn.Add($"D(r{cur.Rank},vo{cur.VOrder},y{cur.Position.y:F0}|nbr {up}/{dn})");
                            cur = cur.Prerequisites.FirstOrDefault();
                        }
                        if (cur != null) profIn.Add($"SRC[{cur.Label}](r{cur.Rank},y{cur.Position.y:F0})");
                        profIn.Reverse();
                        Console.WriteLine($"      <= IN-CHAIN: {string.Join(" -> ", profIn)} -> [{n.Label} y{n.Position.y:F0}]");
                    }

                    foreach (var c in n.Children)
                    {
                        if (!c.IsDummy)
                        {
                            Console.WriteLine($"      -> {c.Label} rank={c.Rank} vo={c.VOrder} y={c.Position.y:F0}");
                            continue;
                        }
                        // 더미 체인 끝까지 Y 프로파일과 종착 실타깃
                        var prof = new System.Collections.Generic.List<string>();
                        var cur = c; var guard = 0;
                        while (cur != null && cur.IsDummy && guard++ < 50)
                        {
                            prof.Add($"D(r{cur.Rank},vo{cur.VOrder},y{cur.Position.y:F0})");
                            foreach (var t in cur.Children.Where(z => !z.IsDummy))
                                Console.WriteLine($"      ~> [via trunk] {t.Label} rank={t.Rank} y={t.Position.y:F0}");
                            cur = cur.Children.FirstOrDefault(z => z.IsDummy);
                        }
                        Console.WriteLine($"      -> TRUNK: {string.Join(" ", prof)}");
                    }
                }
            }

            string outPath = opt.OutPath ?? Sanitize(picked.Key) + ".svg";
            File.WriteAllText(outPath, SvgRenderer.Render(graph));
            Console.WriteLine($"SVG -> {Path.GetFullPath(outPath)}");
            return 0;
        }

        // 게임은 ".../AllTabs" 또는 ".../Preset:...", 구버전 export는 "Unified"
        private static bool IsMergedKey(string key)
            => key.IndexOf("AllTabs", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("Preset", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("Unified", StringComparison.OrdinalIgnoreCase) >= 0;

        private static ResearchSubGraph BuildGraph(DumpGraph d, Dictionary<string, string> groupMap = null)
        {
            var key = new GraphKey(d.Key, IsMergedKey(d.Key));
            var g = new ResearchSubGraph(key);
            var byId = new Dictionary<string, ResearchNode>(d.Nodes.Count);
            foreach (var n in d.Nodes)
            {
                string group = n.Group;
                if (group == null) groupMap?.TryGetValue(n.Id, out group);
                var node = new ResearchNode
                {
                    Id = n.Id,
                    Label = n.Label ?? n.Id,
                    TechLevel = (TechLevel)n.Tech,
                    IsProxy = n.Proxy,
                    Key = key,
                    GroupKey = group,
                };
                g.AddNode(node);
                byId[n.Id] = node;
            }
            foreach (var e in d.Edges)
            {
                if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to)) continue;
                from.Children.Add(to);
                to.Prerequisites.Add(from);
                g.AddEdge(from, to);
            }
            return g;
        }

        private static void PrintMetrics(ResearchSubGraph g)
        {
            int real = g.Nodes.Count(n => !n.IsDummy && !n.IsProxy);
            int proxy = g.Nodes.Count(n => n.IsProxy);
            int dummy = g.Nodes.Count(n => n.IsDummy);
            int ranks = g.Nodes.Count == 0 ? 0 : g.Nodes.Max(n => n.Rank) + 1;
            int overlaps = CountSeparationViolations(g);
            int visual = CountVisualOverlaps(g);
            float realMaxCol = MaxColumnPx(g);
            var m = g.Metrics;

            // 실노드 박스 기준 maxY-minY
            float tTop = float.MaxValue, tBot = float.MinValue;
            foreach (var n in g.Nodes)
            {
                if (n.IsDummy || n.IsProxy) continue;
                float half = Constraints.NodeSize.y / 2f;
                tTop = Math.Min(tTop, n.Position.y - half);
                tBot = Math.Max(tBot, n.Position.y + half);
            }
            float treeHeight = (tBot >= tTop) ? tBot - tTop : 0f;

            // slant: 정규화 후 g.Edges는 렌더 단위 세그먼트(실 <-> 더미)라 세그먼트별 |dy|를 합산
            float edgeDy = 0f, edgeLen = 0f, edgeDyMax = 0f;
            int segs = 0;
            foreach (var e in g.Edges)
            {
                float dy = Math.Abs(e.From.Position.y - e.To.Position.y);
                float dx = Math.Abs(e.From.Position.x - e.To.Position.x);
                edgeDy += dy;
                edgeLen += (float)Math.Sqrt(dx * dx + dy * dy);
                if (dy > edgeDyMax) edgeDyMax = dy;
                segs++;
            }
            float edgeDyAvg = segs > 0 ? edgeDy / segs : 0f;

            // 실제 Y로 교차하는 세그먼트 쌍 수. VOrder 위상 교차와 달리 번들이 반영됨
            // (a->b), (c->d)는 (a.y-c.y)(b.y-d.y) < 0이면 교차. 같은 Y로 합류한 더미끼리는 교차 아님
            var segByRank = new Dictionary<int, List<ResearchEdge>>();
            foreach (var e in g.Edges)
            {
                int r = e.From.Rank;
                if (!segByRank.TryGetValue(r, out var lst)) { lst = new List<ResearchEdge>(); segByRank[r] = lst; }
                lst.Add(e);
            }
            int visualCross = 0;
            foreach (var lst in segByRank.Values)
            {
                for (int i = 0; i < lst.Count; i++)
                    for (int j = i + 1; j < lst.Count; j++)
                    {
                        float da = lst[i].From.Position.y - lst[j].From.Position.y;
                        float db = lst[i].To.Position.y - lst[j].To.Position.y;
                        if (da * db < -1e-3f) visualCross++;
                    }
            }

            int isolated = g.Nodes.Count(n => !n.IsDummy && !n.IsProxy && n.Prerequisites.Count == 0 && n.Children.Count == 0);
            Console.WriteLine($"  nodes: {real} real ({isolated} isolated) + {proxy} proxy + {dummy} dummy   ranks: {ranks}");
            Console.WriteLine($"  TREE HEIGHT (real maxY-minY): {treeHeight:F0} px   <-- 낮을수록 컴팩트");
            Console.WriteLine($"  VISUAL OVERLAPS (box 침범): {visual}        <-- 진짜 '안 깔끔' 지표 (Fix A 타깃, 낮을수록 좋음)");
            Console.WriteLine($"  sep-violations (참고: 양성 통로 포함): {overlaps}");
            Console.WriteLine($"  maxColumnHeight: {realMaxCol:F0} px");
            if (m != null)
            {
                Console.WriteLine($"  RankCost={m.RankCost:F1} (overflow={m.OverflowCost:F1}, span={m.TotalEdgeSpan}, usedRanks={m.UsedRankCount})");
                Console.WriteLine($"  crossings={m.Crossings}  bends={m.BendCount} (unjustified={m.UnjustifiedBends})  nonConvergedChains={m.NonConvergedChains}");
            }
            Console.WriteLine($"  EDGE ΔY (slant): Σ={edgeDy:F0}px  avg={edgeDyAvg:F1}px/seg  max={edgeDyMax:F0}px   (ΣLen={edgeLen:F0}px, {segs} segs)   <-- 낮을수록 엣지가 수평");
            Console.WriteLine($"  VISUAL crossings (실제 Y 세그먼트 교차): {visualCross}   <-- 번들/인력 반영 (위상 crossings와 별개)");

            // 그룹 분산도: 랭크마다 그룹의 연속 런 수. 한 그룹이 여러 조각으로 흩어지면 extraRuns가 늘어남. 실노드만 집계
            bool anyGroup = g.Nodes.Any(n => !n.IsDummy && !string.IsNullOrEmpty(n.GroupKey));
            if (anyGroup)
            {
                int extraRuns = 0, nonEmptyPairs = 0, totalRuns = 0;
                var groupSet = new HashSet<string>();
                foreach (var grp in g.Nodes.GroupBy(n => n.Rank))
                {
                    var reals = grp.Where(n => !n.IsDummy).OrderBy(n => n.VOrder).ToList();
                    if (reals.Count == 0) continue;
                    var groupsInRank = new HashSet<string>();
                    string prev = null;
                    foreach (var n in reals)
                    {
                        string gk = string.IsNullOrEmpty(n.GroupKey) ? "(none)" : n.GroupKey;
                        groupSet.Add(gk);
                        if (gk != prev) { totalRuns++; prev = gk; }
                        groupsInRank.Add(gk);
                    }
                    nonEmptyPairs += groupsInRank.Count;
                }
                extraRuns = totalRuns - nonEmptyPairs; // 0 = 모든 (랭크,그룹)이 단일 런
                Console.WriteLine($"  GROUP DISPERSION: extraRuns={extraRuns} (runs={totalRuns}, ideal={nonEmptyPairs}, groups={groupSet.Count})   <-- 0에 가까울수록 출처 탭이 한 덩어리 (낮을수록 '정렬됨')");
            }
        }

        private static void DumpRanks(ResearchSubGraph g)
        {
            float rowReal = Constraints.NodeSize.y + Constraints.NodeSpacing.y;
            int ranks = g.Nodes.Count == 0 ? 0 : g.Nodes.Max(n => n.Rank) + 1;
            var rows = new List<(int rank, int real, int dummy, float ext, float center, float realCenter)>();
            for (int r = 0; r < ranks; r++)
            {
                var layer = g.Nodes.Where(n => n.Rank == r).ToList();
                if (layer.Count == 0) continue;
                float top = float.MaxValue, bot = float.MinValue;
                foreach (var n in layer)
                {
                    float half = (n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y) / 2f;
                    top = Math.Min(top, n.Position.y - half);
                    bot = Math.Max(bot, n.Position.y + half);
                }
                var reals = layer.Where(n => !n.IsDummy).ToList();
                float realCenter = reals.Count > 0 ? reals.Average(n => n.Position.y) : float.NaN;
                rows.Add((r, reals.Count, layer.Count(n => n.IsDummy), bot - top, (top + bot) / 2f, realCenter));
            }
            Console.WriteLine("  --- 랭크별 세로 중심 드리프트 (center=박스중심, realCenter=실노드 평균y) ---");
            foreach (var x in rows.OrderBy(r => r.rank))
            {
                Console.WriteLine($"  rank {x.rank,2}: {x.real,3} real + {x.dummy,3} dummy   ext={x.ext,6:F0}px  center={x.center,7:F0}  realCenter={x.realCenter,7:F0}");
            }
        }

        private static float MaxColumnPx(ResearchSubGraph g)
        {
            var min = new Dictionary<int, float>();
            var max = new Dictionary<int, float>();
            foreach (var n in g.Nodes)
            {
                float half = (n.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y) / 2f;
                float top = n.Position.y - half, bot = n.Position.y + half;
                if (!min.TryGetValue(n.Rank, out var t) || top < t) min[n.Rank] = top;
                if (!max.TryGetValue(n.Rank, out var b) || bot > b) max[n.Rank] = bot;
            }
            float best = 0f;
            foreach (var r in min.Keys)
            {
                float h = max[r] - min[r];
                if (h > best) best = h;
            }
            return best;
        }

        // SugiyamaLayout.CountSeparationViolations와 동일
        private static int CountSeparationViolations(ResearchSubGraph g)
        {
            int count = 0;
            foreach (var grp in g.Nodes.GroupBy(n => n.Rank))
            {
                var layer = grp.OrderBy(n => n.VOrder).ToList();
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < SeparationOf(layer[i], layer[i + 1]) - 0.5f) count++;
                }
            }
            return count;
        }

        // SugiyamaLayout.ValidateLayerSeparation와 동일
        private static int CountVisualOverlaps(ResearchSubGraph g, bool quiet = false)
        {
            int count = 0;
            foreach (var grp in g.Nodes.GroupBy(n => n.Rank))
            {
                var layer = grp.OrderBy(n => n.VOrder).ToList();
                for (int i = 0; i + 1 < layer.Count; i++)
                {
                    float ha = layer[i].IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
                    float hb = layer[i + 1].IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
                    float visualMin = (ha + hb) / 2f - 0.5f;
                    float gap = layer[i + 1].Position.y - layer[i].Position.y;
                    if (gap < visualMin)
                    {
                        count++;
                        if (!quiet) Console.Error.WriteLine($"  overlap rank {layer[i].Rank}: [{Desc(layer[i])} y={layer[i].Position.y:F0} vo={layer[i].VOrder}] | [{Desc(layer[i + 1])} y={layer[i + 1].Position.y:F0} vo={layer[i + 1].VOrder}]  gap={gap:F0} need={visualMin:F0}");
                    }
                }
            }
            return count;
        }

        private static string Desc(ResearchNode n)
            => n.IsDummy ? "D" : n.IsProxy ? ("P:" + n.Label) : (n.Prerequisites.Count == 0 && n.Children.Count == 0 ? "ISO:" + n.Label : n.Label);

        private static float SeparationOf(ResearchNode a, ResearchNode b)
        {
            float ha = a.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
            float hb = b.IsDummy ? Constraints.LayoutDummyNodeHeight : Constraints.NodeSize.y;
            float spacing = (a.IsDummy && b.IsDummy) ? Constraints.LayoutDummySpacing : Constraints.NodeSpacing.y;
            return (ha + hb) / 2f + spacing;
        }

        private static DumpFile LoadDump(string path)
        {
            var file = new DumpFile();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("input", out var input)) file.Input = ParseInput(input);
            if (!doc.RootElement.TryGetProperty("graphs", out var graphs)) return file;

            var list = file.Graphs = new List<DumpGraph>();
            foreach (var g in graphs.EnumerateArray())
            {
                var dg = new DumpGraph { Key = g.GetProperty("key").GetString() };
                foreach (var n in g.GetProperty("nodes").EnumerateArray())
                {
                    dg.Nodes.Add(new DumpNode
                    {
                        Id = n.GetProperty("id").GetString(),
                        Label = n.GetProperty("label").GetString(),
                        Tech = n.GetProperty("tech").GetInt32(),
                        Proxy = n.GetProperty("proxy").GetBoolean(),
                    });
                }
                foreach (var e in g.GetProperty("edges").EnumerateArray())
                    dg.Edges.Add(new DumpEdge { From = e.GetProperty("from").GetString(), To = e.GetProperty("to").GetString() });
                list.Add(dg);
            }
            return file;
        }

        #region args
        private sealed class Options
        {
            public string InPath = "YART_layout_graph.json";
            public string Graph = "Unified";
            public string OutPath;
            public bool DevMode;
            public bool List;
            public bool Dump;
            public bool Group; // 출처 탭별 세로 묶기
            public string Trace;
            public bool Raw;    // input 섹션이 있어도 export된 graphs를 사용
            public bool Verify; // input으로 재구성한 그래프와 export된 graphs 비교
            public string Against; // --verify 비교 대상 graphs를 이 파일에서
            // 튜닝
            public string Params;   // 상수 덮어쓰기 JSON
            public string Score;    // 채점할 평가 세트 폴더
            public string Json;     // --score 결과 출력 파일
            public string Tune;     // CMA-ES 튜닝용 평가 세트 폴더
            public string OutDir;
            public int MinNodes = 10;
            public int Generations = 40;
            public int Parallel = Math.Max(1, Environment.ProcessorCount / 2);
            public int Seed = 1;
            public double Sigma = 0.25;
            public string Start;    // 시작점 JSON. baseline은 그대로 현재 기본값
            public string Rescore;  // 저장된 튜닝 결과를 현재 목적 함수로 재순위
            public string Holdout;  // 튜닝 후 상위 후보를 튜닝에 안 쓴 평가 세트로 검증
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--in": o.InPath = args[++i]; break;
                    case "--graph": o.Graph = args[++i]; break;
                    case "--out": o.OutPath = args[++i]; break;
                    case "--devmode": o.DevMode = true; break;
                    case "--list": o.List = true; break;
                    case "--dump": o.Dump = true; break;
                    case "--group": o.Group = true; break;
                    case "--trace": o.Trace = args[++i]; break;
                    case "--raw": o.Raw = true; break;
                    case "--verify": o.Verify = true; break;
                    case "--against": o.Against = args[++i]; o.Verify = true; break;
                    case "--params": o.Params = args[++i]; break;
                    case "--score": o.Score = args[++i]; break;
                    case "--json": o.Json = args[++i]; break;
                    case "--tune": o.Tune = args[++i]; break;
                    case "--start": o.Start = args[++i]; break;
                    case "--rescore": o.Rescore = args[++i]; break;
                    case "--holdout": o.Holdout = args[++i]; break;
                    case "--out-dir": o.OutDir = args[++i]; break;
                    case "--min-nodes": o.MinNodes = int.Parse(args[++i]); break;
                    case "--generations": o.Generations = int.Parse(args[++i]); break;
                    case "--parallel": o.Parallel = int.Parse(args[++i]); break;
                    case "--seed": o.Seed = int.Parse(args[++i]); break;
                    case "--sigma": o.Sigma = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "-h": case "--help": return null;
                    default: Console.Error.WriteLine($"알 수 없는 인자: {args[i]}"); return null;
                }
            }
            return o;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("LayoutHarness");
            Console.WriteLine("  --in <path>     입력 그래프 JSON (기본: YART_layout_graph.json)");
            Console.WriteLine("  --graph <key>   대상 그래프 키 또는 부분일치 (기본: Unified)");
            Console.WriteLine("  --out <path>    SVG 출력 경로 (기본: <graph>_<fix>.svg)");
            Console.WriteLine("  --devmode       Prefs.DevMode=true (불변식 경고 출력)");
            Console.WriteLine("  --list          JSON 안의 그래프 목록만 출력");
            Console.WriteLine("  --raw           input(spec) 섹션이 있어도 export된 graphs를 사용");
            Console.WriteLine("  --verify        input으로 재구성한 그래프가 export된 graphs와 같은지 비교");
            Console.WriteLine("  --against <p>   --verify의 비교 대상 graphs를 다른 export 파일에서 (생성기 spec vs 인게임 덤프)");
            Console.WriteLine("  --params <p>    튜닝 대상 Constraints 상수를 JSON({이름: 값})으로 덮어쓰기");
            Console.WriteLine("  --score <dir>   평가 세트(*_specs.json) 전체 채점 [--json out] [--min-nodes 10]");
            Console.WriteLine("  --tune <dir>    CMA-ES 튜닝 [--out-dir d] [--generations 40] [--parallel n] [--seed 1] [--sigma 0.25] [--start p.json] [--holdout dir]");
            Console.WriteLine("  --rescore <d>   저장된 튜닝 결과 폴더를 현재 목적 함수로 다시 순위 매기기");
        }
        #endregion

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_');
        }

        // DTO
        private sealed class DumpFile { public List<DumpGraph> Graphs; public GraphInput Input; }
        private sealed class DumpGraph { public string Key; public List<DumpNode> Nodes = new(); public List<DumpEdge> Edges = new(); }
        private sealed class DumpNode { public string Id; public string Label; public int Tech; public bool Proxy; public string Group; }
        private sealed class DumpEdge { public string From; public string To; }
    }
}
