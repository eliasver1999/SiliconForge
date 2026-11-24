using System;
using System.Collections.Generic;
using SiliconForge.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>
    /// The interactive board. Left-drag on empty space draws a selection box, right- or middle-drag pans,
    /// the wheel zooms. Parts can be moved as a group, copied and pasted. Pins show a pinTip with their name and value.
    /// It edits a CircuitData in place and raises Edited after every change.
    /// </summary>
    public sealed class CircuitView : VisualElement
    {
        public event Action Edited;
        public event Action<int> InputToggled;
        public event Action<string> Message;

        enum Mode { None, Pan, MoveParts, Wire, Toggle, Box }

        /// <summary>Parts and the wires between them, kept between contracts so designs can be carried over.</summary>
        sealed class Clip
        {
            public readonly List<PartData> parts = new List<PartData>();
            public readonly List<WireData> wires = new List<WireData>();
        }

        static Clip clipboard;

        readonly PartCatalog catalog;
        readonly GridLayer grid;
        readonly VisualElement content;
        readonly WireLayer wireLayer;
        readonly VisualElement selectionBox;
        readonly VisualElement pinTip;
        readonly Label pinTipName, pinTipValue;
        readonly Dictionary<int, PartElement> elements = new Dictionary<int, PartElement>();
        readonly Dictionary<long, WireData> drivers = new Dictionary<long, WireData>();
        readonly HashSet<int> selected = new HashSet<int>();

        /// <summary>Part types this contract allows. Pasting skips anything else.</summary>
        public HashSet<string> AllowedTypes;

        public CircuitData Circuit { get; private set; }
        public Netlist Netlist { get; private set; }
        public ulong[] State { get; private set; }
        public Vector2 Pan { get; private set; }
        public float Zoom { get; private set; } = 1f;
        public WireData SelectedWire { get; private set; }
        public WireData HoverWire { get; private set; }
        public int SelectionCount => selected.Count;

        Mode mode;
        int pointerId = -1;
        int downButton;
        Vector2 downLocal, lastLocal;
        bool moved;
        PartElement dragPart;
        Vector2 dragBoardStart;
        readonly Dictionary<int, Vector2> dragStarts = new Dictionary<int, Vector2>();
        PinRef wireFrom;
        Vector2 wireCursor;
        PinElement hoverPin, pinTipPin;
        object rightClickTarget;
        bool boxAdditive;
        readonly HashSet<int> boxBase = new HashSet<int>();
        bool fitPending = true;
        bool hovering;
        Vector2 hoverLocal;

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

            selectionBox = new VisualElement { pickingMode = PickingMode.Ignore };
            selectionBox.AddToClassList("selection-box");
            Add(selectionBox);

            pinTip = new VisualElement { pickingMode = PickingMode.Ignore };
            pinTip.AddToClassList("pin-tip");
            pinTipName = new Label { pickingMode = PickingMode.Ignore };
            pinTipName.AddToClassList("pin-tip__name");
            pinTipValue = new Label { pickingMode = PickingMode.Ignore };
            pinTipValue.AddToClassList("pin-tip__value");
            pinTip.Add(pinTipName);
            pinTip.Add(pinTipValue);
            Add(pinTip);

            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerLeaveEvent>(_ => OnLeave());
            RegisterCallback<PointerCaptureOutEvent>(_ => EndInteraction());
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            ApplyTransform();
        }

        // ------------------------------------------------------------------ data

        /// <summary>The element showing a part, or null. Elements are rebuilt by SetCircuit, so don't hold on to it.</summary>
        public VisualElement ElementFor(int partId) => elements.TryGetValue(partId, out var e) ? e : null;

        public void SetCircuit(CircuitData circuit)
        {
            Circuit = circuit;
            foreach (var e in elements.Values) e.RemoveFromHierarchy();
            elements.Clear();
            selected.Clear();
            SelectedWire = null;
            HoverWire = null;
            HideTooltip();
            foreach (var p in circuit.parts) CreateElement(p);
            RebuildDrivers();
            wireLayer.MarkDirtyRepaint();
        }

        public void Bind(Netlist netlist, ulong[] state)
        {
            Netlist = netlist;
            State = state;
        }

        PartElement CreateElement(PartData p)
        {
            var el = new PartElement(p, catalog.Get(p.type));
            elements[p.id] = el;
            content.Add(el);
            return el;
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
            if (pinTipPin != null) UpdateTooltipValue();
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
            SelectOnly(part.id);
            Sfx.Play(SfxId.Place);
            NotifyEdited();
        }

        public void DeleteSelection()
        {
            if (SelectedWire != null)
            {
                Circuit.wires.Remove(SelectedWire);
                SelectedWire = null;
                Sfx.Play(SfxId.Delete);
                NotifyEdited();
                return;
            }
            if (selected.Count == 0) return;
            DeleteParts(new List<int>(selected));
        }

        void DeleteParts(List<int> ids)
        {
            int removed = 0;
            foreach (var id in ids)
            {
                var part = Circuit.Find(id);
                if (part == null || PartTypes.IsIo(part.type)) continue;
                Circuit.RemovePart(id);
                if (elements.TryGetValue(id, out var el)) { el.RemoveFromHierarchy(); elements.Remove(id); }
                selected.Remove(id);
                removed++;
            }
            if (removed == 0)
            {
                Message?.Invoke("Inputs and outputs belong to the contract and can't be removed.");
                return;
            }
            Sfx.Play(SfxId.Delete);
            NotifyEdited();
        }

        public void SelectAll()
        {
            selected.Clear();
            foreach (var el in elements.Values)
                if (!PartTypes.IsIo(el.Data.type)) selected.Add(el.Data.id);
            SelectedWire = null;
            ApplySelection();
        }

        public void ClearSelection()
        {
            selected.Clear();
            SelectedWire = null;
            ApplySelection();
        }

        /// <summary>Copies the selected parts and the wires between them.</summary>
        public bool Copy()
        {
            if (selected.Count == 0) return false;
            var clip = new Clip();
            foreach (var id in selected)
            {
                var part = Circuit.Find(id);
                if (part != null && !PartTypes.IsIo(part.type)) clip.parts.Add(part.Clone());
            }
            foreach (var w in Circuit.wires)
                if (selected.Contains(w.fromPart) && selected.Contains(w.toPart)) clip.wires.Add(w.Clone());
            if (clip.parts.Count == 0) return false;
            clipboard = clip;
            Message?.Invoke(clip.parts.Count == 1 ? "Copied 1 part." : $"Copied {clip.parts.Count} parts.");
            return true;
        }

        /// <summary>Pastes at the cursor when it's over the board, otherwise just below and right of the originals.</summary>
        public void Paste(bool atCursor = true)
        {
            if (clipboard == null || clipboard.parts.Count == 0) { Message?.Invoke("Nothing to paste yet. Select parts and press Ctrl+C."); return; }

            var min = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var p in clipboard.parts) min = Vector2.Min(min, new Vector2(p.x, p.y));
            var offset = atCursor && hovering
                ? Snap(ToBoard(hoverLocal) - min)
                : new Vector2(PartLayout.Grid * 4, PartLayout.Grid * 4);

            var map = new Dictionary<int, int>();
            int skipped = 0;
            selected.Clear();
            SelectedWire = null;
            foreach (var p in clipboard.parts)
            {
                if (!catalog.Has(p.type) || (AllowedTypes != null && !AllowedTypes.Contains(p.type))) { skipped++; continue; }
                var copy = Circuit.AddPart(p.type, p.x + offset.x, p.y + offset.y);
                map[p.id] = copy.id;
                CreateElement(copy);
                selected.Add(copy.id);
            }
            foreach (var w in clipboard.wires)
                if (map.TryGetValue(w.fromPart, out var from) && map.TryGetValue(w.toPart, out var to))
                    Circuit.Connect(from, w.fromPin, to, w.toPin);

            ApplySelection();
            if (map.Count > 0)
            {
                Sfx.Play(SfxId.Place);
                NotifyEdited();
            }
            if (skipped > 0) Message?.Invoke(skipped == 1 ? "1 part isn't available in this contract and was left out." : $"{skipped} parts aren't available in this contract and were left out.");
        }

        public void Duplicate()
        {
            if (Copy()) Paste(false);
        }

        void SelectOnly(int partId)
        {
            selected.Clear();
            selected.Add(partId);
            SelectedWire = null;
            ApplySelection();
        }

        void ToggleSelected(int partId)
        {
            if (!selected.Remove(partId)) selected.Add(partId);
            SelectedWire = null;
            ApplySelection();
        }

        void SelectWire(WireData w)
        {
            selected.Clear();
            SelectedWire = w;
            ApplySelection();
        }

        void ApplySelection()
        {
            foreach (var el in elements.Values) el.SetSelected(selected.Contains(el.Data.id));
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
            HideTooltip();
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
                BeginWiring(hit.pin);
                Capture(e.pointerId);
            }
            else if (hit.part != null && hit.part.Data.type == PartTypes.Input)
            {
                mode = Mode.Toggle;
                dragPart = hit.part;
                Capture(e.pointerId);
            }
            else if (hit.part != null && hit.part.Data.type != PartTypes.Output)
            {
                int id = hit.part.Data.id;
                if (e.shiftKey)
                {
                    ToggleSelected(id);
                    mode = Mode.None;
                }
                else
                {
                    if (!selected.Contains(id)) SelectOnly(id);
                    SelectedWire = null;
                    mode = Mode.MoveParts;
                    dragBoardStart = ToBoard(local);
                    dragStarts.Clear();
                    foreach (var sid in selected)
                        if (elements.TryGetValue(sid, out var el))
                        {
                            dragStarts[sid] = new Vector2(el.Data.x, el.Data.y);
                            el.BringToFront();
                        }
                    Capture(e.pointerId);
                }
            }
            else
            {
                var wire = hit.part == null && !e.shiftKey ? HitWire(ToBoard(local)) : null;
                if (wire != null)
                {
                    SelectWire(wire);
                    mode = Mode.None;
                }
                else
                {
                    boxAdditive = e.shiftKey;
                    boxBase.Clear();
                    if (boxAdditive) boxBase.UnionWith(selected);
                    else ClearSelection();
                    mode = Mode.Box;
                    Capture(e.pointerId);
                }
            }
            e.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            var local = this.WorldToLocal(e.position);
            hovering = true;
            hoverLocal = local;

            if (pointerId == -1 || e.pointerId != pointerId)
            {
                UpdateHover(e.target as VisualElement, local);
                return;
            }
            if (!moved && (local - downLocal).sqrMagnitude > 16f) moved = true;

            switch (mode)
            {
                case Mode.Pan:
                    Pan += local - lastLocal;
                    ApplyTransform();
                    break;

                case Mode.MoveParts:
                    if (moved)
                    {
                        var delta = Snap(ToBoard(local) - dragBoardStart);
                        foreach (var kv in dragStarts)
                        {
                            if (!elements.TryGetValue(kv.Key, out var el)) continue;
                            var pos = kv.Value + delta;
                            el.Data.x = pos.x;
                            el.Data.y = pos.y;
                            el.SetPosition(pos);
                        }
                        wireLayer.MarkDirtyRepaint();
                    }
                    break;

                case Mode.Wire:
                    wireCursor = ToBoard(local);
                    SetHoverPin(FindTargetPin(e.position));
                    wireLayer.MarkDirtyRepaint();
                    break;

                case Mode.Box:
                    if (moved) UpdateBox(local);
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
                        if (rightClickTarget is PartElement part) DeleteParts(new List<int> { part.Data.id });
                        else if (rightClickTarget is WireData wire)
                        {
                            Circuit.wires.Remove(wire);
                            if (SelectedWire == wire) SelectedWire = null;
                            if (HoverWire == wire) HoverWire = null;
                            Sfx.Play(SfxId.Delete);
                            NotifyEdited();
                        }
                    }
                    break;

                case Mode.MoveParts:
                    bool changed = false;
                    foreach (var kv in dragStarts)
                        if (elements.TryGetValue(kv.Key, out var el) && (el.Data.x != kv.Value.x || el.Data.y != kv.Value.y)) changed = true;
                    if (changed) NotifyEdited();
                    break;

                case Mode.Wire:
                    if (target != null) CompleteWire(target.Ref);
                    break;

                case Mode.Toggle:
                    if (!moved && dragPart != null)
                    {
                        Sfx.Play(SfxId.Toggle);
                        InputToggled?.Invoke(dragPart.Data.index);
                    }
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
            dragStarts.Clear();
            selectionBox.style.display = DisplayStyle.None;
            SetHoverPin(null);
            EndWiring();
            wireLayer.MarkDirtyRepaint();
        }

        void OnLeave()
        {
            hovering = false;
            HideTooltip();
            if (HoverWire != null) { HoverWire = null; wireLayer.MarkDirtyRepaint(); }
        }

        void OnWheel(WheelEvent e)
        {
            var local = this.WorldToLocal(e.mousePosition);
            float factor = e.delta.y > 0 ? 1f / 1.12f : 1.12f;
            ZoomAround(local, Zoom * factor);
            HideTooltip();
            e.StopPropagation();
        }

        // ------------------------------------------------------------------ box selection

        void UpdateBox(Vector2 local)
        {
            var min = Vector2.Min(downLocal, local);
            var max = Vector2.Max(downLocal, local);
            selectionBox.style.display = DisplayStyle.Flex;
            selectionBox.style.left = min.x;
            selectionBox.style.top = min.y;
            selectionBox.style.width = max.x - min.x;
            selectionBox.style.height = max.y - min.y;

            var bMin = ToBoard(min);
            var bMax = ToBoard(max);
            var box = Rect.MinMaxRect(bMin.x, bMin.y, bMax.x, bMax.y);
            selected.Clear();
            selected.UnionWith(boxBase);
            foreach (var el in elements.Values)
            {
                if (PartTypes.IsIo(el.Data.type)) continue;
                var rect = new Rect(new Vector2(el.Data.x, el.Data.y), PartLayout.Size(el.Data.type, el.Info));
                if (box.Overlaps(rect)) selected.Add(el.Data.id);
            }
            ApplySelection();
        }

        // ------------------------------------------------------------------ wiring

        void BeginWiring(PinElement origin)
        {
            AddToClassList("circuit-view--wiring");
            origin.AddToClassList("pin--origin");
            foreach (var el in elements.Values)
                foreach (var pin in wireFrom.isOutput ? el.Inputs : el.Outputs)
                    pin.AddToClassList("pin--candidate");
        }

        void EndWiring()
        {
            if (!ClassListContains("circuit-view--wiring")) return;
            RemoveFromClassList("circuit-view--wiring");
            foreach (var el in elements.Values)
            {
                foreach (var pin in el.Inputs) { pin.RemoveFromClassList("pin--candidate"); pin.RemoveFromClassList("pin--origin"); }
                foreach (var pin in el.Outputs) { pin.RemoveFromClassList("pin--candidate"); pin.RemoveFromClassList("pin--origin"); }
            }
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
            Sfx.Play(SfxId.Wire);
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

        // ------------------------------------------------------------------ hover: tooltips and wire highlight

        void UpdateHover(VisualElement target, Vector2 local)
        {
            var hit = Classify(target);
            if (hit.pin != null) ShowTooltip(hit.pin, local);
            else HideTooltip();

            var wire = hit.part == null ? HitWire(ToBoard(local)) : null;
            if (wire != HoverWire)
            {
                HoverWire = wire;
                wireLayer.MarkDirtyRepaint();
            }
        }

        void ShowTooltip(PinElement pin, Vector2 local)
        {
            pinTipPin = pin;
            pinTipName.text = PinName(pin.Ref);
            UpdateTooltipValue();
            pinTip.style.display = DisplayStyle.Flex;
            pinTip.style.left = local.x + 16;
            pinTip.style.top = local.y - 36;
        }

        void UpdateTooltipValue()
        {
            bool on = PinValue(pinTipPin.Ref);
            pinTipValue.text = on ? "1" : "0";
            pinTipValue.EnableInClassList("pin-tip__value--on", on);
        }

        void HideTooltip()
        {
            pinTipPin = null;
            pinTip.style.display = DisplayStyle.None;
        }

        string PinName(PinRef r)
        {
            if (!elements.TryGetValue(r.part, out var el)) return "";
            if (PartTypes.IsIo(el.Data.type)) return el.Data.label;
            var names = r.isOutput ? el.Info.OutputNames : el.Info.InputNames;
            string pin = r.pin < names.Length ? names[r.pin] : r.pin.ToString();
            return $"{el.Info.Name}  ·  {pin}";
        }
    }
}
