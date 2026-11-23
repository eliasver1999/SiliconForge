using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>
    /// The 4-bit processor's instruction set. An instruction is an opcode and a 4-bit number N.
    /// The first processor understands the four 2-bit opcodes; Processor II adds a third opcode bit for STA.
    /// There is one register, A, and a 4-bit program counter, so a program holds up to 16 instructions.
    /// </summary>
    public static class Isa
    {
        public const int Ldi = 0; // A = N
        public const int Add = 1; // A = A + N (wraps at 16)
        public const int Jmp = 2; // PC = N
        public const int Jz = 3;  // if A == 0 then PC = N
        public const int Sta = 4; // write A to memory row N (0-3). Processor II only.
        public static readonly string[] Names = { "LDI", "ADD", "JMP", "JZ", "STA" };
        /// <summary>Labels for a 3-bit opcode column; 5 to 7 are unused.</summary>
        public static readonly string[] OpLabels = { "LDI", "ADD", "JMP", "JZ", "STA", "op5", "op6", "op7" };
        public const int RomSize = 16;

        public static int Encode(int op, int n) => ((op & 7) << 4) | (n & 15);
        public static int Op(int word) => (word >> 4) & 7;
        public static int N(int word) => word & 15;
        public static string Disassemble(int word) => $"{OpLabels[Op(word)]} {N(word)}";

        /// <summary>What the instruction should do. Used to generate tests and to check the player's processor.</summary>
        public static void Execute(int word, ref int pc, ref int a)
        {
            int n = N(word);
            switch (Op(word))
            {
                case Ldi: a = n; pc = (pc + 1) & 15; break;
                case Add: a = (a + n) & 15; pc = (pc + 1) & 15; break;
                case Jmp: pc = n; break;
                case Jz: pc = a == 0 ? n : (pc + 1) & 15; break;
                default: pc = (pc + 1) & 15; break; // STA changes neither A nor the flow
            }
        }
    }

    public sealed class AssemblyError
    {
        public int line;
        public string message;
        public override string ToString() => $"Line {line}: {message}";
    }

    /// <summary>Turns assembly text into ROM words. Supports labels ("loop:"), comments (";" or "#") and numbers 0 to 15.</summary>
    public static class Assembler
    {
        /// <param name="allowStore">STA only exists on Processor II.</param>
        public static int[] Assemble(string source, List<AssemblyError> errors, bool allowStore = false)
        {
            var rom = new int[Isa.RomSize];
            var lines = (source ?? "").Replace("\r", "").Split('\n');
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<(int line, string op, string arg)>();

            // Pass 1: strip comments, record labels, collect instructions.
            for (int i = 0; i < lines.Length; i++)
            {
                string text = lines[i];
                int cut = text.IndexOfAny(new[] { ';', '#' });
                if (cut >= 0) text = text.Substring(0, cut);
                text = text.Trim();
                while (text.Length > 0)
                {
                    int colon = text.IndexOf(':');
                    if (colon < 0) break;
                    string label = text.Substring(0, colon).Trim();
                    if (!IsName(label)) { errors.Add(new AssemblyError { line = i + 1, message = $"'{label}' isn't a valid label name." }); break; }
                    if (labels.ContainsKey(label)) errors.Add(new AssemblyError { line = i + 1, message = $"The label '{label}' is defined twice." });
                    labels[label] = pending.Count;
                    text = text.Substring(colon + 1).Trim();
                }
                if (text.Length == 0) continue;
                var parts = text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    errors.Add(new AssemblyError { line = i + 1, message = "Write one instruction and one number, like ADD 3." });
                    continue;
                }
                pending.Add((i + 1, parts[0], parts[1]));
            }

            if (pending.Count > Isa.RomSize)
                errors.Add(new AssemblyError { line = pending[Isa.RomSize].line, message = $"The ROM holds {Isa.RomSize} instructions. This program has {pending.Count}." });

            // Pass 2: encode.
            for (int k = 0; k < pending.Count && k < Isa.RomSize; k++)
            {
                var (line, opText, argText) = pending[k];
                int op = Array.FindIndex(Isa.Names, n => string.Equals(n, opText, StringComparison.OrdinalIgnoreCase));
                string known = allowStore ? "LDI, ADD, JMP, JZ or STA" : "LDI, ADD, JMP or JZ";
                if (op < 0) { errors.Add(new AssemblyError { line = line, message = $"Unknown instruction '{opText}'. Use {known}." }); continue; }
                if (op == Isa.Sta && !allowStore) { errors.Add(new AssemblyError { line = line, message = "STA needs Processor II, which you build in Era VI." }); continue; }
                int value;
                if (int.TryParse(argText, out value))
                {
                    if (value < 0 || value > 15) { errors.Add(new AssemblyError { line = line, message = "Numbers must be between 0 and 15." }); continue; }
                }
                else if (!labels.TryGetValue(argText, out value))
                {
                    errors.Add(new AssemblyError { line = line, message = $"There's no label called '{argText}'." });
                    continue;
                }
                if (op == Isa.Sta && value > 3) { errors.Add(new AssemblyError { line = line, message = "Memory has rows 0 to 3, so STA takes 0 to 3." }); continue; }
                rom[k] = Isa.Encode(op, value);
            }
            return rom;
        }

        static bool IsName(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char ch in s) if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
            return true;
        }
    }

    /// <summary>
    /// Runs a ROM on the player's own processor netlist.
    /// Processor pins: inputs N0..N3, Op0, Op1, Clk, Reset; outputs PC0..PC3, A0..A3.
    /// </summary>
    public sealed class CpuMachine
    {
        public const int ClkBit = 6, ResetBit = 7;

        readonly Netlist cpu;
        readonly ulong[] state;
        public int[] Rom;
        public int Pc { get; private set; }
        public int A { get; private set; }
        public bool Clock { get; private set; }

        public CpuMachine(Netlist cpu, int[] rom)
        {
            this.cpu = cpu;
            state = cpu.CreateState();
            Rom = rom;
        }

        public int CurrentWord => Rom[Pc & 15];

        /// <summary>Sets the clock level and settles. The ROM output follows the PC, so it settles twice if the PC moved.</summary>
        public void SetClock(bool level, bool reset)
        {
            Clock = level;
            for (int pass = 0; pass < 4; pass++)
            {
                int inputs = CurrentWord | (level ? 1 << ClkBit : 0) | (reset ? 1 << ResetBit : 0);
                int outputs = TestBench.Probe(cpu, state, inputs);
                int pc = outputs & 15;
                A = (outputs >> 4) & 15;
                bool moved = pc != Pc;
                Pc = pc;
                if (!moved) break;
            }
        }

        public void Reset()
        {
            SetClock(false, true);
            SetClock(true, true);
            SetClock(false, true);
            SetClock(false, false);
        }

        /// <summary>One full clock cycle: rising edge, then falling edge.</summary>
        public void Cycle()
        {
            SetClock(true, false);
            SetClock(false, false);
        }
    }

    public static class CpuGoals
    {
        /// <summary>Boot's goal: A counts down 9, 8, ... 0 and then stays at 0.</summary>
        public static bool CheckCountdown(Netlist cpu, int[] rom, out string reason)
        {
            var machine = new CpuMachine(cpu, rom);
            machine.Reset();
            var trace = new List<int>();
            for (int i = 0; i < 96; i++)
            {
                machine.Cycle();
                trace.Add(machine.A);
            }

            int start = trace.IndexOf(9);
            if (start < 0) { reason = "A never reaches 9. Start with LDI 9."; return false; }

            var steps = new List<int>();
            for (int i = start; i < trace.Count; i++)
                if (steps.Count == 0 || steps[steps.Count - 1] != trace[i]) steps.Add(trace[i]);

            for (int k = 0; k < steps.Count; k++)
            {
                int want = 9 - k;
                if (want < 0) { reason = "A keeps changing after it reaches 0. The program has to stop there."; return false; }
                if (steps[k] != want) { reason = $"After {steps[k - 1]} the display shows {steps[k]}, not {want}."; return false; }
            }
            if (steps[steps.Count - 1] != 0) { reason = $"The countdown stops at {steps[steps.Count - 1]} instead of 0."; return false; }
            for (int i = trace.Count - 16; i < trace.Count; i++)
                if (trace[i] != 0) { reason = "A doesn't stay at 0."; return false; }

            reason = null;
            return true;
        }
    }
}
