using System;
using System.Collections.Generic;
using System.Text;
using SiliconForge.Core;
using SiliconForge.Game;
using UnityEditor;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    /// <summary>
    /// Development shortcut: certifies every chip contract with a built-in reference design,
    /// so later eras (which need earlier chips) can be playtested straight away.
    /// Your own boards are left empty; replaying a contract and passing it replaces the reference chip with yours.
    /// </summary>
    public static class DevUnlock
    {
        [MenuItem("Silicon Forge/Unlock All Contracts")]
        public static void UnlockAll()
        {
            var save = Build(out string report);
            if (EditorApplication.isPlaying && GameApp.Instance != null) GameApp.Instance.ReplaceSave(save);
            else SaveSystem.Write(save);
            Debug.Log(report);
        }

        // ------------------------------------------------------------------ building

        /// <summary>A tiny helper for wiring reference circuits. Inputs and outputs follow the contract's pin order.</summary>
        sealed class Board
        {
            public readonly CircuitData c = new CircuitData();
            public readonly int[] i, o;

            public Board(LevelSpec level)
            {
                i = new int[level.inputs.Length];
                o = new int[level.outputs.Length];
                for (int k = 0; k < i.Length; k++) i[k] = c.AddPart(PartTypes.Input, 0, k * 60, k, level.inputs[k]).id;
                for (int k = 0; k < o.Length; k++) o[k] = c.AddPart(PartTypes.Output, 900, k * 72, k, level.outputs[k]).id;
            }

            public int Part(string type) => c.AddPart(type, 300, 0).id;
            public void Wire(int from, int fromPin, int to, int toPin) => c.Connect(from, fromPin, to, toPin);
            public void Out(int from, int fromPin, int output) => c.Connect(from, fromPin, o[output], 0);

            public int Nand(int a, int ap, int b, int bp)
            {
                int g = Part(PartTypes.Nand);
                Wire(a, ap, g, 0);
                Wire(b, bp, g, 1);
                return g;
            }

            public int Not(int a, int ap = 0) => Nand(a, ap, a, ap);
        }

        static readonly Dictionary<string, Func<Board, CircuitData>> Designs = new Dictionary<string, Func<Board, CircuitData>>
        {
            ["NOT"] = b => { b.Out(b.Not(b.i[0]), 0, 0); return b.c; },
            ["AND"] = b => { b.Out(b.Not(b.Nand(b.i[0], 0, b.i[1], 0)), 0, 0); return b.c; },
            ["OR"] = b => { b.Out(b.Nand(b.Not(b.i[0]), 0, b.Not(b.i[1]), 0), 0, 0); return b.c; },
            ["XOR"] = b => { b.Out(Xor(b, b.i[0], 0, b.i[1], 0, out _), 0, 0); return b.c; },
            ["HADD"] = b =>
            {
                int sum = Xor(b, b.i[0], 0, b.i[1], 0, out int first);
                b.Out(sum, 0, 0);
                b.Out(b.Not(first), 0, 1);
                return b.c;
            },
            ["FADD"] = b =>
            {
                int x = Xor(b, b.i[0], 0, b.i[1], 0, out int n1);
                int s = Xor(b, x, 0, b.i[2], 0, out int n5);
                b.Out(s, 0, 0);
                b.Out(b.Nand(n1, 0, n5, 0), 0, 1);
                return b.c;
            },
            ["ADD4"] = b =>
            {
                int h = b.Part("HADD");
                b.Wire(b.i[0], 0, h, 0); b.Wire(b.i[4], 0, h, 1); b.Out(h, 0, 0);
                int carry = h;
                for (int k = 1; k < 4; k++)
                {
                    int f = b.Part("FADD");
                    b.Wire(b.i[k], 0, f, 0); b.Wire(b.i[4 + k], 0, f, 1); b.Wire(carry, 1, f, 2);
                    b.Out(f, 0, k);
                    carry = f;
                }
                b.Out(carry, 1, 4);
                return b.c;
            },
            ["SR"] = b =>
            {
                Latch(b, b.Not(b.i[0]), b.Not(b.i[1]), out int q);
                b.Out(q, 0, 0);
                return b.c;
            },
            ["DLATCH"] = b =>
            {
                int n1 = b.Nand(b.i[0], 0, b.i[1], 0);
                int n2 = b.Nand(n1, 0, b.i[1], 0);
                Latch(b, n1, n2, out int q);
                b.Out(q, 0, 0);
                return b.c;
            },
            ["MUX"] = b => { b.Out(Mux(b, b.i[0], 0, b.i[1], 0, b.i[2], b.Not(b.i[2])), 0, 0); return b.c; },
            ["DFF"] = b =>
            {
                int nClk = b.Part("NOT"); b.Wire(b.i[1], 0, nClk, 0);
                int master = b.Part("DLATCH"); b.Wire(b.i[0], 0, master, 0); b.Wire(nClk, 0, master, 1);
                int slave = b.Part("DLATCH"); b.Wire(master, 0, slave, 0); b.Wire(b.i[1], 0, slave, 1);
                b.Out(slave, 0, 0);
                return b.c;
            },
            ["REG"] = b =>
            {
                int mux = b.Part("MUX"), dff = b.Part("DFF");
                b.Wire(dff, 0, mux, 0); b.Wire(b.i[0], 0, mux, 1); b.Wire(b.i[1], 0, mux, 2);
                b.Wire(mux, 0, dff, 0); b.Wire(b.i[2], 0, dff, 1);
                b.Out(dff, 0, 0);
                return b.c;
            },
            ["COUNT4"] = b =>
            {
                var ff = FlipFlops(b, b.i[0]);
                var next = Increment(b, ff);
                ResetGate(b, ff, next, b.i[1]);
                for (int k = 0; k < 4; k++) b.Out(ff[k], 0, k);
                return b.c;
            },
            ["REG4"] = b =>
            {
                for (int k = 0; k < 4; k++)
                {
                    int r = b.Part("REG");
                    b.Wire(b.i[k], 0, r, 0); b.Wire(b.i[4], 0, r, 1); b.Wire(b.i[5], 0, r, 2);
                    b.Out(r, 0, k);
                }
                return b.c;
            },
            ["MUX4"] = b =>
            {
                int nSel = b.Not(b.i[8]);
                for (int k = 0; k < 4; k++) b.Out(Mux(b, b.i[k], 0, b.i[4 + k], 0, b.i[8], nSel), 0, k);
                return b.c;
            },
            ["ZERO"] = b =>
            {
                int or1 = b.Nand(b.Not(b.i[0]), 0, b.Not(b.i[1]), 0);
                int or2 = b.Nand(b.Not(b.i[2]), 0, b.Not(b.i[3]), 0);
                int any = b.Nand(b.Not(or1), 0, b.Not(or2), 0);
                b.Out(b.Not(any), 0, 0);
                return b.c;
            },
            ["PC"] = b =>
            {
                var ff = FlipFlops(b, b.i[0]);
                var inc = Increment(b, ff);
                int sel = b.Part("MUX4");
                for (int k = 0; k < 4; k++) { b.Wire(inc[k].part, inc[k].pin, sel, k); b.Wire(b.i[3 + k], 0, sel, 4 + k); }
                b.Wire(b.i[2], 0, sel, 8);
                var chosen = new (int part, int pin)[4];
                for (int k = 0; k < 4; k++) chosen[k] = (sel, k);
                ResetGate(b, ff, chosen, b.i[1]);
                for (int k = 0; k < 4; k++) b.Out(ff[k], 0, k);
                return b.c;
            },
            ["CPU"] = b =>
            {
                int add = b.Part("ADD4"), mux = b.Part("MUX4"), reg = b.Part("REG4"), zero = b.Part("ZERO"), pc = b.Part("PC");
                for (int k = 0; k < 4; k++)
                {
                    b.Wire(reg, k, add, k); b.Wire(b.i[k], 0, add, 4 + k);   // A + N
                    b.Wire(b.i[k], 0, mux, k); b.Wire(add, k, mux, 4 + k);   // N or A + N
                    b.Wire(mux, k, reg, k);
                    b.Wire(reg, k, zero, k);
                    b.Wire(b.i[k], 0, pc, 3 + k);                            // jump target N
                    b.Out(pc, k, k);
                    b.Out(reg, k, 4 + k);
                }
                b.Wire(b.i[4], 0, mux, 8);                                   // Op0 picks ADD
                b.Wire(b.Not(b.i[5]), 0, reg, 4);                            // load on LDI and ADD
                b.Wire(b.i[6], 0, reg, 5);
                int allowed = b.Nand(b.i[4], 0, b.Not(zero), 0);             // 0 only for JZ while A isn't 0
                b.Wire(b.Not(b.Nand(b.i[5], 0, allowed, 0)), 0, pc, 2);       // jump = Op1 AND allowed
                b.Wire(b.i[6], 0, pc, 0);
                b.Wire(b.i[7], 0, pc, 1);
                return b.c;
            },
            ["DEC2"] = b =>
            {
                int na0 = b.Not(b.i[0]), na1 = b.Not(b.i[1]);
                int[,] pairs = { { na0, na1 }, { b.i[0], na1 }, { na0, b.i[1] }, { b.i[0], b.i[1] } };
                for (int k = 0; k < 4; k++) b.Out(b.Not(b.Nand(pairs[k, 0], 0, pairs[k, 1], 0)), 0, k);
                return b.c;
            },
            ["SEL4"] = b =>
            {
                int low = b.Part("MUX4"), high = b.Part("MUX4"), pick = b.Part("MUX4");
                for (int k = 0; k < 4; k++)
                {
                    b.Wire(b.i[k], 0, low, k); b.Wire(b.i[4 + k], 0, low, 4 + k);
                    b.Wire(b.i[8 + k], 0, high, k); b.Wire(b.i[12 + k], 0, high, 4 + k);
                    b.Wire(low, k, pick, k); b.Wire(high, k, pick, 4 + k);
                    b.Out(pick, k, k);
                }
                b.Wire(b.i[16], 0, low, 8); b.Wire(b.i[16], 0, high, 8); b.Wire(b.i[17], 0, pick, 8);
                return b.c;
            },
            ["RAM4"] = b =>
            {
                int dec = b.Part("DEC2"), sel = b.Part("SEL4");
                b.Wire(b.i[4], 0, dec, 0); b.Wire(b.i[5], 0, dec, 1);
                b.Wire(b.i[4], 0, sel, 16); b.Wire(b.i[5], 0, sel, 17);
                for (int k = 0; k < 4; k++)
                {
                    int reg = b.Part("REG4");
                    for (int bit = 0; bit < 4; bit++) { b.Wire(b.i[bit], 0, reg, bit); b.Wire(reg, bit, sel, k * 4 + bit); }
                    b.Wire(b.Not(b.Nand(b.i[6], 0, dec, k)), 0, reg, 4); // load = Write AND this word's line
                    b.Wire(b.i[7], 0, reg, 5);
                }
                for (int bit = 0; bit < 4; bit++) b.Out(sel, bit, bit);
                return b.c;
            },
            ["CPU2"] = b =>
            {
                int add = b.Part("ADD4"), mux = b.Part("MUX4"), reg = b.Part("REG4"), zero = b.Part("ZERO"), pc = b.Part("PC");
                for (int k = 0; k < 4; k++)
                {
                    b.Wire(reg, k, add, k); b.Wire(b.i[k], 0, add, 4 + k);
                    b.Wire(b.i[k], 0, mux, k); b.Wire(add, k, mux, 4 + k);
                    b.Wire(mux, k, reg, k);
                    b.Wire(reg, k, zero, k);
                    b.Wire(b.i[k], 0, pc, 3 + k);
                    b.Out(pc, k, k);
                    b.Out(reg, k, 4 + k);
                }
                b.Wire(b.i[4], 0, mux, 8);
                int notOp2 = b.Not(b.i[6]);
                b.Wire(b.Not(b.Nand(b.Not(b.i[5]), 0, notOp2, 0)), 0, reg, 4); // load only for LDI and ADD
                b.Wire(b.i[7], 0, reg, 5);
                int allowed = b.Nand(b.i[4], 0, b.Not(zero), 0);
                int oldJump = b.Not(b.Nand(b.i[5], 0, allowed, 0));
                b.Wire(b.Not(b.Nand(oldJump, 0, notOp2, 0)), 0, pc, 2);       // never jump during STA
                b.Wire(b.i[7], 0, pc, 0);
                b.Wire(b.i[8], 0, pc, 1);
                b.Out(b.i[6], 0, 8);                                           // W = Op2
                return b.c;
            },
            ["PRESS"] = b =>
            {
                int s1 = b.Part("DFF"), s2 = b.Part("DFF"), s3 = b.Part("DFF");
                b.Wire(b.i[0], 0, s1, 0); b.Wire(s1, 0, s2, 0); b.Wire(s2, 0, s3, 0);
                foreach (var f in new[] { s1, s2, s3 }) b.Wire(b.i[1], 0, f, 1);
                int recent = b.Nand(b.Not(s1), 0, b.Not(s2), 0);              // s1 OR s2
                b.Out(b.Not(b.Nand(recent, 0, b.Not(s3), 0)), 0, 0);          // ... AND NOT s3
                return b.c;
            },
            ["CPU3"] = b =>
            {
                int opnd = b.Part("MUX4"), sum = b.Part("ADD4"), val = b.Part("MUX4"), reg = b.Part("REG4"), zero = b.Part("ZERO"), pc = b.Part("PC");
                for (int k = 0; k < 4; k++)
                {
                    b.Wire(b.i[k], 0, opnd, k); b.Wire(b.i[7 + k], 0, opnd, 4 + k);   // operand: N or M
                    b.Wire(reg, k, sum, k); b.Wire(opnd, k, sum, 4 + k);             // A + operand
                    b.Wire(opnd, k, val, k); b.Wire(sum, k, val, 4 + k);             // load operand or sum
                    b.Wire(val, k, reg, k);
                    b.Wire(reg, k, zero, k);
                    b.Wire(b.i[k], 0, pc, 3 + k);
                    b.Out(pc, k, k);
                    b.Out(reg, k, 4 + k);
                }
                int op0 = b.i[4], op1 = b.i[5], op2 = b.i[6];
                b.Wire(op2, 0, opnd, 8);
                b.Wire(Xor(b, op0, 0, op2, 0, out _), 0, val, 8);                  // ADD and ADA add
                int nOp0 = b.Not(op0), nOp1 = b.Not(op1), nOp2 = b.Not(op2);
                int load = Mux(b, nOp1, 0, Xor(b, op1, 0, op0, 0, out _), 0, op2, nOp2); // Op2 ? Op1^Op0 : !Op1
                b.Wire(load, 0, reg, 4);
                b.Wire(b.i[12], 0, reg, 5);
                int low = b.Nand(op0, 0, b.Not(zero), 0);                          // JMP, or JZ with A = 0
                int high = b.Not(b.Nand(op0, 0, b.i[11], 0));                      // JB with the button down
                b.Wire(b.Not(b.Nand(op1, 0, Mux(b, low, 0, high, 0, op2, nOp2), 0)), 0, pc, 2);
                b.Wire(b.i[12], 0, pc, 0);
                b.Wire(b.i[13], 0, pc, 1);
                int either = b.Nand(nOp1, 0, nOp0, 0);                             // Op1 OR Op0
                b.Out(b.Not(b.Nand(op2, 0, b.Not(either), 0)), 0, 8);              // W only for STA
                return b.c;
            },
        };

        static int Xor(Board b, int a, int ap, int c, int cp, out int first)
        {
            first = b.Nand(a, ap, c, cp);
            return b.Nand(b.Nand(a, ap, first, 0), 0, b.Nand(c, cp, first, 0), 0);
        }

        static void Latch(Board b, int setLow, int resetLow, out int q)
        {
            q = b.Part(PartTypes.Nand);
            int qb = b.Part(PartTypes.Nand);
            b.Wire(setLow, 0, q, 0); b.Wire(qb, 0, q, 1);
            b.Wire(resetLow, 0, qb, 0); b.Wire(q, 0, qb, 1);
        }

        static int Mux(Board b, int a, int ap, int c, int cp, int sel, int notSel) =>
            b.Nand(b.Nand(a, ap, notSel, 0), 0, b.Nand(c, cp, sel, 0), 0);

        static int[] FlipFlops(Board b, int clk)
        {
            var ff = new int[4];
            for (int k = 0; k < 4; k++) { ff[k] = b.Part("DFF"); b.Wire(clk, 0, ff[k], 1); }
            return ff;
        }

        /// <summary>Q + 1 from four flip-flops, as (part, pin) pairs.</summary>
        static (int part, int pin)[] Increment(Board b, int[] ff)
        {
            var next = new (int part, int pin)[4];
            next[0] = (b.Not(ff[0]), 0);
            int carry = ff[0];
            for (int k = 1; k < 4; k++)
            {
                next[k] = (Xor(b, ff[k], 0, carry, 0, out int both), 0);
                if (k < 3) carry = b.Not(both);
            }
            return next;
        }

        /// <summary>Feeds each flip-flop its next value ANDed with NOT Reset.</summary>
        static void ResetGate(Board b, int[] ff, (int part, int pin)[] next, int reset)
        {
            int notReset = b.Not(reset);
            for (int k = 0; k < 4; k++) b.Wire(b.Not(b.Nand(next[k].part, next[k].pin, notReset, 0)), 0, ff[k], 0);
        }

        static SaveData Build(out string report)
        {
            var save = new SaveData();
            var catalog = new PartCatalog();
            var log = new StringBuilder("Silicon Forge: unlocked all contracts with reference designs.\n");

            foreach (var level in Campaign.Levels)
            {
                if (level.workshop) continue;
                if (!Designs.TryGetValue(level.id, out var design))
                {
                    log.AppendLine($"  {level.id}: no reference design, skipped");
                    continue;
                }
                var circuit = design(new Board(level));
                var netlist = Netlist.Compile(circuit, catalog, level.inputs.Length, level.outputs.Length);
                var result = TestBench.Run(netlist, level);
                if (!result.AllPassed) log.AppendLine($"  {level.id}: reference FAILED {result.passed}/{result.total}");

                var progress = save.Progress(level.id);
                progress.solved = true;
                progress.bestGates = netlist.GateCount;
                progress.stars = Campaign.Stars(level, netlist.GateCount);

                var chip = new ChipDefinition
                {
                    id = level.chipId,
                    name = level.chipName,
                    inputNames = new List<string>(level.inputs),
                    outputNames = new List<string>(level.outputs),
                    circuit = circuit
                };
                save.SetChip(chip);
                catalog.SetChip(chip);
                log.AppendLine($"  {level.id}: {netlist.GateCount} gates (par {level.par})");
            }

            // Every workshop except the last is marked as built, so the whole campaign is open.
            var lastWorkshop = Campaign.Levels.FindLast(l => l.workshop);
            foreach (var level in Campaign.Levels)
            {
                if (!level.workshop || level == lastWorkshop) continue;
                var progress = save.Progress(level.id);
                progress.solved = true;
                progress.stars = 3;
            }
            // Mark the story as read so testing isn't interrupted by log cards. The ending still plays after Press Start.
            save.MarkSeen(Story.Prologue.id);
            foreach (var level in Campaign.Levels) save.MarkSeen(level.id);
            save.machinePsuWatts = 100;
            save.machineCooler = 2;
            report = log.ToString();
            return save;
        }
    }
}
