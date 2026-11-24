using System.Collections.Generic;
using System.Linq;

namespace SiliconForge.Core
{
    /// <summary>
    /// Turns a failed test run into plain words about the first case that went wrong,
    /// e.g. "With A = 1 and B = 0, Out should be 0, but your circuit gives 1."
    /// </summary>
    public static class TestExplainer
    {
        public const string Unstable =
            "Your circuit never settles: a loop of gates keeps flipping. Break the loop and run the tests again.";

        /// <summary>The first failing case (a truth-table row or a sequence step), or -1 if everything passed.</summary>
        public static int FirstFailure(TestReport report)
        {
            foreach (var key in report.cases.Keys.OrderBy(k => k))
                if (!report.cases[key].Pass) return key;
            return -1;
        }

        public static string Explain(LevelSpec level, TestReport report)
        {
            if (report.unstable) return Unstable;
            int first = FirstFailure(report);
            if (first < 0) return null;

            var result = report.cases[first];
            string unit = level.IsSequential ? "steps" : "cases";
            string summary = $"{report.passed} of {report.total} {unit} pass.";
            string outputs = WrongOutputs(level, result);

            if (!level.IsSequential)
                return $"{summary} With {Inputs(level, result.inputs)}, {outputs}";

            string when = first == 0
                ? "At step 1"
                : $"At step {first + 1}, when {Changes(level, level.sequence[first - 1].inputs, result.inputs)}";
            return $"{summary} {when}, {outputs}";
        }

        /// <summary>"A = 1 and B = 0", using number columns where the contract groups its pins.</summary>
        static string Inputs(LevelSpec level, int bits)
        {
            var parts = Columns(level.inputs, level.inputGroups).Select(c => $"{c.name} = {c.Format(bits)}").ToList();
            return Join(parts);
        }

        /// <summary>What changed since the step before: "Clk rises", "D becomes 1", "Op becomes ADD".</summary>
        static string Changes(LevelSpec level, int before, int after)
        {
            var changes = new List<string>();
            foreach (var c in Columns(level.inputs, level.inputGroups))
            {
                if (((before ^ after) & c.mask) == 0) continue;
                bool singlePin = c.group == null || c.group.width == 1 && c.group.labels == null;
                if (singlePin && c.name == "Clk") changes.Add((after & c.mask) != 0 ? "Clk rises" : "Clk falls");
                else changes.Add($"{c.name} becomes {c.Format(after)}");
            }
            if (changes.Count == 0) return "nothing changes on the inputs";
            if (changes.Count > 3) return Join(changes.Take(3).ToList()) + " (and more)";
            return Join(changes);
        }

        /// <summary>"Out should be 0, but your circuit gives 1." or a list when several outputs are wrong.</summary>
        static string WrongOutputs(LevelSpec level, CaseResult result)
        {
            int wrong = (result.expected ^ result.actual) & result.mask;
            var cols = Columns(level.outputs, level.outputGroups).Where(c => (wrong & c.mask) != 0).ToList();
            if (cols.Count == 1)
                return $"{cols[0].name} should be {cols[0].Format(result.expected)}, but your circuit gives {cols[0].Format(result.actual)}.";
            var parts = cols.Select(c => $"{c.name} should be {c.Format(result.expected)} (yours is {c.Format(result.actual)})").ToList();
            return Join(parts) + ".";
        }

        static string Join(List<string> items)
        {
            if (items.Count <= 1) return items.Count == 0 ? "" : items[0];
            return string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
        }

        struct Column
        {
            public string name;
            public int mask;
            public BitGroup group;
            public string Format(int bits) => group != null ? group.Format(bits) : (bits & mask) != 0 ? "1" : "0";
        }

        static List<Column> Columns(string[] names, BitGroup[] groups)
        {
            var cols = new List<Column>();
            if (groups == null)
                for (int i = 0; i < names.Length; i++) cols.Add(new Column { name = names[i], mask = 1 << i });
            else
                foreach (var g in groups) cols.Add(new Column { name = g.name, mask = g.Mask, group = g });
            return cols;
        }
    }
}
