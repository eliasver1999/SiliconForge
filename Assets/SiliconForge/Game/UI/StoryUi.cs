using System;
using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>Station log cards with a typewriter reveal, and the journal of everything read so far.</summary>
    public static class StoryUi
    {
        const float CharactersPerSecond = 90f;

        /// <summary>Shows one log entry over the given screen. Clicking the text finishes the typing early.</summary>
        public static void ShowEntry(VisualElement host, LogEntry entry, Action onClose = null)
        {
            var card = new VisualElement();
            card.AddToClassList("log-card");

            var head = Ui.Div("log-card__head");
            head.Add(Ui.Div("log-card__led"));
            head.Add(Ui.Text(Story.Station.ToUpperInvariant() + "  ·  LOG", "t-eyebrow"));
            head.Add(Ui.Div("spacer"));
            head.Add(Ui.Text($"DAY {entry.day}", "log-card__day"));
            card.Add(head);

            if (!string.IsNullOrEmpty(entry.heading) && entry.heading != "STATION LOG")
                card.Add(Ui.Text(entry.heading, "log-card__heading"));

            var text = Ui.Text("", "log-card__text");
            card.Add(text);

            VisualElement overlay = null;
            var actions = Ui.Div("modal-actions");
            var button = Ui.Button("Continue", () =>
            {
                Modal.Close(overlay);
                onClose?.Invoke();
            }, "btn--big", "btn--primary");
            actions.Add(button);
            card.Add(actions);

            overlay = Modal.Show(host, card);
            Typewriter(text, entry.text, card);
        }

        static void Typewriter(Label label, string full, VisualElement clickTarget)
        {
            float shown = 0f;
            long last = DateTime.Now.Ticks;
            IVisualElementScheduledItem item = null;
            item = label.schedule.Execute(() =>
            {
                long now = DateTime.Now.Ticks;
                shown += (now - last) / (float)TimeSpan.TicksPerSecond * CharactersPerSecond;
                last = now;
                int count = Math.Min(full.Length, (int)shown);
                label.text = full.Substring(0, count);
                if (count >= full.Length) item.Pause();
            }).Every(16);
            clickTarget.RegisterCallback<PointerDownEvent>(_ =>
            {
                shown = full.Length;
                label.text = full;
            });
        }

        /// <summary>Every entry the player has read, oldest first.</summary>
        public static List<LogEntry> ReadEntries(SaveData save)
        {
            var entries = new List<LogEntry>();
            if (save.HasSeen(Story.Prologue.id)) entries.Add(Story.Prologue);
            foreach (var level in Campaign.Levels)
            {
                if (!save.HasSeen(level.id)) continue;
                var entry = Story.ForLevel(level);
                if (entry != null) entries.Add(entry);
            }
            if (save.HasSeen(Story.Ending.id)) entries.Add(Story.Ending);
            return entries;
        }

        public static void ShowJournal(VisualElement host, SaveData save)
        {
            var card = new VisualElement();
            card.AddToClassList("journal-card");

            var head = Ui.Div("log-card__head");
            head.Add(Ui.Div("log-card__led"));
            head.Add(Ui.Text(Story.Station.ToUpperInvariant() + "  ·  STATION LOG", "t-eyebrow"));
            card.Add(head);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("journal__scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden; // matches the other panels; the wheel still scrolls
            var entries = ReadEntries(save);
            if (entries.Count == 0) scroll.Add(Ui.Text("No entries yet.", "t-dim"));
            foreach (var entry in entries)
            {
                var item = Ui.Div("journal__entry");
                var top = Ui.Div("row");
                top.Add(Ui.Text($"DAY {entry.day}", "log-card__day"));
                if (!string.IsNullOrEmpty(entry.heading) && entry.heading != "STATION LOG")
                    top.Add(Ui.Text(entry.heading, "journal__heading"));
                item.Add(top);
                item.Add(Ui.Text(entry.text, "journal__text"));
                scroll.Add(item);
            }
            card.Add(scroll);

            VisualElement overlay = null;
            var actions = Ui.Div("modal-actions");
            actions.Add(Ui.Button("Close", () => Modal.Close(overlay), "btn--big"));
            card.Add(actions);
            overlay = Modal.Show(host, card);
            // Open at the newest entry.
            scroll.schedule.Execute(() => scroll.scrollOffset = new UnityEngine.Vector2(0, float.MaxValue)).ExecuteLater(30);
        }
    }
}
