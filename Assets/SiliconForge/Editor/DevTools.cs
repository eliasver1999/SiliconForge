using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    /// <summary>Editor helpers for testing: a fixed 1080p Game view and screenshots.</summary>
    public static class DevTools
    {
        [MenuItem("Silicon Forge/Game View 1920x1080")]
        public static void SetGameView1080p() => SetGameViewSize(1920, 1080);

        public static void SetGameViewSize(int width, int height)
        {
            var asm = typeof(EditorWindow).Assembly;
            var gameViewType = asm.GetType("UnityEditor.GameView");
            var sizesType = asm.GetType("UnityEditor.GameViewSizes");
            var singleton = asm.GetType("UnityEditor.ScriptableSingleton`1").MakeGenericType(sizesType);
            var instance = singleton.GetProperty("instance").GetValue(null);
            var group = instance.GetType().GetProperty("currentGroup").GetValue(instance);
            var groupType = group.GetType();
            int count = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null);
            int found = -1;
            for (int i = 0; i < count; i++)
            {
                var size = groupType.GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                var st = size.GetType();
                if ((int)st.GetProperty("width").GetValue(size) == width &&
                    (int)st.GetProperty("height").GetValue(size) == height &&
                    st.GetProperty("sizeType").GetValue(size).ToString() == "FixedResolution")
                {
                    found = i;
                    break;
                }
            }
            if (found < 0)
            {
                var sizeType = asm.GetType("UnityEditor.GameViewSize");
                var enumType = asm.GetType("UnityEditor.GameViewSizeType");
                var ctor = sizeType.GetConstructor(new[] { enumType, typeof(int), typeof(int), typeof(string) });
                var newSize = ctor.Invoke(new object[] { Enum.Parse(enumType, "FixedResolution"), width, height, $"{width}x{height}" });
                groupType.GetMethod("AddCustomSize").Invoke(group, new[] { newSize });
                found = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null) - 1;
            }
            var window = EditorWindow.GetWindow(gameViewType);
            gameViewType.GetMethod("SizeSelectionCallback", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(window, new object[] { found, null });
        }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Reads a private field (test automation only).</summary>
        public static object Get(object target, string field) => target.GetType().GetField(field, Any)?.GetValue(target);

        /// <summary>Writes a private field (test automation only).</summary>
        public static void Set(object target, string field, object value) => target.GetType().GetField(field, Any)?.SetValue(target, value);

        /// <summary>Calls a private method (test automation only).</summary>
        public static object Call(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, Any);
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            var ps = m.GetParameters();
            for (int i = 0; i < args.Length && i < ps.Length; i++)
                if (ps[i].ParameterType.IsEnum && args[i] is int n) args[i] = Enum.ToObject(ps[i].ParameterType, n);
            return m.Invoke(target, args);
        }

        public static void Screenshot(string path)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            ScreenCapture.CaptureScreenshot(path);
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
