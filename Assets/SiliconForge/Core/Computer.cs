using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>
    /// Processor II wired to a RAM chip, both running the player's own designs.
    /// Processor II pins: inputs N0..N3, Op0..Op2, Clk, Reset; outputs PC0..PC3, A0..A3, W.
    /// RAM pins: inputs D0..D3, A0..A1, Write, Clk; output Q0..Q3 (the word at the address).
    /// STA puts A on the RAM's data pins, the low bits of N on its address pins, and raises W.
    /// </summary>
    public sealed class ComputerMachine
    {
        public const int CpuClkBit = 7, CpuResetBit = 8;
        public const int RamWriteBit = 6, RamClkBit = 7;

        readonly Netlist cpu, ram;
        readonly ulong[] cpuState, ramState;
        public int[] Rom;
        public int Pc { get; private set; }
        public int A { get; private set; }
        public bool Write { get; private set; }

        public ComputerMachine(Netlist cpu, Netlist ram, int[] rom)
        {
            this.cpu = cpu;
            this.ram = ram;
            cpuState = cpu.CreateState();
            ramState = ram.CreateState();
            Rom = rom;
        }

        public int CurrentWord => Rom[Pc & 15];
        bool clock;
        int ramInputs;

        /// <summary>Sets the shared clock and settles both chips. The ROM follows the PC, so it may settle twice.</summary>
        public void SetClock(bool level, bool reset)
        {
            clock = level;
            for (int pass = 0; pass < 4; pass++)
            {
                int word = CurrentWord;
                int outputs = TestBench.Probe(cpu, cpuState, word | (level ? 1 << CpuClkBit : 0) | (reset ? 1 << CpuResetBit : 0));
                int pc = outputs & 15;
                A = (outputs >> 4) & 15;
                Write = ((outputs >> 8) & 1) != 0;
                ramInputs = A | ((Isa.N(word) & 3) << 4) | (Write ? 1 << RamWriteBit : 0) | (level ? 1 << RamClkBit : 0);
                TestBench.Probe(ram, ramState, ramInputs);
                bool moved = pc != Pc;
                Pc = pc;
                if (!moved) break;
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

        public void Cycle()
        {
            SetClock(true, false);
            SetClock(false, false);
        }
    }

    public static class ComputerGoals
    {
        /// <summary>The target for First Picture: a 4x4 smiley. Bit 3 is the leftmost pixel.</summary>
        public static readonly int[] Smiley = { 0b1001, 0b0000, 0b1001, 0b0110 };

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

        /// <summary>A row as dots and squares, leftmost pixel first, e.g. "#..#".</summary>
        public static string Pattern(int row)
        {
            var chars = new char[4];
            for (int c = 0; c < 4; c++) chars[c] = ((row >> (3 - c)) & 1) != 0 ? '#' : '.';
            return new string(chars);
        }
    }
}
