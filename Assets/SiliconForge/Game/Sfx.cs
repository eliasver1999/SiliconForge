using System;
using System.Collections.Generic;
using UnityEngine;

namespace SiliconForge.Game
{
    public enum SfxId { Click, Place, Wire, Delete, Toggle, Success, Fail, Drop, PowerOn, Brownout, Tick }

    /// <summary>
    /// All sound effects, synthesized at startup so the game ships without audio files.
    /// Short, soft and slightly electronic to match the workshop look.
    /// </summary>
    public static class Sfx
    {
        const int Rate = 44100;
        const string PrefKey = "sf_sound";

        static AudioSource oneShots, hum;
        static readonly Dictionary<SfxId, AudioClip> clips = new Dictionary<SfxId, AudioClip>();
        static readonly Dictionary<SfxId, float> volumes = new Dictionary<SfxId, float>
        {
            [SfxId.Click] = 0.22f, [SfxId.Place] = 0.5f, [SfxId.Wire] = 0.35f, [SfxId.Delete] = 0.4f,
            [SfxId.Toggle] = 0.3f, [SfxId.Success] = 0.55f, [SfxId.Fail] = 0.4f, [SfxId.Drop] = 0.6f,
            [SfxId.PowerOn] = 0.55f, [SfxId.Brownout] = 0.5f, [SfxId.Tick] = 0.12f,
        };
        static readonly Dictionary<SfxId, float> lastPlayed = new Dictionary<SfxId, float>();
        static bool enabled = true;

        public static bool Enabled
        {
            get => enabled;
            set
            {
                enabled = value;
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                if (hum != null) hum.mute = !value;
            }
        }

        public static void Init(GameObject host)
        {
            enabled = PlayerPrefs.GetInt(PrefKey, 1) == 1;
            oneShots = host.AddComponent<AudioSource>();
            oneShots.playOnAwake = false;
            hum = host.AddComponent<AudioSource>();
            hum.playOnAwake = false;
            hum.loop = true;
            hum.volume = 0f;
            hum.mute = !enabled;
            hum.clip = HumLoop();

            clips[SfxId.Click] = Click();
            clips[SfxId.Place] = Place();
            clips[SfxId.Wire] = Wire();
            clips[SfxId.Delete] = Delete();
            clips[SfxId.Toggle] = Toggle();
            clips[SfxId.Success] = Success();
            clips[SfxId.Fail] = Fail();
            clips[SfxId.Drop] = Drop();
            clips[SfxId.PowerOn] = PowerOn();
            clips[SfxId.Brownout] = Brownout();
            clips[SfxId.Tick] = Tick();
        }

        public static void Play(SfxId id, float volume = 1f)
        {
            if (!enabled || oneShots == null || !clips.TryGetValue(id, out var clip)) return;
            // The same sound twice within 30 ms just sounds like a glitch.
            float now = Time.unscaledTime;
            if (lastPlayed.TryGetValue(id, out var last) && now - last < 0.03f) return;
            lastPlayed[id] = now;
            oneShots.PlayOneShot(clip, volumes[id] * volume);
        }

        public static void StartHum()
        {
            if (hum == null) return;
            if (!hum.isPlaying) hum.Play();
            hum.volume = 0.16f;
        }

        public static void StopHum()
        {
            if (hum == null) return;
            hum.Stop();
            hum.volume = 0f;
        }

        // ------------------------------------------------------------------ synthesis

        static AudioClip Make(string name, float[] data)
        {
            float peak = 0.0001f;
            foreach (var s in data) peak = Mathf.Max(peak, Mathf.Abs(s));
            float gain = 0.85f / peak;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static float[] Buffer(float seconds) => new float[Mathf.CeilToInt(seconds * Rate)];

        /// <summary>Adds a tone whose frequency and loudness change over time, starting at offset seconds.</summary>
        static void Tone(float[] buf, float offset, float seconds, Func<float, float> freq, Func<float, float> env, float overtone = 0f)
        {
            int start = (int)(offset * Rate);
            int count = Mathf.Min((int)(seconds * Rate), buf.Length - start);
            double phase = 0;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)Rate;
                phase += 2.0 * Math.PI * freq(t) / Rate;
                float s = (float)Math.Sin(phase) + overtone * (float)Math.Sin(phase * 2.0) + overtone * 0.4f * (float)Math.Sin(phase * 3.0);
                buf[start + i] += s * env(t);
            }
        }

        /// <summary>Adds filtered noise. Lower smoothing values give darker noise.</summary>
        static void Noise(float[] buf, float offset, float seconds, Func<float, float> env, float smoothing, int seed)
        {
            var rng = new System.Random(seed);
            int start = (int)(offset * Rate);
            int count = Mathf.Min((int)(seconds * Rate), buf.Length - start);
            float y = 0f;
            for (int i = 0; i < count; i++)
            {
                float x = (float)(rng.NextDouble() * 2 - 1);
                y += smoothing * (x - y);
                buf[start + i] += y * env(i / (float)Rate);
            }
        }

        static float Decay(float t, float rate) => Mathf.Exp(-t * rate);

        static AudioClip Click()
        {
            var b = Buffer(0.03f);
            Tone(b, 0, 0.03f, _ => 3200f, t => Decay(t, 420f));
            Noise(b, 0, 0.01f, t => 0.5f * Decay(t, 600f), 0.6f, 1);
            return Make("click", b);
        }

        static AudioClip Place()
        {
            var b = Buffer(0.14f);
            Tone(b, 0, 0.14f, t => 240f - 700f * t, t => Decay(t, 35f), 0.2f);
            Noise(b, 0, 0.02f, t => 0.35f * Decay(t, 300f), 0.3f, 2);
            return Make("place", b);
        }

        static AudioClip Wire()
        {
            var b = Buffer(0.14f);
            Tone(b, 0f, 0.07f, _ => 1046.5f, t => Decay(t, 60f), 0.15f);
            Tone(b, 0.04f, 0.1f, _ => 1568f, t => Decay(t, 45f), 0.15f);
            return Make("wire", b);
        }

        static AudioClip Delete()
        {
            var b = Buffer(0.12f);
            Tone(b, 0, 0.12f, t => 520f - 2400f * t, t => Decay(t, 32f), 0.35f);
            return Make("delete", b);
        }

        static AudioClip Toggle()
        {
            var b = Buffer(0.04f);
            Tone(b, 0, 0.04f, _ => 1800f, t => Decay(t, 220f), 0.3f);
            Noise(b, 0, 0.008f, t => 0.6f * Decay(t, 700f), 0.7f, 3);
            return Make("toggle", b);
        }

        static AudioClip Success()
        {
            var b = Buffer(1.4f);
            float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
            float[] starts = { 0f, 0.09f, 0.18f, 0.3f };
            for (int i = 0; i < notes.Length; i++)
            {
                float f = notes[i];
                Tone(b, starts[i], 1.1f, _ => f, t => Mathf.Min(1f, t * 200f) * Decay(t, i == 3 ? 3.2f : 6f), 0.25f);
            }
            return Make("success", b);
        }

        static AudioClip Fail()
        {
            var b = Buffer(0.34f);
            Tone(b, 0f, 0.15f, _ => 220f, t => Mathf.Min(1f, t * 300f) * Decay(t, 14f), 0.5f);
            Tone(b, 0.16f, 0.18f, _ => 185f, t => Mathf.Min(1f, t * 300f) * Decay(t, 12f), 0.5f);
            return Make("fail", b);
        }

        static AudioClip Drop()
        {
            var b = Buffer(0.25f);
            Tone(b, 0, 0.25f, t => 130f - 220f * t, t => Decay(t, 22f), 0.3f);
            Noise(b, 0, 0.06f, t => 0.5f * Decay(t, 80f), 0.15f, 4);
            return Make("drop", b);
        }

        static AudioClip PowerOn()
        {
            var b = Buffer(1.3f);
            Func<float, float> env = t => Mathf.Clamp01(t * 6f) * Mathf.Clamp01((1.3f - t) * 3f);
            Tone(b, 0, 1.3f, t => 48f + 60f * Mathf.Clamp01(t / 0.9f), t => 0.8f * env(t), 0.6f);
            Tone(b, 0, 1.3f, t => 900f + 500f * Mathf.Clamp01(t / 0.9f), t => 0.06f * env(t));
            Noise(b, 0, 1.3f, t => 0.35f * Mathf.Clamp01(t / 0.8f) * env(t), 0.08f, 5);
            return Make("power-on", b);
        }

        static AudioClip Brownout()
        {
            var b = Buffer(0.9f);
            Tone(b, 0, 0.9f, t => 140f - 110f * t, t => 0.7f * Decay(t, 4f), 0.6f);
            var rng = new System.Random(6);
            for (int k = 0; k < 9; k++)
            {
                float at = (float)rng.NextDouble() * 0.7f;
                Noise(b, at, 0.03f, t => Decay(t, 120f), 0.5f, 10 + k);
            }
            return Make("brownout", b);
        }

        static AudioClip Tick()
        {
            var b = Buffer(0.02f);
            Tone(b, 0, 0.02f, _ => 2600f, t => Decay(t, 450f));
            return Make("tick", b);
        }

        /// <summary>A 2 second loop of mains hum and fan whir. Every partial completes whole cycles, so it loops without a click.</summary>
        static AudioClip HumLoop()
        {
            var b = Buffer(2f);
            float[] partials = { 50f, 100f, 150f, 173.5f, 211f, 347f };
            float[] levels = { 0.6f, 0.35f, 0.15f, 0.12f, 0.08f, 0.05f };
            for (int p = 0; p < partials.Length; p++)
            {
                float f = partials[p], level = levels[p];
                Tone(b, 0, 2f, _ => f, _ => level);
            }
            return Make("hum", b);
        }
    }
}
