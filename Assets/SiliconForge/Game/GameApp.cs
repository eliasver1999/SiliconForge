using SiliconForge.Core;
using SiliconForge.Game.UI;
using SiliconForge.Game.Workshop;
using UnityEngine;
using UnityEngine.UIElements;

namespace SiliconForge.Game
{
    public interface IScreen
    {
        VisualElement Root { get; }
        void Tick(float dt);
        void Dispose();
    }

    /// <summary>Owns the save, the part catalog, the UI panel and whichever screen is showing.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class GameApp : MonoBehaviour
    {
        public static readonly Color Background = new Color32(13, 16, 21, 255);

        public static GameApp Instance { get; private set; }
        public PartCatalog Catalog { get; private set; }
        public SaveData Save { get; private set; }
        public Camera MainCamera { get; private set; }
        public VisualElement Root { get; private set; }

        UIDocument document;
        IScreen screen;

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 120;
            Application.runInBackground = true;
            Save = SaveSystem.Load();
            RebuildCatalog();
            EnsureCamera();
            CreateUi();
            ShowCampaign();
        }

        void Update() => screen?.Tick(Time.unscaledDeltaTime);

        void OnDestroy()
        {
            screen?.Dispose();
            if (Instance == this) Instance = null;
        }

        void RebuildCatalog()
        {
            Catalog = new PartCatalog();
            foreach (var chip in Save.chips) Catalog.SetChip(chip);
        }

        void EnsureCamera()
        {
            MainCamera = Camera.main;
            if (MainCamera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                MainCamera = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            MainCamera.clearFlags = CameraClearFlags.SolidColor;
            MainCamera.backgroundColor = Background;
        }

        void CreateUi()
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = "Silicon Forge Panel";
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("SiliconForge/SiliconForgeTheme");
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;

            var uiObject = new GameObject("UI");
            uiObject.transform.SetParent(transform, false);
            uiObject.SetActive(false);
            document = uiObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            uiObject.SetActive(true);

            Root = document.rootVisualElement;
            Root.styleSheets.Add(Resources.Load<StyleSheet>("SiliconForge/SiliconForge"));
            Root.AddToClassList("sf-root");
        }

        public void ShowScreen(IScreen next)
        {
            screen?.Dispose();
            Root.Clear();
            screen = next;
            Root.Add(next.Root);
        }

        public void ShowCampaign() => ShowScreen(new CampaignScreen(this));

        public void OpenLevel(LevelSpec level)
        {
            if (level == null) { ShowCampaign(); return; }
            if (level.workshop) ShowScreen(new WorkshopScreen(this, level));
            else ShowScreen(new BenchScreen(this, level));
        }

        public bool IsUnlocked(LevelSpec level)
        {
            int i = Campaign.Levels.IndexOf(level);
            return i <= 0 || Save.IsSolved(Campaign.Levels[i - 1].id);
        }

        /// <summary>Records a solve. A certified chip replaces the old design everywhere it is used.</summary>
        public void RecordSolve(LevelSpec level, int gates, int stars, CircuitData design)
        {
            var progress = Save.Progress(level.id);
            progress.bestGates = progress.solved ? Mathf.Min(progress.bestGates, gates) : gates;
            progress.stars = Mathf.Max(progress.stars, stars);
            progress.solved = true;

            if (design != null && !string.IsNullOrEmpty(level.chipId))
            {
                var chip = new ChipDefinition
                {
                    id = level.chipId,
                    name = level.chipName,
                    inputNames = new System.Collections.Generic.List<string>(level.inputs),
                    outputNames = new System.Collections.Generic.List<string>(level.outputs),
                    circuit = design.Clone()
                };
                Save.SetChip(chip);
                Catalog.SetChip(chip);
            }
            Persist();
        }

        public void Persist() => SaveSystem.Write(Save);

        public void ResetProgress() => ReplaceSave(new SaveData());

        /// <summary>Swaps in a whole save (used by Reset and the dev unlock tool) and returns to the contracts screen.</summary>
        public void ReplaceSave(SaveData data)
        {
            Save = data;
            Persist();
            RebuildCatalog();
            ShowCampaign();
        }
    }
}
