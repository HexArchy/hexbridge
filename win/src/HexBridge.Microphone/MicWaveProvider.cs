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

    private readonly int _floorFrames;
    private readonly int _maxFrames;
    private readonly float _gain;

    /// <summary>
    /// How deep the buffer fills before playing — the one number that decides whether a
    /// link with jitter sounds continuous or chopped.
    ///
    /// It moves. A fixed depth is right for a network with a known worst case, which a
    /// LAN is and the internet is not: when the PC is being streamed from somewhere else,
    /// a burst of late packets empties the buffer, playback restarts at the same shallow
    /// depth, and the next burst empties it again. So an underrun raises it and calm
    /// lowers it, between the configured depth and the configured ceiling.
    /// </summary>
    private int _target;

    /// <summary>Frames rendered since the last underrun, for deciding when calm has lasted.</summary>
    private long _sinceUnderrun;

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

    /// <summary>
    /// Gaps filled by handing the decoder the *following* packet, which rebuilds the lost
    /// frame from the redundancy Opus carries inside it.
    ///
    /// Not a promise that redundancy was there: when the packet carries none, the decoder
    /// falls back to concealment internally and there is no way to ask which happened.
    /// What this counts is the good case being possible at all — a successor had arrived —
    /// as against <see cref="Concealed"/>, where nothing followed the gap and there was
    /// nothing to work from.
    /// </summary>
    public long Rebuilt;
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

    /// <summary>Frames the buffer fills to before it starts playing, as it stands now.</summary>
    public int TargetDepth
    {
        get { lock (_gate) { return _target; } }
    }

    /// <summary>Depth above which the buffer is trimmed back to the target.</summary>
    public int MaxDepth => _maxFrames;

    /// <summary>Loudest frame since the previous call; resets the hold.</summary>
    public float TakePeakHold() => Interlocked.Exchange(ref _peakHold, 0f);

    public MicWaveProvider(int jitterMs, int maxJitterMs, float gain)
    {
        _floorFrames = Math.Max(1, jitterMs / 20);
        _maxFrames = Math.Max(_floorFrames + 2, maxJitterMs / 20);
        _target = _floorFrames;
        _gain = gain;
    }

    /// <summary>Frames rendered without an underrun before the buffer gives a frame back.</summary>
    private const long CalmFrames = 15 * 50;  // fifteen seconds at 20 ms a frame

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
        byte[]? successor = null;
        var conceal = false;

        lock (_gate)
        {
            if (!_playing)
            {
                // Wait until enough frames have piled up to ride out normal jitter.
                if (_frames.Count < _target)
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
                var drop = _frames.Count - _target;
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

                // Opus carries a coarse copy of the previous frame inside the next one,
                // and the Mac pays for it on every packet. Handing that successor to the
                // decoder recovers what was actually said; concealment only invents
                // something plausible. This is the moment the redundancy was bought for.
                _frames.TryGetValue(_nextFrame, out successor);
            }
            else
            {
                // Nothing left at all — go back to buffering instead of stuttering.
                Underruns++;
                Deepen();
                _playing = false;
                Silence();
                return;
            }
        }

        var samples = 0;
        var rebuilt = false;
        try
        {
            if (!conceal)
            {
                samples = _decoder.Decode(packet!.AsSpan(), _mono.AsSpan(0, FrameSamples), FrameSamples, false);
            }
            else if (successor is not null)
            {
                // decodeFec: the packet handed over is the *following* frame, and what
                // comes back is the missing one rebuilt from the redundancy inside it.
                samples = _decoder.Decode(successor.AsSpan(), _mono.AsSpan(0, FrameSamples), FrameSamples, true);
                rebuilt = samples > 0;
            }

            if (conceal && !rebuilt)
            {
                // No successor, or it carried no redundancy for this slot. Invent one.
                samples = _decoder.Decode(ReadOnlySpan<byte>.Empty, _mono.AsSpan(0, FrameSamples), FrameSamples, false);
            }
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

        if (!conceal)
        {
            Interlocked.Increment(ref Decoded);
        }
        else if (rebuilt)
        {
            Interlocked.Increment(ref Rebuilt);
        }
        else
        {
            Interlocked.Increment(ref Concealed);
        }

        NoteCalm();
        WriteStereo(samples);
    }

    /// <summary>
    /// Widens the buffer after an underrun, up to the configured ceiling.
    ///
    /// Two frames at a time: one is too slow to escape a burst of late packets, and the
    /// ceiling is what keeps this from turning a jittery link into a slow one.
    /// </summary>
    private void Deepen()
    {
        _sinceUnderrun = 0;
        _target = Math.Min(_target + 2, _maxFrames);
    }

    /// <summary>
    /// Gives a frame back after a long stretch without an underrun, so a link that was
    /// briefly bad does not stay padded for the rest of the session.
    ///
    /// One frame per calm stretch, against two per underrun: latency that was earned back
    /// slowly is cheap, and an underrun the person can hear is not.
    /// </summary>
    private void NoteCalm()
    {
        if (_target <= _floorFrames) return;

        lock (_gate)
        {
            if (++_sinceUnderrun < CalmFrames) return;
            _sinceUnderrun = 0;
            _target = Math.Max(_floorFrames, _target - 1);
        }
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
