using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SiliconForge.Game.Workshop
{
    /// <summary>Marks a clickable DIP switch knob.</summary>
    public sealed class DipSwitch : MonoBehaviour
    {
        public int bit;
    }

    public sealed class PowerButton : MonoBehaviour { }

    /// <summary>The big arcade button the player's programs can read.</summary>
    public sealed class ArcadeButton : MonoBehaviour { }

    /// <summary>
    /// Builds the 3D workbench out of primitives: an open-frame case, a mainboard with DIP switches,
    /// LEDs and a 7-segment display, the player's chip and a power supply with a spinning fan.
    /// </summary>
    public sealed class WorkshopRig : MonoBehaviour
    {
        public event Action<int> SwitchClicked;
        public event Action PowerButtonClicked;
        public event Action ArcadeButtonClicked;

        static readonly Color Amber = new Color(1f, 0.62f, 0.2f);
        static readonly int[] SegmentMasks = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

        Camera cam;
        Shader litShader;
        Material plainTemplate, glowTemplate;
        readonly List<GameObject> spawned = new List<GameObject>();

        GameObject caseRoot, boardRoot, chipRoot, psuRoot, cableRoot, counterRoot, clockRoot;
        GameObject ramRoot, heatsinkRoot, fanCoolerRoot, monitorRoot, pressRoot, arcadeRoot;
        Transform arcadeCap;
        Renderer arcadeGlow;
        Vector3 arcadeCapHome;
        Transform coolerFan;
        Renderer clockLed, chipLid;
        readonly Renderer[] pixels = new Renderer[16];
        readonly int[] shownRows = { -1, -1, -1, -1 };
        Transform fan;
        Renderer powerRing, powerLed;
        Light displayGlow;
        readonly Transform[] knobs = new Transform[8];
        readonly Renderer[] knobRenderers = new Renderer[8];
        readonly Vector3[] knobHome = new Vector3[8];
        readonly Renderer[] leds = new Renderer[5];
        readonly Renderer[,] segments = new Renderer[2, 7];

        Material knobOff, knobOn;
        float fanSpeed, fanTarget;
        bool powered;
        int outputs, ledBits;
        int testPattern = -1;

        float yaw = -26f, pitch = 36f, distance = 3.4f;
        float yawTarget, pitchTarget, distanceTarget;
        Vector3 focus = new Vector3(0.02f, 0.14f, 0f);

        AmbientMode savedAmbientMode;
        Color savedSky, savedEquator, savedGround;
        bool savedFog;

        public bool Powered => powered;

        // ------------------------------------------------------------------ build

        public void Build(Camera camera)
        {
            cam = camera;
            plainTemplate = Resources.Load<Material>("SiliconForge/WorkshopLit");
            glowTemplate = Resources.Load<Material>("SiliconForge/WorkshopLitEmissive");
            litShader = plainTemplate != null ? plainTemplate.shader : Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null) litShader = Shader.Find("Standard");

            yawTarget = yaw; pitchTarget = pitch; distanceTarget = distance;
            SetupCameraAndLighting();
            BuildDesk();
            BuildCase();
            BuildBoard();
            BuildPsu();
            caseRoot.SetActive(false);
            boardRoot.SetActive(false);
            chipRoot.SetActive(false);
            psuRoot.SetActive(false);
            counterRoot.SetActive(false);
            clockRoot.SetActive(false);
            BuildMonitor();
            BuildArcadeButton();
            arcadeRoot.SetActive(false);
            pressRoot.SetActive(false);
            ramRoot.SetActive(false);
            heatsinkRoot.SetActive(false);
            fanCoolerRoot.SetActive(false);
            monitorRoot.SetActive(false);
            SetInputs(0);
            ApplyOutputs();
        }

        void OnDestroy()
        {
            Sfx.StopHum();
            RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientSkyColor = savedSky;
            RenderSettings.ambientEquatorColor = savedEquator;
            RenderSettings.ambientGroundColor = savedGround;
            RenderSettings.fog = savedFog;
            foreach (var go in spawned) if (go != null) Destroy(go);
            if (cam != null)
            {
                cam.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                cam.rect = new Rect(0, 0, 1, 1);
                var data = cam.GetUniversalAdditionalCameraData();
                if (data != null) data.renderPostProcessing = false;
            }
        }

        void SetupCameraAndLighting()
        {
            savedAmbientMode = RenderSettings.ambientMode;
            savedSky = RenderSettings.ambientSkyColor;
            savedEquator = RenderSettings.ambientEquatorColor;
            savedGround = RenderSettings.ambientGroundColor;
            savedFog = RenderSettings.fog;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.20f, 0.24f, 0.30f);
            RenderSettings.ambientEquatorColor = new Color(0.10f, 0.11f, 0.13f);
            RenderSettings.ambientGroundColor = new Color(0.04f, 0.04f, 0.05f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.16f;
            RenderSettings.fogColor = GameApp.Background;

            cam.fieldOfView = 34f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 60f;
            var camData = cam.GetUniversalAdditionalCameraData();
            if (camData != null)
            {
                camData.renderPostProcessing = true;
                camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.95f);
            bloom.intensity.Override(1.1f);
            bloom.scatter.Override(0.7f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.ACES);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.28f);
            vignette.smoothness.Override(0.5f);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.35f);
            color.contrast.Override(8f);

            var volumeGo = Track(new GameObject("Workshop Volume"));
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10;
            volume.profile = profile;

            var key = Track(new GameObject("Key Light")).AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.94f, 0.86f);
            key.intensity = 1.25f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.8f;
            key.transform.rotation = Quaternion.Euler(52f, -32f, 0f);

            var rim = Track(new GameObject("Rim Light")).AddComponent<Light>();
            rim.type = LightType.Point;
            rim.color = new Color(0.45f, 0.65f, 1f);
            rim.intensity = 1.0f;
            rim.range = 4f;
            rim.transform.position = new Vector3(-0.6f, 1.0f, 1.3f);

            displayGlow = Track(new GameObject("Display Glow")).AddComponent<Light>();
            displayGlow.type = LightType.Point;
            displayGlow.color = Amber;
            displayGlow.intensity = 0f;
            displayGlow.range = 0.6f;
            displayGlow.transform.position = new Vector3(0.46f, 0.3f, 0.16f);
        }

        void BuildDesk()
        {
            Box(null, "Desk", new Vector3(0, -0.05f, 0), new Vector3(9f, 0.1f, 6f), Mat(new Color(0.075f, 0.082f, 0.095f), 0.3f));
            Box(null, "Desk Mat", new Vector3(0, 0.002f, 0), new Vector3(2.5f, 0.004f, 1.7f), Mat(new Color(0.11f, 0.125f, 0.15f), 0.12f));
        }

        void BuildCase()
        {
            caseRoot = Track(new GameObject("Case"));
            var plate = Mat(new Color(0.62f, 0.64f, 0.68f), 0.55f, 0.85f);
            var frame = Mat(new Color(0.16f, 0.17f, 0.2f), 0.5f, 0.7f);

            Box(caseRoot.transform, "Base Plate", new Vector3(0, 0.019f, 0), new Vector3(1.72f, 0.03f, 1.16f), plate);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Box(caseRoot.transform, "Post", new Vector3(sx * 0.83f, 0.28f, sz * 0.55f), new Vector3(0.035f, 0.5f, 0.035f), frame);
                Cylinder(caseRoot.transform, "Foot", new Vector3(sx * 0.78f, 0.004f, sz * 0.5f), new Vector3(0.07f, 0.004f, 0.07f), Mat(new Color(0.02f, 0.02f, 0.02f), 0.2f));
            }
            // Open front: only the back rail spans the width, so the board stays in clear view.
            Box(caseRoot.transform, "Rail", new Vector3(0, 0.53f, 0.55f), new Vector3(1.69f, 0.03f, 0.03f), frame);
            for (int sx = -1; sx <= 1; sx += 2)
                Box(caseRoot.transform, "Rail", new Vector3(sx * 0.83f, 0.53f, 0), new Vector3(0.03f, 0.03f, 1.13f), frame);

            var ring = Cylinder(caseRoot.transform, "Power Ring", new Vector3(-0.72f, 0.036f, -0.47f), new Vector3(0.09f, 0.006f, 0.09f), Mat(new Color(0.1f, 0.1f, 0.1f), 0.6f, 0f, true));
            powerRing = ring.GetComponent<Renderer>();
            var button = Cylinder(caseRoot.transform, "Power Button", new Vector3(-0.72f, 0.045f, -0.47f), new Vector3(0.07f, 0.012f, 0.07f), Mat(new Color(0.08f, 0.085f, 0.1f), 0.7f, 0.6f));
            button.AddComponent<PowerButton>();
        }

        void BuildBoard()
        {
            boardRoot = Track(new GameObject("Mainboard"));
            boardRoot.transform.position = new Vector3(0.16f, 0.034f, 0f);
            var t = boardRoot.transform;

            var gold = Mat(new Color(0.78f, 0.62f, 0.34f), 0.6f, 0.9f);
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
                Cylinder(t, "Standoff", new Vector3(sx * 0.46f, 0.01f, sz * 0.35f), new Vector3(0.025f, 0.01f, 0.025f), gold);

            Box(t, "PCB", new Vector3(0, 0.03f, 0), new Vector3(1.0f, 0.02f, 0.8f), Mat(new Color(0.04f, 0.2f, 0.15f), 0.6f));

            // Copper traces radiating from the socket.
            var trace = Mat(new Color(0.7f, 0.55f, 0.3f), 0.55f, 0.9f);
            var socketPos = new Vector3(0f, 0f, 0.06f);
            for (int i = 0; i < 8; i++)
            {
                float z = socketPos.z - 0.105f + i * 0.03f;
                Box(t, "Trace", new Vector3(-0.27f, 0.0405f, z), new Vector3(0.28f, 0.002f, 0.006f), trace);
                Box(t, "Trace", new Vector3(0.27f, 0.0405f, z), new Vector3(0.28f, 0.002f, 0.006f), trace);
            }
            for (int i = 0; i < 5; i++)
            {
                float x = -0.06f + i * 0.03f;
                Box(t, "Trace", new Vector3(x, 0.0405f, -0.17f), new Vector3(0.006f, 0.002f, 0.2f), trace);
            }

            Box(t, "Socket", new Vector3(socketPos.x, 0.047f, socketPos.z), new Vector3(0.26f, 0.014f, 0.26f), Mat(new Color(0.86f, 0.83f, 0.74f), 0.35f));
            Box(t, "Power Header", new Vector3(-0.44f, 0.055f, 0.22f), new Vector3(0.05f, 0.03f, 0.1f), Mat(new Color(0.92f, 0.92f, 0.9f), 0.3f));

            // DIP switches: A3..A0 then B3..B0, most significant bit on the left.
            var housing = Mat(new Color(0.72f, 0.1f, 0.12f), 0.5f);
            Box(t, "DIP Housing", new Vector3(-0.27f, 0.055f, -0.29f), new Vector3(0.38f, 0.03f, 0.075f), housing);
            knobOff = Mat(new Color(0.9f, 0.9f, 0.88f), 0.5f);
            knobOn = Mat(new Color(1f, 0.7f, 0.3f), 0.5f, 0f, true, Amber * 1.2f);
            for (int bit = 0; bit < 8; bit++)
            {
                int slot = bit < 4 ? 3 - bit : 4 + (7 - bit);
                float x = -0.27f - 0.165f + slot * 0.04f + (slot >= 4 ? 0.01f : 0f);
                var home = new Vector3(x, 0.074f, -0.3f);
                var knob = Box(t, $"Switch {bit}", home, new Vector3(0.022f, 0.014f, 0.03f), knobOff);
                var col = knob.GetComponent<BoxCollider>();
                col.size = new Vector3(1.6f, 3f, 2f);
                knob.AddComponent<DipSwitch>().bit = bit;
                knobs[bit] = knob.transform;
                knobRenderers[bit] = knob.GetComponent<Renderer>();
                knobHome[bit] = home;
            }

            // Five LEDs: C S3 S2 S1 S0.
            for (int i = 0; i < 5; i++)
            {
                int bit = 4 - i;
                var led = Cylinder(t, $"LED {bit}", new Vector3(0.06f + i * 0.055f, 0.05f, -0.3f), new Vector3(0.026f, 0.012f, 0.026f),
                    Mat(new Color(0.22f, 0.14f, 0.06f), 0.8f, 0f, true));
                leds[bit] = led.GetComponent<Renderer>();
            }
            var pled = Cylinder(t, "Power LED", new Vector3(0.42f, 0.05f, -0.33f), new Vector3(0.018f, 0.01f, 0.018f),
                Mat(new Color(0.05f, 0.15f, 0.08f), 0.8f, 0f, true));
            powerLed = pled.GetComponent<Renderer>();

            // Seven-segment display module standing at the back right.
            var module = new Vector3(0.3f, 0.13f, 0.3f);
            Box(t, "Display", module, new Vector3(0.26f, 0.17f, 0.035f), Mat(new Color(0.025f, 0.025f, 0.03f), 0.85f));
            Box(t, "Display Leg", new Vector3(module.x, 0.055f, module.z), new Vector3(0.2f, 0.04f, 0.03f), Mat(new Color(0.05f, 0.05f, 0.06f), 0.4f));
            for (int d = 0; d < 2; d++)
                BuildDigit(t, d, new Vector3(module.x - 0.055f + d * 0.11f, module.y, module.z - 0.019f));

            chipRoot = Track(new GameObject("Chip"));
            chipRoot.transform.SetParent(t, false);
            chipRoot.transform.localPosition = new Vector3(socketPos.x, 0.068f, socketPos.z);
            Box(chipRoot.transform, "Package", Vector3.zero, new Vector3(0.2f, 0.026f, 0.2f), Mat(new Color(0.035f, 0.035f, 0.04f), 0.75f));
            chipLid = Box(chipRoot.transform, "Lid", new Vector3(0, 0.015f, 0), new Vector3(0.14f, 0.004f, 0.14f), Mat(new Color(0.38f, 0.4f, 0.43f), 0.35f, 0.6f, true)).GetComponent<Renderer>();
            BuildCoolers(chipRoot.transform);

            // RAM slot at the back left; the stick itself arrives in Era VI.
            var slotPos = new Vector3(-0.2f, 0f, 0.27f);
            Box(t, "RAM Slot", new Vector3(slotPos.x, 0.049f, slotPos.z), new Vector3(0.27f, 0.018f, 0.03f), Mat(new Color(0.06f, 0.06f, 0.07f), 0.4f));
            ramRoot = Track(new GameObject("RAM Stick"));
            ramRoot.transform.SetParent(t, false);
            ramRoot.transform.localPosition = slotPos;
            Box(ramRoot.transform, "PCB", new Vector3(0, 0.097f, 0), new Vector3(0.25f, 0.075f, 0.008f), Mat(new Color(0.05f, 0.3f, 0.2f), 0.55f));
            Box(ramRoot.transform, "Contacts", new Vector3(0, 0.063f, 0), new Vector3(0.24f, 0.008f, 0.009f), Mat(new Color(0.8f, 0.65f, 0.35f), 0.6f, 0.9f));
            var ramChip = Mat(new Color(0.03f, 0.03f, 0.035f), 0.7f);
            for (int k = 0; k < 4; k++)
                Box(ramRoot.transform, "RAM Chip", new Vector3(-0.09f + k * 0.06f, 0.102f, -0.006f), new Vector3(0.045f, 0.03f, 0.004f), ramChip);

            // A small socket and chip for the press detector, between the second socket and the display.
            pressRoot = Track(new GameObject("Press Detector"));
            pressRoot.transform.SetParent(t, false);
            pressRoot.transform.localPosition = new Vector3(0.33f, 0f, 0.1f);
            Box(pressRoot.transform, "Socket", new Vector3(0, 0.047f, 0), new Vector3(0.095f, 0.012f, 0.095f), Mat(new Color(0.86f, 0.83f, 0.74f), 0.35f));
            Box(pressRoot.transform, "Package", new Vector3(0, 0.062f, 0), new Vector3(0.07f, 0.02f, 0.07f), Mat(new Color(0.035f, 0.035f, 0.04f), 0.75f));

            // A second, smaller socket waits empty until the counter arrives.
            var socket2 = new Vector3(0.33f, 0f, -0.13f);
            Box(t, "Socket 2", new Vector3(socket2.x, 0.047f, socket2.z), new Vector3(0.17f, 0.014f, 0.17f), Mat(new Color(0.86f, 0.83f, 0.74f), 0.35f));
            counterRoot = Track(new GameObject("Counter"));
            counterRoot.transform.SetParent(t, false);
            counterRoot.transform.localPosition = new Vector3(socket2.x, 0.066f, socket2.z);
            Box(counterRoot.transform, "Package", Vector3.zero, new Vector3(0.13f, 0.024f, 0.13f), Mat(new Color(0.035f, 0.035f, 0.04f), 0.75f));
            Box(counterRoot.transform, "Lid", new Vector3(0, 0.014f, 0), new Vector3(0.09f, 0.004f, 0.09f), Mat(new Color(0.38f, 0.4f, 0.43f), 0.35f, 0.6f));

            // Clock crystal: a silver can and a small LED that blinks with every tick.
            clockRoot = Track(new GameObject("Clock"));
            clockRoot.transform.SetParent(t, false);
            clockRoot.transform.localPosition = new Vector3(-0.16f, 0.05f, -0.15f);
            Cylinder(clockRoot.transform, "Crystal", Vector3.zero, new Vector3(0.05f, 0.012f, 0.028f), Mat(new Color(0.75f, 0.77f, 0.8f), 0.75f, 0.9f));
            var cled = Cylinder(clockRoot.transform, "Clock LED", new Vector3(0.06f, 0f, 0f), new Vector3(0.018f, 0.01f, 0.018f),
                Mat(new Color(0.05f, 0.12f, 0.15f), 0.8f, 0f, true));
            clockLed = cled.GetComponent<Renderer>();
        }

        void BuildCoolers(Transform chip)
        {
            var alu = Mat(new Color(0.7f, 0.72f, 0.75f), 0.55f, 0.85f);
            GameObject Heatsink(string name)
            {
                var root = new GameObject(name);
                root.transform.SetParent(chip, false);
                Box(root.transform, "Base", new Vector3(0, 0.024f, 0), new Vector3(0.16f, 0.012f, 0.16f), alu);
                for (int k = 0; k < 7; k++)
                    Box(root.transform, "Fin", new Vector3(-0.066f + k * 0.022f, 0.055f, 0), new Vector3(0.008f, 0.05f, 0.15f), alu);
                return root;
            }

            heatsinkRoot = Heatsink("Heatsink");
            fanCoolerRoot = Heatsink("Fan Cooler");
            var frame = Mat(new Color(0.1f, 0.1f, 0.12f), 0.4f);
            Box(fanCoolerRoot.transform, "Fan Frame", new Vector3(0, 0.092f, 0), new Vector3(0.16f, 0.022f, 0.16f), frame);
            coolerFan = new GameObject("Fan").transform;
            coolerFan.SetParent(fanCoolerRoot.transform, false);
            coolerFan.localPosition = new Vector3(0, 0.106f, 0);
            var blade = Mat(new Color(0.22f, 0.23f, 0.27f), 0.4f);
            Cylinder(coolerFan, "Hub", Vector3.zero, new Vector3(0.04f, 0.004f, 0.04f), blade);
            for (int i = 0; i < 5; i++)
            {
                var b = Box(coolerFan, "Blade", Vector3.zero, new Vector3(0.06f, 0.003f, 0.026f), blade);
                b.transform.localRotation = Quaternion.Euler(18f, i * 72f, 0f);
                b.transform.localPosition = b.transform.localRotation * new Vector3(0.036f, 0f, 0f);
            }
        }

        /// <summary>A big red arcade button on the front of the case, next to the power button.</summary>
        void BuildArcadeButton()
        {
            arcadeRoot = Track(new GameObject("Arcade Button"));
            arcadeRoot.transform.position = new Vector3(-0.42f, 0.034f, -0.47f);
            Cylinder(arcadeRoot.transform, "Bezel", new Vector3(0, 0.006f, 0), new Vector3(0.13f, 0.006f, 0.13f), Mat(new Color(0.08f, 0.08f, 0.09f), 0.5f, 0.4f));
            var cap = Cylinder(arcadeRoot.transform, "Cap", new Vector3(0, 0.022f, 0), new Vector3(0.095f, 0.016f, 0.095f),
                Mat(new Color(0.78f, 0.1f, 0.12f), 0.75f, 0f, true));
            cap.AddComponent<ArcadeButton>();
            var col = cap.GetComponent<BoxCollider>();
            col.size = new Vector3(1.3f, 3f, 1.3f);
            arcadeCap = cap.transform;
            arcadeCapHome = cap.transform.localPosition;
            arcadeGlow = cap.GetComponent<Renderer>();
        }

        public void InstallPressChip(string label)
        {
            SetChipLabel(pressRoot, "Press Label", label, 0.073f, 0.0016f);
            Show(pressRoot, 0.3f, true);
        }

        public void InstallArcadeButton() => Show(arcadeRoot, 0.3f, true);

        /// <summary>Pushes the cap down and lights it while the button is held.</summary>
        public void SetArcadePressed(bool down)
        {
            if (arcadeCap == null) return;
            arcadeCap.localPosition = arcadeCapHome + (down ? new Vector3(0, -0.009f, 0) : Vector3.zero);
            SetEmission(arcadeGlow, down ? new Color(1f, 0.15f, 0.12f) * 1.5f : Color.black);
        }

        /// <summary>A small monitor on the desk to the right of the case, with a 4x4 grid of pixels.</summary>
        void BuildMonitor()
        {
            monitorRoot = Track(new GameObject("Monitor"));
            monitorRoot.transform.position = new Vector3(1.3f, 0f, -0.05f);
            monitorRoot.transform.rotation = Quaternion.Euler(0f, -12f, 0f);
            var t = monitorRoot.transform;
            var body = Mat(new Color(0.07f, 0.075f, 0.085f), 0.5f, 0.3f);
            Box(t, "Stand", new Vector3(0, 0.006f, 0.02f), new Vector3(0.22f, 0.012f, 0.14f), body);
            Box(t, "Neck", new Vector3(0, 0.12f, 0.04f), new Vector3(0.04f, 0.22f, 0.03f), body);
            Box(t, "Bezel", new Vector3(0, 0.4f, 0.02f), new Vector3(0.48f, 0.4f, 0.035f), body);
            Box(t, "Glass", new Vector3(0, 0.4f, 0.0015f), new Vector3(0.42f, 0.34f, 0.002f), Mat(new Color(0.015f, 0.015f, 0.02f), 0.9f));
            for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                var pos = new Vector3(-0.135f + c * 0.09f, 0.512f - r * 0.075f, -0.001f);
                var px = Box(t, $"Pixel {r}{c}", pos, new Vector3(0.074f, 0.062f, 0.003f), Mat(new Color(0.07f, 0.055f, 0.04f), 0.6f, 0f, true));
                Destroy(px.GetComponent<Collider>());
                pixels[r * 4 + c] = px.GetComponent<Renderer>();
            }

            // Cable from the monitor back to the case.
            var cable = Mat(new Color(0.05f, 0.05f, 0.06f), 0.35f);
            var a = new Vector3(1.32f, 0.015f, 0.08f);
            var b2 = new Vector3(0.7f, 0.015f, 0.3f);
            var mid = (a + b2) * 0.5f + new Vector3(0f, 0.01f, 0.12f);
            Vector3 prev = a;
            for (int i = 1; i <= 14; i++)
            {
                float u = i / 14f;
                var p = Vector3.Lerp(Vector3.Lerp(a, mid, u), Vector3.Lerp(mid, b2, u), u);
                var seg = Cylinder(null, "Monitor Cable", (prev + p) * 0.5f, new Vector3(0.016f, Vector3.Distance(prev, p) * 0.5f + 0.003f, 0.016f), cable);
                seg.transform.SetParent(t, true);
                seg.transform.up = (p - prev).normalized;
                prev = p;
            }
        }

        void BuildDigit(Transform parent, int digit, Vector3 center)
        {
            const float w = 0.06f, h = 0.11f, th = 0.011f;
            var offPos = new[]
            {
                new Vector3(0, h / 2, 0), new Vector3(w / 2, h / 4, 0), new Vector3(w / 2, -h / 4, 0),
                new Vector3(0, -h / 2, 0), new Vector3(-w / 2, -h / 4, 0), new Vector3(-w / 2, h / 4, 0), new Vector3(0, 0, 0)
            };
            var horizontal = new Vector3(w * 0.78f, th, 0.004f);
            var vertical = new Vector3(th, h / 2 * 0.8f, 0.004f);
            for (int s = 0; s < 7; s++)
            {
                bool isHorizontal = s == 0 || s == 3 || s == 6;
                var seg = Box(parent, $"Seg {digit}{s}", center + offPos[s], isHorizontal ? horizontal : vertical,
                    Mat(new Color(0.08f, 0.05f, 0.04f), 0.5f, 0f, true));
                segments[digit, s] = seg.GetComponent<Renderer>();
            }
        }

        void BuildPsu()
        {
            psuRoot = Track(new GameObject("Power Supply"));
            psuRoot.transform.position = new Vector3(-0.6f, 0.034f, 0.22f);
            var t = psuRoot.transform;
            Box(t, "Body", new Vector3(0, 0.09f, 0), new Vector3(0.34f, 0.18f, 0.28f), Mat(new Color(0.12f, 0.13f, 0.15f), 0.45f, 0.5f));
            Cylinder(t, "Grill", new Vector3(0, 0.181f, 0), new Vector3(0.22f, 0.002f, 0.22f), Mat(new Color(0.04f, 0.04f, 0.045f), 0.4f));
            fan = new GameObject("Fan").transform;
            fan.SetParent(t, false);
            fan.localPosition = new Vector3(0, 0.186f, 0);
            var blade = Mat(new Color(0.2f, 0.21f, 0.24f), 0.4f);
            Cylinder(fan, "Hub", Vector3.zero, new Vector3(0.05f, 0.004f, 0.05f), blade);
            for (int i = 0; i < 5; i++)
            {
                var b = Box(fan, "Blade", Vector3.zero, new Vector3(0.09f, 0.003f, 0.032f), blade);
                b.transform.localRotation = Quaternion.Euler(18f, i * 72f, 0f);
                b.transform.localPosition = b.transform.localRotation * new Vector3(0.05f, 0f, 0f);
            }
        }

        // ------------------------------------------------------------------ installs

        public void InstallCase() => StartCoroutine(Drop(caseRoot, 0.4f));
        public void InstallBoard() => StartCoroutine(Drop(boardRoot, 0.45f));

        public void InstallChip(string label, bool animate = true)
        {
            SetChipLabel(chipRoot, "Chip Label", label, 0.018f, 0.0032f);
            Show(chipRoot, 0.35f, animate);
        }

        public void InstallCounter(string label, bool animate = true)
        {
            SetChipLabel(counterRoot, "Counter Label", label, 0.017f, 0.0022f);
            Show(counterRoot, 0.35f, animate);
        }

        /// <summary>The ROM goes into the second socket, where the counter used to sit.</summary>
        public void InstallRom() => InstallCounter("ROM\n16x6");

        /// <summary>Lifts both chips out of their sockets. Returns how long the animation takes.</summary>
        public float RemoveOldChips()
        {
            if (chipRoot.activeSelf) StartCoroutine(Lift(chipRoot));
            if (counterRoot.activeSelf) StartCoroutine(Lift(counterRoot));
            return 0.5f;
        }

        void SetChipLabel(GameObject chip, string name, string text, float height, float size)
        {
            var old = chip.transform.Find(name);
            if (old != null) { old.name = "Old Label"; Destroy(old.gameObject); }
            var label = AddLabel(chip.transform, text, new Vector3(0, height, 0), Quaternion.Euler(90, 0, 0), size, new Color(0.88f, 0.9f, 0.93f));
            label.name = name;
        }

        IEnumerator Lift(GameObject go)
        {
            var home = go.transform.localPosition;
            const float duration = 0.45f;
            Sfx.Play(SfxId.Delete, 0.7f);
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                float u = t / duration;
                go.transform.localPosition = home + Vector3.up * (0.45f * u * u);
                yield return null;
            }
            go.SetActive(false);
            go.transform.localPosition = home;
        }

        public void InstallClock() => Show(clockRoot, 0.3f, true);
        public void InstallRam() => Show(ramRoot, 0.35f, true);
        public void InstallMonitor() => Show(monitorRoot, 0.3f, true);

        /// <summary>0 removes any cooler, 1 fits the heatsink, 2 the heatsink with a fan.</summary>
        public void InstallCooler(int kind)
        {
            heatsinkRoot.SetActive(false);
            fanCoolerRoot.SetActive(false);
            if (kind == 1) Show(heatsinkRoot, 0.25f, true);
            if (kind == 2) Show(fanCoolerRoot, 0.25f, true);
        }

        /// <summary>Lifts the processor out and drops the new one in, then refits the cooler if one was given. Returns how long it takes.</summary>
        public float SwapChip(string label, int refitCooler = -1)
        {
            StartCoroutine(SwapRoutine(label, refitCooler));
            return refitCooler > 0 ? 1.8f : 1.1f;
        }

        IEnumerator SwapRoutine(string label, int refitCooler)
        {
            heatsinkRoot.SetActive(false);
            fanCoolerRoot.SetActive(false);
            yield return Lift(chipRoot);
            yield return new WaitForSeconds(0.1f);
            InstallChip(label);
            if (refitCooler <= 0) yield break;
            yield return new WaitForSeconds(0.7f);
            InstallCooler(refitCooler);
        }

        /// <summary>Puts a cooler on without animation, for a machine carried over from an earlier build.</summary>
        public void ShowCooler(int kind)
        {
            heatsinkRoot.SetActive(kind == 1);
            fanCoolerRoot.SetActive(kind == 2);
        }

        /// <summary>0 is cool, 1 is about to shut down: the processor lid glows red.</summary>
        public void SetHeat(float amount) =>
            SetEmission(chipLid, amount <= 0f ? Color.black : new Color(1f, 0.22f, 0.04f) * (Mathf.Clamp01(amount) * 4f));

        /// <summary>Lights the monitor's 4x4 grid. Bit 3 of each row is its leftmost pixel.</summary>
        public void SetPixels(int[] rows)
        {
            bool on = powered;
            for (int r = 0; r < 4; r++)
            {
                int row = on ? rows[r] : -2;
                if (row == shownRows[r]) continue;
                shownRows[r] = row;
                for (int c = 0; c < 4; c++)
                {
                    bool lit = on && ((rows[r] >> (3 - c)) & 1) != 0;
                    // Kept under the bloom threshold's sweet spot so lit pixels stay amber instead of washing out to white.
                    SetEmission(pixels[r * 4 + c], lit ? new Color(1f, 0.55f, 0.15f) * 1.6f : Color.black);
                }
            }
        }

        /// <summary>The finished, powered machine with a picture on screen. Used by the build smoke test.</summary>
        public void Showcase()
        {
            caseRoot.SetActive(true);
            boardRoot.SetActive(true);
            clockRoot.SetActive(true);
            ramRoot.SetActive(true);
            monitorRoot.SetActive(true);
            InstallChip("CPU-II\n190 G", false);
            InstallCounter("ROM\n16x6", false);
            InstallPsu(100, false);
            fanCoolerRoot.SetActive(true);
            powered = true;
            SetPowerLights(true, 1f);
            fanTarget = 1100f;
            displayGlow.intensity = 0.12f;
            SetPixels(new[] { 9, 0, 9, 6 });
            SetReadout(7, 3);
            SetClockLed(true);
            SetFocus(new Vector3(0.4f, 0.16f, 0f), 4.1f);
        }

        public void SetFocus(Vector3 point, float viewDistance)
        {
            focus = point;
            distance = distanceTarget = viewDistance;
        }

        public void InstallPsu(int watts, bool animate = true)
        {
            var old = psuRoot.transform.Find("Watt Label");
            if (old != null) Destroy(old.gameObject);
            var label = AddLabel(psuRoot.transform, $"{watts}W", new Vector3(0, 0.09f, -0.142f), Quaternion.identity, 0.006f, new Color(0.85f, 0.87f, 0.9f));
            label.name = "Watt Label";
            psuRoot.SetActive(false);
            Show(psuRoot, 0.4f, animate);
            if (cableRoot == null) BuildCable(animate);
        }

        /// <summary>Puts the machine from earlier eras back on the bench without animation.</summary>
        /// <summary>The First Picture machine, for builds that start from it.</summary>
        public void ShowComputerParts()
        {
            ramRoot.SetActive(true);
            monitorRoot.SetActive(true);
        }

        public void BuildPreinstalled(string chipLabel, int psuWatts, bool clock = false, string socket2Label = null)
        {
            caseRoot.SetActive(true);
            boardRoot.SetActive(true);
            InstallChip(chipLabel, false);
            InstallPsu(psuWatts, false);
            if (clock) clockRoot.SetActive(true);
            if (socket2Label != null) InstallCounter(socket2Label, false);
        }

        public void SetClockLed(bool on) => SetEmission(clockLed, on ? new Color(0.3f, 0.85f, 1f) * 3f : Color.black);

        void Show(GameObject go, float height, bool animate)
        {
            if (animate) StartCoroutine(Drop(go, height));
            else go.SetActive(true);
        }

        void BuildCable(bool animate)
        {
            cableRoot = Track(new GameObject("Cable"));
            var mat = Mat(new Color(0.05f, 0.05f, 0.06f), 0.35f);
            var a = new Vector3(-0.43f, 0.11f, 0.22f);
            var b = new Vector3(-0.29f, 0.09f, 0.22f);
            var mid = (a + b) * 0.5f + Vector3.up * 0.09f;
            Vector3 prev = a;
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                float u = i / (float)steps;
                var p = Vector3.Lerp(Vector3.Lerp(a, mid, u), Vector3.Lerp(mid, b, u), u);
                var seg = Cylinder(cableRoot.transform, "Cable", (prev + p) * 0.5f, new Vector3(0.022f, Vector3.Distance(prev, p) * 0.5f + 0.004f, 0.022f), mat);
                seg.transform.up = (p - prev).normalized;
                Destroy(seg.GetComponent<Collider>());
                prev = p;
            }
            if (animate)
            {
                cableRoot.SetActive(false);
                StartCoroutine(ShowLater(cableRoot, 0.5f));
            }
        }

        IEnumerator ShowLater(GameObject go, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (go != null) go.SetActive(true);
        }

        IEnumerator Drop(GameObject go, float height)
        {
            var target = go.transform.localPosition;
            go.transform.localPosition = target + Vector3.up * height;
            go.SetActive(true);
            const float duration = 0.6f;
            bool landed = false;
            for (float t = 0; t < duration; t += Time.deltaTime)
            {
                float u = t / duration;
                float eased = 1f + 2.2f * Mathf.Pow(u - 1f, 3) + 1.2f * Mathf.Pow(u - 1f, 2); // ease-out with a soft settle
                go.transform.localPosition = Vector3.LerpUnclamped(target + Vector3.up * height, target, eased);
                // The thunk plays when the part first touches down.
                if (!landed && eased >= 0.97f) { landed = true; Sfx.Play(SfxId.Drop); }
                yield return null;
            }
            go.transform.localPosition = target;
        }

        // ------------------------------------------------------------------ power and signals

        public IEnumerator PowerOn(bool enoughPower, Action done)
        {
            if (!enoughPower)
            {
                // Brownout: everything flickers, the fan twitches, then it all dies.
                Sfx.Play(SfxId.Brownout);
                for (int i = 0; i < 4; i++)
                {
                    SetPowerLights(true, 0.6f);
                    fanTarget = 260f;
                    testPattern = 8;
                    ApplyOutputs(0.35f);
                    yield return new WaitForSeconds(0.07f + i * 0.03f);
                    SetPowerLights(false, 0f);
                    testPattern = -1;
                    ApplyOutputs(0f);
                    yield return new WaitForSeconds(0.18f);
                }
                fanTarget = 0f;
                done?.Invoke();
                yield break;
            }

            powered = true;
            Sfx.Play(SfxId.PowerOn);
            SetPowerLights(true, 1f);
            fanTarget = 1100f;
            testPattern = 8;
            ApplyOutputs();
            for (float t = 0; t < 0.8f; t += Time.deltaTime)
            {
                displayGlow.intensity = Mathf.Lerp(0f, 0.12f, t / 0.8f);
                yield return null;
            }
            testPattern = -1;
            ApplyOutputs();
            if (powered) Sfx.StartHum();
            done?.Invoke();
        }

        public void PowerOff()
        {
            powered = false;
            fanTarget = 0f;
            Sfx.StopHum();
            SetPowerLights(false, 0f);
            SetClockLed(false);
            SetHeat(0f);
            SetPixels(new int[4]);
            displayGlow.intensity = 0f;
            ApplyOutputs();
        }

        void SetPowerLights(bool on, float strength)
        {
            SetEmission(powerRing, on ? new Color(0.25f, 0.85f, 1f) * (2.5f * strength) : Color.black);
            SetEmission(powerLed, on ? new Color(0.25f, 1f, 0.45f) * (3f * strength) : Color.black);
        }

        public void SetInputs(int bits)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                bool on = ((bits >> bit) & 1) != 0;
                knobs[bit].localPosition = knobHome[bit] + new Vector3(0, on ? 0.003f : 0f, on ? 0.012f : -0.006f);
                knobRenderers[bit].sharedMaterial = on ? knobOn : knobOff;
            }
        }

        public void SetOutputs(int bits) => SetReadout(bits, bits);

        /// <summary>Shows one number on the display and another on the five LEDs.</summary>
        public void SetReadout(int display, int leds)
        {
            if (display == outputs && leds == ledBits) return;
            outputs = display;
            ledBits = leds;
            ApplyOutputs();
        }

        void ApplyOutputs(float strength = 1f)
        {
            bool lit = powered || testPattern >= 0;
            for (int bit = 0; bit < 5; bit++)
            {
                bool on = lit && (testPattern >= 0 || ((ledBits >> bit) & 1) != 0);
                SetEmission(leds[bit], on ? Amber * (4f * strength) : Color.black);
            }

            int value = testPattern >= 0 ? 88 : outputs;
            int tens = value / 10, ones = value % 10;
            for (int d = 0; d < 2; d++)
            {
                int digit = d == 0 ? tens : ones;
                bool blank = d == 0 && digit == 0 && testPattern < 0;
                int mask = blank ? 0 : SegmentMasks[digit];
                for (int s = 0; s < 7; s++)
                {
                    bool on = lit && ((mask >> s) & 1) != 0;
                    SetEmission(segments[d, s], on ? Amber * (5f * strength) : Color.black);
                }
            }
        }

        static void SetEmission(Renderer r, Color c)
        {
            if (r == null) return;
            r.material.SetColor("_EmissionColor", c);
        }

        // ------------------------------------------------------------------ camera and clicks

        public void Orbit(Vector2 delta)
        {
            yawTarget += delta.x * 0.25f;
            pitchTarget = Mathf.Clamp(pitchTarget + delta.y * 0.2f, 8f, 80f);
        }

        /// <summary>Renders only into the part of the screen the UI leaves open, so the machine stays centred.</summary>
        public void SetViewRect(Rect normalized)
        {
            if (cam != null) cam.rect = normalized;
        }

        public void ZoomBy(float steps) => distanceTarget = Mathf.Clamp(distanceTarget * Mathf.Pow(1.1f, steps), 1.2f, 5f);

        public void Click(Vector2 screenPosition)
        {
            var ray = cam.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out var hit, 20f)) return;
            var dip = hit.collider.GetComponent<DipSwitch>();
            if (dip != null) { SwitchClicked?.Invoke(dip.bit); return; }
            if (hit.collider.GetComponent<ArcadeButton>() != null) { ArcadeButtonClicked?.Invoke(); return; }
            if (hit.collider.GetComponent<PowerButton>() != null) PowerButtonClicked?.Invoke();
        }

        void Update()
        {
            fanSpeed = Mathf.MoveTowards(fanSpeed, fanTarget, Time.deltaTime * (fanTarget > fanSpeed ? 900f : 450f));
            if (fan != null) fan.Rotate(0f, fanSpeed * Time.deltaTime, 0f, Space.Self);
            if (coolerFan != null) coolerFan.Rotate(0f, fanSpeed * 1.4f * Time.deltaTime, 0f, Space.Self);
        }

        void LateUpdate()
        {
            if (cam == null) return;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            yaw = Mathf.Lerp(yaw, yawTarget, k);
            pitch = Mathf.Lerp(pitch, pitchTarget, k);
            distance = Mathf.Lerp(distance, distanceTarget, k);
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            cam.transform.SetPositionAndRotation(focus + rot * new Vector3(0, 0, -distance), rot);
        }

        // ------------------------------------------------------------------ primitives

        GameObject Track(GameObject go)
        {
            spawned.Add(go);
            go.transform.SetParent(transform, false);
            return go;
        }

        GameObject Box(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat) =>
            Primitive(PrimitiveType.Cube, parent, name, pos, scale, mat);

        GameObject Cylinder(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat) =>
            Primitive(PrimitiveType.Cylinder, parent, name, pos, scale, mat);

        GameObject Primitive(PrimitiveType type, Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (parent != null) go.transform.SetParent(parent, false);
            else Track(go);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (type == PrimitiveType.Cylinder)
            {
                // Cylinders come with capsule colliders; boxes click more predictably.
                Destroy(go.GetComponent<Collider>());
                go.AddComponent<BoxCollider>();
            }
            return go;
        }

        Material Mat(Color color, float smoothness, float metallic = 0f, bool emissive = false, Color? emission = null)
        {
            // Copy the material assets in Resources when they exist: a build only ships the shader variants
            // (like emission) that some asset uses. Shader.Find is the editor fallback.
            var template = emissive ? glowTemplate : plainTemplate;
            var m = template != null ? new Material(template) : new Material(litShader);
            m.SetColor("_BaseColor", color);
            m.color = color;
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", emission ?? Color.black);
            }
            return m;
        }

        static GameObject AddLabel(Transform parent, string text, Vector3 localPos, Quaternion localRot, float size, Color color)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            mesh.fontSize = 64;
            mesh.characterSize = size;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = color;
            mesh.fontStyle = FontStyle.Bold;
            go.GetComponent<MeshRenderer>().sharedMaterial = mesh.font.material;
            return go;
        }
    }
}
