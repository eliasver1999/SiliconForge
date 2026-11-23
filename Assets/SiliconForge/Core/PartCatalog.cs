using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>A chip the player built and certified. Its circuit uses IN/OUT parts for its pins.</summary>
    [Serializable]
    public class ChipDefinition
    {
        public string id;
        public string name;
        public List<string> inputNames = new List<string>();
        public List<string> outputNames = new List<string>();
        public CircuitData circuit = new CircuitData();
    }

    public sealed class PartInfo
    {
        public string Type;
        public string Name;
        public int Inputs;
        public int Outputs;
        public string[] InputNames;
        public string[] OutputNames;
        public ChipDefinition Chip;

        public bool IsChip => Chip != null;
    }

    /// <summary>Knows every part type: the NAND primitive, IN/OUT pins and the player's chips.</summary>
    public sealed class PartCatalog
    {
        static readonly PartInfo NandInfo = new PartInfo
        {
            Type = PartTypes.Nand, Name = "NAND", Inputs = 2, Outputs = 1,
            InputNames = new[] { "A", "B" }, OutputNames = new[] { "Q" }
        };

        static readonly PartInfo InputInfo = new PartInfo
        {
            Type = PartTypes.Input, Name = "Input", Inputs = 0, Outputs = 1,
            InputNames = new string[0], OutputNames = new[] { "Q" }
        };

        static readonly PartInfo OutputInfo = new PartInfo
        {
            Type = PartTypes.Output, Name = "Output", Inputs = 1, Outputs = 0,
            InputNames = new[] { "D" }, OutputNames = new string[0]
        };

        readonly Dictionary<string, PartInfo> chips = new Dictionary<string, PartInfo>();
        readonly Dictionary<string, int> gateCostCache = new Dictionary<string, int>();

        public IEnumerable<PartInfo> Chips => chips.Values;

        public void SetChip(ChipDefinition def)
        {
            chips[def.id] = new PartInfo
            {
                Type = def.id,
                Name = def.name,
                Inputs = def.inputNames.Count,
                Outputs = def.outputNames.Count,
                InputNames = def.inputNames.ToArray(),
                OutputNames = def.outputNames.ToArray(),
                Chip = def
            };
            gateCostCache.Clear();
        }

        public bool Has(string type) =>
            type == PartTypes.Nand || type == PartTypes.Input || type == PartTypes.Output || chips.ContainsKey(type);

        public bool TryGetChip(string id, out ChipDefinition def)
        {
            if (chips.TryGetValue(id, out var info)) { def = info.Chip; return true; }
            def = null;
            return false;
        }

        public PartInfo Get(string type)
        {
            switch (type)
            {
                case PartTypes.Nand: return NandInfo;
                case PartTypes.Input: return InputInfo;
                case PartTypes.Output: return OutputInfo;
            }
            if (chips.TryGetValue(type, out var info)) return info;
            throw new KeyNotFoundException($"Unknown part type '{type}'");
        }

        /// <summary>How many NAND gates one copy of this part costs once flattened.</summary>
        public int GateCost(string type)
        {
            if (type == PartTypes.Nand) return 1;
            if (PartTypes.IsIo(type)) return 0;
            if (gateCostCache.TryGetValue(type, out var cost)) return cost;
            var info = Get(type);
            cost = Netlist.Compile(info.Chip.circuit, this, info.Inputs, info.Outputs).GateCount;
            gateCostCache[type] = cost;
            return cost;
        }
    }
}
