using System.Reflection;
using NAudio.Wave;
using NVorbis;

namespace Catsino.Plugin.Ui;

// The roulette clips (Assets/Roulette/*.ogg), decoded once into memory and played through NAudio. They are
// the same two files the web app serves, so the dealer hears exactly what the players do.
//
// Decoding up front matters: the panel plays a clip from the ImGui draw loop, and decoding a Vorbis stream
// there would stutter the frame. Each play gets its own output device so the stop clip can land while the
// spin is still finishing.
//
// NVorbis decodes and NAudio only plays: the NAudio.Vorbis bridge package is built against NAudio 2.x and
// would break against the 3.x ISampleProvider this project resolves.
public sealed class RouletteSounds : IDisposable
{
    private readonly object sync = new();
    private readonly List<IDisposable> playing = [];
    private CachedSound? spin;
    private CachedSound? stop;
    private bool disposed;

    // `fromSeconds` lets a panel opened mid-spin pick the clip up where the wheel actually is, exactly as
    // the browser seeks its <audio> element instead of restarting it.
    public void PlaySpin(double fromSeconds = 0) => Play(spin ??= Load("roulette_spin"), fromSeconds);

    public void PlayStop() => Play(stop ??= Load("roulette_stop"));

    // Stops anything still audible — used when the dealer mutes mid-spin, so the wheel does not keep
    // whirring after the switch is flipped.
    public void Silence()
    {
        lock (sync)
        {
            foreach (var item in playing)
            {
                item.Dispose();
            }

            playing.Clear();
        }
    }

    private static CachedSound? Load(string name)
    {
        try
        {
            var assembly = typeof(RouletteSounds).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(item => item.EndsWith($".{name}.ogg", StringComparison.OrdinalIgnoreCase));
            if (resource is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            using var reader = new VorbisReader(buffer);
            var samples = new List<float>(reader.Channels * reader.SampleRate * 8);
            var chunk = new float[16384];
            int read;
            while ((read = reader.ReadSamples(chunk, 0, chunk.Length)) > 0)
            {
                samples.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            return new CachedSound(samples.ToArray(), WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels));
        }
        catch (Exception)
        {
            // A missing or unreadable clip must never take the dealer window down; the table still works,
            // it is just silent.
            return null;
        }
    }

    private void Play(CachedSound? sound, double fromSeconds = 0)
    {
        if (sound is null || disposed)
        {
            return;
        }

        try
        {
            var output = new WaveOut();
            output.Init(new CachedSoundSampleProvider(sound, fromSeconds));
            output.PlaybackStopped += (_, _) => Retire(output);
            lock (sync)
            {
                playing.Add(output);
            }

            output.Play();
        }
        catch (Exception)
        {
            // no audio device, or the device disappeared — silence is the acceptable outcome
        }
    }

    private void Retire(IDisposable output)
    {
        lock (sync)
        {
            playing.Remove(output);
        }

        output.Dispose();
    }

    public void Dispose()
    {
        disposed = true;
        Silence();
    }

    private sealed record CachedSound(float[] Samples, WaveFormat Format);

    // Plays a decoded clip straight out of memory; nothing is read from disk or decoded while it runs.
    private sealed class CachedSoundSampleProvider : ISampleProvider
    {
        private readonly CachedSound sound;
        private long position;

        public CachedSoundSampleProvider(CachedSound sound, double fromSeconds = 0)
        {
            this.sound = sound;
            var frame = sound.Format.SampleRate * sound.Format.Channels;
            position = Math.Clamp((long)(fromSeconds * frame), 0, sound.Samples.Length);
            // Never start mid-frame: that would swap the stereo channels for the rest of the clip.
            position -= position % Math.Max(1, sound.Format.Channels);
        }

        public WaveFormat WaveFormat => sound.Format;

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            var available = sound.Samples.Length - position;
            var taken = (int)Math.Min(available, buffer.Length);
            if (taken <= 0)
            {
                return 0;
            }

            sound.Samples.AsSpan((int)position, taken).CopyTo(buffer);
            position += taken;
            return taken;
        }
    }
}
