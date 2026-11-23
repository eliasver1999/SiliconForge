using System.Collections.Generic;

namespace SiliconForge.Core
{
    public sealed class LogEntry
    {
        /// <summary>"prologue", "ending", or a level id.</summary>
        public string id;
        public int day;
        /// <summary>Shown above the text, e.g. "ERA II · ARITHMETIC". Empty for a plain contract note.</summary>
        public string heading;
        public string text;
    }

    /// <summary>
    /// The story: an engineer left alone on Kestrel Station after a storm killed every computer aboard.
    /// The fabricator can still print one thing, a NAND gate, and the relief ship can't find the station
    /// without a working beacon. Each era opens with a log entry and each contract adds a short note.
    /// </summary>
    public static class Story
    {
        public const string Station = "Kestrel Station";

        public static readonly LogEntry Prologue = new LogEntry
        {
            id = "prologue", day = 1, heading = "STATION LOG",
            text = "Main computer: offline.\nComms: offline.\nFabricator: online. Feedstock: silicon.\n\n" +
                   "The storm took out everything with a chip in it. The crew left on the last shuttle; I stayed to keep the station breathing until the relief ship arrives. Without a computer to run the beacon, it won't find me.\n\n" +
                   "One blueprint survived in the fabricator's memory: a single NAND gate.\n\nIt's a start."
        };

        public static readonly LogEntry Ending = new LogEntry
        {
            id = "ending", day = 63, heading = "STATION LOG",
            text = "Relief ship Calder, this is Kestrel Station. I'm still here.\n\n" +
                   "The beacon is running on a computer I built from sand, one NAND gate at a time. It took sixty-three days, and it answers to a single red button.\n\n" +
                   "Comms: online."
        };

        /// <summary>The day each era begins. Contracts inside an era fall on the days after it.</summary>
        static readonly int[] EraDays = { 2, 9, 15, 21, 34, 47, 58 };

        static readonly string[] EraLogs =
        {
            "The fabricator can print NAND gates all day. Everything else I'll have to design myself, one gate at a time. Every design that works goes into its memory, so I never have to build anything twice.",
            "Logic is fine, but this station runs on numbers: the air mix, the power budget, the days until the relief ship's window. Time to teach sand to count.",
            "Found an empty test frame and a mainboard in cargo bay 2. The adder has a home. If I'm careful with power, the backup cells will carry it.",
            "The machine forgets everything the moment I let go of the switches. A real computer has to remember, and it needs a heartbeat to keep time.",
            "Today the parts stop being parts. A processor takes instructions and follows them on its own. The fabricator says its finer printing process halves the power per gate. Good: the cells are running low.",
            "Memory, a screen, and heat. The bay is already warm with one chip running. The next one will need proper cooling, and another process shrink.",
            "The relief ship's window opens in a few days. The beacon needs a computer that reacts to me: one button, one signal. Let's finish this.",
        };

        static readonly Dictionary<string, string> Notes = new Dictionary<string, string>
        {
            ["NOT"] = "First design: flip a signal. If a NAND can't do that alone, nothing else will work.",
            ["AND"] = "The inverter works, and the fabricator filed it as a part. One down, a computer to go.",
            ["OR"] = "The airlock alarm needs to know if either door is open. That's an OR.",
            ["XOR"] = "Exactly one, never both. Every adder I've ever read about starts with this gate.",
            ["HADD"] = "1 + 1 = 10. Out here even arithmetic feels like an achievement.",
            ["FADD"] = "Columns and carries, like adding on paper. Three bits in, two bits out.",
            ["ADD4"] = "Four columns: enough to add up a day of oxygen cycles. This one goes into the machine.",
            ["LIGHT"] = "Mount it, wire it to the backup cells, and hope I counted the gates right.",
            ["SR"] = "The first circuit that remembers. Set it, walk away, and it's still set when I come back.",
            ["DLATCH"] = "Remember only when told to. That's the difference between memory and a stuck switch.",
            ["MUX"] = "Choose between two signals. A small part, but every register will need one.",
            ["DFF"] = "From now on everything moves on the tick. This is how the machine will keep time.",
            ["REG"] = "Keep a value, or take a new one. A few of these will hold my numbers.",
            ["COUNT4"] = "A circuit that counts by itself. The station almost has a pulse again.",
            ["TICK"] = "Clock crystal salvaged from the dead navigation board. Let's see if the counter can keep up.",
            ["REG4"] = "A whole number in one register. The fabricator switched to the finer process overnight.",
            ["MUX4"] = "Pick one number of two. The processor's register will be fed through this.",
            ["ZERO"] = "Knowing when something has run out is the first real decision a machine can make.",
            ["PC"] = "A counter that can jump. It will point at the next instruction, forever.",
            ["CPU"] = "Every part in one chip. If this works, I stop building and start programming.",
            ["BOOT"] = "Pull the old chips, seat the processor, and write the first program this station has run in weeks.",
            ["DEC2"] = "Another process shrink came through. An address goes in, exactly one line comes out.",
            ["SEL4"] = "Four words, pick one. Reading memory is really just choosing.",
            ["RAM4"] = "Four words I can rewrite. Tiny, but it's memory a program can use.",
            ["CPU2"] = "One new instruction: write. The machine can finally leave marks.",
            ["PIXELS"] = "A 4x4 panel from the galley oven's timer. Sixteen pixels. Let's draw something friendly.",
            ["PRESS"] = "A button held down is a thousand presses to a fast clock. The beacon needs to hear just one.",
            ["CPU3"] = "Read memory, react to a button. The last processor I'll need.",
            ["PLAY"] = "The beacon console: one button, one light. If it answers my hand, it can answer the ship.",
        };

        /// <summary>The log for a level: the era's entry if it's the era's first contract, followed by the contract's note.</summary>
        public static LogEntry ForLevel(LevelSpec level)
        {
            var era = Campaign.Eras[level.era];
            int position = 0;
            LevelSpec firstInEra = null;
            foreach (var l in Campaign.Levels)
            {
                if (l.era != level.era) continue;
                firstInEra ??= l;
                if (l == level) break;
                position++;
            }

            Notes.TryGetValue(level.id, out var note);
            bool opensEra = level == firstInEra;
            string text = opensEra ? EraLogs[level.era] + (note != null ? "\n\n" + note : "") : note;
            if (string.IsNullOrEmpty(text)) return null;
            return new LogEntry
            {
                id = level.id,
                day = EraDays[level.era] + position * 2,
                heading = opensEra ? $"ERA {era.numeral}  ·  {era.name.ToUpperInvariant()}" : "",
                text = text
            };
        }
    }
}
