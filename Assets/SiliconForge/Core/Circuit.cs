using System;
using System.Collections.Generic;

namespace SiliconForge.Core
{
    /// <summary>Built-in part type ids. Any other type id refers to a chip the player designed.</summary>
    public static class PartTypes
    {
        public const string Nand = "NAND";
        public const string Input = "IN";
        public const string Output = "OUT";

        public static bool IsIo(string type) => type == Input || type == Output;
    }

    [Serializable]
    public class PartData
    {
        public int id;
        public string type;
        public float x;
        public float y;
        /// <summary>For IN/OUT parts: which pin of the level (or chip) this is.</summary>
        public int index;
        public string label;

        public PartData Clone() => (PartData)MemberwiseClone();
    }

    /// <summary>A wire always runs from an output pin to an input pin.</summary>
    [Serializable]
    public class WireData
    {
        public int fromPart;
        public int fromPin;
        public int toPart;
        public int toPin;

        public WireData Clone() => (WireData)MemberwiseClone();
        public bool Touches(int partId) => fromPart == partId || toPart == partId;
    }

    [Serializable]
    public class CircuitData
    {
        public List<PartData> parts = new List<PartData>();
        public List<WireData> wires = new List<WireData>();
        public int nextId = 1;

        public PartData Find(int id)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].id == id) return parts[i];
            return null;
        }

        public PartData AddPart(string type, float x, float y, int index = 0, string label = null)
        {
            var part = new PartData { id = nextId++, type = type, x = x, y = y, index = index, label = label };
            parts.Add(part);
            return part;
        }

        public void RemovePart(int id)
        {
            parts.RemoveAll(p => p.id == id);
            wires.RemoveAll(w => w.Touches(id));
        }

        public WireData DriverOf(int partId, int pin)
        {
            for (int i = 0; i < wires.Count; i++)
                if (wires[i].toPart == partId && wires[i].toPin == pin) return wires[i];
            return null;
        }

        /// <summary>Connects an output to an input. An input has one driver, so any old wire into it is replaced.</summary>
        public WireData Connect(int fromPart, int fromPin, int toPart, int toPin)
        {
            wires.RemoveAll(w => w.toPart == toPart && w.toPin == toPin);
            var wire = new WireData { fromPart = fromPart, fromPin = fromPin, toPart = toPart, toPin = toPin };
            wires.Add(wire);
            return wire;
        }

        public CircuitData Clone()
        {
            var copy = new CircuitData { nextId = nextId };
            foreach (var p in parts) copy.parts.Add(p.Clone());
            foreach (var w in wires) copy.wires.Add(w.Clone());
            return copy;
        }
    }
}
