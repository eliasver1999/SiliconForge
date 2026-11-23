using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>
    /// A circuit flattened down to plain NAND gates and numbered nets.
    /// Every net holds a 64-bit word, so one Evaluate call runs 64 independent test cases at once
    /// (bit k of every net belongs to case k). The live board just uses bit 0.
    /// </summary>
    public sealed class Netlist
    {
        public const int Zero = 0; // net 0 is tied to logic 0

        public int NetCount { get; private set; }
        public int GateCount => gateOut.Length;
        /// <summary>Longest chain of gates a signal passes through (the chip's delay).</summary>
        public int Depth { get; private set; }
        /// <summary>True when gates feed back into themselves (latches, oscillators).</summary>
        public bool HasCycles { get; private set; }
        public int[] InputNets { get; private set; }
        public int[] OutputNets { get; private set; }

        int[] gateA, gateB, gateOut, order;
        Dictionary<long, int> pinNets;

        Netlist() { }

        public static long Key(int part, int pin) => ((long)part << 20) | (uint)pin;

        /// <summary>The net driven by an output pin of a top-level part.</summary>
        public int NetOfOutputPin(int partId, int pin) =>
            pinNets != null && pinNets.TryGetValue(Key(partId, pin), out var net) ? net : Zero;

        public ulong[] CreateState() => new ulong[Math.Max(1, NetCount)];

        /// <summary>Settles every gate. Returns false if the circuit never settles (it oscillates).</summary>
        public bool Evaluate(ulong[] s, int maxPasses = 256)
        {
            s[Zero] = 0UL;
            if (!HasCycles)
            {
                for (int k = 0; k < order.Length; k++)
                {
                    int g = order[k];
                    s[gateOut[g]] = ~(s[gateA[g]] & s[gateB[g]]);
                }
                return true;
            }

            for (int pass = 0; pass < maxPasses; pass++)
            {
                bool changed = false;
                for (int k = 0; k < order.Length; k++)
                {
                    int g = order[k];
                    ulong v = ~(s[gateA[g]] & s[gateB[g]]);
                    if (s[gateOut[g]] != v) { s[gateOut[g]] = v; changed = true; }
                }
                if (!changed) return true;
            }
            return false;
        }

        public static Netlist Compile(CircuitData circuit, PartCatalog catalog, int inputCount, int outputCount)
        {
            var builder = new Builder(catalog);
            builder.NewNet(); // Zero
            var inputs = new int[inputCount];
            var outputs = new int[outputCount];
            var pins = new Dictionary<long, int>();
            builder.Flatten(circuit, 0, null, inputs, outputs, pins);
            return builder.Finish(inputs, outputs, pins);
        }

        void BuildOrder()
        {
            int g = gateOut.Length;
            var producer = new int[NetCount];
            for (int i = 0; i < producer.Length; i++) producer[i] = -1;
            for (int i = 0; i < g; i++) producer[gateOut[i]] = i;

            var indegree = new int[g];
            var consumers = new List<int>[g];
            for (int i = 0; i < g; i++)
            {
                AddEdge(producer[gateA[i]], i);
                AddEdge(producer[gateB[i]], i);
            }

            void AddEdge(int from, int to)
            {
                if (from < 0) return;
                indegree[to]++;
                (consumers[from] ??= new List<int>()).Add(to);
            }

            var queue = new Queue<int>();
            for (int i = 0; i < g; i++) if (indegree[i] == 0) queue.Enqueue(i);

            var sorted = new List<int>(g);
            var level = new int[g];
            int maxLevel = -1;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                sorted.Add(i);
                if (level[i] > maxLevel) maxLevel = level[i];
                if (consumers[i] == null) continue;
                foreach (int c in consumers[i])
                {
                    if (level[i] + 1 > level[c]) level[c] = level[i] + 1;
                    if (--indegree[c] == 0) queue.Enqueue(c);
                }
            }

            Depth = maxLevel + 1;
            HasCycles = sorted.Count < g;
            if (HasCycles)
                for (int i = 0; i < g; i++) if (indegree[i] > 0) sorted.Add(i);
            order = sorted.ToArray();
        }

        /// <summary>Flattens nested chips with a union-find over nets, so pin-to-pin aliases cost nothing at run time.</summary>
        sealed class Builder
        {
            readonly PartCatalog catalog;
            readonly List<int> parent = new List<int>();
            readonly List<int> ga = new List<int>(), gb = new List<int>(), go = new List<int>();

            public Builder(PartCatalog catalog) { this.catalog = catalog; }

            public int NewNet() { parent.Add(parent.Count); return parent.Count - 1; }

            int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (b == Zero) parent[a] = Zero; // Zero always stays a root
                else parent[b] = a;
            }

            public void Flatten(CircuitData c, int depth, int[] externalInputs, int[] topInputs, int[] outputs, Dictionary<long, int> pinNets)
            {
                if (depth > 64) throw new InvalidOperationException("Chips are nested too deeply. Does a chip contain itself?");

                var outNet = new Dictionary<long, int>();
                foreach (var p in c.parts)
                {
                    var info = catalog.Get(p.type);
                    for (int i = 0; i < info.Outputs; i++) outNet[Key(p.id, i)] = NewNet();
                }

                var driver = new Dictionary<long, int>();
                foreach (var w in c.wires)
                    if (outNet.TryGetValue(Key(w.fromPart, w.fromPin), out var net))
                        driver[Key(w.toPart, w.toPin)] = net;

                int InputNet(int part, int pin) => driver.TryGetValue(Key(part, pin), out var n) ? n : Zero;

                foreach (var p in c.parts)
                {
                    switch (p.type)
                    {
                        case PartTypes.Nand:
                            ga.Add(InputNet(p.id, 0));
                            gb.Add(InputNet(p.id, 1));
                            go.Add(outNet[Key(p.id, 0)]);
                            break;

                        case PartTypes.Input:
                        {
                            int net = outNet[Key(p.id, 0)];
                            if (externalInputs != null)
                                Union(net, p.index < externalInputs.Length ? externalInputs[p.index] : Zero);
                            else if (topInputs != null && p.index < topInputs.Length)
                                topInputs[p.index] = net;
                            break;
                        }

                        case PartTypes.Output:
                            if (outputs != null && p.index < outputs.Length) outputs[p.index] = InputNet(p.id, 0);
                            break;

                        default:
                        {
                            var info = catalog.Get(p.type);
                            var inner = new int[info.Inputs];
                            for (int i = 0; i < inner.Length; i++) inner[i] = InputNet(p.id, i);
                            var innerOut = new int[info.Outputs];
                            Flatten(info.Chip.circuit, depth + 1, inner, null, innerOut, null);
                            for (int j = 0; j < innerOut.Length; j++) Union(outNet[Key(p.id, j)], innerOut[j]);
                            break;
                        }
                    }
                }

                if (pinNets != null)
                    foreach (var kv in outNet) pinNets[kv.Key] = kv.Value;
            }

            public Netlist Finish(int[] inputs, int[] outputs, Dictionary<long, int> pins)
            {
                var map = new Dictionary<int, int> { [Zero] = Zero };
                int Compact(int n)
                {
                    int root = Find(n);
                    if (!map.TryGetValue(root, out var id)) { id = map.Count; map[root] = id; }
                    return id;
                }

                int count = go.Count;
                var a = new int[count];
                var b = new int[count];
                var o = new int[count];
                for (int i = 0; i < count; i++)
                {
                    a[i] = Compact(ga[i]);
                    b[i] = Compact(gb[i]);
                    o[i] = Compact(go[i]);
                }

                var nl = new Netlist
                {
                    gateA = a, gateB = b, gateOut = o,
                    InputNets = Array.ConvertAll(inputs, Compact),
                    OutputNets = Array.ConvertAll(outputs, Compact),
                    pinNets = new Dictionary<long, int>()
                };
                foreach (var kv in pins) nl.pinNets[kv.Key] = Compact(kv.Value);

                // A gate must never drive the constant-0 net.
                int extra = map.Count;
                for (int i = 0; i < count; i++) if (o[i] == Zero) o[i] = extra++;
                nl.NetCount = extra;

                nl.BuildOrder();
                return nl;
            }
        }
    }
}
