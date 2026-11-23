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
        };

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
