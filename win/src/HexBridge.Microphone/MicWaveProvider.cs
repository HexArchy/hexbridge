using Concentus;
using NAudio.Wave;

namespace HexBridge.Microphone;

/// <summary>
/// Jitter buffer plus Opus decoder, exposed as a wave provider so the audio device
/// clock drives the whole pipeline: whatever pulls from us sets the pace.
/// </summary>
public sealed class MicWaveProvider : IWaveProvider
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;  // 20 ms
    private const int OutputChannels = 2;
    private const int BytesPerFrame = FrameSamples * OutputChannels * sizeof(float);

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
    private readonly Dictionary<uint, byte[]> _frames = new();
    private readonly object _gate = new();

    private readonly int _targetFrames;
    private readonly int _maxFrames;
    private readonly float _gain;

    private readonly float[] _mono = new float[FrameSamples];
    private readonly byte[] _stereo = new byte[BytesPerFrame];
    private int _stereoOffset = BytesPerFrame;  // nothing buffered yet

    private uint _nextFrame;
    private bool _playing;
    private bool _primed;

    // Counters for the stats line.
    public long Received;
    public long Decoded;
    public long Concealed;
    public long DroppedLate;
    public long Underruns;
    public float LastPeak;

    // Peak-hold for the UI meter: a 10 Hz redraw samples five 20 ms frames at a time, so
    // reading LastPeak alone would drop transients on the floor.
    private float _peakHold;

    /// <summary>Frames currently waiting in the jitter buffer.</summary>
    public int Depth
    {
        get { lock (_gate) { return _frames.Count; } }
    }

    /// <summary>Frames the buffer fills to before it starts playing.</summary>
    public int TargetDepth => _targetFrames;

    /// <summary>Depth above which the buffer is trimmed back to the target.</summary>
    public int MaxDepth => _maxFrames;

    /// <summary>Loudest frame since the previous call; resets the hold.</summary>
    public float TakePeakHold() => Interlocked.Exchange(ref _peakHold, 0f);

    public MicWaveProvider(int jitterMs, int maxJitterMs, float gain)
    {
        _targetFrames = Math.Max(1, jitterMs / 20);
        _maxFrames = Math.Max(_targetFrames + 2, maxJitterMs / 20);
        _gain = gain;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, OutputChannels);

    /// <summary>Queues a received Opus frame. Safe to call from the network thread.</summary>
    public void Push(uint frameIndex, byte[] opusPacket)
    {
        lock (_gate)
        {
            Received++;

            if (!_primed)
            {
                // First frame of a session sets where playback starts.
                _primed = true;
                _nextFrame = frameIndex;
            }
            else if (frameIndex < _nextFrame)
            {
                // Arrived after we already played past it.
                DroppedLate++;
                return;
            }

            _frames[frameIndex] = opusPacket;
        }
    }

    /// <summary>Forgets buffered audio, e.g. when the sender restarts.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _frames.Clear();
            _playing = false;
            _primed = false;
            _stereoOffset = BytesPerFrame;
            _decoder.ResetState();
        }
    }

    public int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length)
        {
            if (_stereoOffset >= BytesPerFrame)
            {
                RenderNextFrame();
                _stereoOffset = 0;
            }

            var chunk = Math.Min(buffer.Length - written, BytesPerFrame - _stereoOffset);
            _stereo.AsSpan(_stereoOffset, chunk).CopyTo(buffer[written..]);
            _stereoOffset += chunk;
            written += chunk;
        }
        return written;
    }

    // MARK: - Frame production

    private void RenderNextFrame()
    {
        byte[]? packet = null;
        var conceal = false;

        lock (_gate)
        {
            if (!_playing)
            {
                // Wait until enough frames have piled up to ride out normal jitter.
                if (_frames.Count < _targetFrames)
                {
                    Silence();
                    return;
                }
                _playing = true;
            }

            // If we have fallen far behind (a network stall that then flushed), skip
            // ahead rather than playing out a growing delay.
            if (_frames.Count > _maxFrames)
            {
                var ordered = _frames.Keys.Order().ToArray();
                var drop = _frames.Count - _targetFrames;
                for (var i = 0; i < drop; i++)
                {
                    _frames.Remove(ordered[i]);
                    DroppedLate++;
                }
                _nextFrame = ordered[drop];
            }

            if (_frames.Remove(_nextFrame, out packet))
            {
                _nextFrame++;
            }
            else if (_frames.Count > 0)
            {
                // The frame is missing but later ones are here: conceal this slot.
                conceal = true;
                _nextFrame++;
            }
            else
            {
                // Nothing left at all — go back to buffering instead of stuttering.
                Underruns++;
                _playing = false;
                Silence();
                return;
            }
        }

        var samples = 0;
        try
        {
            samples = conceal
                ? _decoder.Decode(ReadOnlySpan<byte>.Empty, _mono.AsSpan(0, FrameSamples), FrameSamples, false)
                : _decoder.Decode(packet!.AsSpan(), _mono.AsSpan(0, FrameSamples), FrameSamples, false);
        }
        catch (Exception)
        {
            samples = 0;
        }

        if (samples <= 0)
        {
            Silence();
            return;
        }

        if (conceal)
        {
            Interlocked.Increment(ref Concealed);
        }
        else
        {
            Interlocked.Increment(ref Decoded);
        }

        WriteStereo(samples);
    }

    private void Silence()
    {
        Array.Clear(_stereo);
        LastPeak = 0;
    }

    private void RecordPeak(float peak)
    {
        LastPeak = peak;
        if (peak > _peakHold) _peakHold = peak;
    }

    /// <summary>Duplicates the mono frame into both output channels.</summary>
    private void WriteStereo(int samples)
    {
        var peak = 0f;
        var span = _stereo.AsSpan();

        for (var i = 0; i < FrameSamples; i++)
        {
            var value = i < samples ? _mono[i] * _gain : 0f;
            value = Math.Clamp(value, -1f, 1f);
            peak = Math.Max(peak, Math.Abs(value));

            var at = i * OutputChannels * sizeof(float);
            BitConverter.TryWriteBytes(span[at..], value);
            BitConverter.TryWriteBytes(span[(at + sizeof(float))..], value);
        }

        RecordPeak(peak);
    }
}
