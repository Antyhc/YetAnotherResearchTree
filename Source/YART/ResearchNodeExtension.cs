using UnityEngine;
using Verse;

namespace YART
{
    /// <summary>
    /// A research node customization
    /// <modExtensions>
    ///   <li Class="YART.ResearchNodeExtension">
    ///     <iconPath>UI/Icons/MyResearchIcon</iconPath>
    ///   </li>
    /// </modExtensions>
    /// </summary>
    public class ResearchNodeExtension : DefModExtension
    {
        [Description("iconPath where textures are located")]
        public string iconPath;

        [Unsaved] private Texture2D resolvedIcon;
        [Unsaved] private bool resolveAttempted;

        /// <summary>지정 아이콘 (lazy — 메인스레드 OnGUI 최초 접근 시 로드, 결과는 null 포함 캐시).</summary>
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
