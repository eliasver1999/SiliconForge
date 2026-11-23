using System;
using System.IO;
using SiliconForge.Core;
using UnityEngine;

namespace SiliconForge.Game
{
    public static class SaveSystem
    {
        static string FilePath => Path.Combine(Application.persistentDataPath, "siliconforge_save.json");

        public static SaveData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath));
                    if (data != null)
                    {
                        data.progress ??= new System.Collections.Generic.List<LevelProgress>();
                        data.work ??= new System.Collections.Generic.List<LevelWork>();
                        data.chips ??= new System.Collections.Generic.List<ChipDefinition>();
                        data.seenLogs ??= new System.Collections.Generic.List<string>();
                        return data;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Silicon Forge: could not read the save file, starting fresh. {e.Message}");
            }
            return new SaveData();
        }

        public static void Write(SaveData data)
        {
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Silicon Forge: could not write the save file. {e.Message}");
            }
        }
    }
}
