using SiliconForge.Core;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>Title screen and contract list, grouped by era.</summary>
    public sealed class CampaignScreen : IScreen
    {
        public VisualElement Root { get; }
        readonly GameApp app;

        public CampaignScreen(GameApp app)
        {
            this.app = app;
            Root = Ui.Div("screen", "campaign");

            var header = Ui.Div("campaign__header");
            var brand = Ui.Div("brand");
            brand.Add(Ui.Logo());
            var titles = new VisualElement();
            titles.Add(Ui.Text("SILICON FORGE", "brand__name"));
            titles.Add(Ui.Text("From one logic gate to a working computer.", "brand__tagline"));
            brand.Add(titles);
            header.Add(brand);
            header.Add(Ui.Div("spacer"));

            var stats = Ui.Div("header-stats");
            stats.Add(HeaderStat($"{app.Save.SolvedCount} / {Campaign.Levels.Count}", "CONTRACTS"));
            stats.Add(HeaderStat(app.Save.chips.Count.ToString(), "CHIPS DESIGNED"));
            header.Add(stats);
            Root.Add(header);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("campaign__scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;

            LevelSpec next = FirstOpenLevel();
            for (int e = 0; e < Campaign.Eras.Count; e++)
            {
                var era = Campaign.Eras[e];
                var section = Ui.Div("era");
                var head = Ui.Div("era__head");
                head.Add(Ui.Text($"ERA {era.numeral}", "era__numeral"));
                head.Add(Ui.Text(era.name, "era__name"));
                head.Add(Ui.Text(era.description, "era__desc"));
                section.Add(head);

                var cards = Ui.Div("era__cards");
                foreach (var level in Campaign.Levels)
                    if (level.era == e) cards.Add(Card(level, level == next));
                section.Add(cards);
                scroll.Add(section);
            }
            Root.Add(scroll);

            var footer = Ui.Div("campaign__footer");
            footer.Add(Ui.Text("Progress saves automatically.", "footer-note"));
            footer.Add(Ui.Div("spacer"));
            footer.Add(Ui.Button("Reset progress", ConfirmReset, "btn--ghost", "btn--small"));
            Root.Add(footer);
        }

        public void Tick(float dt) { }
        public void Dispose() { }

        LevelSpec FirstOpenLevel()
        {
            foreach (var level in Campaign.Levels)
                if (app.IsUnlocked(level) && !app.Save.IsSolved(level.id)) return level;
            return null;
        }

        static VisualElement HeaderStat(string value, string label)
        {
            var stat = Ui.Div("header-stat");
            stat.Add(Ui.Text(value, "header-stat__value"));
            stat.Add(Ui.Text(label, "header-stat__label"));
            return stat;
        }

        VisualElement Card(LevelSpec level, bool isNext)
        {
            bool unlocked = app.IsUnlocked(level);
            var progress = app.Save.Progress(level.id);

            var card = Ui.Div("card");
            if (level.workshop) card.AddToClassList("card--wide");
            if (!unlocked) card.AddToClassList("card--locked");
            if (isNext) card.AddToClassList("card--next");

            var top = Ui.Div("card__top");
            top.Add(Ui.Text(level.number.ToString("00"), "card__num"));
            top.Add(Ui.Div("spacer"));
            if (progress.solved) top.Add(Ui.Pill(level.workshop ? "RUNNING" : "CERTIFIED", "pill--ok"));
            else if (!unlocked) top.Add(Ui.Pill("LOCKED"));
            else if (isNext) top.Add(Ui.Pill(level.workshop ? "3D BUILD" : "OPEN", "pill--accent"));
            else top.Add(Ui.Pill("OPEN"));
            card.Add(top);

            card.Add(Ui.Text(level.title, "card__title"));
            card.Add(Ui.Text(level.tagline, "card__tagline"));

            var foot = Ui.Div("card__foot");
            if (progress.solved)
            {
                foot.Add(Ui.Pips(progress.stars));
                foot.Add(Ui.Div("spacer"));
                if (!level.workshop) foot.Add(Ui.Text($"{progress.bestGates} gates", "card__meta"));
            }
            else if (level.workshop)
            {
                foot.Add(Ui.Text("Assemble a real machine in the workshop", "card__meta"));
            }
            else
            {
                foot.Add(Ui.Text(level.par == 1 ? "Par 1 gate" : $"Par {level.par} gates", "card__meta"));
            }
            card.Add(foot);

            foreach (var child in card.Children()) child.pickingMode = PickingMode.Ignore;
            if (unlocked) card.AddManipulator(new Clickable(() => app.OpenLevel(level)));
            return card;
        }

        void ConfirmReset()
        {
            VisualElement overlay = null;
            var card = Modal.Card(
                Ui.Badge(false),
                "Reset all progress?",
                "This deletes every chip you designed and every contract you completed. It can't be undone.",
                "Cancel", () => Modal.Close(overlay),
                "Reset", () => app.ResetProgress());
            overlay = Modal.Show(Root, card);
        }
    }
}
