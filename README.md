# Silicon Forge

A hardware puzzle game made in Unity 6. You are an engineer stranded on Kestrel Station. You build chips up from NAND gates to a working processor, then put a real computer together on the workbench to call for help.

- **Seven eras, 29 contracts.** Logic gates, arithmetic, memory, a CPU, RAM and a display, then a press detector and Processor III.
- **Chip contracts** are checked by a truth table or a timed test sequence. You earn stars for staying at or under par (counted in NAND gates).
- **Workshop contracts** happen in a 3D workshop. You install your chips, choose a power supply and cooler, program the processor in a small assembly language, and power the machine on.

## Requirements

- Unity **6000.6.3f1** with the Universal Render Pipeline (the project's packages install on first open).
- Optional: the [.NET SDK](https://dotnet.microsoft.com/download) (9 or later) to run the core tests without Unity.

## Play in the editor

1. Open the project in Unity Hub.
2. Open `Assets/SiliconForge/Scenes/Main.unity` (if it's missing, run **Silicon Forge → Create Main Scene**).
3. Press Play. **Silicon Forge → Game View 1920x1080** gives the intended layout.

Useful menu items while developing:

| Menu item | What it does |
| --- | --- |
| Silicon Forge → Unlock All Contracts | Certifies every chip with its reference design so later eras open straight away. |
| Silicon Forge → Delete Save File | Starts the campaign from scratch. |
| Silicon Forge → Preview Workbench in Scene View | Builds the 3D workshop in edit mode so you can look at it without playing. |

## Build for Windows

Use **Silicon Forge → Build Windows**. The game is written to `Builds/Windows/SiliconForge.exe`. You can also build from the command line with the editor closed:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe" -batchmode -quit -projectPath . -executeMethod SiliconForge.EditorTools.BuildTools.BuildWindows -logFile Builds\build.log
```

The result is written to `Builds/build_result.txt`. Launching the game with `-sf-smoketest <folder>` saves screenshots of the menu, the settings panel and a finished machine into that folder, then quits.

## Tests

The tests cover the simulator and campaign data. They check that:

- every contract can be solved at exactly par with its reference design;
- the workshop goals accept the right programs and reject wrong ones;
- the assembler accepts and rejects the right programs for each processor;
- the campaign data is consistent.

The tests live in `Assets/SiliconForge/Tests/Editor` and only use the engine-free `SiliconForge.Core` assembly, so there are two ways to run them.

**In Unity:** open **Window → General → Test Runner** and run the EditMode tests. From the command line (editor closed):

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -testResults TestResults.xml
```

**Without Unity (fast):**

```powershell
dotnet test Tests/Core
```

## Project layout

| Folder | Contents |
| --- | --- |
| `Assets/SiliconForge/Core` | Pure C# with no Unity references: the circuit model, netlist compiler, test bench, ISA and assembler, machines, campaign, story and reference designs. |
| `Assets/SiliconForge/Game` | The Unity side: app and save system, UI Toolkit screens, the 3D workshop, sound and settings. |
| `Assets/SiliconForge/Editor` | Editor tools: scene setup, builds, dev unlock, Scene view preview. |
| `Assets/SiliconForge/Tests/Editor` | NUnit tests for the core. |
| `Assets/SiliconForge/Resources/SiliconForge` | The USS stylesheet, the theme and the workshop material templates. |
| `Tests/Core` | A .NET project that runs the same tests outside Unity. |
