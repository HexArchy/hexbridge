using Concentus;
using Concentus.Enums;

namespace HexBridge.Microphone;

/// <summary>
/// Mono 48 kHz voice encoder producing one packet per 20 ms frame — the same settings, in
/// the same order, as <c>mac/Sources/HexBridge/Features/Microphone/OpusEncoder.swift</c>.
///
/// <para>
/// Every one of them is part of the wire contract rather than a preference. VOIP mode picks
/// the SILK/CELT split the receiver's decoder is going to be handed; inband FEC is what the
/// jitter buffer's concealment leans on; and the frame size is 960 samples because the
/// receiver counts frames, not bytes. A build that quietly encoded 40 ms frames would still
/// decode — into audio a fifth of a second late, with no error anywhere to explain it.
/// </para>
/// </summary>
public sealed class VoiceEncoder
{
    public const int SampleRate = FrameAssembler.SampleRate;
    public const int FrameSamples = FrameAssembler.FrameSamples;

    /// <summary>Opus never emits more than this for one frame.</summary>
    private const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private readonly byte[] _out = new byte[MaxPacketBytes];

    public VoiceEncoder(int bitrate, int complexity = 10, bool fec = true, int expectedLossPercent = 10)
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.Complexity = Math.Clamp(complexity, 0, 10);
        _encoder.UseInbandFEC = fec;
        _encoder.PacketLossPercent = Math.Clamp(expectedLossPercent, 0, 100);
    }

    /// <summary>Bytes the last frame encoded to, for the «сколько это стоит» line.</summary>
    public int LastPacketBytes { get; private set; }

    /// <summary>
    /// Encodes exactly <see cref="FrameSamples"/> mono float samples into one Opus packet.
    /// The returned span is valid until the next call — the caller copies it onto the wire
    /// immediately, and allocating 50 arrays a second for the life of a session is not
    /// something to do out of habit.
    /// </summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length != FrameSamples)
        {
            throw new ArgumentException($"кадр должен быть {FrameSamples} сэмплов, а не {pcm.Length}", nameof(pcm));
        }

        var written = _encoder.Encode(pcm, FrameSamples, _out, _out.Length);
        if (written <= 0) throw new InvalidOperationException($"opus_encode вернул {written}");

        LastPacketBytes = written;
        return _out.AsSpan(0, written);
    }
}
