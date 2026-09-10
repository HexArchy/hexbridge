using System.Buffers.Binary;

namespace HexBridge.Devices;

/// <summary>
/// Turns the isochronous PCM Windows writes to the controller into HAPTIC packets.
///
/// The DualSense's audio-streaming OUT interface carries four channels at 48 kHz. Channels
/// 0 and 1 are the speaker and the headset jack; 2 and 3 are the voice coils in the grips,
/// and they are the only two this forwards. That is not a saving made for its own sake:
/// 4 × 240 × 2 bytes is 1920, and a 1400-byte datagram cannot hold it. Two channels is
/// 960 bytes, which fits with room to spare — and the two left behind are the ones the Mac
/// has no business playing anyway, since the headset is already a separate feature.
///
/// Silence is not sent. A game writes to this endpoint continuously whether anything is
/// happening or not, so a stream that forwarded every block would cost 1.6 Mbit/s of a link
/// that is also carrying voice and 250 input reports a second, to deliver zeros. Blocks are
/// numbered rather than counted, so a gap in the numbering says "this much nothing" just as
/// well as the nothing itself would have — and the same arithmetic covers a block that was
/// sent and lost, which is the case the protocol was designed around.
///
/// One trailing block after the last non-zero sample still goes out. It costs one packet and
/// it means the Mac's buffer drains through zero instead of stopping on whatever it happened
/// to be holding.
/// </summary>
public sealed class HapticStream
{
    /// <summary>Five milliseconds, as the contract says. 240 frames at 48 kHz.</summary>
    public const int BlockMilliseconds = 5;

    /// <summary>
    /// The largest block that still fits a datagram: 1400 minus the 24-byte header, the
    /// 16-byte tag and the six bytes of block header, over two channels of two bytes.
    /// </summary>
    public const int MaxFramesPerBlock = (1400 - 24 - 16 - DeviceChannel.HapticHeaderSize) / 4;

    /// <summary>
    /// Twice the nominal 200 blocks a second. Real hardware cannot exceed the nominal rate —
    /// the endpoint is clocked by the bus — so this only ever catches a host that has gone
    /// wrong, and catching it matters: the same socket is carrying somebody's voice.
    /// </summary>
    public const int MaxBlocksPerSecond = 400;

    /// <summary>Channels forwarded. Two, and the two the actuators are on.</summary>
    public const byte ForwardedChannels = 2;

    private readonly object _gate = new();
    private readonly Action<byte[]> _send;
    private readonly byte _device;

    private readonly int _sourceChannels;
    private readonly int _firstForwardedChannel;
    private readonly int _framesPerBlock;

    /// <summary>Frames of the source stream, interleaved, waiting to fill the current block.</summary>
    private readonly short[] _pending;
    private int _pendingFrames;

    private uint _blockIndex;
    private int _silentBlocks;

    private long _windowStartedTicks;
    private int _blocksThisSecond;

    private long _blocksSent;
    private long _blocksSilent;
    private long _blocksDropped;
    private long _bytesSent;
    private long _framesReceived;

    public HapticStream(byte device, UsbAudioStreamFormat format, Action<byte[]> send)
    {
        _device = device;
        _send = send;
        _sourceChannels = Math.Max(1, format.Channels);
        SampleRate = format.SampleRate > 0 ? format.SampleRate : 48000;
        BytesPerSample = format.BytesPerSample;

        // The actuators are the last pair. On a four-channel DualSense that is 2 and 3; on a
        // hypothetical two-channel stream it is the whole of it, which is the only reading
        // that does not silently forward a speaker as a grip.
        _firstForwardedChannel = Math.Max(0, _sourceChannels - ForwardedChannels);

        var frames = Math.Max(1, SampleRate * BlockMilliseconds / 1000);
        _framesPerBlock = Math.Min(frames, MaxFramesPerBlock);
        _pending = new short[_framesPerBlock * _sourceChannels];
    }

    public int SampleRate { get; }

    /// <summary>Bytes per sample the interface declares. Only 16-bit PCM is forwarded.</summary>
    public int BytesPerSample { get; }

    /// <summary>Frames of source audio in one block: 240 at 48 kHz.</summary>
    public int FramesPerBlock => _framesPerBlock;

    /// <summary>
    /// False when the stream's format is one we cannot take apart — anything but 16-bit PCM
    /// with at least two channels. Such a stream is consumed and discarded rather than
    /// guessed at: the endpoint still has to accept its packets or Windows stalls on it.
    /// </summary>
    public bool IsSupported => BytesPerSample == 2 && _sourceChannels >= ForwardedChannels;

    public long BlocksSent { get { lock (_gate) return _blocksSent; } }
    public long BlocksSkippedAsSilent { get { lock (_gate) return _blocksSilent; } }
    public long BlocksDropped { get { lock (_gate) return _blocksDropped; } }
    public long BytesSent { get { lock (_gate) return _bytesSent; } }

    /// <summary>Frames of PCM taken off the endpoint, whatever became of them.</summary>
    public long FramesReceived { get { lock (_gate) return _framesReceived; } }

    /// <summary>
    /// The host stopped streaming — alternate setting back to zero, or the session gone.
    /// A part-filled block is dropped rather than padded: it belongs to a moment that has
    /// ended, and half of it arriving late is worse than none of it arriving.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pendingFrames = 0;
            _silentBlocks = 0;
        }
    }

    /// <summary>
    /// One isochronous packet's worth of PCM, straight off the endpoint. Called from the URB
    /// loop at up to a thousand times a second, so everything here is a copy and a compare.
    /// </summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (!IsSupported) return;

        var frameBytes = _sourceChannels * BytesPerSample;
        if (frameBytes <= 0) return;

        var frames = pcm.Length / frameBytes;
        if (frames <= 0) return;

        var ready = new List<byte[]>();
        lock (_gate)
        {
            _framesReceived += frames;

            for (var frame = 0; frame < frames; frame++)
            {
                var source = pcm.Slice(frame * frameBytes, frameBytes);
                var at = _pendingFrames * _sourceChannels;
                for (var channel = 0; channel < _sourceChannels; channel++)
                {
                    _pending[at + channel] = BinaryPrimitives.ReadInt16LittleEndian(source[(channel * 2)..]);
                }

                if (++_pendingFrames < _framesPerBlock) continue;
                _pendingFrames = 0;
                if (Emit() is { } payload) ready.Add(payload);
            }
        }

        // Outside the lock: the send goes through the socket, and a feature that held its own
        // lock across a syscall would be holding it against the UI's next state read.
        foreach (var payload in ready) _send(payload);
    }

    /// <summary>
    /// Packs the filled block and decides whether it is worth a packet. Returns null when it
    /// is not — silence past the tail, or a block over the rate ceiling.
    /// </summary>
    private byte[]? Emit()
    {
        var index = _blockIndex++;

        var pcm = new byte[_framesPerBlock * ForwardedChannels * 2];
        var silent = true;
        for (var frame = 0; frame < _framesPerBlock; frame++)
        {
            for (var channel = 0; channel < ForwardedChannels; channel++)
            {
                var sample = _pending[frame * _sourceChannels + _firstForwardedChannel + channel];
                if (sample != 0) silent = false;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((frame * ForwardedChannels + channel) * 2), sample);
            }
        }

        // One block of silence still goes out, so the far end lands on zero rather than on
        // whatever the last block left in its buffer. The second one and everything after it
        // is the gap in the numbering doing the same job for free.
        _silentBlocks = silent ? _silentBlocks + 1 : 0;
        if (_silentBlocks > 1)
        {
            _blocksSilent++;
            return null;
        }

        if (!Allow())
        {
            _blocksDropped++;
            return null;
        }

        var payload = DeviceChannel.WriteHaptic(_device, ForwardedChannels, index, pcm);
        _blocksSent++;
        _bytesSent += payload.Length;
        return payload;
    }

    /// <summary>A one-second token window. Called under the lock.</summary>
    private bool Allow()
    {
        var now = Environment.TickCount64;
        if (now - _windowStartedTicks >= 1000)
        {
            _windowStartedTicks = now;
            _blocksThisSecond = 0;
        }
        if (_blocksThisSecond >= MaxBlocksPerSecond) return false;
        _blocksThisSecond++;
        return true;
    }
}
