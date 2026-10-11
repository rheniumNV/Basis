using Unity.Scripting.LifecycleManagement;
using UnityEngine;

/// <summary>
/// The fishing world's background music, synthesized from code: a calm loop for a quiet forest
/// pond. Soft pad chords, a sparse kalimba melody on the pentatonic scale, a low bass, wind in
/// the trees and the odd bird, through a small reverb.
///
/// This music (like the world's other sounds) was composed and written by an AI assistant, so it
/// is AI-generated audio: per AI.md it must not be contributed to the Basis repository. The
/// fishing world is kept off upstream for that reason.
///
/// The loop is seamless: every sound that runs past the end wraps around to the start, and the
/// reverb and wind filters are run over the loop twice so their tails carry over too.
/// </summary>
[AutoStaticsCleanup]
public static partial class FishingWorldMusic
{
    public const int Rate = 22050;
    const float SecondsPerBeat = 1f;   // 60 BPM
    const int Bars = 16;               // 4/4, so 64 seconds
    const float ChordSeconds = 8f;     // two bars per chord

    // MIDI note numbers.
    static readonly int[][] Chords =
    {
        new[] { 53, 57, 60, 64 },  // Fmaj7
        new[] { 52, 55, 59, 62 },  // Em7
        new[] { 50, 53, 57, 60 },  // Dm7
        new[] { 48, 52, 55, 59 },  // Cmaj7
        new[] { 53, 57, 60, 64 },  // Fmaj7
        new[] { 57, 60, 64, 67 },  // Am7
        new[] { 50, 53, 57, 60 },  // Dm7
        new[] { 55, 60, 62, 65 },  // G7sus4
    };
    static readonly int[] Roots = { 41, 40, 38, 36, 41, 45, 38, 43 };
    static readonly int[] Pentatonic = { 67, 69, 72, 74, 76, 79, 81, 84 };

    public static float LengthSeconds => Bars * 4 * SecondsPerBeat;

    public static float[] Render()
    {
        int n = Mathf.RoundToInt(LengthSeconds * Rate);
        float[] dry = new float[n];
        float[] send = new float[n];
        System.Random random = new System.Random(2026);

        for (int c = 0; c < Chords.Length; c++)
        {
            float start = c * ChordSeconds;
            foreach (int note in Chords[c])
            {
                Pad(dry, send, start - 0.6f, ChordSeconds + 2.4f, Frequency(note), 0.05f);
            }
            Bass(dry, start, ChordSeconds, Frequency(Roots[c]), 0.09f);
            Melody(dry, send, start, Chords[c], random);
        }

        Wind(dry, 0.022f);
        for (int i = 0; i < 7; i++)
        {
            float at = (float)random.NextDouble() * LengthSeconds;
            Bird(dry, send, at, random);
        }

        float[] wet = Reverb(send);
        for (int i = 0; i < n; i++) dry[i] += wet[i] * 0.45f;

        Normalize(dry, 0.85f);
        return dry;
    }

    static float Frequency(int midi)
    {
        return 440f * Mathf.Pow(2f, (midi - 69) / 12f);
    }

    // Adds a sample at a time in seconds, wrapping round the loop.
    static void Add(float[] buffer, int index, float value)
    {
        int n = buffer.Length;
        index %= n;
        if (index < 0) index += n;
        buffer[index] += value;
    }

    // A slow, slightly detuned pad voice that swells in and fades out.
    static void Pad(float[] dry, float[] send, float start, float duration, float f, float amp)
    {
        int first = Mathf.RoundToInt(start * Rate);
        int count = Mathf.RoundToInt(duration * Rate);
        float attack = 2.2f, release = 2.6f;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / attack) * Mathf.SmoothStep(0f, 1f, (duration - t) / release);
            float wobble = 1f + 0.12f * Mathf.Sin(2f * Mathf.PI * 0.17f * t);
            float v = 0.5f * Mathf.Sin(2f * Mathf.PI * f * t)
                    + 0.5f * Mathf.Sin(2f * Mathf.PI * f * 1.004f * t + 1.3f)
                    + 0.12f * Mathf.Sin(2f * Mathf.PI * f * 2f * t);
            v *= amp * env * wobble;
            Add(dry, first + i, v * 0.7f);
            Add(send, first + i, v * 0.5f);
        }
    }

    static void Bass(float[] dry, float start, float duration, float f, float amp)
    {
        int first = Mathf.RoundToInt(start * Rate);
        int count = Mathf.RoundToInt((duration + 0.8f) * Rate);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Min(1f, t / 0.25f) * Mathf.Exp(-t * 0.18f) * Mathf.SmoothStep(0f, 1f, (duration + 0.8f - t) / 1.2f);
            float v = Mathf.Sin(2f * Mathf.PI * f * t) + 0.2f * Mathf.Sin(2f * Mathf.PI * f * 2f * t);
            Add(dry, first + i, v * amp * env);
        }
    }

    // Sparse kalimba notes over a chord: mostly chord tones, sometimes a neighbouring pentatonic note.
    static void Melody(float[] dry, float[] send, float start, int[] chord, System.Random random)
    {
        int beats = Mathf.RoundToInt(ChordSeconds / SecondsPerBeat);
        int previous = Pentatonic[3];
        for (int b = 0; b < beats; b++)
        {
            if (random.NextDouble() > 0.5) continue;
            float at = start + b * SecondsPerBeat + (random.NextDouble() < 0.3 ? 0.5f : 0f);
            int note;
            if (random.NextDouble() < 0.6)
            {
                note = chord[random.Next(chord.Length)] + 12;
                while (note < 64) note += 12;
                while (note > 84) note -= 12;
            }
            else
            {
                // Step to a nearby note of the scale, so the line wanders rather than jumps.
                int index = System.Array.IndexOf(Pentatonic, previous);
                if (index < 0) index = 3;
                index = Mathf.Clamp(index + random.Next(-2, 3), 0, Pentatonic.Length - 1);
                note = Pentatonic[index];
            }
            previous = note;
            float velocity = 0.07f + (float)random.NextDouble() * 0.05f;
            Kalimba(dry, send, at, Frequency(note), velocity);
            // Now and then a soft echo of the same note an octave up.
            if (random.NextDouble() < 0.2) Kalimba(dry, send, at + 0.25f, Frequency(note + 12), velocity * 0.4f);
        }
    }

    static void Kalimba(float[] dry, float[] send, float start, float f, float amp)
    {
        int first = Mathf.RoundToInt(start * Rate);
        int count = Mathf.RoundToInt(2.8f * Rate);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t * 3.2f);
            float v = Mathf.Sin(2f * Mathf.PI * f * t)
                    + 0.22f * Mathf.Sin(2f * Mathf.PI * f * 5.4f * t) * Mathf.Exp(-t * 14f)
                    + 0.1f * Mathf.Sin(2f * Mathf.PI * f * 3f * t) * Mathf.Exp(-t * 6f);
            v *= amp * env;
            Add(dry, first + i, v * 0.8f);
            Add(send, first + i, v * 0.6f);
        }
    }

    // Wind in the trees: filtered noise swelling slowly. Periods divide the loop so it wraps.
    static void Wind(float[] dry, float amp)
    {
        int n = dry.Length;
        System.Random random = new System.Random(7);
        float low = 0f, lower = 0f;
        float[] wind = new float[n];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < n; i++)
            {
                float noise = (float)random.NextDouble() * 2f - 1f;
                low += (noise - low) * 0.05f;
                lower += (low - lower) * 0.01f;
                if (pass == 1) wind[i] = low - lower;
            }
        }
        float length = n / (float)Rate;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float swell = 0.45f + 0.35f * Mathf.Sin(2f * Mathf.PI * t * 2f / length) + 0.2f * Mathf.Sin(2f * Mathf.PI * t * 5f / length + 1.7f);
            dry[i] += wind[i] * amp * Mathf.Max(0.1f, swell) * 4f;
        }
    }

    // A small bird: a few quick chirps sweeping up or down.
    static void Bird(float[] dry, float[] send, float start, System.Random random)
    {
        int chirps = 2 + random.Next(4);
        float baseF = 2600f + (float)random.NextDouble() * 1600f;
        bool up = random.NextDouble() < 0.5;
        float at = start;
        for (int c = 0; c < chirps; c++)
        {
            float duration = 0.06f + (float)random.NextDouble() * 0.05f;
            int first = Mathf.RoundToInt(at * Rate);
            int count = Mathf.RoundToInt(duration * Rate);
            float phase = 0f;
            for (int i = 0; i < count; i++)
            {
                float u = i / (float)count;
                float f = baseF * (up ? Mathf.Lerp(0.85f, 1.25f, u) : Mathf.Lerp(1.25f, 0.85f, u));
                f *= 1f + 0.03f * Mathf.Sin(2f * Mathf.PI * 40f * i / Rate);
                phase += 2f * Mathf.PI * f / Rate;
                float env = Mathf.Sin(Mathf.PI * u);
                float v = Mathf.Sin(phase) * env * 0.018f;
                Add(dry, first + i, v);
                Add(send, first + i, v * 1.2f);
            }
            at += duration + 0.04f + (float)random.NextDouble() * 0.06f;
        }
    }

    // A Schroeder reverb (four combs, two allpasses), run over the loop twice so its tail wraps.
    static float[] Reverb(float[] input)
    {
        int n = input.Length;
        float[] combTimes = { 0.0297f, 0.0371f, 0.0411f, 0.0437f };
        float[] output = new float[n];
        float[][] combs = new float[combTimes.Length][];
        int[] positions = new int[combTimes.Length];
        for (int c = 0; c < combTimes.Length; c++) combs[c] = new float[Mathf.RoundToInt(combTimes[c] * Rate)];

        float[] ap1 = new float[Mathf.RoundToInt(0.005f * Rate)];
        float[] ap2 = new float[Mathf.RoundToInt(0.0017f * Rate)];
        int p1 = 0, p2 = 0;
        const float feedback = 0.84f, g = 0.7f;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < n; i++)
            {
                float x = input[i];
                float sum = 0f;
                for (int c = 0; c < combs.Length; c++)
                {
                    float[] line = combs[c];
                    float delayed = line[positions[c]];
                    line[positions[c]] = x + delayed * feedback;
                    positions[c] = (positions[c] + 1) % line.Length;
                    sum += delayed;
                }
                sum *= 0.25f;

                float d1 = ap1[p1];
                float y1 = -g * sum + d1;
                ap1[p1] = sum + g * y1;
                p1 = (p1 + 1) % ap1.Length;

                float d2 = ap2[p2];
                float y2 = -g * y1 + d2;
                ap2[p2] = y1 + g * y2;
                p2 = (p2 + 1) % ap2.Length;

                if (pass == 1) output[i] = y2;
            }
        }
        return output;
    }

    static void Normalize(float[] buffer, float peak)
    {
        float max = 0f;
        for (int i = 0; i < buffer.Length; i++) max = Mathf.Max(max, Mathf.Abs(buffer[i]));
        if (max <= 0f) return;
        float scale = peak / max;
        for (int i = 0; i < buffer.Length; i++) buffer[i] *= scale;
    }
}
