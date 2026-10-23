using UnityEngine;
using Verse;

namespace YART
{
    /// <summary>게임 중 YART 창에서 여는 설정 창</summary>
    public class Dialog_YARTSettings : Window
    {
        public Dialog_YARTSettings()
        {
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            closeOnAccept = false;
            preventCameraMotion = false;
            draggable = true;
        }

        public override Vector2 InitialSize => new Vector2(760f, 540f);

        public override void DoWindowContents(Rect inRect)
        {
            using (Utils.Temporary.Font(GameFont.Medium))
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f),
                    "Yet Another Research Tree");

            Rect body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            YARTMod.Settings.DoSettingsWindowContents(body);
        }

        public override void PostClose()
        {
            base.PostClose();
            YARTMod.Settings.Write();
        }
    }
}
