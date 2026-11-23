using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>Subtle engineering-paper grid behind the board. Not scaled; it follows pan and zoom itself.</summary>
    public sealed class GridLayer : VisualElement
    {
        // Alpha blends in linear space here, so these read stronger than the numbers suggest.
        static readonly Color Minor = new Color(1f, 1f, 1f, 0.006f);
        static readonly Color Major = new Color(1f, 1f, 1f, 0.018f);

        readonly CircuitView view;

        public GridLayer(CircuitView view)
        {
            this.view = view;
            pickingMode = PickingMode.Ignore;
            AddToClassList("grid-layer");
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 1 || r.height <= 1) return;

            float step = PartLayout.Grid * 2f * view.Zoom;
            while (step < 14f) step *= 2f;

            var p = mgc.painter2D;
            p.lineWidth = 1f;
            DrawLines(p, step, Minor, r);
            DrawLines(p, step * 5f, Major, r);
        }

        void DrawLines(Painter2D p, float step, Color color, Rect r)
        {
            p.strokeColor = color;
            p.BeginPath();
            float ox = Mathf.Repeat(view.Pan.x, step);
            float oy = Mathf.Repeat(view.Pan.y, step);
            for (float x = ox; x < r.width; x += step)
            {
                p.MoveTo(new Vector2(x, 0));
                p.LineTo(new Vector2(x, r.height));
            }
            for (float y = oy; y < r.height; y += step)
            {
                p.MoveTo(new Vector2(0, y));
                p.LineTo(new Vector2(r.width, y));
            }
            p.Stroke();
        }
    }

    /// <summary>
    /// Draws every wire as a bezier, with small pulses travelling along live ones.
    /// It lives inside the scaled board content and is made huge so nothing is ever clipped.
    /// </summary>
    public sealed class WireLayer : VisualElement
    {
        public const float Offset = 6000f;

        static readonly Color WireOff = new Color32(0x3A, 0x45, 0x56, 0xFF);
        static readonly Color WireOn = new Color32(0xFF, 0xB5, 0x47, 0xFF);
        static readonly Color Pulse = new Color32(0xFF, 0xE6, 0xB8, 0xFF);
        static readonly Color Glow = new Color(1f, 0.71f, 0.28f, 0.16f);
        static readonly Color SelectHalo = new Color(1f, 1f, 1f, 0.16f);
        static readonly Color Preview = new Color(1f, 0.71f, 0.28f, 0.85f);

        readonly CircuitView view;

        public WireLayer(CircuitView view)
        {
            this.view = view;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = -Offset;
            style.top = -Offset;
            style.width = Offset * 2f;
            style.height = Offset * 2f;
            generateVisualContent += Draw;
        }

        public static void Controls(Vector2 a, Vector2 b, out Vector2 c1, out Vector2 c2)
        {
            float dx = Mathf.Max(36f, Mathf.Abs(b.x - a.x) * 0.5f);
            c1 = a + new Vector2(dx, 0);
            c2 = b - new Vector2(dx, 0);
        }

        public static Vector2 Point(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, float t)
        {
            float u = 1f - t;
            return u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
        }

        void Draw(MeshGenerationContext mgc)
        {
            if (view.Circuit == null) return;
            var p = mgc.painter2D;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            var off = new Vector2(Offset, Offset);
            float time = Time.unscaledTime;

            foreach (var w in view.Circuit.wires)
            {
                if (!view.TryGetWireEnds(w, out var a, out var b)) continue;
                a += off;
                b += off;
                Controls(a, b, out var c1, out var c2);
                bool on = view.WireValue(w);

                if (w == view.SelectedWire) Stroke(p, a, c1, c2, b, SelectHalo, 11f);
                if (on) Stroke(p, a, c1, c2, b, Glow, 8f);
                Stroke(p, a, c1, c2, b, on ? WireOn : WireOff, 3f);

                if (on)
                {
                    float length = Vector2.Distance(a, b) * 1.15f + 40f;
                    int count = Mathf.Max(1, Mathf.RoundToInt(length / 110f));
                    float speed = 150f / length;
                    p.fillColor = Pulse;
                    for (int k = 0; k < count; k++)
                    {
                        float t = Mathf.Repeat(time * speed + k / (float)count, 1f);
                        var pt = Point(a, c1, c2, b, t);
                        p.BeginPath();
                        p.Arc(pt, 2.6f, Angle.Degrees(0f), Angle.Degrees(360f));
                        p.Fill();
                    }
                }
            }

            if (view.TryGetPreview(out var from, out var to))
            {
                from += off;
                to += off;
                Controls(from, to, out var c1, out var c2);
                Stroke(p, from, c1, c2, to, Preview, 2.5f);
                p.fillColor = Preview;
                p.BeginPath();
                p.Arc(view.PreviewCursorIsOutput ? from : to, 4f, Angle.Degrees(0f), Angle.Degrees(360f));
                p.Fill();
            }
        }

        static void Stroke(Painter2D p, Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, Color color, float width)
        {
            p.strokeColor = color;
            p.lineWidth = width;
            p.BeginPath();
            p.MoveTo(a);
            p.BezierCurveTo(c1, c2, b);
            p.Stroke();
        }
    }
}
