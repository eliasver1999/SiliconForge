using System;
using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>
    /// The interactive board: pan, zoom, place, move, wire and delete parts.
    /// It edits a CircuitData in place and raises Edited after every change.
    /// </summary>
    public sealed class CircuitView : VisualElement
    {
        public event Action Edited;
        public event Action<int> InputToggled;
        public event Action<string> Message;

        enum Mode { None, Pan, MovePart, Wire, Toggle }

        readonly PartCatalog catalog;
        readonly GridLayer grid;
        readonly VisualElement content;
        readonly WireLayer wireLayer;
        readonly Dictionary<int, PartElement> elements = new Dictionary<int, PartElement>();
        readonly Dictionary<long, WireData> drivers = new Dictionary<long, WireData>();

        public CircuitData Circuit { get; private set; }
        public Netlist Netlist { get; private set; }
        public ulong[] State { get; private set; }
        public Vector2 Pan { get; private set; }
        public float Zoom { get; private set; } = 1f;
        public WireData SelectedWire { get; private set; }
        public int SelectedPart { get; private set; } = -1;

        Mode mode;
        int pointerId = -1;
        int downButton;
        Vector2 downLocal, lastLocal;
        bool moved;
        PartElement dragPart;
        Vector2 dragOffset, dragStart;
        PinRef wireFrom;
        Vector2 wireCursor;
        PinElement hoverPin;
        object rightClickTarget;
        bool fitPending = true;

        public CircuitView(PartCatalog catalog)
        {
            this.catalog = catalog;
            AddToClassList("circuit-view");
            focusable = true;

            grid = new GridLayer(this);
            Add(grid);

            content = new VisualElement { pickingMode = PickingMode.Ignore };
            content.AddToClassList("circuit-content");
            Add(content);

            wireLayer = new WireLayer(this);
            content.Add(wireLayer);

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndInteraction());
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            ApplyTransform();
        }

        // ------------------------------------------------------------------ data

        public void SetCircuit(CircuitData circuit)
        {
            Circuit = circuit;
            foreach (var e in elements.Values) e.RemoveFromHierarchy();
            elements.Clear();
            SelectedWire = null;
            SelectedPart = -1;
            foreach (var p in circuit.parts) CreateElement(p);
            RebuildDrivers();
            wireLayer.MarkDirtyRepaint();
        }

        public void Bind(Netlist netlist, ulong[] state)
        {
            Netlist = netlist;
            State = state;
        }

        void CreateElement(PartData p)
        {
            var el = new PartElement(p, catalog.Get(p.type));
            elements[p.id] = el;
            content.Add(el);
        }

        void RebuildDrivers()
        {
            drivers.Clear();
            foreach (var w in Circuit.wires) drivers[Netlist.Key(w.toPart, w.toPin)] = w;
        }

        void NotifyEdited()
        {
            RebuildDrivers();
            wireLayer.MarkDirtyRepaint();
            Edited?.Invoke();
        }

        public void RefreshSignals()
        {
            foreach (var e in elements.Values) e.Refresh(this);
            wireLayer.MarkDirtyRepaint();
        }

        public bool PinValue(PinRef r)
        {
            if (Netlist == null || State == null) return false;
            if (r.isOutput) return (State[Netlist.NetOfOutputPin(r.part, r.pin)] & 1UL) != 0;
            return drivers.TryGetValue(Netlist.Key(r.part, r.pin), out var w) &&
                   (State[Netlist.NetOfOutputPin(w.fromPart, w.fromPin)] & 1UL) != 0;
        }

        public bool WireValue(WireData w) => PinValue(new PinRef(w.fromPart, w.fromPin, true));

        Vector2 PinBoardPosition(PinRef r)
        {
            var el = elements[r.part];
            return new Vector2(el.Data.x, el.Data.y) + PartLayout.PinLocal(el.Data.type, el.Info, r.isOutput, r.pin);
        }

        public bool TryGetWireEnds(WireData w, out Vector2 a, out Vector2 b)
        {
            a = b = default;
            if (!elements.ContainsKey(w.fromPart) || !elements.ContainsKey(w.toPart)) return false;
            a = PinBoardPosition(new PinRef(w.fromPart, w.fromPin, true));
            b = PinBoardPosition(new PinRef(w.toPart, w.toPin, false));
            return true;
        }

        public bool PreviewCursorIsOutput => !wireFrom.isOutput;

        public bool TryGetPreview(out Vector2 from, out Vector2 to)
        {
            from = to = default;
            if (mode != Mode.Wire || !moved) return false;
            var pin = PinBoardPosition(wireFrom);
            var cursor = hoverPin != null ? PinBoardPosition(hoverPin.Ref) : wireCursor;
            if (wireFrom.isOutput) { from = pin; to = cursor; }
            else { from = cursor; to = pin; }
            return true;
        }

        // ------------------------------------------------------------------ editing

        public void AddPart(string type, Vector2 boardCenter)
        {
            var size = PartLayout.Size(type, catalog.Get(type));
            var pos = Snap(boardCenter - size * 0.5f);
            var part = Circuit.AddPart(type, pos.x, pos.y);
            CreateElement(part);
            Select(part.id);
            NotifyEdited();
        }

        public void DeleteSelection()
        {
            if (SelectedWire != null)
            {
                Circuit.wires.Remove(SelectedWire);
                SelectedWire = null;
                NotifyEdited();
            }
            else if (SelectedPart >= 0)
            {
                DeletePart(SelectedPart);
            }
        }

        void DeletePart(int id)
        {
            var part = Circuit.Find(id);
            if (part == null) return;
            if (PartTypes.IsIo(part.type))
            {
                Message?.Invoke("Inputs and outputs belong to the contract and can't be removed.");
                return;
            }
            Circuit.RemovePart(id);
            if (elements.TryGetValue(id, out var el)) { el.RemoveFromHierarchy(); elements.Remove(id); }
            if (SelectedPart == id) SelectedPart = -1;
            NotifyEdited();
        }

        void Select(int partId)
        {
            if (SelectedPart >= 0 && elements.TryGetValue(SelectedPart, out var old)) old.SetSelected(false);
            SelectedPart = partId;
            if (partId >= 0 && elements.TryGetValue(partId, out var el)) el.SetSelected(true);
            if (partId >= 0) SelectedWire = null;
            wireLayer.MarkDirtyRepaint();
        }

        void SelectWire(WireData w)
        {
            Select(-1);
            SelectedWire = w;
            wireLayer.MarkDirtyRepaint();
        }

        // ------------------------------------------------------------------ view transform

        public Vector2 ToBoard(Vector2 local) => (local - Pan) / Zoom;
        public Vector2 PanelToBoard(Vector2 panelPosition) => ToBoard(this.WorldToLocal(panelPosition));
        public Vector2 BoardCenter => ToBoard(new Vector2(layout.width, layout.height) * 0.5f);

        static Vector2 Snap(Vector2 v) =>
            new Vector2(Mathf.Round(v.x / PartLayout.Grid) * PartLayout.Grid, Mathf.Round(v.y / PartLayout.Grid) * PartLayout.Grid);

        void ApplyTransform()
        {
            content.style.translate = new Translate(Pan.x, Pan.y);
            content.style.scale = new Scale(new Vector3(Zoom, Zoom, 1f));
            grid.MarkDirtyRepaint();
        }

        public void ZoomBy(float factor) => ZoomAround(new Vector2(layout.width, layout.height) * 0.5f, Zoom * factor);

        void ZoomAround(Vector2 local, float zoom)
        {
            zoom = Mathf.Clamp(zoom, 0.3f, 2.5f);
            var board = ToBoard(local);
            Zoom = zoom;
            Pan = local - board * zoom;
            ApplyTransform();
        }

        public void Fit()
        {
            if (Circuit == null || Circuit.parts.Count == 0 || layout.width < 10) return;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var el in elements.Values)
            {
                var pos = new Vector2(el.Data.x, el.Data.y);
                min = Vector2.Min(min, pos);
                max = Vector2.Max(max, pos + PartLayout.Size(el.Data.type, el.Info));
            }
            var size = max - min;
            const float margin = 120f;
            float zoom = Mathf.Min((layout.width - margin * 2) / Mathf.Max(size.x, 1f),
                                   (layout.height - margin * 2) / Mathf.Max(size.y, 1f));
            Zoom = Mathf.Clamp(zoom, 0.3f, 1.25f);
            var center = (min + max) * 0.5f;
            Pan = new Vector2(layout.width, layout.height) * 0.5f - center * Zoom;
            ApplyTransform();
        }

        void OnGeometryChanged(GeometryChangedEvent e)
        {
            if (fitPending && layout.width > 10)
            {
                fitPending = false;
                Fit();
            }
            grid.MarkDirtyRepaint();
        }

        // ------------------------------------------------------------------ input

        struct Hit
        {
            public PinElement pin;
            public PartElement part;
        }

        Hit Classify(VisualElement ve)
        {
            for (; ve != null && ve != this; ve = ve.parent)
            {
                if (ve is PinElement pin) return new Hit { pin = pin, part = ve.parent as PartElement };
                if (ve is PartElement part) return new Hit { part = part };
            }
            return default;
        }

        void Capture(int id)
        {
            pointerId = id;
            this.CapturePointer(id);
        }

        void OnPointerDown(PointerDownEvent e)
        {
            if (pointerId != -1) return;
            Focus();
            var local = this.WorldToLocal(e.position);
            downLocal = lastLocal = local;
            moved = false;
            downButton = e.button;
            var hit = Classify(e.target as VisualElement);

            if (e.button == 1 || e.button == 2)
            {
                mode = Mode.Pan;
                rightClickTarget = null;
                if (e.button == 1)
                    rightClickTarget = hit.part != null ? hit.part : (object)HitWire(ToBoard(local));
                Capture(e.pointerId);
                e.StopPropagation();
                return;
            }
            if (e.button != 0) return;

            if (hit.pin != null)
            {
                mode = Mode.Wire;
                wireFrom = hit.pin.Ref;
                wireCursor = ToBoard(local);
                Capture(e.pointerId);
            }
            else if (hit.part != null)
            {
                var data = hit.part.Data;
                if (data.type == PartTypes.Input)
                {
                    mode = Mode.Toggle;
                    dragPart = hit.part;
                }
                else if (data.type == PartTypes.Output)
                {
                    Select(-1);
                    mode = Mode.Pan;
                }
                else
                {
                    Select(data.id);
                    mode = Mode.MovePart;
                    dragPart = hit.part;
                    dragStart = new Vector2(data.x, data.y);
                    dragOffset = ToBoard(local) - dragStart;
                    hit.part.BringToFront();
                }
                Capture(e.pointerId);
            }
            else
            {
                var wire = HitWire(ToBoard(local));
                if (wire != null)
                {
                    SelectWire(wire);
                    mode = Mode.None;
                }
                else
                {
                    Select(-1);
                    SelectedWire = null;
                    mode = Mode.Pan;
                    Capture(e.pointerId);
                }
            }
            e.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            if (pointerId == -1 || e.pointerId != pointerId) return;
            var local = this.WorldToLocal(e.position);
            if (!moved && (local - downLocal).sqrMagnitude > 16f) moved = true;

            switch (mode)
            {
                case Mode.Pan:
                    Pan += local - lastLocal;
                    ApplyTransform();
                    break;

                case Mode.MovePart:
                    if (moved)
                    {
                        var pos = Snap(ToBoard(local) - dragOffset);
                        if (pos.x != dragPart.Data.x || pos.y != dragPart.Data.y)
                        {
                            dragPart.Data.x = pos.x;
                            dragPart.Data.y = pos.y;
                            dragPart.SetPosition(pos);
                            wireLayer.MarkDirtyRepaint();
                        }
                    }
                    break;

                case Mode.Wire:
                    wireCursor = ToBoard(local);
                    SetHoverPin(FindTargetPin(e.position));
                    wireLayer.MarkDirtyRepaint();
                    break;
            }
            lastLocal = local;
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (pointerId == -1 || e.pointerId != pointerId) return;
            var finished = mode;
            var target = finished == Mode.Wire ? FindTargetPin(e.position) : null;
            this.ReleasePointer(pointerId);

            switch (finished)
            {
                case Mode.Pan:
                    if (downButton == 1 && !moved && rightClickTarget != null)
                    {
                        if (rightClickTarget is PartElement part) DeletePart(part.Data.id);
                        else if (rightClickTarget is WireData wire)
                        {
                            Circuit.wires.Remove(wire);
                            if (SelectedWire == wire) SelectedWire = null;
                            NotifyEdited();
                        }
                    }
                    break;

                case Mode.MovePart:
                    if (dragPart != null && (dragPart.Data.x != dragStart.x || dragPart.Data.y != dragStart.y))
                        NotifyEdited();
                    break;

                case Mode.Wire:
                    if (target != null) CompleteWire(target.Ref);
                    break;

                case Mode.Toggle:
                    if (!moved && dragPart != null) InputToggled?.Invoke(dragPart.Data.index);
                    break;
            }
            EndInteraction();
        }

        void EndInteraction()
        {
            pointerId = -1;
            mode = Mode.None;
            dragPart = null;
            rightClickTarget = null;
            SetHoverPin(null);
            wireLayer.MarkDirtyRepaint();
        }

        void OnWheel(WheelEvent e)
        {
            var local = this.WorldToLocal(e.mousePosition);
            float factor = e.delta.y > 0 ? 1f / 1.12f : 1.12f;
            ZoomAround(local, Zoom * factor);
            e.StopPropagation();
        }

        void CompleteWire(PinRef target)
        {
            if (target.part == wireFrom.part && target.pin == wireFrom.pin && target.isOutput == wireFrom.isOutput) return;
            if (target.isOutput == wireFrom.isOutput)
            {
                Message?.Invoke(wireFrom.isOutput ? "Wires run from an output to an input." : "Wires run from an input to an output.");
                return;
            }
            var output = wireFrom.isOutput ? wireFrom : target;
            var input = wireFrom.isOutput ? target : wireFrom;
            Circuit.Connect(output.part, output.pin, input.part, input.pin);
            NotifyEdited();
        }

        void SetHoverPin(PinElement pin)
        {
            if (hoverPin == pin) return;
            hoverPin?.RemoveFromClassList("pin--target");
            hoverPin = pin;
            hoverPin?.AddToClassList("pin--target");
        }

        /// <summary>The pin a wire being drawn would snap to: the pin under the cursor, the nearest free side of a hovered part, or any pin close by.</summary>
        PinElement FindTargetPin(Vector2 panelPosition)
        {
            var board = PanelToBoard(panelPosition);
            var hit = Classify(panel?.Pick(panelPosition));
            if (hit.pin != null) return IsCandidate(hit.pin) ? hit.pin : null;

            PinElement best = null;
            float bestDist = hit.part != null ? float.MaxValue : 22f / Zoom;
            IEnumerable<PartElement> pool = hit.part != null ? new[] { hit.part } : (IEnumerable<PartElement>)elements.Values;
            foreach (var part in pool)
            {
                var pins = wireFrom.isOutput ? part.Inputs : part.Outputs;
                foreach (var pin in pins)
                {
                    float d = Vector2.Distance(board, PinBoardPosition(pin.Ref));
                    if (d < bestDist) { bestDist = d; best = pin; }
                }
            }
            return best;
        }

        bool IsCandidate(PinElement pin) =>
            pin.Ref.isOutput != wireFrom.isOutput || (pin.Ref.part == wireFrom.part && pin.Ref.pin == wireFrom.pin);

        WireData HitWire(Vector2 board)
        {
            float threshold = 7f / Zoom;
            WireData best = null;
            float bestDist = threshold;
            foreach (var w in Circuit.wires)
            {
                if (!TryGetWireEnds(w, out var a, out var b)) continue;
                WireLayer.Controls(a, b, out var c1, out var c2);
                var prev = a;
                for (int i = 1; i <= 24; i++)
                {
                    var pt = WireLayer.Point(a, c1, c2, b, i / 24f);
                    float d = DistanceToSegment(board, prev, pt);
                    if (d < bestDist) { bestDist = d; best = w; }
                    prev = pt;
                }
            }
            return best;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
