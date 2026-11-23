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
    /// Boot swaps both for the player's processor plus a ROM holding a program they write,
    /// and First Picture upgrades to Processor II with RAM, a cooler and a screen.
    /// Power draw (and in First Picture, heat) comes from the real gate counts.
    /// </summary>
    public sealed class WorkshopScreen : IScreen
    {
        const float BoardWatts = 12f;
        const float ClockWatts = 2f;
        const float RomWatts = 3f;
        const float ScreenWatts = 4f;
        const float ShutdownSeconds = 2.5f;
        static readonly int[] PsuWatts = { 20, 30, 60, 100 };
        static readonly string[] PsuNames = { "Spark", "Volt", "Forge", "Titan" };
        static readonly string[] PsuNotes = { "Tiny and silent", "A sensible middle", "Plenty of headroom", "For big builds" };
        static readonly int[] CoolerCapacity = { 8, 20, 40 };
        static readonly int[] CoolerWatts = { 0, 0, 2 };
        static readonly string[] CoolerNames = { "No cooler", "Heatsink", "Fan cooler" };
        static readonly string[] CoolerNotes = { "Bare chip, handles 8 W", "Silent aluminium fins, 20 W", "Fins and a fan, 40 W, draws 2 W" };
        static readonly float[] ClockSpeeds = { 1f, 2f, 4f };

        const string BootStarter = "; Counts up forever. Your goal: count down from 9 to 0, then stop.\nLDI 0\nloop: ADD 1\nJMP loop\n";
        const string PixelsStarter = "; Draws a frame. Your goal: the smiley.\nLDI 15\nSTA 0\nSTA 3\nLDI 9\nSTA 1\nSTA 2\nh: JMP h\n";

        enum Mode { FirstLight, Heartbeat, Boot, Pixels }
        enum Kind { Install, Program, Cooler, Psu, Power }

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
        readonly Netlist adder, counter, cpu, ram;
        readonly ulong[] adderState, counterState;
        readonly int adderGates, counterGates, cpuGates, ramGates;
        readonly float baseWatts;
        readonly List<StepDef> steps = new List<StepDef>();

        int current;
        int psuSelected = 1, psuInstalled = -1;
        int coolerSelected = 1, coolerInstalled = -1;
        bool busy;
        int a = 5, b = 3;

        // Clocked machines
        float hz = 2f, phase;
        bool clk, resetPending, running;
        int count;
        CpuMachine machine;
        ComputerMachine computer;
        int[] rom;
        bool romFlashed;
        float heatTimer;

        readonly VisualElement[] psuRows = new VisualElement[PsuWatts.Length];
        readonly VisualElement[] coolerRows = new VisualElement[CoolerNames.Length];
        readonly VisualElement[] bitDots = new VisualElement[5];
        readonly List<UiButton> speedButtons = new List<UiButton>();
        VisualElement meterFill, coolerFill, readout, viewport;
        Label meterLabel, coolerLabel, statusText, aValue, bValue, resultValue, pcValue, instrValue, drawStat;
        UiButton psuButton, coolerButton, powerButton;
        int pointerId = -1;
        Vector2 downPos, lastPos;
        bool dragged;

        int IndexOf(Kind kind) => steps.FindIndex(s => s.Kind == kind);
        int PsuIndex => IndexOf(Kind.Psu);
        int PowerIndex => IndexOf(Kind.Power);
        bool Done => current >= steps.Count;
        float Watts => level.wattsPerGate;
        bool Programmable => mode == Mode.Boot || mode == Mode.Pixels;
        int TotalGates => mode switch
        {
            Mode.Boot => cpuGates,
            Mode.Pixels => cpuGates + ramGates,
            Mode.Heartbeat => adderGates + counterGates,
            _ => adderGates,
        };
        float Draw => TotalGates * Watts + baseWatts + (coolerInstalled >= 0 ? CoolerWatts[coolerInstalled] : 0);
        float Heat => cpuGates * Watts;
        bool Overheating => mode == Mode.Pixels && coolerInstalled >= 0 && Heat > CoolerCapacity[coolerInstalled];

        public WorkshopScreen(GameApp app, LevelSpec level)
        {
            this.app = app;
            this.level = level;
            mode = level.id switch
            {
                "TICK" => Mode.Heartbeat,
                "BOOT" => Mode.Boot,
                "PIXELS" => Mode.Pixels,
                _ => Mode.FirstLight,
            };

            string[] needed = mode switch
            {
                Mode.Pixels => new[] { "CPU2", "RAM4" },
                Mode.Boot => new[] { "CPU" },
                Mode.Heartbeat => new[] { "ADD4", "COUNT4" },
                _ => new[] { "ADD4" },
            };
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
            if (mode == Mode.Boot && app.Catalog.TryGetChip("CPU", out var cpuDef))
            {
                cpu = Netlist.Compile(cpuDef.circuit, app.Catalog, 8, 8);
                cpuGates = cpu.GateCount;
            }
            if (mode == Mode.Pixels && app.Catalog.TryGetChip("CPU2", out var cpu2Def) && app.Catalog.TryGetChip("RAM4", out var ramDef))
            {
                cpu = Netlist.Compile(cpu2Def.circuit, app.Catalog, 9, 9);
                cpuGates = cpu.GateCount;
                ram = Netlist.Compile(ramDef.circuit, app.Catalog, 8, 4);
                ramGates = ram.GateCount;
            }

            baseWatts = BoardWatts
                + (mode != Mode.FirstLight ? ClockWatts : 0f)
                + (Programmable ? RomWatts : 0f)
                + (mode == Mode.Pixels ? ScreenWatts : 0f);

            rig = new GameObject("Workshop").AddComponent<WorkshopRig>();
            rig.Build(app.MainCamera);
            rig.SwitchClicked += OnSwitch;
            rig.PowerButtonClicked += () => { if (!Done && current == PowerIndex) PowerOn(); };

            DefineSteps();

            if (mode != Mode.FirstLight)
            {
                int saved = Array.IndexOf(PsuWatts, app.Save.machinePsuWatts);
                psuInstalled = saved >= 0 ? saved : 1;
                psuSelected = psuInstalled;
                switch (mode)
                {
                    case Mode.Heartbeat:
                        rig.BuildPreinstalled(AdderLabel, PsuWatts[psuInstalled]);
                        break;
                    case Mode.Boot:
                        rig.BuildPreinstalled(AdderLabel, PsuWatts[psuInstalled], true, counter != null ? $"CNT-4\n{counter.GateCount} G" : null);
                        break;
                    case Mode.Pixels:
                        int oldCpu = app.Catalog.Has("CPU") ? app.Catalog.GateCost("CPU") : 0;
                        rig.BuildPreinstalled($"CPU-4\n{oldCpu} G", PsuWatts[psuInstalled], true, "ROM\n16x6");
                        rig.SetFocus(new Vector3(0.4f, 0.16f, 0f), 4.1f);
                        break;
                }
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

        void DefineSteps()
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
                    AddStep(Kind.Install, "Pull the old chips", "Your adder and counter retire. The processor has its own adder and program counter inside.", "Pull",
                        () => Busy(rig.RemoveOldChips()));
                    AddStep(Kind.Install, "Your Processor", $"{cpuGates} gates, {cpuGates * Watts:0.0} W at the new 0.25 W per gate. Designed by you.", "Install",
                        () => rig.InstallChip($"CPU-4\n{cpuGates} G"));
                    AddStep(Kind.Program, "Program ROM", $"Holds 16 instructions for the processor to run. Draws {RomWatts:0} W. Write your program, then flash it.", "Write",
                        OpenEditor);
                    break;

                case Mode.Pixels:
                    AddStep(Kind.Install, "Processor II", $"{cpuGates} gates, {cpuGates * Watts:0.0} W at 0.125 W per gate. It replaces your first processor.", "Swap",
                        () => Busy(rig.SwapChip($"CPU-II\n{cpuGates} G")));
                    AddStep(Kind.Install, "RAM stick", $"Your Memory chip: {ramGates} gates, {ramGates * Watts:0.0} W. The screen shows what's in it.", "Insert", rig.InstallRam);
                    AddStep(Kind.Cooler, "Cooling", $"Every watt your processor uses turns into heat: {Heat:0.0} W of it. Too little cooling and it shuts down.", null, null);
                    AddStep(Kind.Install, "Screen", $"A 4x4 pixel monitor that scans the RAM's four rows. Draws {ScreenWatts:0} W.", "Connect", rig.InstallMonitor);
                    AddStep(Kind.Program, "Program ROM", "Rewrite the ROM with a program for Processor II. STA writes A into a row of RAM, and the screen shows it.", "Write",
                        OpenEditor);
                    break;
            }

            string psuDesc = mode switch
            {
                Mode.FirstLight => $"The board and display need {BoardWatts:0} W, plus {Watts} W for every gate.",
                Mode.Heartbeat => $"Your adder and counter share one supply now. Board and clock need {baseWatts:0} W, plus {Watts} W per gate.",
                Mode.Boot => $"Board, clock and ROM need {baseWatts:0} W, plus {Watts} W per gate.",
                _ => $"Board, clock, ROM and screen need {baseWatts:0} W, plus {Watts} W per gate, plus the cooler's fan if it has one.",
            };
            AddStep(Kind.Psu, "Power supply", psuDesc, null, null);
            AddStep(Kind.Power, "Switch it on", mode == Mode.Pixels
                ? "If the supply can't carry the load, the machine browns out. If the cooler can't keep up, it shuts down."
                : "If the supply can't carry the load, the machine browns out.", null, null);
        }

        void AddStep(Kind kind, string title, string desc, string action, Action install) =>
            steps.Add(new StepDef { Kind = kind, Title = title, Desc = desc, Action = action, Install = install });

        void Busy(float seconds)
        {
            busy = true;
            app.Root.schedule.Execute(() => { busy = false; Refresh(); }).ExecuteLater((long)(seconds * 1000) + 50);
        }

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
                    ShowCpu(machine.Pc, machine.A, machine.CurrentWord);
                    break;

                case Mode.Pixels:
                    AdvanceClock(dt, high => computer.SetClock(high, resetPending));
                    rig.SetReadout(computer.A, computer.Pc);
                    rig.SetPixels(computer.Rows());
                    ShowCpu(computer.Pc, computer.A, computer.CurrentWord);
                    if (Overheating) UpdateHeat(dt);
                    break;
            }
        }

        void ShowCpu(int pc, int value, int word)
        {
            resultValue.text = value.ToString();
            pcValue.text = pc.ToString();
            instrValue.text = Isa.Disassemble(word);
        }

        /// <summary>Too little cooling: the lid glows hotter until the machine cuts out.</summary>
        void UpdateHeat(float dt)
        {
            heatTimer += dt;
            rig.SetHeat(heatTimer / ShutdownSeconds);
            if (heatTimer < ShutdownSeconds) return;
            running = false;
            rig.PowerOff();
            Sfx.Play(SfxId.Brownout);
            readout.RemoveFromClassList("readout--visible");
            SetStatus($"Thermal shutdown. Your processor makes {Heat:0.0} W of heat but the {CoolerNames[coolerInstalled].ToLowerInvariant()} only handles {CoolerCapacity[coolerInstalled]} W. Fit better cooling, or trim gates from Processor II.", true);
            Refresh();
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
                if (clk) Sfx.Play(SfxId.Tick);
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
            if (Programmable)
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
            Sfx.Play(SfxId.Toggle);
            UpdateInputs();
        }

        void Install(int index)
        {
            if (busy || index != current) return;
            var step = steps[index];
            if (step.Kind != Kind.Install && step.Kind != Kind.Program) return;
            step.Install();
            if (step.Kind == Kind.Program) return; // advances when the program is flashed
            AdvanceStep();
        }

        void AdvanceStep()
        {
            current++;
            if (current == PsuIndex && psuInstalled >= 0) current = PowerIndex;
            if (current < steps.Count)
            {
                switch (steps[current].Kind)
                {
                    case Kind.Psu: SetStatus("Pick a supply that can carry the load, then install it."); break;
                    case Kind.Cooler: SetStatus("Pick a cooler that can take your processor's heat, then fit it."); break;
                    case Kind.Power: SetStatus("Everything is in. Press Power on, or click the round button on the case."); break;
                }
            }
            Refresh();
        }

        void StopMachine()
        {
            running = false;
            heatTimer = 0f;
            if (rig.Powered) rig.PowerOff();
            readout.RemoveFromClassList("readout--visible");
        }

        void InstallPsu()
        {
            if (busy || current < PsuIndex) return;
            StopMachine();
            psuInstalled = psuSelected;
            rig.InstallPsu(PsuWatts[psuInstalled]);
            current = PowerIndex;
            SetStatus($"{PsuNames[psuInstalled]} {PsuWatts[psuInstalled]} W installed. Press Power on, or click the round button on the case.");
            Refresh();
        }

        void InstallCooler()
        {
            int index = IndexOf(Kind.Cooler);
            if (busy || current < index) return;
            StopMachine();
            coolerInstalled = coolerSelected;
            rig.InstallCooler(coolerInstalled);
            if (coolerInstalled == 0) Sfx.Play(SfxId.Click);
            if (current == index) AdvanceStep();
            else
            {
                if (current > PsuIndex) current = PowerIndex;
                SetStatus($"{CoolerNames[coolerInstalled]} fitted. Power on to try it.");
                Refresh();
            }
        }

        void PowerOn()
        {
            if (busy || psuInstalled < 0 || current != PowerIndex) return;
            busy = true;
            bool enough = PsuWatts[psuInstalled] >= Draw;
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
                        Mode.Boot => "trim gates from your processor",
                        _ => "trim gates from Processor II or your Memory",
                    };
                    SetStatus($"Brownout. Your machine needs {Draw:0.0} W but the {PsuNames[psuInstalled]} only delivers {PsuWatts[psuInstalled]} W. Pick a bigger supply, or {leaner}.", true);
                    Refresh();
                    return;
                }

                phase = 0f;
                clk = false;
                heatTimer = 0f;
                switch (mode)
                {
                    case Mode.Heartbeat:
                        resetPending = true;
                        count = TestBench.Probe(counter, counterState, 0b10);
                        break;
                    case Mode.Boot:
                        machine = new CpuMachine(cpu, rom);
                        machine.Reset();
                        break;
                    case Mode.Pixels:
                        computer = new ComputerMachine(cpu, ram, rom);
                        computer.Reset();
                        break;
                }
                running = true;
                readout.AddToClassList("readout--visible");
                app.Save.machinePsuWatts = PsuWatts[psuInstalled];

                if (Programmable)
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

        /// <summary>Programmable builds only count as done once the program does the job on the player's own chips.</summary>
        void CheckProgram()
        {
            if (Overheating)
            {
                SetStatus($"Running hot: your processor makes {Heat:0.0} W of heat and the {CoolerNames[coolerInstalled].ToLowerInvariant()} handles {CoolerCapacity[coolerInstalled]} W.", true);
                return;
            }

            bool ok;
            string reason;
            if (mode == Mode.Boot) ok = CpuGoals.CheckCountdown(cpu, rom, out reason);
            else ok = ComputerGoals.CheckPicture(cpu, ram, rom, ComputerGoals.Smiley, out reason);

            if (ok)
            {
                SetStatus(mode == Mode.Boot
                    ? "Your processor is running your program: 9 down to 0, then it stops. Edit the program any time to try something else."
                    : "There it is: a smiley, drawn by your program on your processor into your memory. Edit the program to draw anything you like.");
                Complete();
            }
            else
            {
                SetStatus($"The machine runs, but the program isn't there yet. {reason} Edit the program and flash it again.", true);
                Sfx.Play(SfxId.Fail);
                Refresh();
            }
        }

        void Complete()
        {
            bool first = !app.Save.IsSolved(level.id);
            current = steps.Count;
            app.RecordSolve(level, TotalGates, Stars(), null);
            Refresh();
            if (first) app.Root.schedule.Execute(() => { Sfx.Play(SfxId.Success); ShowSuccess(); }).ExecuteLater(1600);
        }

        int SmallestSufficientPsu()
        {
            for (int i = 0; i < PsuWatts.Length; i++) if (PsuWatts[i] >= Draw) return i;
            return PsuWatts.Length - 1;
        }

        int SmallestSufficientCooler()
        {
            for (int i = 0; i < CoolerCapacity.Length; i++) if (CoolerCapacity[i] >= Heat) return i;
            return CoolerCapacity.Length - 1;
        }

        int Stars()
        {
            bool psuRight = psuInstalled == SmallestSufficientPsu();
            bool coolerRight = mode != Mode.Pixels || coolerInstalled == SmallestSufficientCooler();
            return psuRight && coolerRight ? 3 : 2;
        }

        void ShowSuccess()
        {
            if (Root.panel == null) return; // the player already left
            int stars = Stars();
            var metrics = Ui.Div("metric-grid");
            metrics.Add(Ui.Metric(TotalGates.ToString(), "GATES"));
            metrics.Add(Ui.Metric($"{Draw:0.0} W", "DRAW"));
            metrics.Add(Ui.Metric($"{PsuWatts[psuInstalled]} W", "SUPPLY", true));

            string body = mode switch
            {
                Mode.Pixels => "Your processor wrote a picture into memory you built, and a screen shows it. Every part of that path is yours.",
                Mode.Boot => "A processor you built from single NAND gates is running a program you wrote. That's a computer.",
                Mode.Heartbeat => "Your machine runs on its own now: a clock you fitted, a counter you designed, feeding an adder you designed.",
                _ => "Your machine adds numbers on a chip you built from single NAND gates.",
            };
            if (stars == 3) body += " It runs on the smallest supply that could carry it.";
            else if (psuInstalled != SmallestSufficientPsu()) body += $" A {PsuWatts[SmallestSufficientPsu()]} W supply would have been enough.";
            else body += $" A {CoolerNames[SmallestSufficientCooler()].ToLowerInvariant()} would have been enough.";
            string title = mode switch { Mode.Pixels => "First picture", Mode.Boot => "It computes", Mode.Heartbeat => "It has a pulse", _ => "First light" };

            VisualElement overlay = null;
            var card = Modal.Card(Ui.Badge(), title, body,
                "Keep watching", () => Modal.Close(overlay),
                "Contracts", app.ShowCampaign,
                Ui.Pips(stars, true), metrics);
            overlay = Modal.Show(Root, card);
        }

        void UpdateInputs()
        {
            if (Programmable) return;
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
                    Kind.Cooler => coolerInstalled >= 0 && current > i,
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
            float draw = Draw;
            float capacity = PsuWatts[psuSelected];
            meterFill.style.width = Length.Percent(Mathf.Clamp01(draw / capacity) * 100f);
            meterFill.EnableInClassList("meter__fill--over", draw > capacity);
            meterLabel.text = draw > capacity
                ? $"Draws {draw:0.0} W of {capacity:0} W. Not enough."
                : $"Draws {draw:0.0} W of {capacity:0} W. {capacity - draw:0.0} W to spare.";
            drawStat.text = $"{draw:0.0} W";

            psuButton.Text = psuInstalled < 0 ? "Install supply" : psuInstalled == psuSelected ? "Installed" : "Swap supply";
            psuButton.SetEnabled(current >= PsuIndex && psuInstalled != psuSelected && !busy);

            if (mode == Mode.Pixels)
            {
                for (int i = 0; i < coolerRows.Length; i++) coolerRows[i].EnableInClassList("psu--selected", i == coolerSelected);
                float cap = CoolerCapacity[coolerSelected];
                coolerFill.style.width = Length.Percent(Mathf.Clamp01(Heat / cap) * 100f);
                coolerFill.EnableInClassList("meter__fill--over", Heat > cap);
                coolerLabel.text = Heat > cap
                    ? $"Processor heat {Heat:0.0} W of {cap:0} W. Too hot."
                    : $"Processor heat {Heat:0.0} W of {cap:0} W. {cap - Heat:0.0} W to spare.";
                coolerButton.Text = coolerInstalled < 0 ? "Fit cooler" : coolerInstalled == coolerSelected ? "Fitted" : "Swap cooler";
                coolerButton.SetEnabled(current >= IndexOf(Kind.Cooler) && coolerInstalled != coolerSelected && !busy);
            }

            powerButton.SetEnabled(current == PowerIndex && psuInstalled >= 0 && !busy && !rig.Powered);
            powerButton.Text = rig.Powered ? "Running" : "Power on";
        }

        // ------------------------------------------------------------------ program editor

        void OpenEditor()
        {
            bool store = mode == Mode.Pixels;
            var card = new VisualElement();
            card.AddToClassList("editor-card");

            var head = Ui.Div("editor__head");
            var titles = new VisualElement();
            titles.Add(Ui.Text("PROGRAM ROM", "t-eyebrow"));
            titles.Add(Ui.Text(store ? "Program Processor II" : "Write your program", "editor__title"));
            head.Add(titles);
            card.Add(head);

            string saved = store ? app.Save.pixelsProgram : app.Save.program;
            string starter = store ? PixelsStarter : BootStarter;
            var body = Ui.Div("editor__body");
            var field = new TextField
            {
                multiline = true,
                value = saved ?? starter,
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
            if (store) help.Add(HelpRow("STA n", "write A to screen row n (0 to 3)"));
            help.Add(Ui.Text("Numbers are 0 to 15. Name a line with a label like \"loop:\" and jump to it by name. Anything after ; is a comment. Up to 16 instructions.", "editor__note"));
            help.Add(Ui.Text("EXAMPLES", "t-eyebrow", "editor__examples"));
            if (store)
            {
                help.Add(Ui.Button("Frame", () => field.value = PixelsStarter, "btn--small", "editor__example"));
                help.Add(Ui.Button("Checkerboard", () => field.value = "LDI 10\nSTA 0\nSTA 2\nLDI 5\nSTA 1\nSTA 3\nh: JMP h\n", "btn--small", "editor__example"));
            }
            else
            {
                help.Add(Ui.Button("Count up", () => field.value = BootStarter, "btn--small", "editor__example"));
                help.Add(Ui.Button("Count by threes", () => field.value = "LDI 0\nloop: ADD 3\nJMP loop\n", "btn--small", "editor__example"));
            }
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
                var words = Assembler.Assemble(field.value, list, store);
                if (list.Count > 0)
                {
                    errors.text = string.Join("\n", list.ConvertAll(e => e.ToString()));
                    errors.Hidden(false);
                    Sfx.Play(SfxId.Fail);
                    return;
                }
                if (store) app.Save.pixelsProgram = field.value;
                else app.Save.program = field.value;
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
            if (running && rig.Powered)
            {
                if (mode == Mode.Boot) { machine.Rom = rom; machine.Reset(); }
                else { computer.Rom = rom; computer.Reset(); }
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
            void Stat(string name, string value)
            {
                bar.Add(Ui.Stat(name, out var label));
                label.text = value;
            }
            switch (mode)
            {
                case Mode.Pixels:
                    Stat("PROCESSOR II", $"{cpuGates} gates");
                    Stat("MEMORY", $"{ramGates} gates");
                    Stat("HEAT", $"{Heat:0.0} W");
                    break;
                case Mode.Boot:
                    Stat("PROCESSOR", $"{cpuGates} gates");
                    break;
                case Mode.Heartbeat:
                    Stat("ADDER", $"{adderGates} gates");
                    Stat("COUNTER", $"{counterGates} gates");
                    break;
                default:
                    Stat("ADDER", $"{adderGates} gates");
                    break;
            }
            bar.Add(Ui.Stat("DRAW", out drawStat));
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
            hint.Add(Ui.Text(Programmable ? "Drag to orbit  ·  Scroll to zoom" : "Drag to orbit  ·  Scroll to zoom  ·  Click the switches", "viewport__hint-text"));
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
                default:
                    card.Add(Readout("PC", out pcValue));
                    card.Add(Ui.Div("readout__divider"));
                    instrValue = Ui.Text("LDI 0", "readout__instr");
                    card.Add(instrValue);
                    card.Add(Ui.Div("readout__divider"));
                    card.Add(Ui.Text("A", "stepper__name"));
                    resultValue = Ui.Text("0", "readout__result");
                    card.Add(resultValue);
                    AddClockControls(card);
                    card.Add(Ui.Button("Edit program", OpenEditor, "btn--small", "readout__edit"));
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
            if (mode == Mode.Pixels) inner.Add(TargetPreview(ComputerGoals.Smiley));
            inner.Add(Ui.Div("divider"));

            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                int index = i;
                bool clickable = s.Kind == Kind.Install || s.Kind == Kind.Program;
                s.Card = StepCard(i + 1, s.Title, s.Desc, s.Action, clickable ? () => Install(index) : null);
                var main = s.Card.Q(className: "step__main");
                if (s.Kind == Kind.Psu) BuildPsuControls(main);
                if (s.Kind == Kind.Cooler) BuildCoolerControls(main);
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
                Mode.Pixels => "Your machine from Boot is back on the bench. Start by swapping in Processor II.",
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

        /// <summary>A small picture of the goal: four rows of four pixels.</summary>
        static VisualElement TargetPreview(int[] rows)
        {
            var box = Ui.Div("target");
            box.Add(Ui.Text("TARGET", "t-eyebrow"));
            var grid = Ui.Div("target__grid");
            for (int r = 0; r < 4; r++)
            {
                var row = Ui.Div("target__row");
                for (int c = 0; c < 4; c++)
                {
                    var px = Ui.Div("target__px");
                    if (((rows[r] >> (3 - c)) & 1) != 0) px.AddToClassList("target__px--on");
                    row.Add(px);
                }
                row.Add(Ui.Text($"row {r} = {rows[r]}", "target__value"));
                grid.Add(row);
            }
            box.Add(grid);
            return box;
        }

        VisualElement OptionRow(string name, string note, string value, Action onPick)
        {
            var row = Ui.Div("psu");
            var radio = Ui.Div("radio");
            radio.Add(Ui.Div("radio__dot"));
            row.Add(radio);
            var col = new VisualElement();
            col.Add(Ui.Text(name, "psu__name"));
            col.Add(Ui.Text(note, "psu__note"));
            row.Add(col);
            row.Add(Ui.Text(value, "psu__watts"));
            foreach (var child in row.Children()) child.pickingMode = PickingMode.Ignore;
            row.AddManipulator(new Clickable(onPick));
            return row;
        }

        static VisualElement Meter(out Label label, out VisualElement fill)
        {
            var meter = Ui.Div("meter");
            label = Ui.Text("", "meter__label");
            meter.Add(label);
            var track = Ui.Div("meter__track");
            fill = Ui.Div("meter__fill");
            track.Add(fill);
            meter.Add(track);
            return meter;
        }

        void BuildPsuControls(VisualElement main)
        {
            var list = Ui.Div("psu-list");
            for (int i = 0; i < PsuWatts.Length; i++)
            {
                int index = i;
                psuRows[i] = OptionRow(PsuNames[i], PsuNotes[i], $"{PsuWatts[i]} W", () => { psuSelected = index; Refresh(); });
                list.Add(psuRows[i]);
            }
            main.Add(list);
            main.Add(Meter(out meterLabel, out meterFill));

            psuButton = Ui.Button("Install supply", InstallPsu, "btn--small");
            psuButton.style.marginTop = 12;
            psuButton.style.alignSelf = Align.FlexStart;
            main.Add(psuButton);
        }

        void BuildCoolerControls(VisualElement main)
        {
            var list = Ui.Div("psu-list");
            for (int i = 0; i < CoolerNames.Length; i++)
            {
                int index = i;
                coolerRows[i] = OptionRow(CoolerNames[i], CoolerNotes[i], $"{CoolerCapacity[i]} W", () => { coolerSelected = index; Refresh(); });
                list.Add(coolerRows[i]);
            }
            main.Add(list);
            main.Add(Meter(out coolerLabel, out coolerFill));

            coolerButton = Ui.Button("Fit cooler", InstallCooler, "btn--small");
            coolerButton.style.marginTop = 12;
            coolerButton.style.alignSelf = Align.FlexStart;
            main.Add(coolerButton);
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
