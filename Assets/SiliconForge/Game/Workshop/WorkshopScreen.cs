using System;
using System.Collections.Generic;
using SiliconForge.Core;
using SiliconForge.Game.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.Workshop
{
    /// <summary>
    /// The 3D workshop. Each era adds to the same machine:
    /// First Light builds it around the player's adder, Heartbeat adds a clock and the counter,
    /// and Boot swaps both for the player's processor plus a ROM holding a program they write.
    /// Power draw comes from the real gate counts, so leaner chips fit a smaller supply.
    /// </summary>
    public sealed class WorkshopScreen : IScreen
    {
        const float BoardWatts = 12f;
        const float ClockWatts = 2f;
        const float RomWatts = 3f;
        static readonly int[] PsuWatts = { 20, 30, 60, 100 };
        static readonly string[] PsuNames = { "Spark", "Volt", "Forge", "Titan" };
        static readonly string[] PsuNotes = { "Tiny and silent", "A sensible middle", "Plenty of headroom", "For big builds" };
        static readonly float[] ClockSpeeds = { 1f, 2f, 4f };

        const string StarterProgram = "; Counts up forever. Your goal: count down from 9 to 0, then stop.\nLDI 0\nloop: ADD 1\nJMP loop\n";

        enum Mode { FirstLight, Heartbeat, Boot }
        enum Kind { Install, Psu, Power }

        sealed class StepDef
        {
            public string Title, Desc, Action;
            public Action Install;
            public Kind Kind;
            public VisualElement Card;
        }

        public VisualElement Root { get; }

        readonly GameApp app;
        readonly LevelSpec level;
        readonly Mode mode;
        readonly WorkshopRig rig;
        readonly Netlist adder, counter, cpu;
        readonly ulong[] adderState, counterState;
        readonly int adderGates, counterGates, cpuGates;
        readonly float draw;
        readonly List<StepDef> steps = new List<StepDef>();

        int current;
        int psuSelected = 1;
        int psuInstalled = -1;
        bool busy;
        int a = 5, b = 3;

        // Clocked machines (Heartbeat and Boot)
        float hz = 2f, phase;
        bool clk, resetPending, running;
        int count;
        CpuMachine machine;
        int[] rom;
        bool romFlashed;

        readonly VisualElement[] psuRows = new VisualElement[PsuWatts.Length];
        readonly VisualElement[] bitDots = new VisualElement[5];
        readonly List<UiButton> speedButtons = new List<UiButton>();
        VisualElement meterFill, readout, viewport;
        Label meterLabel, statusText, aValue, bValue, resultValue, pcValue, instrValue;
        UiButton psuButton, powerButton;
        int pointerId = -1;
        Vector2 downPos, lastPos;
        bool dragged;

        int PsuIndex => steps.FindIndex(s => s.Kind == Kind.Psu);
        int PowerIndex => steps.FindIndex(s => s.Kind == Kind.Power);
        bool Done => current >= steps.Count;
        float Watts => level.wattsPerGate;
        int TotalGates => mode == Mode.Boot ? cpuGates : adderGates + counterGates;

        public WorkshopScreen(GameApp app, LevelSpec level)
        {
            this.app = app;
            this.level = level;
            mode = level.id == "TICK" ? Mode.Heartbeat : level.id == "BOOT" ? Mode.Boot : Mode.FirstLight;

            string[] needed = mode == Mode.Boot ? new[] { "CPU" } : mode == Mode.Heartbeat ? new[] { "ADD4", "COUNT4" } : new[] { "ADD4" };
            foreach (var id in needed)
            {
                if (app.Catalog.TryGetChip(id, out _)) continue;
                Root = Ui.Div("screen");
                app.Root.schedule.Execute(app.ShowCampaign).ExecuteLater(10);
                return;
            }

            if (app.Catalog.TryGetChip("ADD4", out var adderDef))
            {
                adder = Netlist.Compile(adderDef.circuit, app.Catalog, 8, 5);
                adderState = adder.CreateState();
                adderGates = adder.GateCount;
            }
            if (app.Catalog.TryGetChip("COUNT4", out var counterDef))
            {
                counter = Netlist.Compile(counterDef.circuit, app.Catalog, 2, 4);
                counterState = counter.CreateState();
                counterGates = mode == Mode.Heartbeat ? counter.GateCount : 0;
            }
            if (mode == Mode.Boot)
            {
                app.Catalog.TryGetChip("CPU", out var cpuDef);
                cpu = Netlist.Compile(cpuDef.circuit, app.Catalog, 8, 8);
                cpuGates = cpu.GateCount;
            }

            float baseWatts = BoardWatts + (mode != Mode.FirstLight ? ClockWatts : 0f) + (mode == Mode.Boot ? RomWatts : 0f);
            draw = TotalGates * Watts + baseWatts;

            rig = new GameObject("Workshop").AddComponent<WorkshopRig>();
            rig.Build(app.MainCamera);
            rig.SwitchClicked += OnSwitch;
            rig.PowerButtonClicked += () => { if (!Done && current == PowerIndex) PowerOn(); };

            DefineSteps(baseWatts);

            if (mode != Mode.FirstLight)
            {
                int saved = Array.IndexOf(PsuWatts, app.Save.machinePsuWatts);
                psuInstalled = saved >= 0 ? saved : 1;
                psuSelected = psuInstalled;
                string counterLabel = mode == Mode.Boot && counter != null ? $"CNT-4\n{counter.GateCount} G" : null;
                rig.BuildPreinstalled(AdderLabel, PsuWatts[psuInstalled], mode == Mode.Boot, counterLabel);
                a = 0;
            }

            Root = Ui.Div("screen", "screen--transparent", "workshop");
            Root.Add(BuildTopBar());
            var body = Ui.Div("workshop__body");
            body.Add(BuildViewport());
            body.Add(BuildAssembly());
            Root.Add(body);

            UpdateInputs();
            Refresh();
        }

        string AdderLabel => $"ADD-4\n{adderGates} G";

        void DefineSteps(float baseWatts)
        {
            switch (mode)
            {
                case Mode.FirstLight:
                    AddStep(Kind.Install, "Open-frame case", "An aluminium test bench that holds everything.", "Mount", rig.InstallCase);
                    AddStep(Kind.Install, "Mainboard", "Copper traces, chip sockets, input switches and a display.", "Seat", rig.InstallBoard);
                    AddStep(Kind.Install, "Your 4-bit Adder", $"{adderGates} gates, {adderGates * Watts:0.0} W. Designed by you.", "Install",
                        () => rig.InstallChip(AdderLabel));
                    break;

                case Mode.Heartbeat:
                    AddStep(Kind.Install, "Clock crystal", $"Ticks the machine forward on its own. Draws {ClockWatts:0} W.", "Fit", rig.InstallClock);
                    AddStep(Kind.Install, "Your 4-bit Counter", $"{counterGates} gates, {counterGates * Watts:0.0} W. It goes in the second socket and drives the adder's A input.", "Install",
                        () => rig.InstallCounter($"CNT-4\n{counterGates} G"));
                    break;

                case Mode.Boot:
                    AddStep(Kind.Install, "Pull the old chips", "Your adder and counter retire. The processor has its own adder and program counter inside.", "Pull", () =>
                    {
                        float t = rig.RemoveOldChips();
                        busy = true;
                        app.Root.schedule.Execute(() => { busy = false; Refresh(); }).ExecuteLater((long)(t * 1000) + 50);
                    });
                    AddStep(Kind.Install, "Your Processor", $"{cpuGates} gates, {cpuGates * Watts:0.0} W at the new 0.25 W per gate. Designed by you.", "Install",
                        () => rig.InstallChip($"CPU-4\n{cpuGates} G"));
                    AddStep(Kind.Install, "Program ROM", $"Holds 16 instructions for the processor to run. Draws {RomWatts:0} W. Write your program, then flash it.", "Write",
                        () => OpenEditor());
                    break;
            }

            string psuDesc = mode switch
            {
                Mode.FirstLight => $"The board and display need {BoardWatts:0} W, plus {Watts} W for every gate.",
                Mode.Heartbeat => $"Your adder and counter share one supply now. Board and clock need {baseWatts:0} W, plus {Watts} W per gate.",
                _ => $"Board, clock and ROM need {baseWatts:0} W, plus {Watts} W per gate.",
            };
            AddStep(Kind.Psu, "Power supply", psuDesc, null, null);
            AddStep(Kind.Power, "Switch it on", "If the supply can't carry the load, the machine browns out.", null, null);
        }

        void AddStep(Kind kind, string title, string desc, string action, Action install) =>
            steps.Add(new StepDef { Kind = kind, Title = title, Desc = desc, Action = action, Install = install });

        public void Dispose()
        {
            if (rig != null) UnityEngine.Object.Destroy(rig.gameObject);
        }

        public void Tick(float dt)
        {
            // "running" turns on only after the power-on animation, once the machine exists.
            if (rig == null || !rig.Powered || !running) return;

            switch (mode)
            {
                case Mode.FirstLight:
                    ShowSum();
                    break;

                case Mode.Heartbeat:
                    AdvanceClock(dt, high =>
                    {
                        int reset = resetPending ? 1 : 0;
                        count = TestBench.Probe(counter, counterState, (high ? 1 : 0) | (reset << 1));
                    });
                    if (a != count) { a = count; UpdateInputs(); }
                    ShowSum();
                    break;

                case Mode.Boot:
                    AdvanceClock(dt, high => machine.SetClock(high, resetPending));
                    rig.SetReadout(machine.A, machine.Pc);
                    resultValue.text = machine.A.ToString();
                    pcValue.text = machine.Pc.ToString();
                    instrValue.text = Isa.Disassemble(machine.CurrentWord);
                    break;
            }
        }

        /// <summary>Toggles the clock at the chosen speed. A pending reset is applied on the next rising edge.</summary>
        void AdvanceClock(float dt, Action<bool> drive)
        {
            phase += dt * hz * 2f;
            while (phase >= 1f)
            {
                phase -= 1f;
                clk = !clk;
                drive(clk);
                if (clk && resetPending) resetPending = false;
                rig.SetClockLed(clk);
            }
        }

        void ShowSum()
        {
            int sum = TestBench.Probe(adder, adderState, a | (b << 4));
            rig.SetOutputs(sum);
            resultValue.text = sum.ToString();
            for (int i = 0; i < 5; i++) bitDots[i].EnableInClassList("bit--on", ((sum >> (4 - i)) & 1) != 0);
        }

        // ------------------------------------------------------------------ flow

        void OnSwitch(int bit)
        {
            if (mode == Mode.Boot)
            {
                SetStatus("The program drives this machine now. The switches have nothing to do.");
                return;
            }
            if (mode == Mode.Heartbeat && bit < 4)
            {
                SetStatus("The counter drives A now. Use the B switches to add an offset.");
                return;
            }
            int bits = (a | (b << 4)) ^ (1 << bit);
            a = bits & 15;
            b = (bits >> 4) & 15;
            UpdateInputs();
        }

        void Install(int index)
        {
            if (busy || index != current || steps[index].Kind != Kind.Install) return;
            steps[index].Install();
            if (mode == Mode.Boot && steps[index].Title == "Program ROM") return; // advances when the program is flashed
            AdvanceStep();
        }

        void AdvanceStep()
        {
            current++;
            if (current == PsuIndex && psuInstalled >= 0) current = PowerIndex;
            if (current == PsuIndex) SetStatus("Pick a supply that can carry the load, then install it.");
            else if (current == PowerIndex) SetStatus("Everything is in. Press Power on, or click the round button on the case.");
            Refresh();
        }

        void InstallPsu()
        {
            if (busy || current < PsuIndex) return;
            running = false;
            if (rig.Powered) rig.PowerOff();
            psuInstalled = psuSelected;
            rig.InstallPsu(PsuWatts[psuInstalled]);
            readout.RemoveFromClassList("readout--visible");
            current = PowerIndex;
            SetStatus($"{PsuNames[psuInstalled]} {PsuWatts[psuInstalled]} W installed. Press Power on, or click the round button on the case.");
            Refresh();
        }

        void PowerOn()
        {
            if (busy || psuInstalled < 0 || current != PowerIndex) return;
            busy = true;
            bool enough = PsuWatts[psuInstalled] >= draw;
            SetStatus("Powering on...");
            rig.StartCoroutine(rig.PowerOn(enough, () =>
            {
                busy = false;
                if (!enough)
                {
                    string leaner = mode switch
                    {
                        Mode.FirstLight => "come back with a leaner adder",
                        Mode.Heartbeat => "trim gates from your adder or counter",
                        _ => "trim gates from your processor",
                    };
                    SetStatus($"Brownout. Your machine needs {draw:0.0} W but the {PsuNames[psuInstalled]} only delivers {PsuWatts[psuInstalled]} W. Pick a bigger supply, or {leaner}.", true);
                    Refresh();
                    return;
                }

                phase = 0f;
                clk = false;
                if (mode == Mode.Heartbeat)
                {
                    resetPending = true;
                    count = TestBench.Probe(counter, counterState, 0b10);
                }
                if (mode == Mode.Boot)
                {
                    machine = new CpuMachine(cpu, rom);
                    machine.Reset();
                }
                running = true;
                readout.AddToClassList("readout--visible");
                app.Save.machinePsuWatts = PsuWatts[psuInstalled];

                if (mode == Mode.Boot)
                {
                    Refresh();
                    CheckProgram();
                    return;
                }

                SetStatus(mode == Mode.Heartbeat
                    ? "It ticks. The counter feeds your adder on every clock pulse. Change B to add an offset, or change the clock speed."
                    : "It's alive. Click the DIP switches on the board, or use the controls below, to add two numbers.");
                Complete();
            }));
            Refresh();
        }

        /// <summary>Boot only counts as done once the program does the job on the player's own processor.</summary>
        void CheckProgram()
        {
            if (CpuGoals.CheckCountdown(cpu, rom, out var reason))
            {
                SetStatus("Your processor is running your program: 9 down to 0, then it stops. Edit the program any time to try something else.");
                Complete();
            }
            else
            {
                SetStatus($"The machine runs, but the program isn't there yet. {reason} Edit the program and flash it again.", true);
                Refresh();
            }
        }

        void Complete()
        {
            bool first = !app.Save.IsSolved(level.id);
            current = steps.Count;
            app.RecordSolve(level, TotalGates, StarsFor(psuInstalled), null);
            Refresh();
            if (first) app.Root.schedule.Execute(ShowSuccess).ExecuteLater(1600);
        }

        int SmallestSufficientPsu()
        {
            for (int i = 0; i < PsuWatts.Length; i++) if (PsuWatts[i] >= draw) return i;
            return PsuWatts.Length - 1;
        }

        int StarsFor(int psu) => psu == SmallestSufficientPsu() ? 3 : 2;

        void ShowSuccess()
        {
            if (Root.panel == null) return; // the player already left
            int stars = StarsFor(psuInstalled);
            var metrics = Ui.Div("metric-grid");
            metrics.Add(Ui.Metric(TotalGates.ToString(), "GATES"));
            metrics.Add(Ui.Metric($"{draw:0.0} W", "DRAW"));
            metrics.Add(Ui.Metric($"{PsuWatts[psuInstalled]} W", "SUPPLY", true));

            string body = mode switch
            {
                Mode.Boot => "A processor you built from single NAND gates is running a program you wrote. That's a computer.",
                Mode.Heartbeat => "Your machine runs on its own now: a clock you fitted, a counter you designed, feeding an adder you designed.",
                _ => "Your machine adds numbers on a chip you built from single NAND gates.",
            };
            body += stars == 3
                ? " It runs on the smallest supply that could carry it."
                : $" A {PsuWatts[SmallestSufficientPsu()]} W supply would have been enough.";
            string title = mode switch { Mode.Boot => "It computes", Mode.Heartbeat => "It has a pulse", _ => "First light" };

            VisualElement overlay = null;
            var card = Modal.Card(Ui.Badge(), title, body,
                "Keep watching", () => Modal.Close(overlay),
                "Contracts", app.ShowCampaign,
                Ui.Pips(stars, true), metrics);
            overlay = Modal.Show(Root, card);
        }

        void UpdateInputs()
        {
            if (mode == Mode.Boot) return;
            rig.SetInputs(a | (b << 4));
            if (aValue != null) aValue.text = a.ToString();
            if (bValue != null) bValue.text = b.ToString();
        }

        void SetStatus(string text, bool bad = false)
        {
            statusText.text = text;
            statusText.EnableInClassList("t-bad", bad);
        }

        void Refresh()
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                bool future = i > current;
                bool active = i == current;
                bool done = s.Kind switch
                {
                    Kind.Psu => psuInstalled >= 0 && current >= i,
                    Kind.Power => Done,
                    _ => i < current,
                };
                s.Card.EnableInClassList("step--done", done && !active);
                s.Card.EnableInClassList("step--active", active);
                s.Card.EnableInClassList("step--future", future);
                s.Card.SetEnabled(!future);
                s.Card.Q(className: "step__action")?.Hidden(done);
            }

            for (int i = 0; i < psuRows.Length; i++) psuRows[i].EnableInClassList("psu--selected", i == psuSelected);

            float capacity = PsuWatts[psuSelected];
            meterFill.style.width = Length.Percent(Mathf.Clamp01(draw / capacity) * 100f);
            meterFill.EnableInClassList("meter__fill--over", draw > capacity);
            meterLabel.text = draw > capacity
                ? $"Draws {draw:0.0} W of {capacity:0} W. Not enough."
                : $"Draws {draw:0.0} W of {capacity:0} W. {capacity - draw:0.0} W to spare.";

            psuButton.Text = psuInstalled < 0 ? "Install supply" : psuInstalled == psuSelected ? "Installed" : "Swap supply";
            psuButton.SetEnabled(current >= PsuIndex && psuInstalled != psuSelected && !busy);
            powerButton.SetEnabled(current == PowerIndex && psuInstalled >= 0 && !busy && !rig.Powered);
            powerButton.Text = rig.Powered ? "Running" : "Power on";
        }

        // ------------------------------------------------------------------ program editor (Boot)

        void OpenEditor()
        {
            var card = new VisualElement();
            card.AddToClassList("editor-card");

            var head = Ui.Div("editor__head");
            var titles = new VisualElement();
            titles.Add(Ui.Text("PROGRAM ROM", "t-eyebrow"));
            titles.Add(Ui.Text("Write your program", "editor__title"));
            head.Add(titles);
            card.Add(head);

            var body = Ui.Div("editor__body");
            var field = new TextField
            {
                multiline = true,
                value = app.Save.program ?? StarterProgram,
                selectAllOnFocus = false,
                selectAllOnMouseUp = false
            };
            field.AddToClassList("code-field");
            body.Add(field);

            var help = Ui.Div("editor__help");
            help.Add(Ui.Text("INSTRUCTIONS", "t-eyebrow"));
            help.Add(HelpRow("LDI n", "A = n"));
            help.Add(HelpRow("ADD n", "A = A + n, wraps past 15"));
            help.Add(HelpRow("JMP n", "go to instruction n"));
            help.Add(HelpRow("JZ n", "go to n if A is 0"));
            var notes = Ui.Text("Numbers are 0 to 15. Name a line with a label like \"loop:\" and jump to it by name. Anything after ; is a comment. Up to 16 instructions.", "editor__note");
            help.Add(notes);
            help.Add(Ui.Text("EXAMPLES", "t-eyebrow", "editor__examples"));
            help.Add(Ui.Button("Count up", () => field.value = StarterProgram, "btn--small", "editor__example"));
            help.Add(Ui.Button("Count by threes", () => field.value = "LDI 0\nloop: ADD 3\nJMP loop\n", "btn--small", "editor__example"));
            body.Add(help);
            card.Add(body);

            var errors = Ui.Text("", "editor__errors");
            errors.Hidden(true);
            card.Add(errors);

            VisualElement overlay = null;
            var actions = Ui.Div("modal-actions");
            actions.Add(Ui.Button("Cancel", () => Modal.Close(overlay), "btn--big", "btn--first"));
            actions.Add(Ui.Button("Flash ROM", () =>
            {
                var list = new List<AssemblyError>();
                var words = Assembler.Assemble(field.value, list);
                if (list.Count > 0)
                {
                    errors.text = string.Join("\n", list.ConvertAll(e => e.ToString()));
                    errors.Hidden(false);
                    return;
                }
                app.Save.program = field.value;
                app.Persist();
                Modal.Close(overlay);
                Flash(words);
            }, "btn--big", "btn--primary"));
            card.Add(actions);
            overlay = Modal.Show(Root, card);
            field.schedule.Execute(() => field.Focus()).ExecuteLater(50);
        }

        static VisualElement HelpRow(string code, string meaning)
        {
            var row = Ui.Div("editor__row");
            row.Add(Ui.Text(code, "editor__code"));
            row.Add(Ui.Text(meaning, "editor__meaning"));
            return row;
        }

        void Flash(int[] words)
        {
            rom = words;
            if (!romFlashed)
            {
                romFlashed = true;
                rig.InstallRom();
                AdvanceStep();
                return;
            }
            if (machine != null && rig.Powered)
            {
                machine.Rom = rom;
                machine.Reset();
                phase = 0f;
                clk = false;
                CheckProgram();
            }
            else
            {
                SetStatus("New program flashed.");
            }
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
            if (mode == Mode.Boot)
            {
                bar.Add(Ui.Stat("PROCESSOR", out var cpuLabel));
                cpuLabel.text = $"{cpuGates} gates";
            }
            else
            {
                bar.Add(Ui.Stat("ADDER", out var adderLabel));
                adderLabel.text = $"{adderGates} gates";
            }
            if (mode == Mode.Heartbeat)
            {
                bar.Add(Ui.Stat("COUNTER", out var counterLabel));
                counterLabel.text = $"{counterGates} gates";
            }
            bar.Add(Ui.Stat("DRAW", out var drawLabel));
            drawLabel.text = $"{draw:0.0} W";
            return bar;
        }

        VisualElement BuildViewport()
        {
            viewport = Ui.Div("viewport");
            viewport.RegisterCallback<PointerDownEvent>(e =>
            {
                if (pointerId != -1) return;
                pointerId = e.pointerId;
                downPos = lastPos = e.position;
                dragged = false;
                viewport.CapturePointer(e.pointerId);
            });
            viewport.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pointerId != pointerId) return;
                Vector2 pos = e.position;
                if ((pos - downPos).sqrMagnitude > 16f) dragged = true;
                if (dragged) rig.Orbit(pos - lastPos);
                lastPos = pos;
            });
            viewport.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.pointerId != pointerId) return;
                viewport.ReleasePointer(pointerId);
                pointerId = -1;
                if (!dragged && e.button == 0) rig.Click(PanelToScreen(e.position));
            });
            viewport.RegisterCallback<PointerCaptureOutEvent>(_ => pointerId = -1);
            viewport.RegisterCallback<WheelEvent>(e => rig.ZoomBy(e.delta.y > 0 ? 1f : -1f));
            viewport.RegisterCallback<GeometryChangedEvent>(_ => UpdateCameraRect());

            var hint = Ui.Div("viewport__hint");
            hint.pickingMode = PickingMode.Ignore;
            hint.Add(Ui.Text(mode == Mode.Boot ? "Drag to orbit  ·  Scroll to zoom" : "Drag to orbit  ·  Scroll to zoom  ·  Click the switches", "viewport__hint-text"));
            viewport.Add(hint);

            readout = Ui.Div("readout");
            readout.pickingMode = PickingMode.Ignore;
            var card = Ui.Div("readout__card");
            switch (mode)
            {
                case Mode.FirstLight:
                    card.Add(Stepper("A", out aValue, d => { a = (a + d + 16) % 16; UpdateInputs(); }));
                    AddSumReadout(card);
                    break;
                case Mode.Heartbeat:
                    card.Add(Readout("COUNT", out aValue));
                    AddSumReadout(card);
                    AddClockControls(card);
                    break;
                case Mode.Boot:
                    card.Add(Readout("PC", out pcValue));
                    card.Add(Ui.Div("readout__divider"));
                    instrValue = Ui.Text("LDI 0", "readout__instr");
                    card.Add(instrValue);
                    card.Add(Ui.Div("readout__divider"));
                    card.Add(Ui.Text("A", "stepper__name"));
                    resultValue = Ui.Text("0", "readout__result");
                    card.Add(resultValue);
                    AddClockControls(card);
                    card.Add(Ui.Button("Edit program", () => OpenEditor(), "btn--small", "readout__edit"));
                    break;
            }
            readout.Add(card);
            viewport.Add(readout);
            return viewport;
        }

        static VisualElement Readout(string name, out Label value)
        {
            var box = Ui.Div("stepper");
            box.Add(Ui.Text(name, "stepper__name"));
            value = Ui.Text("0", "stepper__value");
            box.Add(value);
            return box;
        }

        void AddSumReadout(VisualElement card)
        {
            card.Add(Ui.Text("+", "readout__op"));
            card.Add(Stepper("B", out bValue, d => { b = (b + d + 16) % 16; UpdateInputs(); }));
            card.Add(Ui.Text("=", "readout__op"));
            resultValue = Ui.Text("0", "readout__result");
            card.Add(resultValue);
            var bits = Ui.Div("bits");
            for (int i = 0; i < 5; i++) { bitDots[i] = Ui.Div("bit"); bits.Add(bitDots[i]); }
            card.Add(bits);
        }

        void AddClockControls(VisualElement card)
        {
            card.Add(Ui.Div("readout__divider"));
            card.Add(Ui.Button("Reset", () => resetPending = true, "btn--small"));
            var speeds = Ui.Div("segmented");
            foreach (var speed in ClockSpeeds)
            {
                float s = speed;
                var button = Ui.Button($"{s:0} Hz", () => SetSpeed(s), "btn--small", "btn--ghost", "segmented__item");
                speedButtons.Add(button);
                speeds.Add(button);
            }
            card.Add(speeds);
            SetSpeed(hz);
        }

        void SetSpeed(float value)
        {
            hz = value;
            for (int i = 0; i < speedButtons.Count; i++)
                speedButtons[i].EnableInClassList("segmented__item--on", Mathf.Approximately(ClockSpeeds[i], value));
        }

        void UpdateCameraRect()
        {
            var panelSize = app.Root.panel?.visualTree.layout.size ?? Vector2.zero;
            if (panelSize.x <= 0 || panelSize.y <= 0) return;
            var r = viewport.worldBound;
            if (r.width < 1f || r.height < 1f) return; // hidden or detached: keep the last good rect
            rig.SetViewRect(new Rect(r.xMin / panelSize.x, 1f - r.yMax / panelSize.y, r.width / panelSize.x, r.height / panelSize.y));
        }

        Vector2 PanelToScreen(Vector2 panelPos)
        {
            var size = app.Root.panel.visualTree.layout.size;
            return new Vector2(panelPos.x / size.x * Screen.width, Screen.height - panelPos.y / size.y * Screen.height);
        }

        static VisualElement Stepper(string name, out Label value, Action<int> change)
        {
            var s = Ui.Div("stepper");
            s.Add(Ui.Text(name, "stepper__name"));
            s.Add(Ui.Button("-", () => change(-1), "btn--small"));
            value = Ui.Text("0", "stepper__value");
            s.Add(value);
            s.Add(Ui.Button("+", () => change(1), "btn--small"));
            return s;
        }

        VisualElement BuildAssembly()
        {
            var panel = Ui.Div("assembly");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            var inner = Ui.Div("assembly__inner");

            var era = Campaign.Eras[level.era];
            inner.Add(Ui.Text($"ERA {era.numeral}  ·  {era.name.ToUpperInvariant()}", "t-eyebrow"));
            inner.Add(Ui.Text(level.title, "brief__title"));
            inner.Add(Ui.Text(level.brief, "t-body"));
            inner.Add(Ui.Div("divider"));

            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                int index = i;
                s.Card = StepCard(i + 1, s.Title, s.Desc, s.Action, s.Kind == Kind.Install ? () => Install(index) : null);
                var main = s.Card.Q(className: "step__main");
                if (s.Kind == Kind.Psu) BuildPsuControls(main);
                if (s.Kind == Kind.Power)
                {
                    powerButton = Ui.Button("Power on", PowerOn, "btn--primary", "btn--big");
                    powerButton.style.marginTop = 12;
                    main.Add(powerButton);
                }
                inner.Add(s.Card);
            }

            var status = Ui.Div("status-line");
            string first = mode switch
            {
                Mode.Boot => "Your machine is back on the bench. Start by pulling the old chips.",
                Mode.Heartbeat => "Your machine from First Light is back on the bench. Start by fitting the clock.",
                _ => "Start by mounting the case.",
            };
            statusText = Ui.Text(first, "status-line__text");
            status.Add(statusText);
            inner.Add(status);

            scroll.Add(inner);
            panel.Add(scroll);
            return panel;
        }

        void BuildPsuControls(VisualElement main)
        {
            var list = Ui.Div("psu-list");
            for (int i = 0; i < PsuWatts.Length; i++)
            {
                int index = i;
                var row = Ui.Div("psu");
                var radio = Ui.Div("radio");
                radio.Add(Ui.Div("radio__dot"));
                row.Add(radio);
                var col = new VisualElement();
                col.Add(Ui.Text(PsuNames[i], "psu__name"));
                col.Add(Ui.Text(PsuNotes[i], "psu__note"));
                row.Add(col);
                row.Add(Ui.Text($"{PsuWatts[i]} W", "psu__watts"));
                foreach (var child in row.Children()) child.pickingMode = PickingMode.Ignore;
                row.AddManipulator(new Clickable(() => { psuSelected = index; Refresh(); }));
                psuRows[i] = row;
                list.Add(row);
            }
            main.Add(list);

            var meter = Ui.Div("meter");
            meterLabel = Ui.Text("", "meter__label");
            meter.Add(meterLabel);
            var track = Ui.Div("meter__track");
            meterFill = Ui.Div("meter__fill");
            track.Add(meterFill);
            meter.Add(track);
            main.Add(meter);

            psuButton = Ui.Button("Install supply", InstallPsu, "btn--small");
            psuButton.style.marginTop = 12;
            psuButton.style.alignSelf = Align.FlexStart;
            main.Add(psuButton);
        }

        static VisualElement StepCard(int number, string title, string desc, string action, Action onAction)
        {
            var card = Ui.Div("step");
            var num = Ui.Div("step__num");
            num.Add(Ui.Text(number.ToString(), "step__num-text"));
            card.Add(num);
            var main = Ui.Div("step__main");
            main.Add(Ui.Text(title, "step__title"));
            main.Add(Ui.Text(desc, "step__desc"));
            card.Add(main);
            if (action != null) card.Add(Ui.Button(action, onAction, "btn--small", "step__action"));
            return card;
        }
    }
}
