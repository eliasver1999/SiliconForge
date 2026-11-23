using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    public struct CaseResult
    {
        public int inputs;
        public int expected;
        public int actual;
        /// <summary>Which output bits are checked. Steps that don't care about the outputs use 0.</summary>
        public int mask;
        public bool Pass => ((expected ^ actual) & mask) == 0;
    }

    public sealed class TestReport
    {
        public int total;
        public int passed;
        public bool unstable;
        public readonly Dictionary<int, CaseResult> cases = new Dictionary<int, CaseResult>();
        public bool AllPassed => passed == total && !unstable;
    }

    /// <summary>Runs every input combination of a level against a circuit, 64 cases per evaluation.</summary>
    public static class TestBench
    {
        public static TestReport Run(Netlist netlist, LevelSpec level)
        {
            if (level.IsSequential) return RunSequence(netlist, level);
            int inCount = level.inputs.Length;
            int outCount = level.outputs.Length;
            int total = 1 << inCount;
            int outMask = (1 << outCount) - 1;
            var report = new TestReport { total = total };

            for (int start = 0; start < total; start += 64)
            {
                int batch = Math.Min(64, total - start);
                var s = netlist.CreateState();
                for (int i = 0; i < inCount; i++)
                {
                    ulong bits = 0;
                    for (int k = 0; k < batch; k++)
                        if ((((start + k) >> i) & 1) != 0) bits |= 1UL << k;
                    s[netlist.InputNets[i]] = bits;
                }

                if (!netlist.Evaluate(s)) report.unstable = true;

                for (int k = 0; k < batch; k++)
                {
                    int c = start + k;
                    int actual = 0;
                    for (int j = 0; j < outCount; j++)
                        if (((s[netlist.OutputNets[j]] >> k) & 1UL) != 0) actual |= 1 << j;
                    var result = new CaseResult { inputs = c, expected = level.truth(c) & outMask, actual = actual, mask = outMask };
                    report.cases[c] = result;
                    if (result.Pass) report.passed++;
                }
            }
            return report;
        }

        /// <summary>Runs a level's steps in order on one circuit, so memory carries from step to step.</summary>
        static TestReport RunSequence(Netlist netlist, LevelSpec level)
        {
            var report = new TestReport { total = level.sequence.Length };
            var state = netlist.CreateState();
            for (int k = 0; k < level.sequence.Length; k++)
            {
                var step = level.sequence[k];
                for (int i = 0; i < netlist.InputNets.Length; i++)
                    state[netlist.InputNets[i]] = ((step.inputs >> i) & 1) != 0 ? ulong.MaxValue : 0UL;
                if (!netlist.Evaluate(state)) report.unstable = true;
                int actual = 0;
                for (int j = 0; j < netlist.OutputNets.Length; j++)
                    if ((state[netlist.OutputNets[j]] & 1UL) != 0) actual |= 1 << j;
                var result = new CaseResult { inputs = step.inputs, expected = step.expected, actual = actual, mask = step.mask };
                report.cases[k] = result;
                if (result.Pass) report.passed++;
            }
            return report;
        }

        /// <summary>Evaluates one input combination (bit i = input i) and returns the packed outputs.</summary>
        public static int Probe(Netlist netlist, ulong[] state, int inputBits)
        {
            for (int i = 0; i < netlist.InputNets.Length; i++)
                state[netlist.InputNets[i]] = ((inputBits >> i) & 1) != 0 ? ulong.MaxValue : 0UL;
            netlist.Evaluate(state);
            int result = 0;
            for (int j = 0; j < netlist.OutputNets.Length; j++)
                if ((state[netlist.OutputNets[j]] & 1UL) != 0) result |= 1 << j;
            return result;
        }
    }
}
