using System;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    public sealed class UiButton : VisualElement
    {
        public readonly Label Label;

        public UiButton(string text, Action onClick)
        {
            AddToClassList("btn");
            Label = new Label(text) { pickingMode = PickingMode.Ignore };
            Label.AddToClassList("btn__label");
            Add(Label);
            this.AddManipulator(new Clickable(() => { if (enabledSelf) onClick?.Invoke(); }));
        }

        public string Text { get => Label.text; set => Label.text = value; }
    }

    /// <summary>Tiny helpers so screens can be built in code with USS classes.</summary>
    public static class Ui
    {
        public static VisualElement Div(params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static UiButton Button(string text, Action onClick, params string[] classes)
        {
            var b = new UiButton(text, onClick);
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        public static UiButton BackButton(string text, Action onClick)
        {
            var b = Button(text, onClick, "btn--ghost");
            var chevron = Div("chevron");
            chevron.pickingMode = PickingMode.Ignore;
            b.Insert(0, chevron);
            return b;
        }

        public static VisualElement Pill(string text, string variant = null)
        {
            var p = Div("pill");
            if (variant != null) p.AddToClassList(variant);
            p.Add(Text(text, "pill__label"));
            return p;
        }

        public static VisualElement Pips(int filled, bool big = false)
        {
            var row = Div("pips");
            if (big) row.AddToClassList("pips--big");
            for (int i = 0; i < 3; i++)
            {
                var pip = Div("pip");
                if (i < filled) pip.AddToClassList("pip--on");
                row.Add(pip);
            }
            return row;
        }

        public static VisualElement Metric(string value, string label, bool last = false)
        {
            var m = Div("metric");
            if (last) m.AddToClassList("metric--last");
            m.Add(Text(value, "metric__value"));
            m.Add(Text(label, "metric__label"));
            return m;
        }

        public static VisualElement Stat(string label, out Label value)
        {
            var s = Div("stat");
            s.Add(Text(label, "stat__label"));
            value = Text("-", "stat__value");
            s.Add(value);
            return s;
        }

        public static VisualElement Logo()
        {
            var logo = Div("logo");
            logo.Add(Div("logo__core"));
            // Little pins on each side make it read as a chip.
            for (int i = 0; i < 3; i++)
            {
                float offset = 9 + i * 10;
                AddPin(logo, -6, offset, 5, 2);
                AddPin(logo, 43, offset, 5, 2);
                AddPin(logo, offset, -6, 2, 5);
                AddPin(logo, offset, 43, 2, 5);
            }
            return logo;
        }

        static void AddPin(VisualElement parent, float left, float top, float w, float h)
        {
            var pin = Div("logo__pin");
            pin.style.left = left;
            pin.style.top = top;
            pin.style.width = w;
            pin.style.height = h;
            parent.Add(pin);
        }

        public static VisualElement MiniChip()
        {
            var chip = Div("mini-chip");
            for (int i = 0; i < 2; i++)
            {
                float top = 4 + i * 8;
                var l = Div("mini-chip__pin"); l.style.left = -7; l.style.top = top; chip.Add(l);
                var r = Div("mini-chip__pin"); r.style.left = 29; r.style.top = top; chip.Add(r);
            }
            return chip;
        }

        public static VisualElement Badge(bool ok = true)
        {
            var badge = Div("badge");
            if (!ok) badge.AddToClassList("badge--bad");
            if (ok) badge.Add(Div("badge__check"));
            return badge;
        }

        public static T Hidden<T>(this T e, bool hidden) where T : VisualElement
        {
            e.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
            return e;
        }
    }

    public static class Modal
    {
        public static VisualElement Show(VisualElement host, VisualElement card)
        {
            var overlay = Ui.Div("modal-overlay", "modal-overlay--hidden");
            card.AddToClassList("modal-card");
            overlay.Add(card);
            host.Add(overlay);
            overlay.schedule.Execute(() => overlay.RemoveFromClassList("modal-overlay--hidden")).ExecuteLater(16);
            return overlay;
        }

        public static void Close(VisualElement overlay)
        {
            overlay.AddToClassList("modal-overlay--hidden");
            overlay.schedule.Execute(overlay.RemoveFromHierarchy).ExecuteLater(200);
        }

        /// <summary>A two-button card. The second button is the primary one.</summary>
        public static VisualElement Card(VisualElement header, string title, string body,
            string secondary, Action onSecondary, string primary, Action onPrimary, params VisualElement[] extras)
        {
            var card = new VisualElement();
            if (header != null) card.Add(header);
            card.Add(Ui.Text(title, "modal-title"));
            if (!string.IsNullOrEmpty(body)) card.Add(Ui.Text(body, "modal-body"));
            foreach (var e in extras) card.Add(e);
            var actions = Ui.Div("modal-actions");
            if (secondary != null) actions.Add(Ui.Button(secondary, onSecondary, "btn--big", "btn--first"));
            if (primary != null) actions.Add(Ui.Button(primary, onPrimary, "btn--big", "btn--primary"));
            card.Add(actions);
            return card;
        }
    }
}
