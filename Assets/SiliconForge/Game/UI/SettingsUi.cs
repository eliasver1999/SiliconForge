using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game.UI
{
    /// <summary>The settings panel and the quit confirmation, available from the menu and every top bar.</summary>
    public static class SettingsUi
    {
        public static void Show(VisualElement host, GameApp app)
        {
            var card = new VisualElement();
            card.AddToClassList("settings-card");
            card.Add(Ui.Text("Settings", "settings__title"));

            card.Add(Section("AUDIO"));
            card.Add(Row("Sound effects", Switch(Sfx.Enabled, on => Sfx.Enabled = on)));
            card.Add(Row("Volume", VolumeSlider()));

            card.Add(Section("DISPLAY"));
            card.Add(Row("Mode", Cycle(new List<string> { "Windowed", "Fullscreen" }, GameSettings.Fullscreen ? 1 : 0,
                i => GameSettings.SetFullscreen(i == 1))));
            var sizes = GameSettings.Resolutions();
            var labels = sizes.ConvertAll(s => $"{s.x} x {s.y}");
            int current = Mathf.Max(0, sizes.IndexOf(new Vector2Int(Screen.width, Screen.height)));
            card.Add(Row("Resolution", Cycle(labels, current, i => GameSettings.SetResolution(sizes[i]))));
            var scales = new List<string>();
            foreach (var s in GameSettings.UiScales) scales.Add($"{Mathf.RoundToInt(s * 100)}%");
            card.Add(Row("Interface size", Cycle(scales, Array.IndexOf(GameSettings.UiScales, GameSettings.UiScale),
                i => GameSettings.SetUiScale(GameSettings.UiScales[i]))));

            card.Add(Section("PROGRESS"));
            VisualElement overlay = null;
            card.Add(Row("Start the campaign over", Ui.Button("Reset progress", () => ConfirmReset(host, app, overlay), "btn--small", "btn--danger")));

            var actions = Ui.Div("modal-actions");
            actions.Add(Ui.Button("Done", () => Modal.Close(overlay), "btn--big", "btn--primary"));
            card.Add(actions);
            overlay = Modal.Show(host, card);
        }

        public static void ConfirmQuit(VisualElement host)
        {
            VisualElement overlay = null;
            var card = Modal.Card(null, "Quit Silicon Forge?", "Your progress is saved. The station will wait for you.",
                "Cancel", () => Modal.Close(overlay),
                "Quit", GameSettings.Quit);
            overlay = Modal.Show(host, card);
        }

        static void ConfirmReset(VisualElement host, GameApp app, VisualElement settingsOverlay)
        {
            VisualElement overlay = null;
            var card = Modal.Card(Ui.Badge(false), "Reset all progress?",
                "This deletes every chip you designed, every contract you completed and the station log. Your settings stay. It can't be undone.",
                "Cancel", () => Modal.Close(overlay),
                "Reset", () =>
                {
                    Modal.Close(overlay);
                    Modal.Close(settingsOverlay);
                    app.ResetProgress();
                });
            overlay = Modal.Show(host, card);
        }

        // ------------------------------------------------------------------ controls

        static VisualElement Section(string title)
        {
            var label = Ui.Text(title, "t-eyebrow", "settings__section");
            return label;
        }

        static VisualElement Row(string label, VisualElement control)
        {
            var row = Ui.Div("settings__row");
            row.Add(Ui.Text(label, "settings__label"));
            row.Add(Ui.Div("spacer"));
            row.Add(control);
            return row;
        }

        static VisualElement Switch(bool on, Action<bool> changed)
        {
            var track = Ui.Div("switch", "settings__switch");
            track.Add(Ui.Div("switch__knob"));
            track.EnableInClassList("switch--on", on);
            track.AddManipulator(new Clickable(() =>
            {
                on = !on;
                track.EnableInClassList("switch--on", on);
                changed(on);
                Sfx.Play(SfxId.Toggle);
            }));
            return track;
        }

        /// <summary>Previous / value / next. Wraps round at both ends.</summary>
        static VisualElement Cycle(List<string> options, int index, Action<int> changed)
        {
            var box = Ui.Div("cycle");
            var value = Ui.Text(options[Mathf.Clamp(index, 0, options.Count - 1)], "cycle__value");
            void Step(int delta)
            {
                index = (index + delta + options.Count) % options.Count;
                value.text = options[index];
                changed(index);
            }
            var prev = Arrow(() => Step(-1), "cycle__chevron--left");
            var next = Arrow(() => Step(1), "cycle__chevron--right");
            prev.SetEnabled(options.Count > 1);
            next.SetEnabled(options.Count > 1);
            box.Add(prev);
            box.Add(value);
            box.Add(next);
            return box;
        }

        /// <summary>A small button with a chevron drawn from borders, so it doesn't depend on the font having arrow glyphs.</summary>
        static UiButton Arrow(Action onClick, string direction)
        {
            var button = Ui.Button("", onClick, "btn--small", "btn--ghost", "cycle__arrow");
            button.Label.style.display = DisplayStyle.None;
            var chevron = Ui.Div("cycle__chevron", direction);
            chevron.pickingMode = PickingMode.Ignore;
            button.Add(chevron);
            return button;
        }

        static VisualElement VolumeSlider()
        {
            var box = Ui.Div("slider-box");
            var track = Ui.Div("slider");
            var fill = Ui.Div("slider__fill");
            var knob = Ui.Div("slider__knob");
            fill.pickingMode = PickingMode.Ignore;
            knob.pickingMode = PickingMode.Ignore;
            track.Add(fill);
            track.Add(knob);
            var readout = Ui.Text("", "slider__value");

            void Show(float v)
            {
                fill.style.width = Length.Percent(v * 100f);
                knob.style.left = Length.Percent(v * 100f);
                readout.text = $"{Mathf.RoundToInt(v * 100)}%";
            }
            void SetFrom(Vector2 panelPosition)
            {
                float width = track.layout.width;
                if (width <= 0) return;
                float v = Mathf.Clamp01(track.WorldToLocal(panelPosition).x / width);
                GameSettings.SetVolume(v);
                Show(v);
            }

            track.RegisterCallback<PointerDownEvent>(e =>
            {
                track.CapturePointer(e.pointerId);
                SetFrom(e.position);
                e.StopPropagation();
            });
            track.RegisterCallback<PointerMoveEvent>(e => { if (track.HasPointerCapture(e.pointerId)) SetFrom(e.position); });
            track.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!track.HasPointerCapture(e.pointerId)) return;
                track.ReleasePointer(e.pointerId);
                Sfx.Play(SfxId.Place); // let the player hear the new level
            });

            Show(GameSettings.Volume);
            box.Add(track);
            box.Add(readout);
            return box;
        }
    }
}
