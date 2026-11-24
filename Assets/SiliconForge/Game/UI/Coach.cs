using System;
using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>
    /// Walks a new player through the first contract one step at a time. Each step is checked against the board itself,
    /// so it keeps up if the player works ahead and steps back if they delete something.
    /// </summary>
    public sealed class FirstContractCoach
    {
        public const string SeenId = "tutorial";

        sealed class Step
        {
            public string text;
            public Func<bool> done;
            public Func<VisualElement> target = () => null;
        }

        public VisualElement Root { get; }

        readonly Func<CircuitData> circuit;
        readonly List<Step> steps = new List<Step>();
        readonly List<VisualElement> segments = new List<VisualElement>();
        readonly Label counter, text;
        int toggles;
        bool passed;
        int current = -1;
        VisualElement litTarget;
        IVisualElementScheduledItem pulse;

        public FirstContractCoach(Func<CircuitData> circuit, VisualElement nandItem, Func<string, VisualElement> partOfType, VisualElement runButton, Action skip)
        {
            this.circuit = circuit;
            steps.Add(new Step
            {
                text = "Drag a NAND from the parts list onto the board. Clicking it works too.",
                done = () => Any(p => p.type == PartTypes.Nand),
                target = () => nandItem,
            });
            steps.Add(new Step
            {
                text = "Now wire In to the NAND. Press on the dot on the right of In, drag it to one of the dots on the left of the NAND, and let go.",
                done = () => Wired(PartTypes.Input, PartTypes.Nand),
                target = () => partOfType(PartTypes.Input),
            });
            steps.Add(new Step
            {
                text = "A NAND has two inputs. Wire In to the other one as well. One output can feed as many inputs as you like.",
                done = () => Wired(PartTypes.Input, PartTypes.Nand) && Any(p => p.type == PartTypes.Nand && BothInputsWired(p.id)),
                target = () => partOfType(PartTypes.Input),
            });
            steps.Add(new Step
            {
                text = "Last wire: from the NAND's output on its right side to Out.",
                done = () => Wired(PartTypes.Nand, PartTypes.Output),
                target = () => partOfType(PartTypes.Output),
            });
            steps.Add(new Step
            {
                text = "Click In a few times to switch it on and off. Out should always show the opposite. Lit wires carry a 1.",
                done = () => toggles >= 2,
                target = () => partOfType(PartTypes.Input),
            });
            steps.Add(new Step
            {
                text = "Looks right? Press Run tests, or Enter, to check every case and certify your inverter.",
                done = () => passed,
                target = () => runButton,
            });

            Root = Ui.Div("coach");
            Root.pickingMode = PickingMode.Position;
            var head = Ui.Div("coach__head");
            head.Add(Ui.Text("GETTING STARTED", "t-eyebrow", "coach__eyebrow"));
            head.Add(Ui.Div("spacer"));
            counter = Ui.Text("", "coach__count");
            head.Add(counter);
            Root.Add(head);

            var bar = Ui.Div("coach__bar");
            foreach (var _ in steps)
            {
                var segment = Ui.Div("coach__segment");
                segments.Add(segment);
                bar.Add(segment);
            }
            Root.Add(bar);

            text = Ui.Text("", "coach__text");
            Root.Add(text);

            var foot = Ui.Div("coach__foot");
            foot.Add(Ui.Div("spacer"));
            foot.Add(Ui.Button("Skip tutorial", skip, "btn--small", "btn--ghost"));
            Root.Add(foot);

            pulse = Root.schedule.Execute(() => litTarget?.ToggleInClassList("coach-target--on")).Every(650);
            Refresh();
        }

        public void InputToggled()
        {
            toggles++;
            Refresh();
        }

        public void TestsRun(bool allPassed)
        {
            passed = allPassed;
            Refresh();
        }

        /// <summary>Re-checks the board after every edit.</summary>
        public void Refresh()
        {
            int next = steps.FindIndex(s => !s.done());
            if (next < 0) next = steps.Count - 1;
            if (next == current)
            {
                var target = steps[current].target();
                if (target != litTarget) Highlight(target); // the board rebuilds its parts on undo
                return;
            }
            if (next > current && current >= 0) Sfx.Play(SfxId.Click);
            current = next;

            counter.text = $"Step {current + 1} of {steps.Count}";
            text.text = steps[current].text;
            for (int i = 0; i < segments.Count; i++)
            {
                segments[i].EnableInClassList("coach__segment--done", i < current);
                segments[i].EnableInClassList("coach__segment--now", i == current);
            }
            Highlight(steps[current].target());
        }

        public void Dispose()
        {
            pulse?.Pause();
            Highlight(null);
            Root.RemoveFromHierarchy();
        }

        void Highlight(VisualElement target)
        {
            if (litTarget != null)
            {
                litTarget.RemoveFromClassList("coach-target");
                litTarget.RemoveFromClassList("coach-target--on");
            }
            litTarget = target;
            litTarget?.AddToClassList("coach-target");
        }

        bool Any(Predicate<PartData> match) => circuit().parts.Exists(match);

        bool Wired(string fromType, string toType)
        {
            var c = circuit();
            return c.wires.Exists(w => c.Find(w.fromPart)?.type == fromType && c.Find(w.toPart)?.type == toType);
        }

        bool BothInputsWired(int part) => circuit().DriverOf(part, 0) != null && circuit().DriverOf(part, 1) != null;
    }
}
