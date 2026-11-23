using Unity.AI.MCP.Editor;
using UnityEditor;

namespace SiliconForge.EditorTools
{
    /// <summary>
    /// Development helper: starts the Unity MCP bridge when the editor loads, so an AI coding agent
    /// can drive this project without opening the MCP settings page first.
    /// Respects the "stopped" switch in Project Settings > AI > Unity MCP.
    /// </summary>
    [InitializeOnLoad]
    static class McpBridgeAutoStart
    {
        static McpBridgeAutoStart()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool("MCPBridge.ExplicitlyStopped", false)) return;
                if (!UnityMCPBridge.IsRunning) UnityMCPBridge.Start();
            };
        }
    }
}
