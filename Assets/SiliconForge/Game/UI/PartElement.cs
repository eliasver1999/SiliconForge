using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    public readonly struct PinRef
    {
        public readonly int part;
        public readonly int pin;
        public readonly bool isOutput;

        public PinRef(int part, int pin, bool isOutput)
        {
            this.part = part;
            this.pin = pin;
            this.isOutput = isOutput;
        }
    }

    /// <summary>Sizes and pin positions for every part. Wires use these too, so they never wait for layout.</summary>
    public static class PartLayout
    {
        public const float Grid = 12f;
        public const float PinSpacing = 24f;
        public const float TitleStrip = 24f;

        /// <summary>Gates whose inputs are interchangeable don't need pin names; every other chip does.</summary>
        static readonly HashSet<string> SymmetricChips = new HashSet<string> { "NOT", "AND", "OR", "XOR" };

        public static bool ShowsPinLabels(PartInfo info) => info.IsChip && !SymmetricChips.Contains(info.Type);

        public static Vector2 Size(string type, PartInfo info)
        {
            switch (type)
            {
                case PartTypes.Nand: return new Vector2(84, 60);
                case PartTypes.Input:
                case PartTypes.Output: return new Vector2(108, 40);
            }
            int pins = Mathf.Max(2, Mathf.Max(info.Inputs, info.Outputs));
            if (!ShowsPinLabels(info)) return new Vector2(96, pins * PinSpacing + 12);
            // Labelled chips get a title strip on top so the name never collides with pin labels.
            return new Vector2(144, pins * PinSpacing + 12 + TitleStrip);
        }

        public static Vector2 PinLocal(string type, PartInfo info, bool output, int index)
        {
            var size = Size(type, info);
            int count = output ? info.Outputs : info.Inputs;
            float top = info.IsChip && ShowsPinLabels(info) ? TitleStrip : 0f;
            float y = top + (size.y - top) * 0.5f + (index - (count - 1) * 0.5f) * PinSpacing;
            return new Vector2(output ? size.x : 0f, y);
        }
    }

    public sealed class PinElement : VisualElement
    {
        public const float Size = 16f;
        public readonly PinRef Ref;
        bool on;

        public PinElement(PinRef r)
        {
            Ref = r;
            AddToClassList("pin");
            AddToClassList(r.isOutput ? "pin--out" : "pin--in");
        }

        public void SetOn(bool value)
        {
            if (value == on) return;
            on = value;
            EnableInClassList("pin--on", value);
        }
    }

    public sealed class PartElement : VisualElement
    {
        public readonly PartData Data;
        public readonly PartInfo Info;
        public readonly List<PinElement> Inputs = new List<PinElement>();
        public readonly List<PinElement> Outputs = new List<PinElement>();

        readonly VisualElement indicator; // switch track for IN, lamp for OUT
        readonly string indicatorOnClass;
        bool indicatorOn;

        public PartElement(PartData data, PartInfo info)
        {
            Data = data;
            Info = info;
            AddToClassList("part");

            var size = PartLayout.Size(data.type, info);
            style.width = size.x;
            style.height = size.y;
            SetPosition(new Vector2(data.x, data.y));

            switch (data.type)
            {
                case PartTypes.Nand:
                    AddToClassList("part--gate");
                    Add(Ignore(Ui.Text("NAND", "part__title")));
                    break;

                case PartTypes.Input:
                {
                    AddToClassList("part--io");
                    AddToClassList("part--in");
                    var row = Ignore(Ui.Div("io__row"));
                    row.Add(Ignore(Ui.Text(data.label, "io__label")));
                    indicator = Ignore(Ui.Div("switch"));
                    indicator.Add(Ignore(Ui.Div("switch__knob")));
                    indicatorOnClass = "switch--on";
                    row.Add(indicator);
                    Add(row);
                    break;
                }

                case PartTypes.Output:
                {
                    AddToClassList("part--io");
                    AddToClassList("part--out");
                    var row = Ignore(Ui.Div("io__row"));
                    row.Add(Ignore(Ui.Text(data.label, "io__label")));
                    indicator = Ignore(Ui.Div("lamp"));
                    indicatorOnClass = "lamp--on";
                    row.Add(indicator);
                    Add(row);
                    break;
                }

                default:
                    AddToClassList("part--chip");
                    if (PartLayout.ShowsPinLabels(info))
                    {
                        AddToClassList("part--labelled");
                        var strip = Ignore(Ui.Div("part__strip"));
                        strip.style.height = PartLayout.TitleStrip;
                        strip.Add(Ignore(Ui.Text(info.Name.ToUpperInvariant(), "part__strip-title")));
                        Add(strip);
                        AddPinLabels();
                    }
                    else
                    {
                        Add(Ignore(Ui.Text(info.Name.ToUpperInvariant(), "part__title")));
                    }
                    break;
            }

            for (int i = 0; i < info.Inputs; i++) Inputs.Add(AddPin(new PinRef(data.id, i, false)));
            for (int i = 0; i < info.Outputs; i++) Outputs.Add(AddPin(new PinRef(data.id, i, true)));
        }

        static T Ignore<T>(T e) where T : VisualElement
        {
            e.pickingMode = PickingMode.Ignore;
            return e;
        }

        PinElement AddPin(PinRef r)
        {
            var pin = new PinElement(r);
            var p = PartLayout.PinLocal(Data.type, Info, r.isOutput, r.pin);
            // Absolute children sit inside the 1.5px border, so nudge them back onto the edge.
            pin.style.left = p.x - PinElement.Size * 0.5f - 1.5f;
            pin.style.top = p.y - PinElement.Size * 0.5f - 1.5f;
            Add(pin);
            return pin;
        }

        void AddPinLabels()
        {
            for (int i = 0; i < Info.Inputs; i++)
            {
                var label = Ignore(Ui.Text(Info.InputNames[i], "part__pinlabel"));
                label.style.left = 12;
                label.style.top = PartLayout.PinLocal(Data.type, Info, false, i).y - 9;
                Add(label);
            }
            for (int i = 0; i < Info.Outputs; i++)
            {
                var label = Ignore(Ui.Text(Info.OutputNames[i], "part__pinlabel"));
                label.style.right = 12;
                label.style.top = PartLayout.PinLocal(Data.type, Info, true, i).y - 9;
                Add(label);
            }
        }

        public void SetPosition(Vector2 p)
        {
            style.left = p.x;
            style.top = p.y;
        }

        public void SetSelected(bool selected) => EnableInClassList("part--selected", selected);

        public void Refresh(CircuitView view)
        {
            foreach (var pin in Inputs) pin.SetOn(view.PinValue(pin.Ref));
            foreach (var pin in Outputs) pin.SetOn(view.PinValue(pin.Ref));

            if (indicator == null) return;
            bool on = Data.type == PartTypes.Input
                ? view.PinValue(new PinRef(Data.id, 0, true))
                : view.PinValue(new PinRef(Data.id, 0, false));
            if (on == indicatorOn) return;
            indicatorOn = on;
            indicator.EnableInClassList(indicatorOnClass, on);
        }
    }
}
