using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace YART.Compat
{
    /// <summary>World Tech Level: 월드 테크레벨 이하 연구만 표시. WTL 옵션에 따라 게임 중에도 가시 집합이 바뀔 수 있어 재빌드 필요</summary>
    public static class WorldTechLevelCompat
    {
        private const int SigInactive = 0;
        private const int SigUnfiltered = 1;        // 게임 밖

        private static bool _probed;
        private static Func<ResearchProjectDef, bool> _shouldShow;

        private static int _builtSignature = SigInactive;

        private static void EnsureProbed()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (ModLister.GetActiveModWithIdentifier("m00nl1ght.WorldTechLevel", ignorePostfix: true) == null)
                    return;

                var utilType = AccessTools.TypeByName("WorldTechLevel.ResearchUtility");
                var visMethod = utilType == null ? null
                    : AccessTools.Method(utilType, "ShouldProjectBeVisible", new[] { typeof(ResearchProjectDef) });

                if (visMethod == null)
                {
                    Log.Warning("[YART] World Tech Level detected, but binding ShouldProjectBeVisible failed (version mismatch?). Research visibility filter disabled.");
                    return;
                }

                _shouldShow = (Func<ResearchProjectDef, bool>)Delegate.CreateDelegate(
                    typeof(Func<ResearchProjectDef, bool>), visMethod);

                if (Prefs.DevMode) Log.Message("[YART] World Tech Level detected — research visibility filter enabled.");
            }
            catch (Exception e)
            {
                _shouldShow = null;
                Log.Warning("[YART] World Tech Level compatibility init failed — visibility filter disabled: " + e);
            }
        }

        /// <summary>가시 def의 shortHash 누적이라 레벨 변경뿐 아니라 Filter_Research 토글도 잡힘</summary>
        private static int CurrentSignature
        {
            get
            {
                EnsureProbed();
                if (_shouldShow == null) return SigInactive;
                if (Current.ProgramState != ProgramState.Playing) return SigUnfiltered;
                try
                {
                    int hash = 17;
                    var defs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
                    for (int i = 0; i < defs.Count; i++)
                    {
                        if (_shouldShow(defs[i])) hash = hash * 31 + defs[i].shortHash;
                    }
                    return hash;
                }
                catch { return SigUnfiltered; }
            }
        }

        /// <summary>WTL 미설치, 게임 밖, 판정 실패 시 true</summary>
        public static bool ShouldShow(ResearchProjectDef def)
        {
            EnsureProbed();
            if (_shouldShow == null) return true;
            if (Current.ProgramState != ProgramState.Playing) return true;
            try { return _shouldShow(def); }
            catch { return true; }
        }

        /// <summary>그래프 빌드(CreateNodes) 직후 호출</summary>
        public static void MarkBuilt()
        {
            _builtSignature = CurrentSignature;
        }

        public static bool NeedsRebuild
        {
            get
            {
                EnsureProbed();
                if (_shouldShow == null) return false;
                return CurrentSignature != _builtSignature;
            }
        }
    }
}
