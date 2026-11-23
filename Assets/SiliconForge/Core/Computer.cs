using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>
    /// The player's processor wired to their RAM chip (and, for Processor III, their press detector).
    ///
    /// Processor II pins: inputs N0..N3, Op0..Op2, Clk, Reset; outputs PC0..PC3, A0..A3, W.
    /// Processor III pins: inputs N0..N3, Op0..Op2, M0..M3, Btn, Clk, Reset; same outputs.
    /// RAM pins: inputs D0..D3, A0..A1, Write, Clk; output Q0..Q3 (the word at the address).
    /// Press detector pins: inputs Btn, Clk; output P.
    ///
    /// The RAM's address is always the low bits of N. Its data input is A, its Write is W, and for
    /// Processor III its output Q goes back into M. The button goes through the press detector into Btn.
    /// </summary>
    public sealed class ComputerMachine
    {
        public const int CpuClkBit = 7, CpuResetBit = 8;          // Processor II
        public const int Cpu3MemBit = 7, Cpu3BtnBit = 11, Cpu3ClkBit = 12, Cpu3ResetBit = 13;
        public const int RamWriteBit = 6, RamClkBit = 7;

        readonly Netlist cpu, ram, press;
        readonly ulong[] cpuState, ramState, pressState;
        readonly bool processor3;
        public int[] Rom;
        public int Pc { get; private set; }
        public int A { get; private set; }
        public bool Write { get; private set; }
        /// <summary>The press detector's output last time the machine settled.</summary>
        public bool Pressed { get; private set; }

        bool clock;
        int ramInputs;

        /// <summary>Processor II and RAM.</summary>
        public ComputerMachine(Netlist cpu, Netlist ram, int[] rom) : this(cpu, ram, null, rom, false) { }

        /// <summary>Processor III, RAM and press detector.</summary>
        public ComputerMachine(Netlist cpu3, Netlist ram, Netlist press, int[] rom) : this(cpu3, ram, press, rom, true) { }

        ComputerMachine(Netlist cpu, Netlist ram, Netlist press, int[] rom, bool processor3)
        {
            this.cpu = cpu;
            this.ram = ram;
            this.press = press;
            this.processor3 = processor3;
            cpuState = cpu.CreateState();
            ramState = ram.CreateState();
            if (press != null) pressState = press.CreateState();
            Rom = rom;
        }

        public int CurrentWord => Rom[Pc & 15];

        int RamInputs(int addr) => A | (addr << 4) | (Write ? 1 << RamWriteBit : 0) | (clock ? 1 << RamClkBit : 0);

        /// <summary>
        /// Sets the shared clock and settles every chip. The ROM follows the PC and the RAM's output feeds
        /// the processor, so it settles in passes until nothing moves.
        /// </summary>
        public void SetClock(bool level, bool reset, bool button = false)
        {
            clock = level;
            if (press != null)
                Pressed = (TestBench.Probe(press, pressState, (button ? 1 : 0) | (level ? 2 : 0)) & 1) != 0;

            for (int pass = 0; pass < 6; pass++)
            {
                int word = CurrentWord;
                int addr = Isa.N(word) & 3;
                int q = TestBench.Probe(ram, ramState, RamInputs(addr)) & 15;

                int inputs;
                if (processor3)
                    inputs = (word & 0x7F) | (q << Cpu3MemBit) | (Pressed ? 1 << Cpu3BtnBit : 0)
                             | (level ? 1 << Cpu3ClkBit : 0) | (reset ? 1 << Cpu3ResetBit : 0);
                else
                    inputs = (word & 0x7F) | (level ? 1 << CpuClkBit : 0) | (reset ? 1 << CpuResetBit : 0);

                int outputs = TestBench.Probe(cpu, cpuState, inputs);
                int pc = outputs & 15;
                A = (outputs >> 4) & 15;
                Write = ((outputs >> 8) & 1) != 0;
                ramInputs = RamInputs(addr);
                int q2 = TestBench.Probe(ram, ramState, ramInputs) & 15;

                bool moved = pc != Pc;
                Pc = pc;
                if (!moved && q2 == q) break;
            }
        }

        /// <summary>
        /// Reads all four RAM rows the way a display scans them: change the address, never write.
        /// Afterwards the processor's signals go back on the RAM pins. While the clock is low the RAM's latches
        /// are open, so leaving the scan's inputs there would cancel a write the processor has lined up.
        /// </summary>
        public int[] Rows()
        {
            var rows = new int[4];
            for (int r = 0; r < 4; r++)
                rows[r] = TestBench.Probe(ram, ramState, (r << 4) | (clock ? 1 << RamClkBit : 0)) & 15;
            TestBench.Probe(ram, ramState, ramInputs);
            return rows;
        }

        public void Reset()
        {
            SetClock(false, true);
            SetClock(true, true);
            SetClock(false, true);
            SetClock(false, false);
        }

        public void Cycle(bool button = false)
        {
            SetClock(true, false, button);
            SetClock(false, false, button);
        }
    }

    public static class ComputerGoals
    {
        /// <summary>The target for First Picture: a 4x4 smiley. Bit 3 is the leftmost pixel.</summary>
        public static readonly int[] Smiley = { 0b1001, 0b0000, 0b1001, 0b0110 };

        /// <summary>Press Start's goal: row 0 after the program starts and after each press. Rows 1-3 stay dark.</summary>
        public static readonly int[] LightSteps = { 1, 2, 4, 8, 1, 2 };

        public static bool CheckPicture(Netlist cpu, Netlist ram, int[] rom, int[] target, out string reason)
        {
            var machine = new ComputerMachine(cpu, ram, rom);
            machine.Reset();
            var history = new List<int[]>();
            for (int i = 0; i < 64; i++)
            {
                machine.Cycle();
                history.Add(machine.Rows());
            }

            var final = history[history.Count - 1];
            for (int r = 0; r < 4; r++)
            {
                if (final[r] == target[r]) continue;
                reason = $"Row {r} of the screen shows \"{Pattern(final[r])}\" but should show \"{Pattern(target[r])}\".";
                return false;
            }
            for (int i = history.Count - 16; i < history.Count; i++)
                for (int r = 0; r < 4; r++)
                    if (history[i][r] != target[r]) { reason = "The picture appears but doesn't stay. End the program with a loop that jumps to itself."; return false; }

            reason = null;
            return true;
        }

        /// <summary>
        /// Plays Press Start on the player's chips: wait, then press and hold the button a few times.
        /// Each press must move the light in row 0 one pixel left, wrapping round, and nothing may move without a press.
        /// </summary>
        public static bool CheckMovingLight(Netlist cpu3, Netlist ram, Netlist press, int[] rom, out string reason)
        {
            var machine = new ComputerMachine(cpu3, ram, press, rom);
            machine.Reset();
            for (int i = 0; i < 12; i++) machine.Cycle();
            if (!RowsMatch(machine.Rows(), LightSteps[0], out reason, "Before any press,")) return false;

            for (int p = 1; p < LightSteps.Length; p++)
            {
                for (int i = 0; i < 12; i++) machine.Cycle(true);  // press and hold: long enough that a leaky detector moves the light twice
                for (int i = 0; i < 10; i++) machine.Cycle(false); // release and wait
                string when = p == 1 ? "After 1 press," : $"After {p} presses,";
                if (!RowsMatch(machine.Rows(), LightSteps[p], out reason, when)) return false;
            }
            reason = null;
            return true;
        }

        static bool RowsMatch(int[] rows, int row0, out string reason, string when)
        {
            if (rows[0] != row0)
            {
                reason = $"{when} row 0 shows \"{Pattern(rows[0])}\" but should show \"{Pattern(row0)}\".";
                return false;
            }
            for (int r = 1; r < 4; r++)
                if (rows[r] != 0) { reason = $"{when} row {r} shows \"{Pattern(rows[r])}\". Rows 1 to 3 should be dark."; return false; }
            reason = null;
            return true;
        }

        /// <summary>A row as dots and squares, leftmost pixel first, e.g. "#..#".</summary>
        public static string Pattern(int row)
        {
            var chars = new char[4];
            for (int c = 0; c < 4; c++) chars[c] = ((row >> (3 - c)) & 1) != 0 ? '#' : '.';
            return new string(chars);
        }
    }
}
