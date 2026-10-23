using RimWorld;
using UnityEngine;

namespace YART.Data
{
    public static class ActiveColors
    {
        // eraColors, 프리셋 era 배열, EraIndex와 같은 순서여야 함
        public static readonly TechLevel[] EraOrder =
        {
            TechLevel.Animal, TechLevel.Neolithic, TechLevel.Medieval, TechLevel.Industrial,
            TechLevel.Spacer, TechLevel.Ultra, TechLevel.Archotech,
        };

        private static readonly Color[] era = new Color[ColorPalette.EraCount];

        public static Color PrereqMet;
        public static Color PrereqUnmet;

        static ActiveColors()
        {
            Apply(ColorPalettes.Get(ColorPalettes.DefaultId));
        }

        public static void Apply(ColorPalette p)
        {
            int n = Mathf.Min(ColorPalette.EraCount, p.era.Length);
            for (int i = 0; i < n; i++) era[i] = p.era[i];
            PrereqMet = p.prereqMet;
            PrereqUnmet = p.prereqUnmet;
        }

        public static int EraIndex(TechLevel tl)
        {
            switch (tl)
            {
                case TechLevel.Animal: return 0;
                case TechLevel.Neolithic: return 1;
                case TechLevel.Medieval: return 2;
                case TechLevel.Industrial: return 3;
                case TechLevel.Spacer: return 4;
                case TechLevel.Ultra: return 5;
                case TechLevel.Archotech: return 6;
                case TechLevel.Undefined:
                default: return -1;
            }
        }

        public static Color Era(TechLevel tl)
        {
            int i = EraIndex(tl);
            return i < 0 ? Color.gray : era[i];
        }
    }
}
