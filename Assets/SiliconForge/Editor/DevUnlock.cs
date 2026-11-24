using System.Text;
using SiliconForge.Core;
using SiliconForge.Game;
using UnityEditor;
using UnityEngine;

namespace SiliconForge.EditorTools
{
    /// <summary>
    /// Development shortcut: certifies every chip contract with its reference design (see <see cref="ReferenceDesigns"/>),
    /// so later eras (which need earlier chips) can be playtested straight away.
    /// Your own boards are left empty; replaying a contract and passing it replaces the reference chip with yours.
    /// </summary>
    public static class DevUnlock
    {
        [MenuItem("Silicon Forge/Unlock All Contracts")]
        public static void UnlockAll()
        {
            var save = Build(out string report);
            if (EditorApplication.isPlaying && GameApp.Instance != null) GameApp.Instance.ReplaceSave(save);
            else SaveSystem.Write(save);
            Debug.Log(report);
        }

        static SaveData Build(out string report)
        {
            var save = new SaveData();
            var log = new StringBuilder("Silicon Forge: unlocked all contracts with reference designs.\n");

            foreach (var result in ReferenceDesigns.CertifyAll(new PartCatalog()))
            {
                var level = result.level;
                if (!result.report.AllPassed) log.AppendLine($"  {level.id}: reference FAILED {result.report.passed}/{result.report.total}");
                var progress = save.Progress(level.id);
                progress.solved = true;
                progress.bestGates = result.gates;
                progress.stars = Campaign.Stars(level, result.gates);
                save.SetChip(result.chip);
                log.AppendLine($"  {level.id}: {result.gates} gates (par {level.par})");
            }

            // Every workshop except the last is marked as built, so the whole campaign is open.
            var lastWorkshop = Campaign.Levels.FindLast(l => l.workshop);
            foreach (var level in Campaign.Levels)
            {
                if (!level.workshop || level == lastWorkshop) continue;
                var progress = save.Progress(level.id);
                progress.solved = true;
                progress.stars = 3;
            }
            // Mark the story as read so testing isn't interrupted by log cards. The ending still plays after Press Start.
            save.MarkSeen(Story.Prologue.id);
            foreach (var level in Campaign.Levels) save.MarkSeen(level.id);
            save.machinePsuWatts = 100;
            save.machineCooler = 2;
            report = log.ToString();
            return save;
        }
    }
}
