using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SiliconForge.Core;

// These tests only use the pure C# core, so they run both in Unity's Test Runner (EditMode)
// and outside Unity with `dotnet test Tests/Core` (see README).
namespace SiliconForge.Tests
{
    /// <summary>Every chip contract certified once with its reference design, shared by all tests.</summary>
    static class Certified
    {
        static PartCatalog catalog;
        static List<ReferenceDesigns.Certified> results;

        public static PartCatalog Catalog { get { Ensure(); return catalog; } }
        public static List<ReferenceDesigns.Certified> Results { get { Ensure(); return results; } }

        static void Ensure()
        {
            if (catalog != null) return;
            catalog = new PartCatalog();
            results = ReferenceDesigns.CertifyAll(catalog);
        }

        /// <summary>A certified chip as a netlist, wired the way the workshops use it.</summary>
        public static Netlist Chip(string id)
        {
            var info = Catalog.Get(id);
            return Netlist.Compile(info.Chip.circuit, Catalog, info.Inputs, info.Outputs);
        }

        public static int[] Program(string source, int processor)
        {
            var errors = new List<AssemblyError>();
            var rom = Assembler.Assemble(source, errors, processor);
            Assert.That(errors, Is.Empty, "the test program should assemble: " + string.Join("; ", errors));
            return rom;
        }
    }

    public class ContractTests
    {
        static IEnumerable<string> ChipContracts() => Campaign.Levels.Where(l => !l.workshop).Select(l => l.id);

        [TestCaseSource(nameof(ChipContracts))]
        public void ReferenceDesignPassesAtExactlyPar(string id)
        {
            var result = Certified.Results.SingleOrDefault(r => r.level.id == id);
            Assert.That(result, Is.Not.Null, $"{id} has no reference design");
            Assert.That(result.report.unstable, Is.False, $"{id} never settles");
            Assert.That(result.report.passed, Is.EqualTo(result.report.total), $"{id} passes {result.report.passed}/{result.report.total}");
            Assert.That(result.gates, Is.EqualTo(result.level.par), $"{id} uses {result.gates} gates, par is {result.level.par}");
        }

        [Test]
        public void EmptyBoardFailsAnd()
        {
            var level = Campaign.Get("AND");
            var board = new ReferenceDesigns.Board(level);
            var report = TestBench.Run(Netlist.Compile(board.c, Certified.Catalog, 2, 1), level);
            Assert.That(report.AllPassed, Is.False);
        }

        [Test]
        public void DLatchIsNotAFlipFlop()
        {
            var level = Campaign.Get("DFF");
            var b = new ReferenceDesigns.Board(level);
            int latch = b.Part("DLATCH");
            b.Wire(b.i[0], 0, latch, 0);
            b.Wire(b.i[1], 0, latch, 1);
            b.Out(latch, 0, 0);
            Assert.That(TestBench.Run(Netlist.Compile(b.c, Certified.Catalog, 2, 1), level).AllPassed, Is.False);
        }

        [Test]
        public void PlainFlipFlopIsNotAPressDetector()
        {
            var level = Campaign.Get("PRESS");
            Assert.That(TestBench.Run(LeakyPressDetector(), level).AllPassed, Is.False);
        }

        /// <summary>P = the button sampled once: it stays 1 for as long as the button is held.</summary>
        public static Netlist LeakyPressDetector()
        {
            var b = new ReferenceDesigns.Board(Campaign.Get("PRESS"));
            int ff = b.Part("DFF");
            b.Wire(b.i[0], 0, ff, 0);
            b.Wire(b.i[1], 0, ff, 1);
            b.Out(ff, 0, 0);
            return Netlist.Compile(b.c, Certified.Catalog, 2, 1);
        }
    }

    public class GoalTests
    {
        const string Countdown = "LDI 9\nloop: ADD 15\nJZ done\nJMP loop\ndone: JMP done";
        const string Smiley = "LDI 9\nSTA 0\nSTA 2\nLDI 6\nSTA 3\nLDI 0\nSTA 1\nh: JMP h";
        const string MovingLight = "LDI 0\nSTA 1\nSTA 2\nSTA 3\nLDI 1\ndraw: STA 0\nwait: JB move\nJMP wait\nmove: ADA 0\nJZ wrap\nJMP draw\nwrap: LDI 1\nJMP draw";

        [Test]
        public void BootCountdownPasses() =>
            Assert.That(CpuGoals.CheckCountdown(Certified.Chip("CPU"), Certified.Program(Countdown, 1), out var why), Is.True, why);

        [TestCase("LDI 0\nloop: ADD 1\nJMP loop", TestName = "Boot rejects counting up")]
        [TestCase("LDI 9\nloop: ADD 15\nJZ done\nJMP loop\ndone: LDI 5\nh: JMP h", TestName = "Boot rejects not stopping at 0")]
        public void BootRejects(string program) =>
            Assert.That(CpuGoals.CheckCountdown(Certified.Chip("CPU"), Certified.Program(program, 1), out _), Is.False);

        [Test]
        public void FirstPictureSmileyPasses() =>
            Assert.That(ComputerGoals.CheckPicture(Certified.Chip("CPU2"), Certified.Chip("RAM4"), Certified.Program(Smiley, 2), ComputerGoals.Smiley, out var why), Is.True, why);

        [TestCase("LDI 9\nSTA 0\nSTA 2\nLDI 0\nSTA 1\nSTA 3\nh: JMP h", TestName = "First Picture rejects a missing mouth")]
        [TestCase("LDI 9\nSTA 0\nSTA 2\nLDI 6\nSTA 3\nh: JMP h", TestName = "First Picture rejects an uncleared row")]
        [TestCase("LDI 9\nSTA 0\nSTA 2\nLDI 6\nSTA 3\nLDI 0\nSTA 1\nl: LDI 15\nSTA 1\nLDI 0\nSTA 1\nJMP l", TestName = "First Picture rejects a blinking picture")]
        public void FirstPictureRejects(string program) =>
            Assert.That(ComputerGoals.CheckPicture(Certified.Chip("CPU2"), Certified.Chip("RAM4"), Certified.Program(program, 2), ComputerGoals.Smiley, out _), Is.False);

        [Test]
        public void PressStartMovingLightPasses() =>
            Assert.That(ComputerGoals.CheckMovingLight(Certified.Chip("CPU3"), Certified.Chip("RAM4"), Certified.Chip("PRESS"), Certified.Program(MovingLight, 3), out var why), Is.True, why);

        [TestCase("LDI 1\ndraw: STA 0\nwait: JB move\nJMP wait\nmove: ADA 0\nJZ wrap\nJMP draw\nwrap: LDI 1\nJMP draw", TestName = "Press Start rejects uncleared rows")]
        [TestCase("LDI 0\nSTA 1\nSTA 2\nSTA 3\nLDI 1\ndraw: STA 0\nADA 0\nJZ wrap\nJMP draw\nwrap: LDI 1\nJMP draw", TestName = "Press Start rejects moving without presses")]
        public void PressStartRejects(string program) =>
            Assert.That(ComputerGoals.CheckMovingLight(Certified.Chip("CPU3"), Certified.Chip("RAM4"), Certified.Chip("PRESS"), Certified.Program(program, 3), out _), Is.False);

        [Test]
        public void PressStartCatchesALeakyPressDetector() =>
            Assert.That(ComputerGoals.CheckMovingLight(Certified.Chip("CPU3"), Certified.Chip("RAM4"), ContractTests.LeakyPressDetector(), Certified.Program(MovingLight, 3), out _), Is.False);

        [Test]
        public void HeartbeatCounterCountsAndWraps()
        {
            var counter = Certified.Chip("COUNT4");
            var state = counter.CreateState();
            TestBench.Probe(counter, state, 0b10); TestBench.Probe(counter, state, 0b11);
            TestBench.Probe(counter, state, 0b10); TestBench.Probe(counter, state, 0b00);
            for (int k = 1; k <= 20; k++)
            {
                TestBench.Probe(counter, state, 0b01);
                Assert.That(TestBench.Probe(counter, state, 0b00), Is.EqualTo(k % 16));
            }
        }

        [Test]
        public void FirstLightAdderAddsEveryPair()
        {
            var adder = Certified.Chip("ADD4");
            var state = adder.CreateState();
            for (int a = 0; a < 16; a++)
                for (int b = 0; b < 16; b++)
                    Assert.That(TestBench.Probe(adder, state, a | (b << 4)), Is.EqualTo(a + b), $"{a} + {b}");
        }
    }

    public class AssemblerTests
    {
        static List<AssemblyError> Errors(string source, int processor)
        {
            var errors = new List<AssemblyError>();
            Assembler.Assemble(source, errors, processor);
            return errors;
        }

        [Test]
        public void LabelsAndCommentsAssemble()
        {
            var errors = new List<AssemblyError>();
            var rom = Assembler.Assemble("; comment\nstart: LDI 3 # another\n  JMP start", errors);
            Assert.That(errors, Is.Empty);
            Assert.That(rom[0], Is.EqualTo(Isa.Encode(Isa.Ldi, 3)));
            Assert.That(rom[1], Is.EqualTo(Isa.Encode(Isa.Jmp, 0)));
        }

        [TestCase("FOO 3", 1, TestName = "Unknown instruction")]
        [TestCase("ADD 16", 1, TestName = "Number out of range")]
        [TestCase("JMP nowhere", 1, TestName = "Missing label")]
        [TestCase("ADD", 1, TestName = "Missing operand")]
        [TestCase("STA 1", 1, TestName = "STA needs Processor II")]
        [TestCase("LDA 1", 2, TestName = "LDA needs Processor III")]
        [TestCase("STA 4", 2, TestName = "Rows are 0 to 3")]
        [TestCase("x: LDI 1\nx: LDI 2", 1, TestName = "Duplicate label")]
        public void Rejects(string source, int processor) => Assert.That(Errors(source, processor), Has.Count.EqualTo(1));

        [Test]
        public void SeventeenInstructionsDoNotFit()
        {
            string program = string.Join("\n", Enumerable.Repeat("ADD 1", 17));
            Assert.That(Errors(program, 1), Has.Count.EqualTo(1));
        }
    }

    public class SimulatorTests
    {
        [Test]
        public void NandLatchHoldsItsValue()
        {
            var c = new CircuitData();
            int s = c.AddPart(PartTypes.Input, 0, 0, 0, "S").id, r = c.AddPart(PartTypes.Input, 0, 0, 1, "R").id;
            int q = c.AddPart(PartTypes.Output, 0, 0, 0, "Q").id;
            int a = c.AddPart(PartTypes.Nand, 0, 0).id, b = c.AddPart(PartTypes.Nand, 0, 0).id;
            c.Connect(s, 0, a, 0); c.Connect(b, 0, a, 1); c.Connect(r, 0, b, 1); c.Connect(a, 0, b, 0); c.Connect(a, 0, q, 0);
            var net = Netlist.Compile(c, new PartCatalog(), 2, 1);
            var state = net.CreateState();
            Assert.That(net.HasCycles, Is.True);
            Assert.That(TestBench.Probe(net, state, 0b10), Is.EqualTo(1), "set (active low)");
            Assert.That(TestBench.Probe(net, state, 0b11), Is.EqualTo(1), "hold");
            Assert.That(TestBench.Probe(net, state, 0b01), Is.EqualTo(0), "reset");
            Assert.That(TestBench.Probe(net, state, 0b11), Is.EqualTo(0), "hold");
        }

        [Test]
        public void SelfFeedingNandNeverSettles()
        {
            var c = new CircuitData();
            int g = c.AddPart(PartTypes.Nand, 0, 0).id;
            c.Connect(g, 0, g, 0); c.Connect(g, 0, g, 1);
            var net = Netlist.Compile(c, new PartCatalog(), 0, 0);
            Assert.That(net.Evaluate(net.CreateState()), Is.False);
        }

        [Test]
        public void GateCostCountsNestedChips()
        {
            Assert.That(Certified.Catalog.GateCost("NOT"), Is.EqualTo(1));
            Assert.That(Certified.Catalog.GateCost("FADD"), Is.EqualTo(9));
            Assert.That(Certified.Catalog.GateCost("ADD4"), Is.EqualTo(32));
        }
    }

    public class HelpTests
    {
        static TestReport RunEmptyBoard(string id)
        {
            var level = Campaign.Get(id);
            var board = new ReferenceDesigns.Board(level);
            return TestBench.Run(Netlist.Compile(board.c, Certified.Catalog, level.inputs.Length, level.outputs.Length), level);
        }

        [Test]
        public void EveryChipContractHasThreeDifferentHints()
        {
            foreach (var level in Campaign.Levels.Where(l => !l.workshop))
            {
                Assert.That(level.hints, Has.Length.EqualTo(3), $"{level.id} needs three hints");
                Assert.That(level.hints, Has.All.Not.Empty, $"{level.id} has an empty hint");
                Assert.That(level.hints.Distinct().Count(), Is.EqualTo(3), $"{level.id} repeats a hint");
            }
        }

        [Test]
        public void ExplainsTheFirstWrongRow() =>
            Assert.That(TestExplainer.Explain(Campaign.Get("AND"), RunEmptyBoard("AND")),
                Is.EqualTo("3 of 4 cases pass. With A = 1 and B = 1, Out should be 1, but your circuit gives 0."));

        [Test]
        public void ExplainsGroupedPinsAsNumbers() =>
            Assert.That(TestExplainer.Explain(Campaign.Get("MUX4"), RunEmptyBoard("MUX4")),
                Does.EndWith("With A = 1, B = 0 and Sel = 0, Out should be 1, but your circuit gives 0."));

        [Test]
        public void ListsEveryWrongOutput()
        {
            string text = TestExplainer.Explain(Campaign.Get("HADD"), RunEmptyBoard("HADD"));
            Assert.That(text, Does.EndWith("With A = 1 and B = 0, Sum should be 1, but your circuit gives 0."));
            var level = Campaign.Get("DEC2");
            Assert.That(TestExplainer.Explain(level, RunEmptyBoard("DEC2")),
                Does.EndWith("With A0 = 0 and A1 = 0, Y0 should be 1, but your circuit gives 0."));
        }

        [Test]
        public void ExplainsASequenceStepByWhatChanged()
        {
            var level = Campaign.Get("DFF");
            var b = new ReferenceDesigns.Board(level);
            int latch = b.Part("DLATCH");
            b.Wire(b.i[0], 0, latch, 0);
            b.Wire(b.i[1], 0, latch, 1);
            b.Out(latch, 0, 0);
            var report = TestBench.Run(Netlist.Compile(b.c, Certified.Catalog, 2, 1), level);
            string text = TestExplainer.Explain(level, report);
            int step = TestExplainer.FirstFailure(report);
            Assert.That(text, Does.StartWith($"{report.passed} of {report.total} steps pass. At step {step + 1}, when "));
            Assert.That(text, Does.Contain("Q should be"));
        }

        [Test]
        public void SaysNothingWhenEverythingPasses()
        {
            var certified = Certified.Results.First(r => r.level.id == "XOR");
            Assert.That(TestExplainer.Explain(certified.level, certified.report), Is.Null);
        }
    }

    public class CampaignTests
    {
        [Test]
        public void IdsAreUniqueAndNumbersRunInOrder()
        {
            Assert.That(Campaign.Levels.Select(l => l.id).Distinct().Count(), Is.EqualTo(Campaign.Levels.Count));
            for (int k = 0; k < Campaign.Levels.Count; k++) Assert.That(Campaign.Levels[k].number, Is.EqualTo(k + 1));
        }

        [Test]
        public void ErasNeverGoBackwards()
        {
            for (int k = 1; k < Campaign.Levels.Count; k++)
                Assert.That(Campaign.Levels[k].era, Is.GreaterThanOrEqualTo(Campaign.Levels[k - 1].era));
            Assert.That(Campaign.Levels.Last().era, Is.EqualTo(Campaign.Eras.Count - 1));
        }

        [Test]
        public void PartsOnlyComeFromEarlierContracts()
        {
            var available = new HashSet<string> { PartTypes.Nand };
            foreach (var level in Campaign.Levels)
            {
                foreach (var part in level.parts)
                    Assert.That(available, Does.Contain(part), $"{level.id} offers {part} before it's been built");
                if (!level.workshop) available.Add(level.chipId);
            }
        }

        [Test]
        public void EveryChipContractHasExactlyOneKindOfTest()
        {
            foreach (var level in Campaign.Levels.Where(l => !l.workshop))
                Assert.That(level.truth != null ^ level.sequence != null, Is.True, $"{level.id} needs a truth table or a sequence");
        }

        [Test]
        public void TestSequencesStayWithinThePins()
        {
            foreach (var level in Campaign.Levels.Where(l => l.IsSequential))
            {
                int inputMask = (1 << level.inputs.Length) - 1, outputMask = (1 << level.outputs.Length) - 1;
                foreach (var step in level.sequence)
                {
                    Assert.That(step.inputs & ~inputMask, Is.Zero, $"{level.id} drives a pin that doesn't exist");
                    Assert.That(step.mask & ~outputMask, Is.Zero, $"{level.id} checks a pin that doesn't exist");
                }
            }
        }

        [Test]
        public void EveryContractHasAStationLogEntry()
        {
            foreach (var level in Campaign.Levels)
            {
                var entry = Story.ForLevel(level);
                Assert.That(entry, Is.Not.Null, $"{level.id} has no log entry");
                Assert.That(entry.day, Is.GreaterThan(Story.Prologue.day).And.LessThan(Story.Ending.day));
            }
        }

        [Test]
        public void LogDaysNeverGoBackwards()
        {
            int last = Story.Prologue.day;
            foreach (var level in Campaign.Levels)
            {
                int day = Story.ForLevel(level).day;
                Assert.That(day, Is.GreaterThanOrEqualTo(last), $"{level.id} is dated before the contract before it");
                last = day;
            }
        }
    }
}
