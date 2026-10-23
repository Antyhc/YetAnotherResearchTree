using UnityEngine;
using Verse;

namespace YART
{
    // Usage (inside <modExtensions>):
    // <li Class="YART.ResearchNodeExtension">
    //   <iconPath>UI/Icons/MyResearchIcon</iconPath>
    // </li>
    public class ResearchNodeExtension : DefModExtension
    {
        [Description("iconPath where textures are located")]
        public string iconPath;

        [Unsaved] private Texture2D resolvedIcon;
        [Unsaved] private bool resolveAttempted;

        /// <summary>메인 스레드에서 처음 접근할 때 로드. null 결과도 캐시</summary>
        public Texture2D Icon
        {
            get
            {
                if (resolveAttempted) return resolvedIcon;
                resolveAttempted = true;
                if (string.IsNullOrEmpty(iconPath)) return resolvedIcon;

                resolvedIcon = ContentFinder<Texture2D>.Get(iconPath, reportFailure: false);
                if (resolvedIcon == null)
                {
                    Log.WarningOnce($"[YART] ResearchNodeIconExtension: texture not found at '{iconPath}', falling back to source mod icon.",
                        iconPath.GetHashCode() ^ 0x6974);
                }
                return resolvedIcon;
            }
        }
    }
}
