using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    public sealed class EraSpec
    {
        public string numeral;
        public string name;
        public string description;
    }

    /// <summary>One step of a sequential test: set the inputs, let it settle, check the outputs in mask.</summary>
    public struct SeqStep
    {
        public int inputs;
        public int expected;
        public int mask;
    }

    /// <summary>Shows several pins as one number in the truth table, e.g. D0..D3 as "D".</summary>
    public sealed class BitGroup
    {
        public string name;
        public int start;
        public int width;
        /// <summary>Optional names for each value, e.g. instruction mnemonics.</summary>
        public string[] labels;

        public int Mask => ((1 << width) - 1) << start;
        public string Format(int bits)
        {
            int v = (bits >> start) & ((1 << width) - 1);
            return labels != null && v < labels.Length ? labels[v] : v.ToString();
        }
    }

    public sealed class LevelSpec
    {
        public string id;
        public int era;
        public int number;
        public string title;
        public string tagline;
        public string brief;
        public string hint;
        /// <summary>Shown on the board for first-time players.</summary>
        public string tip;
        public string[] inputs = new string[0];
        public string[] outputs = new string[0];
        /// <summary>Packed inputs (bit i = input i) to packed expected outputs (bit j = output j).</summary>
        public Func<int, int> truth;
        /// <summary>Rows shown in the truth table when there are too many to list.</summary>
        public int[] samples;
        public string[] parts = new string[0];
        public string chipId;
        public string chipName;
        public int par;
        public bool workshop;
        /// <summary>For circuits with memory: steps run in order and state carries between them.</summary>
        public SeqStep[] sequence;
        /// <summary>Optional grouping of pins into numbers for the truth table.</summary>
        public BitGroup[] inputGroups, outputGroups;
        /// <summary>Power per gate. Era V moves to a finer process that halves it.</summary>
        public float wattsPerGate = 0.5f;

        public bool IsSequential => sequence != null;
        public int CaseCount => IsSequential ? sequence.Length : 1 << inputs.Length;
    }

    public static class Campaign
    {
        public static readonly List<EraSpec> Eras = new List<EraSpec>
        {
            new EraSpec { numeral = "I", name = "Logic", description = "Every gate, built from a single NAND." },
            new EraSpec { numeral = "II", name = "Arithmetic", description = "Teach sand to count." },
            new EraSpec { numeral = "III", name = "First Machine", description = "Mount it, power it, switch it on." },
            new EraSpec { numeral = "IV", name = "Memory", description = "Circuits that remember, and a clock to drive them." },
            new EraSpec { numeral = "V", name = "Processor", description = "A machine that follows instructions you write." },
            new EraSpec { numeral = "VI", name = "Computer", description = "Memory, a screen, and the heat they make." },
            new EraSpec { numeral = "VII", name = "Interactive", description = "A button, memory reads, and a program you can play with." },
        };

        /// <summary>Btn bit 0, Clk bit 1. P is 1 for exactly two clock cycles after a press is first sampled.</summary>
        static SeqStep[] PressSequence() => Seq(1,
            0b00, -1, 0b10, -1, 0b00, -1, 0b10, -1, 0b00, -1, 0b10, 0, 0b00, 0,   // settle with the button up
            0b01, 0, 0b11, 1, 0b01, 1, 0b11, 1, 0b01, 1, 0b11, 0, 0b01, 0,        // press and hold: two cycles of P
            0b11, 0, 0b01, 0, 0b00, 0, 0b10, 0, 0b00, 0, 0b10, 0, 0b00, 0,        // still held, then released
            0b01, 0, 0b00, 0, 0b10, 0, 0b00, 0,                                   // a tap between edges is never sampled
            0b01, 0, 0b11, 1, 0b00, 1, 0b10, 1, 0b00, 1, 0b10, 0, 0b00, 0);       // a short press still gives two cycles

        public const string Cpu3TestProgram =
            "LDI 3\nSTA 1\nLDI 0\nLDA 1\nADA 1\nJB 8\nADD 1\nJB 10\nLDI 15\nJMP 9\nSTA 2\nADA 2\nh: JMP h";

        /// <summary>
        /// Processor III's test. Inputs: N bits 0-3, Op 4-6, M 7-10 (the memory word at row N), Btn 11, Clk 12, Reset 13.
        /// The test plays the memory itself, so M is whatever the program last stored in that row.
        /// The button is held only while instruction 7 runs, so JB 8 falls through and JB 10 jumps.
        /// </summary>
        static SeqStep[] Cpu3Sequence()
        {
            var rom = Assembler.Assemble(Cpu3TestProgram, new List<AssemblyError>(), 3);
            var memory = new int[4];
            var steps = new List<SeqStep>();
            int pc = 0, a = 0;
            bool aKnown = false;
            int Inputs(int word, int extra) =>
                (word & 0x7F) | (memory[Isa.N(word) & 3] << ComputerMachine.Cpu3MemBit) | ((pc == 7 ? 1 : 0) << ComputerMachine.Cpu3BtnBit) | extra;
            int Expected(int word) => pc | (a << 4) | ((Isa.Op(word) == Isa.Sta ? 1 : 0) << 8);
            int Mask() => 0x10F | (aKnown ? 0xF0 : 0);

            const int clk = 1 << ComputerMachine.Cpu3ClkBit, reset = 1 << ComputerMachine.Cpu3ResetBit;
            steps.Add(new SeqStep { inputs = Inputs(rom[0], reset), mask = 0 });
            steps.Add(new SeqStep { inputs = Inputs(rom[0], reset | clk), expected = Expected(rom[0]), mask = 0x10F });
            steps.Add(new SeqStep { inputs = Inputs(rom[0], reset), expected = Expected(rom[0]), mask = 0x10F });
            steps.Add(new SeqStep { inputs = Inputs(rom[0], 0), expected = Expected(rom[0]), mask = 0x10F });
            for (int cycle = 0; cycle < 16; cycle++)
            {
                int word = rom[pc];
                int rising = Inputs(word, clk);       // M and Btn as they are before the edge
                bool button = pc == 7;
                Isa.Execute(word, ref pc, ref a, memory, button);
                if (Isa.Op(word) == Isa.Ldi || Isa.Op(word) == Isa.Lda) aKnown = true;
                steps.Add(new SeqStep { inputs = rising, expected = Expected(word), mask = Mask() });
                steps.Add(new SeqStep { inputs = Inputs(rom[pc], 0), expected = Expected(rom[pc]), mask = Mask() });
            }
            return steps.ToArray();
        }

        /// <summary>D bits 0-3, A bits 4-5, Write bit 6, Clk bit 7. Q is the word at address A.</summary>
        static SeqStep[] RamSequence()
        {
            var mem = new int[4];
            var known = new bool[4];
            var steps = new List<SeqStep>();
            bool clkWas = false;
            void Step(int d, int a, int write, int clk)
            {
                if (clk == 1 && !clkWas && write == 1) { mem[a] = d; known[a] = true; }
                clkWas = clk == 1;
                steps.Add(new SeqStep { inputs = d | (a << 4) | (write << 6) | (clk << 7), expected = mem[a], mask = known[a] ? 15 : 0 });
            }
            void WriteWord(int a, int d) { Step(d, a, 1, 0); Step(d, a, 1, 1); Step(d, a, 1, 0); }
            void ReadAll() { for (int a = 0; a < 4; a++) Step(0, a, 0, 0); }

            WriteWord(0, 3); WriteWord(1, 12); WriteWord(2, 5); WriteWord(3, 10);
            ReadAll();
            Step(15, 1, 0, 0); Step(15, 1, 0, 1); Step(15, 1, 0, 0); // a clock edge without Write changes nothing
            WriteWord(2, 15);
            ReadAll();
            return steps.ToArray();
        }

        public const string Cpu2TestProgram =
            "LDI 5\nSTA 2\nADD 1\nSTA 0\nJZ 0\nLDI 15\nSTA 3\nADD 1\nJZ 10\nLDI 3\nh: JMP h";

        /// <summary>
        /// Like the first processor's test, with STA. Inputs: N bits 0-3, Op 4-6, Clk 7, Reset 8.
        /// Outputs: PC bits 0-3, A bits 4-7, W bit 8 (1 while the instruction on the inputs is STA).
        /// </summary>
        static SeqStep[] Cpu2Sequence()
        {
            var rom = Assembler.Assemble(Cpu2TestProgram, new List<AssemblyError>(), 2);
            var steps = new List<SeqStep>();
            int pc = 0, a = 0;
            bool aKnown = false;
            int Expected(int word) => pc | (a << 4) | ((Isa.Op(word) == Isa.Sta ? 1 : 0) << 8);
            void Add(int word, int extra, int mask) => steps.Add(new SeqStep { inputs = word | extra, expected = Expected(word), mask = mask });
            int Mask() => 0x10F | (aKnown ? 0xF0 : 0);

            const int clk = 1 << ComputerMachine.CpuClkBit, reset = 1 << ComputerMachine.CpuResetBit;
            steps.Add(new SeqStep { inputs = rom[0] | reset, mask = 0 });
            Add(rom[0], reset | clk, 0x10F);
            Add(rom[0], reset, 0x10F);
            Add(rom[0], 0, 0x10F);
            for (int cycle = 0; cycle < 14; cycle++)
            {
                int word = rom[pc];
                Isa.Execute(word, ref pc, ref a);
                if (Isa.Op(word) == Isa.Ldi) aKnown = true;
                Add(word, clk, Mask());
                Add(rom[pc], 0, Mask());
            }
            return steps.ToArray();
        }

        static BitGroup G(string name, int start, int width = 1, string[] labels = null) =>
            new BitGroup { name = name, start = start, width = width, labels = labels };

        static string[] Pins(string prefix, int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = prefix + i;
            return names;
        }

        static string[] Concat(params string[][] parts)
        {
            var all = new List<string>();
            foreach (var p in parts) all.AddRange(p);
            return all.ToArray();
        }

        /// <summary>D is bits 0-3, Load bit 4, Clk bit 5.</summary>
        static SeqStep[] Register4Sequence() => Seq(4,
            0x15, -1, 0x35, 5, 0x15, 5, 0x1A, 5, 0x0A, 5, 0x2A, 5, 0x0A, 5, 0x1A, 5,
            0x3A, 10, 0x1A, 10, 0x1F, 10, 0x3F, 15, 0x1F, 15, 0x00, 15, 0x20, 15, 0x00, 15);

        /// <summary>Clk bit 0, Reset bit 1, Jump bit 2, J bits 3-6. Q counts, jumps to J, and resets to 0.</summary>
        static SeqStep[] ProgramCounterSequence()
        {
            var p = new List<int>();
            int q = 0;
            void Edge(int jump, int j, int reset)
            {
                int held = (reset << 1) | (jump << 2) | (j << 3);
                p.Add(held); p.Add(q);
                q = reset == 1 ? 0 : jump == 1 ? j : (q + 1) & 15;
                p.Add(held | 1); p.Add(q);
            }
            // The count is unknown until the first reset edge.
            p.AddRange(new[] { 0b10, -1, 0b11, 0, 0b10, 0, 0b00, 0 });
            Edge(0, 0, 0); Edge(0, 0, 0);
            Edge(1, 9, 0);
            for (int k = 0; k < 7; k++) Edge(0, 0, 0); // 10..15 then wraps to 0
            Edge(1, 5, 0);
            Edge(1, 9, 1); // reset wins over jump
            p.Add(0); p.Add(q);
            return Seq(4, p.ToArray());
        }

        public const string CpuTestProgram =
            "LDI 9\nADD 3\nJZ 7\nADD 4\nJZ 6\nLDI 1\nJMP 8\nLDI 2\nLDI 15\nADD 1\nJZ 12\nLDI 7\nhalt: JMP halt";

        /// <summary>
        /// Plays the test program into the processor one clock edge at a time. Inputs: N bits 0-3, Op 4-5, Clk 6, Reset 7.
        /// Outputs: PC bits 0-3, A bits 4-7. A isn't checked until the first LDI has run.
        /// </summary>
        static SeqStep[] CpuSequence()
        {
            var rom = Assembler.Assemble(CpuTestProgram, new List<AssemblyError>());
            var steps = new List<SeqStep>();
            int pc = 0, a = 0;
            bool aKnown = false;
            void Add(int inputs, int mask) => steps.Add(new SeqStep { inputs = inputs, expected = pc | (a << 4), mask = mask });
            int Mask() => 0x0F | (aKnown ? 0xF0 : 0);

            const int clk = 1 << CpuMachine.ClkBit, reset = 1 << CpuMachine.ResetBit;
            steps.Add(new SeqStep { inputs = rom[0] | reset, mask = 0 });
            Add(rom[0] | reset | clk, 0x0F);
            Add(rom[0] | reset, 0x0F);
            Add(rom[0], 0x0F);
            for (int cycle = 0; cycle < 14; cycle++)
            {
                int word = rom[pc];
                Isa.Execute(word, ref pc, ref a);
                if (Isa.Op(word) == Isa.Ldi) aKnown = true;
                Add(word | clk, Mask());
                Add(rom[pc], Mask());
            }
            return steps.ToArray();
        }

        static int Bit(int x, int i) => (x >> i) & 1;

        /// <summary>Builds a test sequence from (inputs, expected) pairs. An expected value of -1 means "don't check".</summary>
        static SeqStep[] Seq(int outputCount, params int[] pairs)
        {
            int all = (1 << outputCount) - 1;
            var steps = new SeqStep[pairs.Length / 2];
            for (int i = 0; i < steps.Length; i++)
            {
                int e = pairs[i * 2 + 1];
                steps[i] = new SeqStep { inputs = pairs[i * 2], expected = e < 0 ? 0 : e, mask = e < 0 ? 0 : all };
            }
            return steps;
        }

        /// <summary>Clk is input 0, Reset is input 1. Reset, then count through a full wrap, then reset mid-count.</summary>
        static SeqStep[] CounterSequence()
        {
            var p = new List<int> { 0b10, -1, 0b11, 0, 0b10, 0, 0b00, 0 };
            for (int k = 1; k <= 17; k++) { p.Add(0b01); p.Add(k % 16); p.Add(0b00); p.Add(k % 16); }
            p.AddRange(new[] { 0b10, 1, 0b11, 0, 0b10, 0, 0b00, 0, 0b01, 1, 0b00, 1 });
            return Seq(4, p.ToArray());
        }

        public static readonly List<LevelSpec> Levels = new List<LevelSpec>
        {
            new LevelSpec
            {
                id = "NOT", era = 0, number = 1, title = "Inverter", tagline = "Flip a signal.",
                brief = "Every chip in this workshop starts from one part: the NAND gate. It outputs 0 only when both of its inputs are 1.\n\nBuild an inverter: Out must always be the opposite of In.",
                hint = "A NAND whose two inputs carry the same signal behaves like an inverter.",
                tip = "Drag a NAND from the parts list onto the board. Then drag from one pin to another to draw a wire. Click the In switch to try it.",
                inputs = new[] { "In" }, outputs = new[] { "Out" },
                truth = x => 1 - Bit(x, 0),
                parts = new[] { PartTypes.Nand },
                chipId = "NOT", chipName = "NOT", par = 1
            },
            new LevelSpec
            {
                id = "AND", era = 0, number = 2, title = "AND Gate", tagline = "Both, or nothing.",
                brief = "Out is 1 only when A and B are both 1.\n\nYour inverter is now in the parts list. Use it.",
                hint = "NAND literally means NOT-AND. Undo the NOT.",
                tip = "Right-click a part or wire to delete it. Scroll to zoom, right-drag to pan, and drag across empty board to select several parts.",
                inputs = new[] { "A", "B" }, outputs = new[] { "Out" },
                truth = x => x == 3 ? 1 : 0,
                parts = new[] { PartTypes.Nand, "NOT" },
                chipId = "AND", chipName = "AND", par = 2
            },
            new LevelSpec
            {
                id = "OR", era = 0, number = 3, title = "OR Gate", tagline = "Either will do.",
                brief = "Out is 1 when A is 1, B is 1, or both are.",
                hint = "Invert both inputs first, and a NAND turns into an OR.",
                inputs = new[] { "A", "B" }, outputs = new[] { "Out" },
                truth = x => x != 0 ? 1 : 0,
                parts = new[] { PartTypes.Nand, "NOT", "AND" },
                chipId = "OR", chipName = "OR", par = 3
            },
            new LevelSpec
            {
                id = "XOR", era = 0, number = 4, title = "Exclusive OR", tagline = "One or the other, not both.",
                brief = "Out is 1 when exactly one of A and B is 1.\n\nThis little gate is the heart of binary addition.",
                hint = "Four NANDs are enough. Start with NAND(A, B), then feed it into two more NANDs: one with A, one with B. Combine those two with a last NAND.",
                inputs = new[] { "A", "B" }, outputs = new[] { "Out" },
                truth = x => Bit(x, 0) ^ Bit(x, 1),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR" },
                chipId = "XOR", chipName = "XOR", par = 4
            },
            new LevelSpec
            {
                id = "HADD", era = 1, number = 5, title = "Half Adder", tagline = "1 + 1 = 10.",
                brief = "Add two 1-bit numbers. Sum is the low bit of A + B, Carry is the high bit.\n\nIn binary 1 + 1 = 10, so that gives Sum 0 and Carry 1.",
                hint = "Sum is XOR and Carry is AND. The 5-gate version reuses the first NAND inside your XOR to make the carry.",
                inputs = new[] { "A", "B" }, outputs = new[] { "Sum", "Carry" },
                truth = x => Bit(x, 0) + Bit(x, 1),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR" },
                chipId = "HADD", chipName = "Half Adder", par = 5
            },
            new LevelSpec
            {
                id = "FADD", era = 1, number = 6, title = "Full Adder", tagline = "Carry the one.",
                brief = "Add three bits: A, B and a carry coming in from the previous column (Cin).\n\nChain these together and you can add numbers of any size.",
                hint = "Two half adders and an OR will do it. For 9 gates: the carry out is a NAND of the two half adders' first NANDs.",
                inputs = new[] { "A", "B", "Cin" }, outputs = new[] { "Sum", "Cout" },
                truth = x => Bit(x, 0) + Bit(x, 1) + Bit(x, 2),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR", "HADD" },
                chipId = "FADD", chipName = "Full Adder", par = 9
            },
            new LevelSpec
            {
                id = "ADD4", era = 1, number = 7, title = "4-bit Adder", tagline = "Numbers up to 15 + 15.",
                brief = "Add two 4-bit numbers. A0 and B0 are the lowest bits. S0 to S3 is the sum, and C is the carry out when the answer reaches 16.\n\nThis chip becomes the brain of your first machine, and every gate you save here shrinks its power bill.",
                hint = "Ripple carry: a half adder for bit 0, then a full adder for each higher bit, each taking the carry from the bit below.",
                inputs = new[] { "A0", "A1", "A2", "A3", "B0", "B1", "B2", "B3" },
                outputs = new[] { "S0", "S1", "S2", "S3", "C" },
                truth = x => (x & 15) + ((x >> 4) & 15),
                samples = new[] { 0x00, 0x11, 0x23, 0x35, 0x77, 0x96, 0xF1, 0xFF },
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR", "HADD", "FADD" },
                chipId = "ADD4", chipName = "4-bit Adder", par = 32
            },
            new LevelSpec
            {
                id = "LIGHT", era = 2, number = 8, title = "First Light", tagline = "Build the machine. Switch it on.",
                brief = "Your adder is certified. Now give it a body: a case, a mainboard and a power supply big enough to feed every gate you used.",
                workshop = true
            },
            new LevelSpec
            {
                id = "SR", era = 3, number = 9, title = "Set-Reset Latch", tagline = "Remember one bit.",
                brief = "Until now every output depended only on the inputs right now. This one has memory.\n\nA pulse on Set turns Q on, a pulse on Reset turns it off, and when both are 0, Q keeps whatever it was. The test runs as a sequence, top to bottom, so the order matters.",
                hint = "Two NANDs that feed each other's outputs back into their inputs form a loop that holds its value. Invert Set and Reset before they enter the loop.",
                tip = "Memory comes from feedback: it's fine to wire a gate's output back into the circuit that drives it. Never test Set and Reset both at 1.",
                inputs = new[] { "Set", "Reset" }, outputs = new[] { "Q" },
                sequence = Seq(1, 0b10, 0, 0b00, 0, 0b01, 1, 0b00, 1, 0b10, 0, 0b00, 0, 0b01, 1, 0b00, 1),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR" },
                chipId = "SR", chipName = "SR Latch", par = 4
            },
            new LevelSpec
            {
                id = "DLATCH", era = 3, number = 10, title = "D Latch", tagline = "Hold on command.",
                brief = "While Enable is 1, Q follows D. When Enable drops to 0, Q freezes on the last value of D and ignores D from then on.",
                hint = "Make the latch's Set equal to D AND E, and its Reset equal to NOT D AND E. With NANDs you can skip the inverter: NAND(D, E) and NAND(that, E) drive the loop directly.",
                inputs = new[] { "D", "E" }, outputs = new[] { "Q" },
                sequence = Seq(1, 0b10, 0, 0b11, 1, 0b10, 0, 0b11, 1, 0b01, 1, 0b00, 1, 0b01, 1, 0b00, 1, 0b10, 0, 0b00, 0, 0b01, 0),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "SR" },
                chipId = "DLATCH", chipName = "D Latch", par = 4
            },
            new LevelSpec
            {
                id = "MUX", era = 3, number = 11, title = "Selector", tagline = "Pick one of two.",
                brief = "Out copies A when Sel is 0 and copies B when Sel is 1.\n\nThis tiny switch lets a circuit choose between keeping its old value and taking a new one.",
                hint = "Out = (A AND NOT Sel) OR (B AND Sel). In NANDs that's four gates: NAND the two halves, then NAND the results.",
                inputs = new[] { "A", "B", "Sel" }, outputs = new[] { "Out" },
                truth = x => Bit(x, 2) == 1 ? Bit(x, 1) : Bit(x, 0),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR" },
                chipId = "MUX", chipName = "Selector", par = 4
            },
            new LevelSpec
            {
                id = "DFF", era = 3, number = 12, title = "Flip-Flop", tagline = "Tick.",
                brief = "Q takes the value of D only at the instant Clk rises from 0 to 1, and holds it until the next rise. Changing D at any other time does nothing.\n\nThis is how computers keep time: everything moves one step per clock tick.",
                hint = "Two D latches in a row. The first is open while Clk is 0, the second while Clk is 1, so the value hops across only on the rising edge.",
                tip = "Press Space to toggle the Clk input.",
                inputs = new[] { "D", "Clk" }, outputs = new[] { "Q" },
                sequence = Seq(1, 0b00, -1, 0b10, 0, 0b00, 0, 0b01, 0, 0b11, 1, 0b10, 1, 0b00, 1, 0b10, 0, 0b00, 0, 0b01, 0, 0b11, 1, 0b01, 1, 0b00, 1),
                parts = new[] { PartTypes.Nand, "NOT", "SR", "DLATCH", "MUX" },
                chipId = "DFF", chipName = "Flip-Flop", par = 9
            },
            new LevelSpec
            {
                id = "REG", era = 3, number = 13, title = "Register", tagline = "Keep it, or replace it.",
                brief = "On each rising edge of Clk: if Load is 1, Q takes D. If Load is 0, Q keeps its value.\n\nA bank of these will one day hold the numbers your processor works on.",
                hint = "Feed the flip-flop's D from a Selector that picks between Q itself and the new D, using Load to choose.",
                tip = "Press Space to toggle the Clk input.",
                inputs = new[] { "D", "Load", "Clk" }, outputs = new[] { "Q" },
                sequence = Seq(1,
                    0b010, -1, 0b110, 0, 0b010, 0, 0b011, 0, 0b001, 0, 0b101, 0, 0b001, 0, 0b011, 0,
                    0b111, 1, 0b011, 1, 0b010, 1, 0b000, 1, 0b100, 1, 0b000, 1, 0b010, 1, 0b110, 0, 0b010, 0),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "MUX", "DLATCH", "DFF" },
                chipId = "REG", chipName = "Register", par = 13
            },
            new LevelSpec
            {
                id = "COUNT4", era = 3, number = 14, title = "4-bit Counter", tagline = "0, 1, 2, ... 15, 0.",
                brief = "On every rising edge of Clk, Q goes up by one, wrapping from 15 back to 0. If Reset is 1 at the rising edge, Q goes to 0 instead.\n\nThis gives your machine a heartbeat. Its gate count joins your adder's on the power bill.",
                hint = "Four flip-flops hold the count. Their next value is the count plus one: bit 0 flips every tick, and each higher bit flips when all the bits below it are 1 (half adders do this). AND every next bit with NOT Reset.",
                tip = "Press Space to toggle the Clk input.",
                inputs = new[] { "Clk", "Reset" }, outputs = new[] { "Q0", "Q1", "Q2", "Q3" },
                sequence = CounterSequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR", "HADD", "MUX", "DFF", "REG" },
                chipId = "COUNT4", chipName = "4-bit Counter", par = 60
            },
            new LevelSpec
            {
                id = "TICK", era = 3, number = 15, title = "Heartbeat", tagline = "Give the machine a pulse.",
                brief = "Add a clock crystal and your counter to the machine. The counter steps through numbers by itself and feeds your adder, while the B switches add an offset.\n\nTwo chips now share one power supply.",
                workshop = true
            },
            new LevelSpec
            {
                id = "REG4", era = 4, number = 16, title = "Nibble Register", tagline = "Four bits at once.",
                brief = "A register that holds a whole 4-bit number. On a rising Clk edge, if Load is 1, Q takes D. Otherwise Q keeps its value.\n\nGood news from the factory: Era V moves to a finer process, so every gate now draws half the power, 0.25 W.",
                hint = "Four of your 1-bit registers side by side, all sharing Load and Clk.",
                tip = "Press Space to toggle the Clk input. The test table shows D and Q as numbers from 0 to 15.",
                inputs = Concat(Pins("D", 4), new[] { "Load", "Clk" }), outputs = Pins("Q", 4),
                inputGroups = new[] { G("D", 0, 4), G("Load", 4), G("Clk", 5) },
                outputGroups = new[] { G("Q", 0, 4) },
                sequence = Register4Sequence(),
                parts = new[] { PartTypes.Nand, "NOT", "MUX", "DFF", "REG" },
                chipId = "REG4", chipName = "Nibble Register", par = 52, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "MUX4", era = 4, number = 17, title = "Bus Selector", tagline = "Pick one of two numbers.",
                brief = "Out is the number A when Sel is 0, and the number B when Sel is 1. Every bit switches together.\n\nYour processor will use this to choose where a new value for A comes from.",
                hint = "Four Selectors. They can all share one inverted Sel, which saves gates.",
                inputs = Concat(Pins("A", 4), Pins("B", 4), new[] { "Sel" }), outputs = Pins("Out", 4),
                inputGroups = new[] { G("A", 0, 4), G("B", 4, 4), G("Sel", 8) },
                outputGroups = new[] { G("Out", 0, 4) },
                truth = x => ((x >> 8) & 1) == 1 ? (x >> 4) & 15 : x & 15,
                samples = new[] { 0x0A5, 0x1A5, 0x00F, 0x10F, 0x0C3, 0x1C3, 0x0F0, 0x1F0 },
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "MUX" },
                chipId = "MUX4", chipName = "Bus Selector", par = 13, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "ZERO", era = 4, number = 18, title = "Zero Detector", tagline = "Is it nothing?",
                brief = "Z is 1 only when all four bits of A are 0.\n\nWith this your processor can make decisions: jump somewhere else when a number runs out.",
                hint = "Z is NOT (A0 OR A1 OR A2 OR A3). Build it as two ORs feeding a NOR.",
                inputs = Pins("A", 4), outputs = new[] { "Z" },
                truth = x => x == 0 ? 1 : 0,
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR" },
                chipId = "ZERO", chipName = "Zero Detector", par = 10, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "PC", era = 4, number = 19, title = "Program Counter", tagline = "Where am I?",
                brief = "A counter that can also jump. On each rising Clk edge:\n- if Reset is 1, Q becomes 0\n- otherwise if Jump is 1, Q becomes J\n- otherwise Q goes up by one\n\nIt points at the instruction your processor runs next.",
                hint = "Start from your counter's design: four flip-flops and a +1 circuit. Put a Bus Selector between the +1 result and J, controlled by Jump, then AND every bit with NOT Reset.",
                tip = "Press Space to toggle the Clk input.",
                inputs = Concat(new[] { "Clk", "Reset", "Jump" }, Pins("J", 4)), outputs = Pins("Q", 4),
                inputGroups = new[] { G("Clk", 0), G("Reset", 1), G("Jump", 2), G("J", 3, 4) },
                outputGroups = new[] { G("Q", 0, 4) },
                sequence = ProgramCounterSequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR", "HADD", "MUX", "MUX4", "DFF", "REG" },
                chipId = "PC", chipName = "Program Counter", par = 73, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "CPU", era = 4, number = 20, title = "Processor", tagline = "It follows orders.",
                brief = "Put it all together. Each instruction is a 2-bit opcode (Op) and a number N:\n- LDI N: A = N\n- ADD N: A = A + N\n- JMP N: jump to instruction N\n- JZ N: jump to N if A is 0\n\nOn every rising Clk edge, run the instruction on the inputs. PC tells the machine which instruction to send next. Reset only needs to set PC to 0.",
                hint = "A Nibble Register holds A and loads on LDI and ADD (Op1 = 0). A Bus Selector picks N or A + N using Op0. Your Program Counter jumps when Op1 is 1 and either Op0 is 0 (JMP) or A is zero (JZ).",
                tip = "The test plays a short program into your processor, one clock edge per row. Op is shown as the instruction name.",
                inputs = Concat(Pins("N", 4), new[] { "Op0", "Op1", "Clk", "Reset" }),
                outputs = Concat(Pins("PC", 4), Pins("A", 4)),
                inputGroups = new[] { G("N", 0, 4), G("Op", 4, 2, Isa.Names), G("Clk", 6), G("Reset", 7) },
                outputGroups = new[] { G("PC", 0, 4), G("A", 4, 4) },
                sequence = CpuSequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "MUX", "MUX4", "ADD4", "ZERO", "REG", "REG4", "PC" },
                chipId = "CPU", chipName = "Processor", par = 185, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "BOOT", era = 4, number = 21, title = "Boot", tagline = "Your first program.",
                brief = "Pull the old chips, seat your processor and flash a program into ROM. Your task: make the machine count down from 9 to 0 on the display, then stop.\n\nThere's no subtract instruction, but adding 15 to a 4-bit number takes one away.",
                workshop = true, wattsPerGate = 0.25f
            },
            new LevelSpec
            {
                id = "DEC2", era = 5, number = 22, title = "Decoder", tagline = "One line of four.",
                brief = "Turn a 2-bit address into one of four lines: exactly one of Y0 to Y3 is 1, the one whose number is the address.\n\nAnother process shrink: from Era VI every gate draws 0.125 W.",
                hint = "Y0 = NOT A0 AND NOT A1, Y1 = A0 AND NOT A1, and so on. Invert each address bit once and share it.",
                inputs = new[] { "A0", "A1" }, outputs = Pins("Y", 4),
                truth = x => 1 << x,
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR" },
                chipId = "DEC2", chipName = "Decoder", par = 10, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "SEL4", era = 5, number = 23, title = "Word Selector", tagline = "Pick one of four numbers.",
                brief = "Four 4-bit numbers come in: A, B, C and D. The 2-bit S picks which one comes out: 0 is A, 1 is B, 2 is C, 3 is D.\n\nYour memory will use this to read the word at an address.",
                hint = "Two Bus Selectors pick within each pair using S0, then a third picks between the pairs using S1.",
                inputs = Concat(Pins("A", 4), Pins("B", 4), Pins("C", 4), Pins("D", 4), new[] { "S0", "S1" }), outputs = Pins("Out", 4),
                inputGroups = new[] { G("A", 0, 4), G("B", 4, 4), G("C", 8, 4), G("D", 12, 4), G("S", 16, 2) },
                outputGroups = new[] { G("Out", 0, 4) },
                truth = x => (x >> (4 * ((x >> 16) & 3))) & 15,
                samples = new[] { 0x08421, 0x18421, 0x28421, 0x38421, 0x05A0F, 0x15A0F, 0x25A0F, 0x35A0F },
                parts = new[] { PartTypes.Nand, "NOT", "MUX", "MUX4" },
                chipId = "SEL4", chipName = "Word Selector", par = 39, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "RAM4", era = 5, number = 24, title = "Memory", tagline = "Four words you can rewrite.",
                brief = "Four 4-bit words of memory. Q always shows the word at address A. On a rising Clk edge, if Write is 1, the word at A becomes D. The other words don't change.\n\nThis is a tiny RAM. Your screen will display it.",
                hint = "Four Nibble Registers hold the words. A Decoder on A picks which one loads: Load is Write AND that register's decoder line. A Word Selector on A picks which one Q shows.",
                tip = "Press Space to toggle the Clk input. Reading is instant: change A and Q follows without a clock.",
                inputs = Concat(Pins("D", 4), new[] { "A0", "A1", "Write", "Clk" }), outputs = Pins("Q", 4),
                inputGroups = new[] { G("D", 0, 4), G("A", 4, 2), G("Write", 6), G("Clk", 7) },
                outputGroups = new[] { G("Q", 0, 4) },
                sequence = RamSequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "DEC2", "SEL4", "MUX4", "REG", "REG4" },
                chipId = "RAM4", chipName = "Memory", par = 265, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "CPU2", era = 5, number = 25, title = "Processor II", tagline = "Now it can write.",
                brief = "Your processor gets a third opcode bit and a new instruction:\n- STA N: write A to memory row N (0 to 3)\n\nDuring STA, raise the new W output; the machine sends A and N to the memory. STA must not change A and must not jump. Op2 is 0 for the four old instructions, which work as before.",
                hint = "Start from your Processor's design. A should load only when Op1 and Op2 are both 0, and a jump needs Op2 to be 0. W is just Op2. Or wrap your old Processor chip: during STA, feed it an ADD with N forced to 0.",
                tip = "The test plays a program with STA into your processor. W should be 1 exactly on the rows where Op is STA.",
                inputs = Concat(Pins("N", 4), Pins("Op", 3), new[] { "Clk", "Reset" }),
                outputs = Concat(Pins("PC", 4), Pins("A", 4), new[] { "W" }),
                inputGroups = new[] { G("N", 0, 4), G("Op", 4, 3, Isa.OpLabels), G("Clk", 7), G("Reset", 8) },
                outputGroups = new[] { G("PC", 0, 4), G("A", 4, 4), G("W", 8) },
                sequence = Cpu2Sequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "MUX", "MUX4", "ADD4", "ZERO", "REG4", "PC", "CPU" },
                chipId = "CPU2", chipName = "Processor II", par = 190, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "PIXELS", era = 5, number = 26, title = "First Picture", tagline = "Draw something.",
                brief = "Upgrade to Processor II, add a RAM stick for video memory, a cooler and a screen. Then write a program that draws the smiley shown below.\n\nThe screen shows the RAM's four rows, top to bottom. STA writes A into a row, and bit 3 is the leftmost pixel, so 9 lights both ends of a row.",
                workshop = true, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "PRESS", era = 6, number = 27, title = "Press Detector", tagline = "One press, one signal.",
                brief = "A button stays down for many clock ticks, but a program wants to hear about each press once.\n\nP is 0 until a rising Clk edge first sees Btn at 1. Then P is 1 for exactly two clock cycles and goes back to 0, even if the button is still held. Two cycles means a program checking the button in a two-instruction loop can never miss it.",
                hint = "Three flip-flops in a row remember the button at the last three edges. P is (the newest OR the middle one) AND NOT the oldest one, which also keeps a quick tap at two cycles.",
                tip = "Press Space to toggle the Clk input.",
                inputs = new[] { "Btn", "Clk" }, outputs = new[] { "P" },
                sequence = PressSequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "DFF" },
                chipId = "PRESS", chipName = "Press Detector", par = 33, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "CPU3", era = 6, number = 28, title = "Processor III", tagline = "Read, add, react.",
                brief = "Three more instructions fill the last opcodes:\n- LDA N: A = memory row N\n- ADA N: A = A + memory row N\n- JB N: jump to N if the button input Btn is 1\n\nThe machine puts memory row N on the new M inputs, so LDA and ADA work like LDI and ADD with M in place of N. Everything from Processor II still works the same way.",
                hint = "Pick the operand first: a Bus Selector chooses N or M using Op2. LDI and LDA load that operand, ADD and ADA load A plus it. A loads for LDI, ADD, LDA and ADA. Jump on JMP, on JZ when A is 0, and on JB when Btn is 1.",
                tip = "The test plays a program into your processor and acts as its memory. M is shown as a number.",
                inputs = Concat(Pins("N", 4), Pins("Op", 3), Pins("M", 4), new[] { "Btn", "Clk", "Reset" }),
                outputs = Concat(Pins("PC", 4), Pins("A", 4), new[] { "W" }),
                inputGroups = new[] { G("N", 0, 4), G("Op", 4, 3, Isa.OpLabels), G("M", 7, 4), G("Btn", 11), G("Clk", 12), G("Rst", 13) },
                outputGroups = new[] { G("PC", 0, 4), G("A", 4, 4), G("W", 8) },
                sequence = Cpu3Sequence(),
                parts = new[] { PartTypes.Nand, "NOT", "AND", "OR", "XOR", "MUX", "MUX4", "ADD4", "ZERO", "REG4", "PC", "CPU2" },
                chipId = "CPU3", chipName = "Processor III", par = 220, wattsPerGate = 0.125f
            },
            new LevelSpec
            {
                id = "PLAY", era = 6, number = 29, title = "Press Start", tagline = "Make it interactive.",
                brief = "Fit Processor III, your press detector and a big arcade button. Then write a program where one light in row 0 starts at the right and moves one pixel left each time you press the button, wrapping back round. Rows 1 to 3 stay dark.\n\nIt all has to fit in 16 instructions. Hint: ADA 0 adds row 0 to A, and adding a number to itself moves every bit one place left.",
                workshop = true, wattsPerGate = 0.125f
            },
        };

        public static LevelSpec Get(string id) => Levels.Find(l => l.id == id);

        public static LevelSpec Next(LevelSpec level)
        {
            int i = Levels.IndexOf(level);
            return i >= 0 && i + 1 < Levels.Count ? Levels[i + 1] : null;
        }

        public static int Stars(LevelSpec level, int gates)
        {
            if (gates <= level.par) return 3;
            if (gates <= (int)Math.Ceiling(level.par * 1.5)) return 2;
            return 1;
        }
    }
}
