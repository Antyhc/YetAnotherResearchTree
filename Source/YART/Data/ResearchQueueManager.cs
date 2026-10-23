using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Compat;

namespace YART.Data
{
    public class ResearchQueueManager : GameComponent
    {
        private const int AutoStartCheckInterval = 250;

        private readonly Dictionary<ResearchChannel, List<ResearchProjectDef>> queues
            = new Dictionary<ResearchChannel, List<ResearchProjectDef>>();

        // BeginProject가 유발하는 SetCurrentProject/StopProject 포스트픽스의 재진입 방지
        private bool suppressSync;

        // 밈 충돌 확인 다이얼로그가 떠 있는 프로젝트
        private ResearchProjectDef pendingConfirmation;

        public static ResearchQueueManager Instance => Current.Game?.GetComponent<ResearchQueueManager>();

        public ResearchQueueManager(Game game)
        {
        }

        private List<ResearchProjectDef> GetOrAddQueue(ResearchChannel channel)
        {
            if (!queues.TryGetValue(channel, out var q))
            {
                q = new List<ResearchProjectDef>();
                queues[channel] = q;
            }
            return q;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            foreach (var channel in ChannelRegistry.All)
            {
                var list = GetOrAddQueue(channel);
                Scribe_Collections.Look(ref list, "queue_" + channel.Id, LookMode.Def);
                queues[channel] = list ?? new List<ResearchProjectDef>();
            }
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            foreach (var channel in queues.Keys.ToList())
            {
                queues[channel].RemoveAll(d => d == null || d.IsFinished);
                EnsureTopologicalOrder(channel);
            }
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (Find.TickManager.TicksGame % AutoStartCheckInterval == 0)
            {
                TryStartAllHeads(playSound: false);
            }
        }

        public IReadOnlyList<ResearchProjectDef> GetQueue(ResearchChannel channel) => GetOrAddQueue(channel);


        /// <summary>1부터 시작, 큐에 없으면 -1</summary>
        public int GetQueuePosition(ResearchProjectDef def)
        {
            if (def == null) return -1;
            int idx = GetOrAddQueue(ChannelOf(def)).IndexOf(def);
            return idx < 0 ? -1 : idx + 1;
        }

        public static ResearchChannel ChannelOf(ResearchProjectDef def)
        {
            var graph = ResearchGraph.Instance;
            if (graph.Initialized && graph.AllNodes.TryGetValue(def, out var node))
            {
                return node.Channel;
            }
            return ResearchNode.DetermineChannel(def);
        }

        public static ResearchProjectDef GetCurrentProject(ResearchChannel channel)
        {
            return channel.CurrentProject;
        }

        /// <summary>def와 미완료 선행 전체(hidden 포함)를 위상 순서로</summary>
        public static List<ResearchProjectDef> CollectMissingChain(ResearchProjectDef def)
        {
            var result = new List<ResearchProjectDef>();
            var visited = new HashSet<ResearchProjectDef>();

            void Visit(ResearchProjectDef d)
            {
                if (d == null || d.IsFinished || !visited.Add(d)) return;
                if (d.prerequisites != null)
                {
                    foreach (var p in d.prerequisites) Visit(p);
                }
                if (d.hiddenPrerequisites != null)
                {
                    foreach (var p in d.hiddenPrerequisites) Visit(p);
                }
                result.Add(d);
            }

            Visit(def);
            return result;
        }

        /// <summary>체인의 각 연구는 자기 채널 큐로 들어감</summary>
        public void EnqueueWithChain(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;
            if (SemiRandomResearchCompat.Active) return;
            if (MultiplayerCompat.TryEnqueue(def, toFront: false)) return;
            DoEnqueue(def, toFront: false);
        }

        public void EnqueueWithChainToFront(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;
            if (SemiRandomResearchCompat.Active) return;
            if (MultiplayerCompat.TryEnqueue(def, toFront: true)) return;
            DoEnqueue(def, toFront: true);
        }

        internal void DoEnqueue(ResearchProjectDef def, bool toFront)
        {
            if (def == null || def.IsFinished) return;

            Dictionary<ResearchChannel, ResearchProjectDef> oldHeads = null;
            if (toFront) oldHeads = InsertChainAtFront(def);
            else AppendChainAtBack(def);

            foreach (var channel in queues.Keys.ToList())
            {
                EnsureTopologicalOrder(channel);
            }

            if (oldHeads != null) StartChangedHeads(oldHeads);

            // 사운드는 여기서 한 번만 재생. 머리 시작(BeginProject)은 무음이어야 두 번 안 울림
            TryStartAllHeads(playSound: false);
            MultiplayerCompat.PlayActionSound(SoundDefOf.ResearchStart);
        }

        private void AppendChainAtBack(ResearchProjectDef def)
        {
            foreach (var d in CollectMissingChain(def))
            {
                var q = GetOrAddQueue(ChannelOf(d));
                if (!q.Contains(d)) q.Add(d);
            }
        }

        /// <summary>삽입 전 채널별 머리를 반환</summary>
        private Dictionary<ResearchChannel, ResearchProjectDef> InsertChainAtFront(ResearchProjectDef def)
        {
            var byChannel = new Dictionary<ResearchChannel, List<ResearchProjectDef>>();
            foreach (var d in CollectMissingChain(def)) // 이미 위상 순서
            {
                var ch = ChannelOf(d);
                if (!byChannel.TryGetValue(ch, out var list))
                {
                    list = new List<ResearchProjectDef>();
                    byChannel[ch] = list;
                }
                list.Add(d);
            }

            var oldHeads = new Dictionary<ResearchChannel, ResearchProjectDef>();
            foreach (var kvp in byChannel)
            {
                var q = GetOrAddQueue(kvp.Key);
                oldHeads[kvp.Key] = HeadOf(q);
                foreach (var d in kvp.Value) q.Remove(d);
                for (int i = 0; i < kvp.Value.Count; i++) q.Insert(i, kvp.Value[i]);
            }
            return oldHeads;
        }

        /// <summary>사운드는 호출부가 한 번에 재생</summary>
        private void StartChangedHeads(Dictionary<ResearchChannel, ResearchProjectDef> oldHeads)
        {
            foreach (var kvp in oldHeads)
            {
                var newHead = HeadOf(GetOrAddQueue(kvp.Key));
                if (ShouldSwitchHead(newHead, kvp.Value)) TryBeginProject(newHead, playSound: false);
            }
        }

        private static ResearchProjectDef HeadOf(List<ResearchProjectDef> queue) => queue.Count > 0 ? queue[0] : null;

        private static bool ShouldSwitchHead(ResearchProjectDef newHead, ResearchProjectDef oldHead)
        {
            return newHead != null && newHead != oldHead
                && !Find.ResearchManager.IsCurrentProject(newHead)
                && newHead.CanStartNow;
        }

        public void EnqueueWithChainExclusive(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;
            if (SemiRandomResearchCompat.Active) return;
            if (MultiplayerCompat.TryClearAndEnqueue(def)) return;
            DoEnqueueExclusive(def);
        }

        internal void DoEnqueueExclusive(ResearchProjectDef def)
        {
            if (def == null || def.IsFinished) return;

            var cleared = new HashSet<ResearchChannel>();
            foreach (var d in CollectMissingChain(def))
            {
                var ch = ChannelOf(d);
                if (cleared.Add(ch)) DoClear(ch);
            }

            DoEnqueue(def, toFront: false); // 비운 뒤라 앞/끝 무관
        }

        /// <summary>진행 중이던 연구도 중단</summary>
        public void ClearQueue(ResearchChannel channel)
        {
            if (channel == null) return;
            if (MultiplayerCompat.TryClear(channel)) return;
            DoClear(channel);
        }

        internal void DoClear(ResearchChannel channel)
        {
            var q = GetOrAddQueue(channel);
            if (q.Count == 0) return;

            q.Clear();

            var current = GetCurrentProject(channel);
            if (current != null && !current.IsFinished) StopProjectSuppressed(current);

            ResearchNode.InvalidateAllStates();
        }

        /// <summary>큐 머리로 옮겨서 시작</summary>
        public void StartNow(ResearchProjectDef def)
        {
            if (def == null || !def.CanStartNow) return;
            // Semi Random Research 활성 시 선택은 그 모드의 'Next Research' 탭이 담당
            if (SemiRandomResearchCompat.Active) return;
            if (MultiplayerCompat.TryStartNow(def)) return;
            DoStartNow(def);
        }

        internal void DoStartNow(ResearchProjectDef def)
        {
            if (def == null || !def.CanStartNow) return;

            var q = GetOrAddQueue(ChannelOf(def));
            q.Remove(def);
            q.Insert(0, def);
            TryBeginProject(def, playSound: true);
        }

        public void Remove(ResearchProjectDef def)
        {
            if (def == null) return;
            if (MultiplayerCompat.TryRemove(def)) return;
            DoRemove(def);
        }

        internal void DoRemove(ResearchProjectDef def)
        {
            if (def == null) return;

            bool wasCurrent = Find.ResearchManager.IsCurrentProject(def);

            foreach (var q in queues.Values) q.Remove(def);

            // 의존 항목 연쇄 제거
            var dependents = queues.Values
                .SelectMany(q => q)
                .Where(d => CollectMissingChain(d).Contains(def))
                .ToList();
            foreach (var dep in dependents)
            {
                foreach (var q in queues.Values) q.Remove(dep);
            }

            if (wasCurrent) StopProjectSuppressed(def);

            ResearchNode.InvalidateAllStates();
            TryStartAllHeads(playSound: false);
        }

        public void Reorder(ResearchChannel channel, ResearchProjectDef def, int targetIndex)
        {
            if (channel == null || def == null) return;
            if (MultiplayerCompat.TryReorder(channel, def, targetIndex)) return;
            DoReorder(channel, def, targetIndex);
        }

        internal void DoReorder(ResearchChannel channel, ResearchProjectDef def, int targetIndex)
        {
            var q = GetOrAddQueue(channel);
            var oldHead = HeadOf(q);

            if (!q.Remove(def)) return;
            q.Insert(Mathf.Clamp(targetIndex, 0, q.Count), def);
            EnsureTopologicalOrder(channel);

            var newHead = HeadOf(q);
            if (ShouldSwitchHead(newHead, oldHead))
            {
                // SetCurrentProject가 기존 프로젝트를 대체함
                TryBeginProject(newHead, playSound: true);
            }
            else
            {
                TryStartAllHeads(playSound: false);
            }
        }

        public void TryStartAllHeads(bool playSound)
        {
            if (Current.ProgramState != ProgramState.Playing) return;

            foreach (var kvp in queues)
            {
                var q = kvp.Value;
                q.RemoveAll(d => d == null || d.IsFinished);
                if (q.Count == 0) continue;
                if (GetCurrentProject(kvp.Key) != null) continue;

                var head = q[0];
                if (!head.CanStartNow) continue; // 막힌 머리는 건너뛰지 않고 대기 (UI에 표시)

                TryBeginProject(head, playSound);
            }
        }

        /// <summary>바닐라 AttemptBeginResearch와 같은 밈 충돌 확인. 취소하면 큐에서 제거</summary>
        private void TryBeginProject(ResearchProjectDef proj, bool playSound)
        {
            // 멀티플레이에선 먼저 시작하고 밈 충돌은 나중에 검사
            if (MultiplayerCompat.InMultiplayer)
            {
                BeginProject(proj, playSound);
                return;
            }

            if (pendingConfirmation == proj) return; // 이미 확인 대기 중

            var missing = ComputeUnlockedDefsThatHaveMissingMemes(proj);
            if (missing.Count == 0)
            {
                BeginProject(proj, playSound);
                return;
            }

            pendingConfirmation = proj;

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                BuildMissingMemesMessage(proj, missing),
                confirmedAct: delegate
                {
                    pendingConfirmation = null;
                    BeginProject(proj, playSound);
                },
                cancelAct: delegate
                {
                    pendingConfirmation = null;
                    DoRemove(proj);
                }));
            SoundDefOf.Tick_Low.PlayOneShotOnCamera();
        }

        private static string BuildMissingMemesMessage(ResearchProjectDef proj, List<(BuildableDef, List<string>)> missing)
        {
            var sb = new StringBuilder();
            sb.Append("ResearchProjectHasDefsWithMissingMemes".Translate(proj.LabelCap)).Append(":");
            sb.AppendLine();
            foreach (var (buildable, memes) in missing)
            {
                sb.AppendLine();
                sb.Append("  - ").Append(buildable.LabelCap.Colorize(ColoredText.NameColor))
                  .Append(" (").Append(memes.ToCommaList()).Append(")");
            }
            return sb.ToString();
        }

        /// <summary>바닐라 MainTabWindow_Research의 같은 이름 메서드와 동일</summary>
        internal static List<(BuildableDef, List<string>)> ComputeUnlockedDefsThatHaveMissingMemes(ResearchProjectDef project)
        {
            var result = new List<(BuildableDef, List<string>)>();
            if (!ModsConfig.IdeologyActive) return result;
            if (Faction.OfPlayer.ideos?.PrimaryIdeo == null) return result;

            foreach (var unlockedDef in project.UnlockedDefs)
            {
                if (!(unlockedDef is BuildableDef buildable) || buildable.canGenerateDefaultDesignator)
                {
                    continue;
                }

                List<string> missingMemes = null;
                foreach (var meme in DefDatabase<MemeDef>.AllDefsListForReading)
                {
                    if (!Faction.OfPlayer.ideos.HasAnyIdeoWithMeme(meme) && meme.AllDesignatorBuildables.Contains(buildable))
                    {
                        if (missingMemes == null) missingMemes = new List<string>();
                        missingMemes.Add(meme.LabelCap);
                    }
                }

                if (missingMemes != null)
                {
                    result.Add((buildable, missingMemes));
                }
            }
            return result;
        }

        private void BeginProject(ResearchProjectDef proj, bool playSound)
        {
            suppressSync = true;
            try
            {
                if (playSound) MultiplayerCompat.PlayActionSound(SoundDefOf.ResearchStart);
                Find.ResearchManager.SetCurrentProject(proj);
                TutorSystem.Notify_Event("StartResearchProject");
            }
            finally
            {
                suppressSync = false;
            }
            ResearchNode.InvalidateAllStates();
        }

        /// <summary>자기가 부른 StopProject의 포스트픽스 재진입을 막음</summary>
        private void StopProjectSuppressed(ResearchProjectDef proj)
        {
            suppressSync = true;
            try { Find.ResearchManager.StopProject(proj); }
            finally { suppressSync = false; }
        }

        public void Notify_ProjectFinished(ResearchProjectDef proj)
        {
            foreach (var q in queues.Values) q.Remove(proj);
            ResearchNode.InvalidateAllStates();
            TryStartAllHeads(playSound: false);
        }

        public void Notify_ProjectStopped(ResearchProjectDef proj)
        {
            if (suppressSync) return;
            // 외부(바닐라/타 모드) StopProject에 반응. 이미 결정론적 컨텍스트라 재sync 없이 코어를 직접 호출
            DoRemove(proj);
        }

        public void Notify_CurrentProjectChanged(ResearchProjectDef proj)
        {
            if (suppressSync || proj == null || proj.IsFinished) return;

            var q = GetOrAddQueue(ChannelOf(proj));
            q.Remove(proj);
            q.Insert(0, proj);
            ResearchNode.InvalidateAllStates();
        }

        /// <summary>기존 큐 순서를 최대한 유지하는 위상 정렬</summary>
        private void EnsureTopologicalOrder(ResearchChannel channel)
        {
            var q = GetOrAddQueue(channel);
            if (q.Count < 2) return;

            var ordered = new List<ResearchProjectDef>(q.Count);
            var visited = new HashSet<ResearchProjectDef>();

            void Visit(ResearchProjectDef d)
            {
                if (d == null || !visited.Add(d)) return;
                if (d.prerequisites != null)
                {
                    foreach (var p in d.prerequisites.Where(p => !p.IsFinished && q.Contains(p)))
                        Visit(p);
                }
                if (d.hiddenPrerequisites != null)
                {
                    foreach (var p in d.hiddenPrerequisites.Where(p => !p.IsFinished && q.Contains(p)))
                        Visit(p);
                }
                ordered.Add(d);
            }

            foreach (var d in q.ToList()) Visit(d);

            q.Clear();
            q.AddRange(ordered);
        }
    }
}
