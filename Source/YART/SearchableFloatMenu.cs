using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using YART.Data;
using YART.Utils;

namespace YART
{
    public class SearchableFloatMenu : Window
    {
        public class TrailingButton
        {
            public readonly Texture2D Icon;
            public readonly Action Action;
            public readonly string Tooltip;
            public readonly Color Color;

            public TrailingButton(Texture2D icon, Action action, string tooltip = null, Color? color = null)
            {
                Icon = icon;
                Action = action;
                Tooltip = tooltip;
                Color = color ?? Color.white;
            }
        }

        public class LeadingToggle
        {
            public readonly Func<bool> IsOn;
            public readonly Action OnToggle;
            public readonly Texture2D OnTex, OffTex;
            public readonly Color OnColor, OffColor;

            public LeadingToggle(Func<bool> isOn, Action onToggle, Texture2D onTex, Texture2D offTex, Color onColor, Color offColor)
            {
                IsOn = isOn;
                OnToggle = onToggle;
                OnTex = onTex;
                OffTex = offTex;
                OnColor = onColor;
                OffColor = offColor;
            }
        }

        public class Option
        {
            public readonly string Label;
            public readonly Action Action;
            public readonly bool Selected;
            public readonly List<TrailingButton> Trailing; // 행 오른쪽 아이콘(편집, 삭제 등)
            public readonly LeadingToggle Leading;          // 행 왼쪽 토글(즐겨찾기 별)

            public Option(string label, Action action, bool selected = false,
                List<TrailingButton> trailing = null, LeadingToggle leading = null)
            {
                Label = label;
                Action = action;
                Selected = selected;
                Trailing = trailing;
                Leading = leading;
            }
        }

        private const float RowHeight = 28f;
        private const float SearchHeight = 28f;
        private const int MaxVisibleRows = 12;
        private const string SearchControlName = "YARTMenuSearch";

        private readonly List<Option> options;
        private readonly List<Option> filtered = new List<Option>();
        private readonly Vector2 anchor;
        private readonly float menuWidth;
        private string query = "";
        private Vector2 scroll;
        private bool focusRequested;

        protected override float Margin => 4f;

        public SearchableFloatMenu(Vector2 anchorScreen, float width, List<Option> options)
        {
            this.options = options;
            anchor = anchorScreen;
            menuWidth = Mathf.Max(220f, width);

            layer = WindowLayer.Super;
            closeOnClickedOutside = true;
            doWindowBackground = false;
            drawShadow = false;
            preventCameraMotion = false;
            soundAppear = SoundDefOf.FloatMenu_Open;
        }

        protected override void SetInitialSizeAndPosition()
        {
            float listHeight = Mathf.Min(options.Count, MaxVisibleRows) * RowHeight;
            float height = Margin * 2 + SearchHeight + 4f + listHeight + 4f;
            windowRect = new Rect(anchor.x, anchor.y, menuWidth, height);
            if (windowRect.xMax > UI.screenWidth) windowRect.x = UI.screenWidth - windowRect.width;
            if (windowRect.yMax > UI.screenHeight) windowRect.y = UI.screenHeight - windowRect.height;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect bgRect = inRect.ExpandedBy(Margin);
            Widgets.DrawBoxSolid(bgRect, Constraints.PanelBg);
            GUIDrawingUtilities.DrawBorderLines(bgRect, Constraints.PanelBorder, 1f);

            // TextField가 키를 먹기 전에 판정해야 한다
            bool enterPressed = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            Rect searchRect = new Rect(inRect.x, inRect.y, inRect.width, SearchHeight);
            DrawSearchField(searchRect);
            ApplyFilter();

            if (enterPressed && filtered.Count > 0)
            {
                Select(filtered[0]);
                Event.current.Use();
                return;
            }

            DrawOptionList(inRect, searchRect, out Option clicked, out Action trailingAction, out Action leadingAction);

            if (leadingAction != null)
            {
                leadingAction();
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                Event.current.Use();
                return;
            }

            if (trailingAction != null)
            {
                Close(doCloseSound: false);
                trailingAction();
                return;
            }

            if (clicked != null)
            {
                Select(clicked);
            }
        }

        private void DrawSearchField(Rect searchRect)
        {
            Widgets.DrawBoxSolid(searchRect, new Color(0f, 0f, 0f, 0.4f));
            GUIDrawingUtilities.DrawBorderLines(searchRect, Constraints.PanelBorder, 1f);

            GUI.SetNextControlName(SearchControlName);
            query = Widgets.TextField(new Rect(searchRect.x + 6f, searchRect.y + 2f, searchRect.width - 12f, 24f), query);
            if (!focusRequested)
            {
                GUI.FocusControl(SearchControlName);
                focusRequested = true;
            }

            if (query.Length != 0 || GUI.GetNameOfFocusedControl() == SearchControlName) return;

            using (Temporary.Font(GameFont.Tiny))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(Color.gray))
            {
                Widgets.Label(new Rect(searchRect.x + 8f, searchRect.y, searchRect.width - 12f, searchRect.height), "Filter...");
            }
        }

        private void ApplyFilter()
        {
            filtered.Clear();
            string q = query.Trim().ToLowerInvariant();
            for (int i = 0; i < options.Count; i++)
            {
                if (q.Length == 0 || options[i].Label.ToLowerInvariant().Contains(q))
                {
                    filtered.Add(options[i]);
                }
            }
        }

        private void DrawOptionList(Rect inRect, Rect searchRect, out Option clicked, out Action trailingAction, out Action leadingAction)
        {
            clicked = null;
            trailingAction = null;
            leadingAction = null;

            Rect outRect = new Rect(inRect.x, searchRect.yMax + 4f, inRect.width, inRect.height - SearchHeight - 8f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - (filtered.Count > MaxVisibleRows ? 16f : 0f), filtered.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float rowY = 0f;
            foreach (var option in filtered)
            {
                if (DrawRow(new Rect(0f, rowY, viewRect.width, RowHeight), option, ref trailingAction, ref leadingAction))
                {
                    clicked = option;
                }
                rowY += RowHeight;
            }

            if (filtered.Count == 0)
            {
                using (Temporary.Font(GameFont.Tiny))
                using (Temporary.Anchor(TextAnchor.MiddleCenter))
                using (Temporary.Color(Color.gray))
                {
                    Widgets.Label(new Rect(0f, 0f, outRect.width, RowHeight), "(no match)");
                }
            }

            Widgets.EndScrollView();
        }

        /// <summary>라벨 클릭 여부를 반환. 아이콘 클릭은 ref 액션으로 전달</summary>
        private static bool DrawRow(Rect rowRect, Option option, ref Action trailingAction, ref Action leadingAction)
        {
            if (Mouse.IsOver(rowRect)) Widgets.DrawHighlight(rowRect);
            if (option.Selected) Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.06f));

            float leadingW = DrawLeadingToggle(rowRect, option.Leading, ref leadingAction);
            float trailingW = DrawTrailingButtons(rowRect, option.Trailing, ref trailingAction);

            Rect selectRect = new Rect(rowRect.x + leadingW, rowRect.y, rowRect.width - leadingW - trailingW, rowRect.height);

            using (Temporary.Font(GameFont.Small))
            using (Temporary.Anchor(TextAnchor.MiddleLeft))
            using (Temporary.Color(option.Selected ? new Color(0.6f, 0.8f, 1f) : Color.white))
            {
                Widgets.Label(new Rect(selectRect.x + 8f, selectRect.y, selectRect.width - 12f, selectRect.height),
                    option.Label.Truncate(selectRect.width - 12f));
            }

            return Widgets.ButtonInvisible(selectRect);
        }

        /// <summary>라벨이 비켜야 할 폭을 반환</summary>
        private static float DrawLeadingToggle(Rect rowRect, LeadingToggle leading, ref Action leadingAction)
        {
            if (leading == null) return 0f;

            float btn = RowHeight - 10f;
            Rect lRect = new Rect(rowRect.x + 5f, rowRect.y + (RowHeight - btn) / 2f, btn, btn);
            bool on = leading.IsOn();
            using (Temporary.Color(on ? leading.OnColor : leading.OffColor))
            {
                if (Widgets.ButtonImage(lRect, on ? leading.OnTex : leading.OffTex))
                    leadingAction = leading.OnToggle;
            }
            return btn + 8f;
        }

        /// <summary>라벨이 비켜야 할 폭을 반환</summary>
        private static float DrawTrailingButtons(Rect rowRect, List<TrailingButton> trailing, ref Action trailingAction)
        {
            if (trailing == null || trailing.Count == 0) return 0f;

            float right = rowRect.xMax - 2f;
            float tx = right;
            foreach (var tb in trailing)
            {
                float btn = RowHeight - 8f;
                tx -= btn + 4f;
                Rect tRect = new Rect(tx, rowRect.y + 4f, btn, btn);
                if (tb.Tooltip != null) TooltipHandler.TipRegion(tRect, tb.Tooltip);
                using (Temporary.Color(Mouse.IsOver(tRect) ? tb.Color : GenColor.WithAlpha(tb.Color, 0.75f)))
                {
                    if (Widgets.ButtonImage(tRect, tb.Icon)) trailingAction = tb.Action;
                }
            }
            return right - tx + 4f;
        }

        private void Select(Option option)
        {
            Close(doCloseSound: false);
            SoundDefOf.Click.PlayOneShotOnCamera();
            option.Action?.Invoke();
        }
    }
}
