using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game
{
    /// <summary>
    /// Player preferences: volume, interface size and display mode. Kept in PlayerPrefs, apart from the save file,
    /// so resetting progress never resets them. Sound on/off lives in <see cref="Sfx"/>.
    /// </summary>
    public static class GameSettings
    {
        const string VolumeKey = "sf_volume";
        const string ScaleKey = "sf_ui_scale";

        public static readonly float[] UiScales = { 0.85f, 1f, 1.15f, 1.3f };

        public static float Volume { get; private set; } = 0.8f;
        public static float UiScale { get; private set; } = 1f;

        static PanelSettings panel;

        public static void Init(PanelSettings panelSettings)
        {
            panel = panelSettings;
            Volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, 0.8f));
            UiScale = PlayerPrefs.GetFloat(ScaleKey, 1f);
            if (!UiScales.Contains(UiScale)) UiScale = 1f;
            AudioListener.volume = Volume;
            panel.scale = UiScale;
        }

        public static void SetVolume(float volume)
        {
            Volume = Mathf.Clamp01(volume);
            AudioListener.volume = Volume;
            PlayerPrefs.SetFloat(VolumeKey, Volume);
        }

        public static void SetUiScale(float scale)
        {
            UiScale = scale;
            if (panel != null) panel.scale = scale;
            PlayerPrefs.SetFloat(ScaleKey, scale);
        }

        public static bool Fullscreen => Screen.fullScreenMode != FullScreenMode.Windowed;

        public static void SetFullscreen(bool fullscreen)
        {
            var mode = fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (fullscreen)
            {
                // Fullscreen uses the monitor's own resolution.
                var native = Screen.currentResolution;
                Screen.SetResolution(native.width, native.height, mode);
            }
            else
            {
                Screen.fullScreenMode = mode;
            }
        }

        /// <summary>Window sizes the display supports, largest first, without duplicate refresh rates.</summary>
        public static List<Vector2Int> Resolutions()
        {
            var list = Screen.resolutions
                .Select(r => new Vector2Int(r.width, r.height))
                .Where(r => r.x >= 1024 && r.y >= 640)
                .Distinct()
                .OrderByDescending(r => r.x * r.y)
                .ToList();
            var current = new Vector2Int(Screen.width, Screen.height);
            if (!list.Contains(current)) list.Insert(0, current);
            return list;
        }

        public static void SetResolution(Vector2Int size) => Screen.SetResolution(size.x, size.y, Screen.fullScreenMode);

        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
