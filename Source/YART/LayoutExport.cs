using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;
using YART.Data;

namespace YART
{
    /// <summary>DevMode에서 레이아웃 전 raw 그래프를 캡처해 오프라인 하니스(Source/LayoutHarness)용 JSON으로 내보냄</summary>
    public static class LayoutExport
    {
        private static readonly object _lock = new object();
        private static readonly List<string> _graphs = new List<string>(); // 그래프별 JSON 조각

        /// <summary>빌드 시작 시 호출해 재생성 때 누적되지 않게 함</summary>
        public static void Begin()
        {
            lock (_lock) _graphs.Clear();
        }

        /// <summary>Calculate 맨 앞, 더미/랭크 생성 전에 호출. 스레드 안전</summary>
        public static void Capture(ResearchSubGraph graph)
        {
            if (!Prefs.DevMode || graph == null) return;

            var sb = new StringBuilder(1024);
            sb.Append("{\"key\":").Append(JsonStr(graph.Key.ToString())).Append(",\"nodes\":[");
            bool first = true;
            foreach (var n in graph.Nodes)
            {
                if (n.IsDummy) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(JsonStr(n.Id))
                  .Append(",\"label\":").Append(JsonStr(n.Label))
                  .Append(",\"tech\":").Append((int)n.TechLevel)
                  .Append(",\"proxy\":").Append(n.IsProxy ? "true" : "false")
                  .Append('}');
            }
            sb.Append("],\"edges\":[");
            first = true;
            foreach (var e in graph.Edges)
            {
                if (e.From.IsDummy || e.To.IsDummy) continue; // raw 단계엔 더미가 없지만 방어용
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"from\":").Append(JsonStr(e.From.Id))
                  .Append(",\"to\":").Append(JsonStr(e.To.Id)).Append('}');
            }
            sb.Append("]}");

            lock (_lock) _graphs.Add(sb.ToString());
        }

        public static void WriteToDesktop()
        {
            // 빌드 때 DevMode가 꺼져 있었으면 버퍼가 비어 있으니 리빌드로 채운다
            bool empty;
            lock (_lock) empty = _graphs.Count == 0;
            if (empty) GraphBuildPipeline.RebuildNow();

            string json;
            lock (_lock)
            {
                if (_graphs.Count == 0)
                {
                    Messages.Message("YART_Export_Empty".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                var sb = new StringBuilder(64 * 1024);
                sb.Append("{\"graphs\":[");
                for (int i = 0; i < _graphs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_graphs[i]);
                }
                sb.Append("],\"input\":");
                AppendInput(sb);
                sb.Append('}');
                json = sb.ToString();
            }

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "YART_layout_graph.json");
            try
            {
                File.WriteAllText(path, json);
                Messages.Message("YART_Export_Done".Translate(path), MessageTypeDefOf.TaskCompletion, false);
                Log.Message("[YART] Layout graph exported to " + path);
            }
            catch (Exception ex)
            {
                Messages.Message("YART_Export_Failed".Translate(ex.Message), MessageTypeDefOf.RejectInput, false);
                Log.Warning("[YART] Layout export failed: " + ex.Message);
            }
        }

        /// <summary>하네스가 이것만으로 서브그래프를 재구성할 수 있어야 함</summary>
        private static void AppendInput(StringBuilder sb)
        {
            var graph = ResearchGraph.Instance;
            var specs = graph.Topology?.Specs;

            sb.Append("{\"includeHidden\":").Append(graph.BuiltWithHiddenPrereqs == true ? "true" : "false");
            sb.Append(",\"specs\":[");
            if (specs != null)
            {
                for (int i = 0; i < specs.Count; i++)
                {
                    var s = specs[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(JsonStr(s.Id))
                      .Append(",\"label\":").Append(JsonStr(s.Label))
                      .Append(",\"tech\":").Append(s.TechLevel)
                      .Append(",\"channel\":").Append(JsonStr(s.ChannelId))
                      .Append(",\"bench\":").Append(s.ChannelIsBench ? "true" : "false")
                      .Append(",\"tab\":").Append(JsonStrOrNull(s.TabId))
                      .Append(",\"sourceTab\":").Append(JsonStrOrNull(s.SourceTab))
                      .Append(",\"prereqs\":");
                    AppendStrArray(sb, s.Prereqs);
                    sb.Append(",\"hiddenPrereqs\":");
                    AppendStrArray(sb, s.HiddenPrereqs);
                    sb.Append('}');
                }
            }
            sb.Append("],\"presets\":[");
            var presets = YARTMod.Settings?.tabPresets;
            if (presets != null)
            {
                bool first = true;
                foreach (var p in presets)
                {
                    if (p == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(JsonStr(p.Id))
                      .Append(",\"name\":").Append(JsonStr(p.Name))
                      .Append(",\"allBenchTabs\":").Append(p.IncludeAllBenchTabs ? "true" : "false")
                      .Append(",\"tabs\":");
                    AppendStrArray(sb, p.TabDefNames?.ToArray());
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private static void AppendStrArray(StringBuilder sb, string[] items)
        {
            sb.Append('[');
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonStr(items[i]));
                }
            }
            sb.Append(']');
        }

        private static string JsonStrOrNull(string s) => s == null ? "null" : JsonStr(s);

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
