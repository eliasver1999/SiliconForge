using SiliconForge.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/SiliconForge/Scenes/Main.unity";

        /// <summary>Creates the single game scene, registers it for builds and sets the player basics.</summary>
        [MenuItem("Silicon Forge/Create Main Scene")]
        public static void CreateMainScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camera = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camera.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = GameApp.Background;
            camera.AddComponent<AudioListener>();

            new GameObject("Silicon Forge").AddComponent<GameApp>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            PlayerSettings.productName = "Silicon Forge";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            Debug.Log("Silicon Forge: main scene created at " + ScenePath);
        }

        [MenuItem("Silicon Forge/Delete Save File")]
        public static void DeleteSave()
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath, "siliconforge_save.json");
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            Debug.Log("Silicon Forge: save file deleted.");
        }
    }
}
