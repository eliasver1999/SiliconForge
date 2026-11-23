using SiliconForge.Game.Workshop;
using UnityEditor;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    /// <summary>
    /// The game builds everything at runtime, so the scene looks empty while editing.
    /// This puts the finished, powered workbench in the Scene view to look at. It is never saved into the scene
    /// and is removed automatically before Play mode starts.
    /// </summary>
    [InitializeOnLoad]
    public static class WorkbenchPreview
    {
        const string Name = "Workbench Preview (not saved)";

        static WorkbenchPreview()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) Clear();
            };
        }

        [MenuItem("Silicon Forge/Preview Workbench in Scene View")]
        public static void Show()
        {
            if (EditorApplication.isPlaying) { Debug.Log("Silicon Forge: the preview is for edit mode."); return; }
            Clear();
            var root = new GameObject(Name);
            var rig = root.AddComponent<WorkshopRig>();
            rig.Build(null);
            rig.Showcase();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;

            var view = SceneView.lastActiveSceneView;
            if (view != null)
            {
                view.LookAt(new Vector3(0.3f, 0.15f, 0f), Quaternion.Euler(32f, -24f, 0f), 2.2f);
                view.Repaint();
            }
        }

        [MenuItem("Silicon Forge/Clear Workbench Preview")]
        public static void Clear()
        {
            foreach (var rig in Resources.FindObjectsOfTypeAll<WorkshopRig>())
                if (rig != null && rig.gameObject.name == Name && !EditorUtility.IsPersistent(rig.gameObject))
                    Object.DestroyImmediate(rig.gameObject);
        }
    }
}
