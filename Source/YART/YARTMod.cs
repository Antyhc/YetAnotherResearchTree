using HarmonyLib;
using RimWorld;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace YART
{
    public class YARTMod : Mod
    {
        public static YARTModSettings Settings { get; private set; }

        public YARTMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<YARTModSettings>();
            Settings.ApplyColors();

            var harmony = new Harmony("seohyeon.yart");
            harmony.PatchAll();

            Log.Message("[YART] Patched!");
        }

        public override string SettingsCategory() => "Yet Another Research Tree";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            Settings.DoSettingsWindowContents(inRect);
        }
    }

    [StaticConstructorOnStartup]
    public static class VanillaTabReplacer
    {
        private static readonly FieldInfo TabWindowIntField = AccessTools.Field(typeof(MainButtonDef), "tabWindowInt");

        static VanillaTabReplacer()
        {
            Apply(YARTMod.Settings.replaceVanillaResearchTab);
        }

        public static void Apply(bool replace)
        {
            var researchButton = MainButtonDefOf.Research;
            researchButton.tabWindowClass = replace ? typeof(MainTabWindow_YART) : typeof(MainTabWindow_Research);
            TabWindowIntField.SetValue(researchButton, null);

            if (Current.ProgramState == ProgramState.Playing)
            {
                Find.MainTabsRoot?.EscapeCurrentTab(playSound: false);
            }
        }

        public static void SwitchTo(bool useYart)
        {
            // 다음에 연구 버튼을 눌러도 같은 창이 열리게 저장
            if (YARTMod.Settings.replaceVanillaResearchTab != useYart)
            {
                YARTMod.Settings.replaceVanillaResearchTab = useYart;
                YARTMod.Settings.Write();
            }

            var researchButton = MainButtonDefOf.Research;

            // 두 창 모두 def가 Research라 SetCurrentTab/ToggleTab으론 같은 프레임에 다시 열리지 않고 닫히기만 함
            // 그래서 WindowStack을 직접 조작해 바꾼다
            var open = Find.WindowStack?.WindowOfType<MainTabWindow>();
            bool wasOpen = open != null && open.def == researchButton;
            if (wasOpen) Find.WindowStack.TryRemove(open, doCloseSound: false);

            researchButton.tabWindowClass = useYart ? typeof(MainTabWindow_YART) : typeof(MainTabWindow_Research);
            TabWindowIntField.SetValue(researchButton, null);

            if (wasOpen)
            {
                Find.WindowStack.Add(researchButton.TabWindow);
                SoundDefOf.TabOpen.PlayOneShotOnCamera();
            }
        }
    }
}
