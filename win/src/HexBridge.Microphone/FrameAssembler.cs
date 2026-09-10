using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HexBridge.Microphone;

/// <summary>One 20 ms frame of mono 48 kHz float, with the loudest sample in it.</summary>
public delegate void AudioFrameHandler(ReadOnlySpan<float> frame, float peak);

/// <summary>
/// Whatever the capture device hands us, turned into exactly what Opus wants: 48 kHz, mono,
/// 960 samples at a time.
///
/// <para>
/// It exists because no two machines agree on a capture format. A headset gives 44.1 kHz
/// stereo, a webcam gives 32 kHz mono, a shared-mode WASAPI endpoint gives whatever the mix
/// format happens to be that day — and the frame on the wire has to be the same in every
/// case, because the receiver's jitter buffer counts in 20 ms frames and the Mac has been
/// sending exactly those since the first version.
/// </para>
///
/// <para>
/// Downmix first, resample second. The other order costs the resampler a channel of work
/// for a result that is averaged away immediately afterwards.
/// </para>
/// </summary>
public sealed class FrameAssembler
{
    public const int SampleRate = 48000;

    /// <summary>20 ms, which is the frame size the whole protocol is built around.</summary>
    public const int FrameSamples = 960;

    private readonly int _channels;
    private readonly float _gain;
    private readonly SourceQueue _queue;
    private readonly ISampleProvider _resampled;

    private readonly float[] _scratch = new float[FrameSamples];
    private readonly float[] _frame = new float[FrameSamples];
    private int _filled;

    public FrameAssembler(int sourceRate, int channels, float gain)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _channels = channels;
        _gain = gain;
        _queue = new SourceQueue(sourceRate);

        // At the rate we already want, the resampler is a filter that does nothing but add
        // latency and rounding. Most capture endpoints on a gaming PC run at 48 kHz, so this
        // is the common path rather than the clever one.
        _resampled = sourceRate == SampleRate
            ? _queue
            : new WdlResamplingSampleProvider(_queue, SampleRate);
    }

    /// <summary>Frames emitted since the last <see cref="Reset"/>.</summary>
    public long FramesProduced { get; private set; }

    /// <summary>
    /// Takes interleaved samples at the source format and calls <paramref name="onFrame"/>
    /// once for every whole 20 ms frame that comes out. Called on the capture thread.
    /// </summary>
    public void Push(ReadOnlySpan<float> interleaved, AudioFrameHandler onFrame)
    {
        _queue.Write(interleaved, _channels, _gain);

        while (true)
        {
            var read = _resampled.Read(_scratch);
            if (read <= 0) break;
            Emit(_scratch.AsSpan(0, read), onFrame);
            // A short read means the queue is dry; asking again would only spin.
            if (read < _scratch.Length) break;
        }
    }

    /// <summary>
    /// Forgets everything buffered. Used when the device is swapped underneath us, where
    /// carrying half a frame across would splice two unrelated recordings together.
    /// </summary>
    public void Reset()
    {
        _queue.Clear();
        _filled = 0;
    }

    private void Emit(ReadOnlySpan<float> mono, AudioFrameHandler onFrame)
    {
        var offset = 0;
        while (offset < mono.Length)
        {
            var take = Math.Min(FrameSamples - _filled, mono.Length - offset);
            mono.Slice(offset, take).CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            offset += take;

            if (_filled < FrameSamples) continue;

            var peak = 0f;
            for (var i = 0; i < FrameSamples; i++)
            {
                _frame[i] = Math.Clamp(_frame[i], -1f, 1f);
                peak = Math.Max(peak, Math.Abs(_frame[i]));
            }

            FramesProduced++;
            onFrame(_frame, peak);
            _filled = 0;
        }
    }

    /// <summary>
    /// A mono ring the capture thread writes and the resampler pulls from.
    ///
    /// Reading returns only what is there rather than padding with silence: padding would
    /// turn every gap between two capture callbacks into an audible tick, and the resampler
    /// is perfectly happy with a short read.
    /// </summary>
    private sealed class SourceQueue(int sampleRate) : ISampleProvider
    {
        private readonly Queue<float> _samples = new(SampleRate / 4);

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public void Write(ReadOnlySpan<float> interleaved, int channels, float gain)
        {
            if (channels == 1)
            {
                foreach (var sample in interleaved) _samples.Enqueue(sample * gain);
                return;
            }

            var frames = interleaved.Length / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += interleaved[frame * channels + channel];
                }
                _samples.Enqueue(sum / channels * gain);
            }
        }

        public void Clear() => _samples.Clear();

        public int Read(Span<float> buffer)
        {
            var written = 0;
            while (written < buffer.Length && _samples.Count > 0) buffer[written++] = _samples.Dequeue();
            return written;
        }
    }
}
