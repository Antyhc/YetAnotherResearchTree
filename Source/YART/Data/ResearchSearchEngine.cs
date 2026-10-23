using System;
using System.Collections.Generic;
using System.Text;
using Verse;
using YART.Utils;

namespace YART.Data
{
    /// <summary>suffix array 기반 전문 검색. Build는 백그라운드 스레드, Search는 UI 스레드에서 호출</summary>
    public class ResearchSearchEngine
    {
        #region 필드 클래스
        public const byte FieldMainLabel = 0;
        public const byte FieldMainDefName = 1;
        public const byte FieldUnlockLabel = 2;
        public const byte FieldUnlockDefName = 3;
        public const byte FieldModName = 4;
        public const byte FieldMainDesc = 5;
        public const byte FieldUnlockDesc = 6;
        public const byte FieldNone = 255; // 구분자('|','\0') 위치

        // 인접 등급 차이(>=10)가 MatchBonus 최대(9)보다 커야 보너스가 필드 순위를 뒤집지 못함
        private static readonly int[] FieldWeights = { 100, 80, 60, 50, 30, 20, 10 };
        #endregion

        public struct SearchResult
        {
            public ResearchNode Node;
            public int Score;
            public byte ReasonField;   // 가장 약한 토큰이 매치된 필드
            public string ReasonLabel; // 해금 def 라벨이나 모드명, 본 연구 필드면 null
        }

        private struct Segment
        {
            public int Start;
            public string Label; // null = 본 연구 def의 필드
        }

        private SuffixStructure _suffix;
        // 노드마다 def별 "label|defName|description|", "모드명|패키지ID|", 끝에 '\0'
        private string _text;
        private readonly List<ResearchNode> _nodes = new List<ResearchNode>();
        private ushort[] _posNode;
        private byte[] _posField;
        private readonly List<Segment[]> _segments = new List<Segment[]>();
        private readonly List<(string name, string packageId)> _knownMods = new List<(string, string)>();

        // 검색 스크래치. stamp 세대로 재사용해 검색마다 할당하지 않음
        private int[] _candStamp;
        private int[] _tokenStamp;
        private int[] _accumScore;
        private int[] _tokenBestScore;
        private int[] _tokenBestPos;
        private int[] _weakScore;
        private int[] _weakPos;
        private int _generation;

        private volatile bool _isBuilt;
        public bool IsBuilt => _isBuilt;

        /// <summary>@ 자동완성용. 빌드 중에 바뀌므로 IsBuilt 이후에만 읽을 것</summary>
        public IReadOnlyList<(string name, string packageId)> KnownMods => _knownMods;

        /// <summary>주입 경로("defName.label") -> 번역 전 영어 원문(DefInjection.replacedString). 영어면 빈 맵</summary>
        public static Dictionary<string, string> BuildOriginalStringMap()
        {
            var map = new Dictionary<string, string>();
            var lang = LanguageDatabase.activeLanguage;
            if (lang == null || lang == LanguageDatabase.defaultLanguage) return map;
            foreach (var package in lang.defInjections)
            {
                foreach (var kvp in package.injections)
                {
                    var inj = kvp.Value;
                    if (!inj.injected || inj.IsFullListInjection) continue;
                    if (string.IsNullOrEmpty(inj.replacedString)) continue;
                    // def 타입이 달라도 경로가 같으면 마지막 값이 이김. 검색용이라 무해
                    map[kvp.Key] = inj.replacedString;
                }
            }
            return map;
        }

        /// <summary>그래프 재빌드로 노드 인스턴스가 바뀌면 호출. 안 하면 Build가 no-op</summary>
        public void Invalidate()
        {
            _isBuilt = false;
            SearchNormalizer.Reset(); // 언어가 바뀌었을 수 있음
        }

        public void Build(IEnumerable<ResearchNode> researchNodes)
        {
            if (_isBuilt) return;

            var sb = new StringBuilder(1 << 18);
            var spans = new List<(int start, int len, byte field)>();
            var nodeEnds = new List<int>();
            var segs = new List<Segment>();
            var seenModIds = new HashSet<string>();

            _nodes.Clear();
            _segments.Clear();
            _knownMods.Clear();

            void AppendField(string raw, byte field)
            {
                if (!string.IsNullOrEmpty(raw))
                {
                    string s = SearchNormalizer.Normalize(raw); // 쿼리와 같은 정규화
                    if (!string.IsNullOrEmpty(s))
                    {
                        spans.Add((sb.Length, s.Length, field));
                        sb.Append(s);
                    }
                }
                sb.Append('|');
            }

            var originals = BuildOriginalStringMap();

            void AddDef(Def def, bool isMain, HashSet<ModContentPack> mods)
            {
                segs.Add(new Segment { Start = sb.Length, Label = isMain ? null : def.label });
                AppendField(def.label, isMain ? FieldMainLabel : FieldUnlockLabel);
                AppendField(def.defName, isMain ? FieldMainDefName : FieldUnlockDefName);
                AppendField(def.description, isMain ? FieldMainDesc : FieldUnlockDesc);

                // 영어로도 검색되게 번역 전 원문도 같은 필드로 인덱싱
                if (originals.Count > 0)
                {
                    if (originals.TryGetValue(def.defName + ".label", out string origLabel) && origLabel != def.label)
                    {
                        AppendField(origLabel, isMain ? FieldMainLabel : FieldUnlockLabel);
                    }
                    if (originals.TryGetValue(def.defName + ".description", out string origDesc) && origDesc != def.description)
                    {
                        AppendField(origDesc, isMain ? FieldMainDesc : FieldUnlockDesc);
                    }
                }
                if (def.modContentPack != null) mods.Add(def.modContentPack);
            }

            foreach (var node in researchNodes)
            {
                if (node.IsDummy || node.IsProxy) continue;
                if (node.Def == null) continue;
                if (_nodes.Count >= ushort.MaxValue)
                {
                    Log.Warning("[YART] Search index: node count exceeds 65535, remainder skipped.");
                    break;
                }

                _nodes.Add(node);
                segs.Clear();

                var mods = new HashSet<ModContentPack>();
                AddDef(node.Def, isMain: true, mods);
                // 스냅샷만 읽을 것. 패치된 vanilla getter를 워커 스레드에서 부르면 텍스처 로드 위반(VFE Tribals 등)
                foreach (var unlocked in node.UnlockedDefs)
                {
                    if (unlocked == null) continue;
                    AddDef(unlocked, isMain: false, mods);
                }

                foreach (var mod in mods)
                {
                    segs.Add(new Segment { Start = sb.Length, Label = mod.Name });
                    AppendField(mod.Name, FieldModName);
                    AppendField(mod.PackageId, FieldModName);
                }

                // @ 필터는 SourceMod만 보므로 해금 def의 모드를 후보에 넣으면 매치 안 되는 제안이 됨
                var mainMod = node.Def.modContentPack;
                if (mainMod != null && seenModIds.Add(mainMod.PackageId))
                {
                    _knownMods.Add((mainMod.Name, mainMod.PackageId));
                }

                sb.Append('\0'); // 쿼리에 못 들어가는 문자라 노드를 넘는 매치가 없음
                nodeEnds.Add(sb.Length);
                _segments.Add(segs.ToArray());
            }

            _text = sb.ToString();
            BuildPositionLookups(spans, nodeEnds);
            _suffix = new SuffixStructure(_text);
            AllocateScratch();

            _isBuilt = true;
            if (Prefs.DevMode) Log.Message($"[YART] Search index built: {_nodes.Count} nodes, {_text.Length} chars.");
        }

        private void BuildPositionLookups(List<(int start, int len, byte field)> spans, List<int> nodeEnds)
        {
            _posNode = new ushort[_text.Length];
            _posField = new byte[_text.Length];
            for (int i = 0; i < _posField.Length; i++) _posField[i] = FieldNone;

            int nodeIdx = 0;
            for (int i = 0; i < _text.Length; i++)
            {
                _posNode[i] = (ushort)nodeIdx;
                if (i + 1 == nodeEnds[nodeIdx]) nodeIdx++;
            }
            foreach (var (start, len, field) in spans)
            {
                for (int i = start; i < start + len; i++) _posField[i] = field;
            }
        }

        private void AllocateScratch()
        {
            int nc = _nodes.Count;
            _candStamp = new int[nc];
            _tokenStamp = new int[nc];
            _accumScore = new int[nc];
            _tokenBestScore = new int[nc];
            _tokenBestPos = new int[nc];
            _weakScore = new int[nc];
            _weakPos = new int[nc];
            _generation = 0;
        }

        /// <summary>점수 내림차순</summary>
        public List<SearchResult> Search(string query)
        {
            var results = new List<SearchResult>();
            if (!_isBuilt || string.IsNullOrWhiteSpace(query)) return results;

            var textTokens = new List<string>();
            var modTokens = new List<string>();
            if (!ParseQuery(query, textTokens, modTokens)) return results;

            var candidates = CollectCandidates(textTokens, modTokens);
            if (candidates.Count == 0) return results;

            if (modTokens.Count > 0) ApplyModFilter(candidates, modTokens);

            foreach (int v in candidates)
            {
                var r = new SearchResult { Node = _nodes[v], Score = _accumScore[v], ReasonField = FieldNone };
                if (_weakPos[v] >= 0)
                {
                    r.ReasonField = _posField[_weakPos[v]];
                    r.ReasonLabel = SegmentLabelAt(v, _weakPos[v]);
                }
                results.Add(r);
            }

            results.Sort(CompareResults);
            return results;
        }

        /// <summary>false면 매치될 수 없는 쿼리</summary>
        private static bool ParseQuery(string query, List<string> textTokens, List<string> modTokens)
        {
            foreach (var part in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part[0] == '@')
                {
                    if (part.Length == 1) continue; // 단독 "@"는 자동완성 입력 중
                    string mod = SearchNormalizer.Normalize(part.Substring(1));
                    if (!string.IsNullOrEmpty(mod)) modTokens.Add(mod);
                    continue;
                }

                string norm = SearchNormalizer.Normalize(part);
                if (string.IsNullOrEmpty(norm)) continue;
                // '|'는 필드 구분자라 이를 포함한 토큰은 어떤 필드와도 매치될 수 없음
                if (norm.IndexOf('|') >= 0) return false;
                textTokens.Add(norm);
            }
            return true;
        }

        /// <summary>토큰 AND. _accumScore, _weakPos도 채움</summary>
        private List<int> CollectCandidates(List<string> textTokens, List<string> modTokens)
        {
            var candidates = new List<int>();
            int gen = ++_generation;

            if (textTokens.Count == 0)
            {
                if (modTokens.Count == 0) return candidates;
                // @ 전용 쿼리: 전 노드를 0점 후보로 두고 모드 필터에 맡김
                for (int v = 0; v < _nodes.Count; v++)
                {
                    candidates.Add(v);
                    _accumScore[v] = 0;
                    _weakPos[v] = -1;
                }
                return candidates;
            }

            for (int t = 0; t < textTokens.Count; t++)
            {
                string token = textTokens[t];
                int tgen = ++_generation;

                int occCount = _suffix.FindOccurrences(token, out int occStart);
                for (int oi = 0; oi < occCount; oi++)
                {
                    int pos = _suffix.PositionAt(occStart + oi);
                    byte field = _posField[pos];
                    if (field == FieldNone) continue;
                    int v = _posNode[pos];
                    if (t > 0 && _candStamp[v] != gen) continue; // 이전 토큰에서 탈락

                    int score = FieldWeights[field] + MatchBonus(pos, token.Length);
                    if (_tokenStamp[v] != tgen)
                    {
                        _tokenStamp[v] = tgen;
                        _tokenBestScore[v] = score;
                        _tokenBestPos[v] = pos;
                        if (t == 0)
                        {
                            _candStamp[v] = gen;
                            candidates.Add(v);
                            _accumScore[v] = 0;
                            _weakScore[v] = int.MaxValue;
                            _weakPos[v] = -1;
                        }
                    }
                    else if (score > _tokenBestScore[v])
                    {
                        _tokenBestScore[v] = score;
                        _tokenBestPos[v] = pos;
                    }
                }

                int w = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int v = candidates[i];
                    if (_tokenStamp[v] != tgen)
                    {
                        _candStamp[v] = 0; // 이후 토큰에서 occurrence 스킵
                        continue;
                    }
                    _accumScore[v] += _tokenBestScore[v];
                    if (_tokenBestScore[v] < _weakScore[v])
                    {
                        _weakScore[v] = _tokenBestScore[v];
                        _weakPos[v] = _tokenBestPos[v];
                    }
                    candidates[w++] = v;
                }
                candidates.RemoveRange(w, candidates.Count - w);
                if (candidates.Count == 0) return candidates;
            }

            return candidates;
        }

        /// <summary>모든 @ 토큰이 SourceMod의 name이나 packageId에 들어 있어야 통과</summary>
        private void ApplyModFilter(List<int> candidates, List<string> modTokens)
        {
            int w = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                int v = candidates[i];
                if (MatchesAllModTokens(_nodes[v].SourceMod, modTokens)) candidates[w++] = v;
            }
            candidates.RemoveRange(w, candidates.Count - w);
        }

        private static bool MatchesAllModTokens(ModContentPack mod, List<string> modTokens)
        {
            if (mod == null) return false;
            string name = SearchNormalizer.Normalize(mod.Name);
            string id = SearchNormalizer.Normalize(mod.PackageId);
            foreach (var m in modTokens)
            {
                if (!name.Contains(m) && !id.Contains(m)) return false;
            }
            return true;
        }

        private static int CompareResults(SearchResult a, SearchResult b)
        {
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            if (a.Score == 0) return string.CompareOrdinal(a.Node.Label, b.Node.Label); // @ 전용 쿼리는 이름순
            int la = a.Node.Label != null ? a.Node.Label.Length : 0;
            int lb = b.Node.Label != null ? b.Node.Label.Length : 0;
            if (la != lb) return la.CompareTo(lb);
            return string.CompareOrdinal(a.Node.Label, b.Node.Label);
        }

        private int MatchBonus(int pos, int len)
        {
            char prev = pos > 0 ? _text[pos - 1] : '\0';
            bool fieldStart = prev == '|' || prev == '\0';
            char next = pos + len < _text.Length ? _text[pos + len] : '\0';
            bool fieldEnd = next == '|' || next == '\0';
            if (fieldStart && fieldEnd) return 9; // 필드 전체 일치
            if (fieldStart) return 6;             // 필드 접두 일치
            if (prev == ' ') return 3;            // 단어 시작 일치
            return 0;
        }

        /// <summary>해금 def 라벨이나 모드명, 본 연구 필드면 null</summary>
        private string SegmentLabelAt(int nodeIdx, int pos)
        {
            var segs = _segments[nodeIdx];
            string label = null;
            for (int i = 0; i < segs.Length; i++)
            {
                if (segs[i].Start > pos) break;
                label = segs[i].Label;
            }
            return label;
        }
    }
}
