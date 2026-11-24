using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>The circuit bench: parts on the left, board in the middle, contract and truth table on the right.</summary>
    public sealed class BenchScreen : IScreen
    {
        const int MaxUndo = 100;

        public VisualElement Root { get; }

        readonly GameApp app;
        readonly LevelSpec level;
        readonly CircuitView view;
        internal CircuitView View => view;
        readonly bool[] inputs;
        readonly Stack<string> undo = new Stack<string>();
        readonly Stack<string> redo = new Stack<string>();

        CircuitData circuit;
        string snapshot;
        Netlist netlist;
        ulong[] state;
        TestReport report;
        int lastLiveRow = -1;

        Label gatesValue, delayValue, powerValue, tableMeta, toastText;
        VisualElement gatesStat, table, banner, toast;
        Label bannerText;
        UiButton undoButton, redoButton, runButton, hintButton;
        VisualElement hintList, nandPaletteItem;
        Label hintNote;
        ScrollView briefScroll;
        FirstContractCoach coach;
        int failedRuns;
        IVisualElementScheduledItem toastHide;

        // Palette drag state
        string spawnType;
        Vector2 spawnDown;
        VisualElement spawnGhost;
        int spawnCount;

        public BenchScreen(GameApp app, LevelSpec level)
        {
            this.app = app;
            this.level = level;
            inputs = new bool[level.inputs.Length];

            Root = Ui.Div("screen", "bench");
            Root.Add(BuildTopBar());

            var body = Ui.Div("bench__body");
            body.Add(BuildPalette());

            var board = Ui.Div("board");
            view = new CircuitView(app.Catalog);
            board.Add(view);
            bool coached = level.number == 1 && !app.Save.IsSolved(level.id) && !app.Save.HasSeen(FirstContractCoach.SeenId);
            if (!coached && !string.IsNullOrEmpty(level.tip) && !app.Save.IsSolved(level.id)) board.Add(BuildTip());
            board.Add(BuildZoomBar());
            toast = Ui.Div("toast");
            toast.pickingMode = PickingMode.Ignore;
            var toastBody = Ui.Div("toast__body");
            toastText = Ui.Text("", "toast__text");
            toastBody.Add(toastText);
            toast.Add(toastBody);
            board.Add(toast);
            body.Add(board);

            body.Add(BuildBrief());
            Root.Add(body);

            circuit = LoadOrCreateCircuit();
            snapshot = JsonUtility.ToJson(circuit);
            view.AllowedTypes = new HashSet<string>(level.parts);
            view.SetCircuit(circuit);
            view.Edited += OnEdited;
            view.InputToggled += i => { inputs[i] = !inputs[i]; coach?.InputToggled(); };
            view.Message += ShowToast;
            view.RegisterCallback<KeyDownEvent>(OnKeyDown);

            Recompile();
            if (coached) StartCoach(board);
        }

        public void Dispose() => coach?.Dispose();

        // ------------------------------------------------------------------ first-contract tutorial

        void StartCoach(VisualElement board)
        {
            coach = new FirstContractCoach(
                () => circuit,
                nandPaletteItem,
                type => view.ElementFor(circuit.parts.Find(p => p.type == type)?.id ?? -1),
                runButton,
                EndCoach);
            board.Insert(board.IndexOf(view) + 1, coach.Root);
        }

        void EndCoach()
        {
            if (coach == null) return;
            coach.Dispose();
            coach = null;
            app.Save.MarkSeen(FirstContractCoach.SeenId);
            app.Persist();
        }

        public void Tick(float dt)
        {
            if (netlist == null) return;
            for (int i = 0; i < inputs.Length; i++)
                state[netlist.InputNets[i]] = inputs[i] ? ulong.MaxValue : 0UL;
            netlist.Evaluate(state);
            view.RefreshSignals();

            int live = InputBits;
            if (!level.IsSequential && live != lastLiveRow)
            {
                lastLiveRow = live;
                RenderTable();
            }
        }

        int InputBits
        {
            get
            {
                int bits = 0;
                for (int i = 0; i < inputs.Length; i++) if (inputs[i]) bits |= 1 << i;
                return bits;
            }
        }

        // ------------------------------------------------------------------ circuit lifecycle

        CircuitData LoadOrCreateCircuit()
        {
            var saved = app.Save.Work(level.id);
            if (saved != null && IsUsable(saved)) return saved;
            var fresh = NewCircuit();
            app.Save.SetWork(level.id, fresh);
            return fresh;
        }

        bool IsUsable(CircuitData c)
        {
            c.parts.RemoveAll(p => !app.Catalog.Has(p.type));
            c.wires.RemoveAll(w => c.Find(w.fromPart) == null || c.Find(w.toPart) == null);
            int ins = 0, outs = 0;
            foreach (var p in c.parts)
            {
                if (p.type == PartTypes.Input) ins++;
                if (p.type == PartTypes.Output) outs++;
            }
            return ins == level.inputs.Length && outs == level.outputs.Length;
        }

        CircuitData NewCircuit()
        {
            var c = new CircuitData();
            float inSpacing = level.inputs.Length > 4 ? 60f : 96f;
            float outSpacing = level.outputs.Length > 4 ? 72f : 96f;
            float right = level.inputs.Length > 4 ? 960f : 660f;
            for (int i = 0; i < level.inputs.Length; i++)
            {
                float y = Snap(i * inSpacing - (level.inputs.Length - 1) * inSpacing * 0.5f);
                c.AddPart(PartTypes.Input, 0, y, i, level.inputs[i]);
            }
            for (int j = 0; j < level.outputs.Length; j++)
            {
                float y = Snap(j * outSpacing - (level.outputs.Length - 1) * outSpacing * 0.5f);
                c.AddPart(PartTypes.Output, right, y, j, level.outputs[j]);
            }
            return c;
        }

        static float Snap(float v) => Mathf.Round(v / PartLayout.Grid) * PartLayout.Grid;

        void OnEdited()
        {
            undo.Push(snapshot);
            if (undo.Count > MaxUndo) TrimUndo();
            redo.Clear();
            snapshot = JsonUtility.ToJson(circuit);
            Recompile();
            app.Persist();
            coach?.Refresh();
        }

        void TrimUndo()
        {
            var items = undo.ToArray();
            undo.Clear();
            for (int i = MaxUndo - 1; i >= 0; i--) undo.Push(items[i]);
        }

        void Undo() => Restore(undo, redo);
        void Redo() => Restore(redo, undo);

        void Restore(Stack<string> from, Stack<string> to)
        {
            if (from.Count == 0) return;
            to.Push(snapshot);
            snapshot = from.Pop();
            circuit = JsonUtility.FromJson<CircuitData>(snapshot);
            app.Save.SetWork(level.id, circuit);
            view.SetCircuit(circuit);
            Recompile();
            app.Persist();
            coach?.Refresh();
        }

        void ClearBoard()
        {
            var removable = circuit.parts.FindAll(p => !PartTypes.IsIo(p.type));
            if (removable.Count == 0 && circuit.wires.Count == 0) return;
            foreach (var p in removable) circuit.RemovePart(p.id);
            circuit.wires.Clear();
            view.SetCircuit(circuit);
            OnEdited();
        }

        void Recompile()
        {
            try
            {
                netlist = Netlist.Compile(circuit, app.Catalog, level.inputs.Length, level.outputs.Length);
                state = netlist.CreateState();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                netlist = null;
                state = null;
            }
            view.Bind(netlist, state);
            report = null;
            banner.Hidden(true);
            UpdateStats();
            RenderTable();
            undoButton.SetEnabled(undo.Count > 0);
            redoButton.SetEnabled(redo.Count > 0);
        }

        void UpdateStats()
        {
            int gates = netlist?.GateCount ?? 0;
            gatesValue.text = gates.ToString();
            delayValue.text = netlist == null ? "-" : netlist.HasCycles ? "loop" : netlist.Depth.ToString();
            powerValue.text = $"{gates * level.wattsPerGate:0.0} W";
            gatesStat.RemoveFromClassList("stat--good");
        }

        // ------------------------------------------------------------------ testing

        internal void RunTests()
        {
            if (netlist == null) return;
            report = TestBench.Run(netlist, level);
            coach?.TestsRun(report.AllPassed);

            if (report.AllPassed)
            {
                RenderTable();
                banner.Hidden(true);
                Sfx.Play(SfxId.Success);
                OnCertified();
                return;
            }

            Sfx.Play(SfxId.Fail);
            failedRuns++;
            string explanation = TestExplainer.Explain(level, report);
            int first = TestExplainer.FirstFailure(report);
            if (!level.IsSequential && first >= 0)
            {
                // Put the failing case on the switches so the player can trace it on the board right away.
                for (int i = 0; i < inputs.Length; i++) inputs[i] = ((first >> i) & 1) != 0;
                explanation += " That case is now on the input switches, so you can trace it on the board.";
            }
            RenderTable();
            banner.Hidden(false);
            bannerText.text = explanation;
            ScrollToFirstFailure();
            if (failedRuns >= 2 && HintsLeft > 0) hintButton.AddToClassList("hints__button--nudge");
        }

        /// <summary>Brings the explanation into view, and the failing row too when it fits below it.</summary>
        void ScrollToFirstFailure()
        {
            var row = table.Q(className: "tt-row--first-fail");
            briefScroll.schedule.Execute(() =>
            {
                if (row != null) briefScroll.ScrollTo(row);
                briefScroll.ScrollTo(banner);
            }).ExecuteLater(30);
        }

        void OnCertified()
        {
            int gates = netlist.GateCount;
            int stars = Campaign.Stars(level, gates);
            app.RecordSolve(level, gates, stars, circuit);
            EndCoach();
            gatesStat.AddToClassList("stat--good");

            var next = Campaign.Next(level);
            string body = level.id == "ADD4"
                ? "Your 4-bit Adder is in the parts list. Time to build a machine around it."
                : $"{level.chipName} is now in your parts list, ready to use in the next contract.";

            var metrics = Ui.Div("metric-grid");
            metrics.Add(Ui.Metric($"{gates} / {level.par}", "GATES / PAR"));
            metrics.Add(Ui.Metric(netlist.Depth.ToString(), "DELAY"));
            metrics.Add(Ui.Metric($"{gates * level.wattsPerGate:0.0} W", "POWER", true));

            VisualElement overlay = null;
            var card = Modal.Card(
                Ui.Badge(),
                level.id == "ADD4" ? "Adder certified" : $"{level.title} certified",
                body,
                "Keep tinkering", () => Modal.Close(overlay),
                next != null ? "Next contract" : "Contracts",
                () => { if (next != null) app.OpenLevel(next); else app.ShowCampaign(); },
                Ui.Pips(stars, true), metrics);
            overlay = Modal.Show(Root, card);
        }

        // ------------------------------------------------------------------ input

        void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.None) return;
            if (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) view.DeleteSelection();
            else if (e.actionKey && e.keyCode == KeyCode.Z) { if (e.shiftKey) Redo(); else Undo(); }
            else if (e.actionKey && e.keyCode == KeyCode.Y) Redo();
            else if (e.actionKey && e.keyCode == KeyCode.C) view.Copy();
            else if (e.actionKey && e.keyCode == KeyCode.V) view.Paste();
            else if (e.actionKey && e.keyCode == KeyCode.D) view.Duplicate();
            else if (e.actionKey && e.keyCode == KeyCode.A) view.SelectAll();
            else if (e.keyCode == KeyCode.Escape) view.ClearSelection();
            else if (e.keyCode == KeyCode.F) view.Fit();
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) RunTests();
            else if (e.keyCode == KeyCode.Space && ToggleClock()) { }
            else return;
            e.StopPropagation();
        }

        /// <summary>Flips the input named Clk, if the contract has one.</summary>
        bool ToggleClock()
        {
            int i = System.Array.IndexOf(level.inputs, "Clk");
            if (i < 0) return false;
            inputs[i] = !inputs[i];
            return true;
        }

        void ShowToast(string message)
        {
            toastText.text = message;
            toast.AddToClassList("toast--visible");
            toastHide?.Pause();
            toastHide = toast.schedule.Execute(() => toast.RemoveFromClassList("toast--visible"));
            toastHide.ExecuteLater(2600);
        }

        // ------------------------------------------------------------------ layout

        VisualElement BuildTopBar()
        {
            var bar = Ui.Div("topbar");
            bar.Add(Ui.BackButton("Contracts", app.ShowCampaign));
            bar.Add(Ui.Div("topbar__divider"));
            var title = Ui.Div("topbar__title");
            title.Add(Ui.Text(level.number.ToString("00"), "topbar__num"));
            title.Add(Ui.Text(level.title, "topbar__name"));
            bar.Add(title);
            bar.Add(Ui.Div("spacer"));
            bar.Add(Ui.Button("Settings", () => SettingsUi.Show(Root, app), "btn--ghost", "topbar__settings"));

            gatesStat = Ui.Stat("GATES", out gatesValue);
            bar.Add(gatesStat);
            bar.Add(Ui.Stat("DELAY", out delayValue));
            bar.Add(Ui.Stat("POWER", out powerValue));

            undoButton = Ui.Button("Undo", Undo, "btn--ghost");
            redoButton = Ui.Button("Redo", Redo, "btn--ghost");
            bar.Add(undoButton);
            bar.Add(redoButton);
            bar.Add(Ui.Button("Clear", ClearBoard, "btn--ghost"));
            runButton = Ui.Button("Run tests", RunTests, "btn--primary");
            bar.Add(runButton);
            return bar;
        }

        VisualElement BuildPalette()
        {
            var palette = Ui.Div("palette");
            palette.Add(Ui.Text("PARTS", "t-eyebrow"));
            var list = new ScrollView(ScrollViewMode.Vertical);
            list.style.flexGrow = 1;
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            list.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            foreach (var type in level.parts)
            {
                if (!app.Catalog.Has(type)) continue;
                var item = PaletteItem(app.Catalog.Get(type));
                if (type == PartTypes.Nand) nandPaletteItem = item;
                list.Add(item);
            }
            palette.Add(list);

            var help = Ui.Div("palette__help");
            help.Add(HelpLine("Drag", "a part onto the board"));
            help.Add(HelpLine("Drag a pin", "to draw a wire"));
            help.Add(HelpLine("Drag empty space", "to select"));
            help.Add(HelpLine("Right-drag", "to pan"));
            help.Add(HelpLine("Right-click", "to delete"));
            help.Add(HelpLine("Ctrl+C / V", "copy and paste"));
            help.Add(HelpLine("Enter", "to run the tests"));
            if (System.Array.IndexOf(level.inputs, "Clk") >= 0) help.Add(HelpLine("Space", "to toggle Clk"));
            palette.Add(help);
            return palette;
        }

        static VisualElement HelpLine(string key, string text)
        {
            var line = Ui.Text($"<b>{key}</b> {text}", "help-line");
            line.enableRichText = true;
            return line;
        }

        VisualElement PaletteItem(PartInfo info)
        {
            var item = Ui.Div("palette-item");
            item.Add(Ui.MiniChip());
            var col = new VisualElement { pickingMode = PickingMode.Ignore };
            col.Add(Ui.Text(info.Name, "palette-item__name"));
            int cost = app.Catalog.GateCost(info.Type);
            col.Add(Ui.Text(cost == 1 ? "1 gate" : $"{cost} gates", "palette-item__cost"));
            item.Add(col);
            foreach (var child in item.Children()) child.pickingMode = PickingMode.Ignore;

            item.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                spawnType = info.Type;
                spawnDown = e.position;
                item.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            item.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!item.HasPointerCapture(e.pointerId)) return;
                if (spawnGhost == null && Vector2.Distance(e.position, spawnDown) > 6f)
                {
                    spawnGhost = Ui.Div("drag-ghost");
                    spawnGhost.pickingMode = PickingMode.Ignore;
                    spawnGhost.Add(Ui.Text(info.Name, "drag-ghost__label"));
                    Root.Add(spawnGhost);
                }
                if (spawnGhost != null)
                {
                    var local = Root.WorldToLocal(e.position);
                    spawnGhost.style.left = local.x - 30;
                    spawnGhost.style.top = local.y - 20;
                }
            });
            item.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!item.HasPointerCapture(e.pointerId)) return;
                item.ReleasePointer(e.pointerId);
                if (spawnGhost != null)
                {
                    spawnGhost.RemoveFromHierarchy();
                    spawnGhost = null;
                    if (view.worldBound.Contains(e.position)) view.AddPart(spawnType, view.PanelToBoard(e.position));
                }
                else
                {
                    // A plain click drops the part in the middle of the view, nudged so repeats don't stack.
                    var offset = new Vector2(spawnCount % 4, spawnCount % 4) * 24f;
                    spawnCount++;
                    view.AddPart(spawnType, view.BoardCenter + offset);
                }
                view.Focus();
            });
            return item;
        }

        VisualElement BuildTip()
        {
            var tip = Ui.Div("board-tip");
            tip.Add(Ui.Div("board-tip__bar"));
            tip.Add(Ui.Text(level.tip, "board-tip__text"));
            tip.Add(Ui.Button("Got it", () => tip.RemoveFromHierarchy(), "btn--small"));
            return tip;
        }

        VisualElement BuildZoomBar()
        {
            var bar = Ui.Div("zoom-bar");
            bar.Add(Ui.Button("-", () => view.ZoomBy(1f / 1.2f), "btn--small", "btn--ghost"));
            bar.Add(Ui.Button("+", () => view.ZoomBy(1.2f), "btn--small", "btn--ghost"));
            bar.Add(Ui.Button("Fit", () => view.Fit(), "btn--small", "btn--ghost"));
            return bar;
        }

        VisualElement BuildBrief()
        {
            var brief = Ui.Div("brief");
            // Wide contracts (Processor III has six input columns) get a wider panel so the table fits.
            float needed = EstimateTableWidth() + 48f;
            if (needed > 404f) brief.style.width = needed;
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("brief__scroll");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            var inner = Ui.Div("brief__inner");

            var era = Campaign.Eras[level.era];
            inner.Add(Ui.Text($"ERA {era.numeral}  ·  {era.name.ToUpperInvariant()}  ·  CONTRACT {level.number:00}", "t-eyebrow"));
            inner.Add(Ui.Text(level.title, "brief__title"));
            inner.Add(Ui.Text(level.brief, "t-body"));

            inner.Add(BuildHints());

            inner.Add(Ui.Div("divider"));

            var head = Ui.Div("section-head");
            head.Add(Ui.Text(level.IsSequential ? "TEST SEQUENCE" : "TRUTH TABLE", "t-eyebrow"));
            head.Add(Ui.Div("spacer"));
            tableMeta = Ui.Text("", "section-head__meta");
            head.Add(tableMeta);
            inner.Add(head);

            // The explanation sits above the table so it's in view even when a test sequence runs to 30+ rows.
            banner = Ui.Div("banner", "banner--bad", "banner--above-table");
            bannerText = Ui.Text("", "banner__text");
            banner.Add(bannerText);
            banner.Hidden(true);
            inner.Add(banner);

            table = Ui.Div("tt");
            inner.Add(table);

            inner.Add(Ui.Div("divider"));
            inner.Add(Ui.Text("TARGET", "t-eyebrow"));
            var target = Ui.Text($"Par is {level.par} {(level.par == 1 ? "gate" : "gates")}. Fewer gates make a smaller chip that runs on less power.", "t-body");
            target.style.marginTop = 8;
            inner.Add(target);

            var progress = app.Save.Progress(level.id);
            if (progress.solved)
            {
                var best = Ui.Div("row");
                best.style.marginTop = 14;
                best.Add(Ui.Pips(progress.stars));
                best.Add(Ui.Text(progress.bestGates == 1 ? "Best: 1 gate" : $"Best: {progress.bestGates} gates", "t-dim"));
                inner.Add(best);
            }

            scroll.Add(inner);
            brief.Add(scroll);
            briefScroll = scroll;
            return brief;
        }

        // ------------------------------------------------------------------ hints

        string[] Hints => level.hints ?? new string[0];
        int HintsShown => Mathf.Min(app.Save.Progress(level.id).hintsShown, Hints.Length);
        int HintsLeft => Hints.Length - HintsShown;

        VisualElement BuildHints()
        {
            var box = Ui.Div("hints");
            hintList = Ui.Div("hints__list");
            box.Add(hintList);
            var row = Ui.Div("hints__row");
            hintButton = Ui.Button("", RevealHint, "btn--small", "hints__button");
            row.Add(hintButton);
            hintNote = Ui.Text("Hints never cost stars.", "hints__note");
            row.Add(hintNote);
            box.Add(row);
            RenderHints();
            return box;
        }

        internal void RevealHint()
        {
            if (HintsLeft <= 0) return;
            app.Save.Progress(level.id).hintsShown = HintsShown + 1;
            app.Persist();
            hintButton.RemoveFromClassList("hints__button--nudge");
            Sfx.Play(SfxId.Click);
            RenderHints();
        }

        void RenderHints()
        {
            hintList.Clear();
            for (int i = 0; i < HintsShown; i++)
            {
                var card = Ui.Div("brief__hint");
                card.Add(Ui.Text($"HINT {i + 1} OF {Hints.Length}", "hints__label"));
                card.Add(Ui.Text(Hints[i], "brief__hint-text"));
                hintList.Add(card);
            }
            hintButton.Text = HintsShown == 0 ? "Need a hint?" : (HintsLeft == 1 ? "Last hint" : "Another hint");
            hintButton.parent.Hidden(HintsLeft == 0);
            hintNote.Hidden(HintsShown > 0);
        }

        void RenderTable()
        {
            if (table == null) return;
            table.Clear();

            bool sequential = level.IsSequential;
            const string StepColumn = "step";

            var head = Ui.Div("tt-row", "tt-row--head");
            if (sequential) head.Add(Cell("#", new Column { name = StepColumn }, true));
            var inCols = Columns(level.inputs, level.inputGroups);
            var outCols = Columns(level.outputs, level.outputGroups);
            foreach (var col in inCols) head.Add(Cell(col.name, col, true));
            head.Add(Ui.Div("tt-sep"));
            foreach (var col in outCols) head.Add(Cell(col.name, col, true));
            table.Add(head);

            var rows = RowsToShow(out string note);
            int live = sequential ? -1 : InputBits;
            int firstFail = report != null ? TestExplainer.FirstFailure(report) : -1;
            int allOutputs = (1 << level.outputs.Length) - 1;
            for (int r = 0; r < rows.Count; r++)
            {
                int key = rows[r];
                int ins, expected, mask;
                if (sequential)
                {
                    var step = level.sequence[key];
                    ins = step.inputs;
                    expected = step.expected;
                    mask = step.mask;
                }
                else
                {
                    ins = key;
                    expected = level.truth(key);
                    mask = allOutputs;
                }

                var row = Ui.Div("tt-row");
                if (r == rows.Count - 1) row.AddToClassList("tt-row--last");
                if (key == live) row.AddToClassList("tt-row--live");
                if (key == firstFail) row.AddToClassList("tt-row--first-fail");
                if (sequential)
                {
                    var num = Cell((key + 1).ToString(), new Column { name = StepColumn }, false);
                    num.AddToClassList("tt-cell--step");
                    row.Add(num);
                }

                foreach (var col in inCols) row.Add(Cell(col.Format(ins), col, false));
                row.Add(Ui.Div("tt-sep"));

                bool hasResult = report != null && report.cases.ContainsKey(key);
                var result = hasResult ? report.cases[key] : default;
                foreach (var col in outCols)
                {
                    if ((mask & col.mask) == 0)
                    {
                        row.Add(Cell("-", col, false));
                        continue;
                    }
                    var cell = Cell(col.Format(expected), col, false);
                    if (hasResult && ((result.actual ^ expected) & col.mask & mask) != 0) cell.AddToClassList("tt-cell--bad");
                    row.Add(cell);
                }

                var status = Ui.Div("tt-status");
                if (hasResult) status.AddToClassList(result.Pass ? "tt-status--ok" : "tt-status--bad");
                row.Add(status);

                int captured = ins;
                row.RegisterCallback<PointerDownEvent>(e =>
                {
                    for (int i = 0; i < inputs.Length; i++) inputs[i] = ((captured >> i) & 1) != 0;
                });
                table.Add(row);
            }

            if (note != null)
            {
                var noteLabel = Ui.Text(note, "tt-note");
                table.Add(noteLabel);
            }

            string unit = sequential ? "steps" : "cases";
            tableMeta.text = report == null
                ? $"{level.CaseCount} {unit}"
                : $"{report.passed}/{report.total} passing";
            tableMeta.EnableInClassList("t-ok", report != null && report.AllPassed);
            tableMeta.EnableInClassList("t-bad", report != null && !report.AllPassed);
        }

        List<int> RowsToShow(out string note)
        {
            var rows = new List<int>();
            note = null;
            if (level.IsSequential)
            {
                for (int k = 0; k < level.sequence.Length; k++) rows.Add(k);
                note = "Steps run top to bottom, and memory carries from one step to the next. A dash means that output isn't checked yet.";
                return rows;
            }
            if (level.CaseCount <= 16)
            {
                for (int c = 0; c < level.CaseCount; c++) rows.Add(c);
                return rows;
            }

            if (report != null && !report.AllPassed)
            {
                for (int c = 0; c < level.CaseCount && rows.Count < 8; c++)
                    if (!report.cases[c].Pass) rows.Add(c);
                note = $"Showing the first {rows.Count} failing cases out of {level.CaseCount}.";
                return rows;
            }

            rows.AddRange(level.samples ?? new int[0]);
            note = $"Showing {rows.Count} of {level.CaseCount} cases. Run tests to check every one.";
            return rows;
        }

        /// <summary>A truth-table column: one pin, or a group of pins shown as a number.</summary>
        struct Column
        {
            public string name;
            public int mask;
            public BitGroup group;

            public string Format(int bits) => group != null ? group.Format(bits) : (bits & mask) != 0 ? "1" : "0";
        }

        /// <summary>Matches the cell widths in the stylesheet: 24px per pin, 38 for longer names, 44 for grouped numbers.</summary>
        float EstimateTableWidth()
        {
            float CellWidth(Column c) => c.group != null && c.group.width > 1 ? 44f : c.name.Length > 2 ? 38f : 24f;
            float width = 2f + 3f + 24f + 21f + 8f + 10f; // border, live-row bar, padding, separator, status dot, slack
            if (level.IsSequential) width += 38f;
            foreach (var c in Columns(level.inputs, level.inputGroups)) width += CellWidth(c);
            foreach (var c in Columns(level.outputs, level.outputGroups)) width += CellWidth(c);
            return width;
        }

        static List<Column> Columns(string[] names, BitGroup[] groups)
        {
            var cols = new List<Column>();
            if (groups == null)
                for (int i = 0; i < names.Length; i++) cols.Add(new Column { name = names[i], mask = 1 << i });
            else
                foreach (var g in groups) cols.Add(new Column { name = g.name, mask = g.Mask, group = g });
            return cols;
        }

        static Label Cell(string text, Column col, bool head)
        {
            var cell = Ui.Text(text, "tt-cell");
            if (col.group != null && col.group.width > 1) cell.AddToClassList("tt-cell--group");
            else if (col.name != null && col.name.Length > 2) cell.AddToClassList("tt-cell--wide");
            if (head) cell.AddToClassList("tt-cell--head");
            else if (text != "0" && text != "-") cell.AddToClassList("tt-cell--one");
            return cell;
        }
    }
}
