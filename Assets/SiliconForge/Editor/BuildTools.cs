using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    /// <summary>Windows builds, and the material assets the workshop needs so a build keeps its looks.</summary>
    public static class BuildTools
    {
        public const string OutputFolder = "Builds/Windows";
        public const string ExeName = "SiliconForge.exe";
        const string MaterialFolder = "Assets/SiliconForge/Resources/SiliconForge";

        /// <summary>
        /// The workshop creates its materials in code. A build only keeps shader variants that some material asset uses,
        /// so these two assets make sure URP Lit, with and without emission, ends up in the player.
        /// </summary>
        [MenuItem("Silicon Forge/Create Workshop Materials")]
        public static void CreateWorkshopMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Debug.LogError("Silicon Forge: URP Lit shader not found."); return; }

            var plain = new Material(shader);
            Save(plain, "WorkshopLit");

            var glow = new Material(shader);
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_EmissionColor", Color.white);
            glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            Save(glow, "WorkshopLitEmissive");
            AssetDatabase.SaveAssets();
            Debug.Log("Silicon Forge: workshop materials created.");
        }

        static void Save(Material material, string name)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(material, path);
        }

        [MenuItem("Silicon Forge/Build Windows")]
        public static void BuildWindows()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/WorkshopLit.mat") == null) CreateWorkshopMaterials();
            Directory.CreateDirectory(OutputFolder);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.ScenePath },
                locationPathName = Path.Combine(OutputFolder, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            string text = $"result={s.result} errors={s.totalErrors} warnings={s.totalWarnings} size={s.totalSize / (1024 * 1024)}MB time={s.totalTime.TotalSeconds:0}s";
            File.WriteAllText(Path.Combine("Builds", "build_result.txt"), text);
            if (s.result == BuildResult.Succeeded) Debug.Log("Silicon Forge: build finished. " + text);
            else Debug.LogError("Silicon Forge: build failed. " + text);
        }
    }
}
