using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    [Serializable]
    public class LevelProgress
    {
        public string id;
        public bool solved;
        public int bestGates;
        public int stars;
        /// <summary>How many of the contract's hints the player has revealed. Hints never cost stars.</summary>
        public int hintsShown;
    }

    [Serializable]
    public class LevelWork
    {
        public string id;
        public CircuitData circuit;
    }

    [Serializable]
    public class SaveData
    {
        public int version = 1;
        public List<LevelProgress> progress = new List<LevelProgress>();
        public List<LevelWork> work = new List<LevelWork>();
        public List<ChipDefinition> chips = new List<ChipDefinition>();
        /// <summary>The supply in the player's machine, carried between workshop builds. 0 means none yet.</summary>
        public int machinePsuWatts;
        /// <summary>The last program the player flashed into ROM.</summary>
        public string program;
        /// <summary>The last program flashed in First Picture (Processor II).</summary>
        public string pixelsProgram;
        /// <summary>The last program flashed in Press Start (Processor III).</summary>
        public string playProgram;
        /// <summary>The cooler on the player's processor: -1 none fitted yet, 0 bare, 1 heatsink, 2 fan cooler.</summary>
        public int machineCooler = -1;
        /// <summary>Station log entries the player has already read ("prologue", level ids, "ending").</summary>
        public List<string> seenLogs = new List<string>();

        public bool HasSeen(string logId) => seenLogs != null && seenLogs.Contains(logId);

        public void MarkSeen(string logId)
        {
            seenLogs ??= new List<string>();
            if (!seenLogs.Contains(logId)) seenLogs.Add(logId);
        }

        public LevelProgress Progress(string id)
        {
            var p = progress.Find(x => x.id == id);
            if (p == null) { p = new LevelProgress { id = id }; progress.Add(p); }
            return p;
        }

        public bool IsSolved(string id)
        {
            var p = progress.Find(x => x.id == id);
            return p != null && p.solved;
        }

        public CircuitData Work(string id) => work.Find(x => x.id == id)?.circuit;

        public void SetWork(string id, CircuitData circuit)
        {
            var w = work.Find(x => x.id == id);
            if (w == null) work.Add(new LevelWork { id = id, circuit = circuit });
            else w.circuit = circuit;
        }

        public void SetChip(ChipDefinition def)
        {
            chips.RemoveAll(c => c.id == def.id);
            chips.Add(def);
        }

        public int SolvedCount
        {
            get
            {
                int n = 0;
                foreach (var p in progress) if (p.solved) n++;
                return n;
            }
        }
    }
}
