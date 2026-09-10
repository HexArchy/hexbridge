using System.Buffers.Binary;

namespace HexBridge.Devices;

/// <summary>Class-specific request codes of USB Audio 1.0.</summary>
public static class UsbAudioRequest
{
    public const byte SetCur = 0x01;
    public const byte SetMin = 0x02;
    public const byte SetMax = 0x03;
    public const byte SetRes = 0x04;
    public const byte GetCur = 0x81;
    public const byte GetMin = 0x82;
    public const byte GetMax = 0x83;
    public const byte GetRes = 0x84;

    /// <summary>Feature unit selectors, the high byte of wValue.</summary>
    public const byte Mute = 0x01;
    public const byte Volume = 0x02;

    /// <summary>Endpoint selectors, same place in wValue.</summary>
    public const byte SamplingFrequency = 0x01;
    public const byte Pitch = 0x02;
}

/// <summary>
/// The audio class requests a composite device has to answer.
///
/// Small, and deliberately so. Windows reads the mute and volume range off the feature unit
/// before it will build a topology filter, and usbaudio.sys sets the sampling frequency on
/// the endpoint before it starts streaming; those are the requests the DualSense's own
/// descriptor promises, so those are the requests answered here. Everything else stalls,
/// which is what a real device does with a control it never claimed.
///
/// Stalling what the descriptor *does* claim is the failure worth avoiding. A device that
/// advertises a volume control and then refuses to say what its range is leaves the driver
/// with a node it cannot initialise, and the symptom of that is not a missing slider — it is
/// a streaming pin that never opens, which reads to the user as "haptics do nothing".
///
/// Nothing here is applied to anything: the values are remembered so a read follows the write
/// that preceded it, and the PCM is passed on untouched. Volume on the haptic channels is the
/// game's business, and scaling it here would be a second gain nobody asked for.
/// </summary>
public sealed class UsbAudioControls(int sampleRate)
{
    /// <summary>0 dB in the class's fixed-point dB, where 1/256 dB is one step.</summary>
    private const short VolumeZero = 0;

    /// <summary>−60 dB, the quietest the range goes before the class's "silence" code.</summary>
    private const short VolumeMinimum = unchecked((short)0xC400);

    /// <summary>One decibel.</summary>
    private const short VolumeStep = 0x0100;

    private readonly Dictionary<uint, byte[]> _current = [];
    private readonly int _sampleRate = sampleRate > 0 ? sampleRate : 48000;

    /// <summary>Values remembered from a SET_CUR, so a GET_CUR agrees with it.</summary>
    private static uint Key(ushort value, ushort index) => ((uint)index << 16) | value;

    /// <summary>
    /// A request aimed at an entity inside the audio-control interface: wValue is the control
    /// selector and the channel, wIndex the entity id and the interface.
    /// </summary>
    public UsbControlResult Entity(byte request, ushort value, ushort index, int length, ReadOnlySpan<byte> data)
    {
        var selector = (byte)(value >> 8);
        var channel = (byte)value;

        // Only the master channel carries controls on the hardware this serves, and its
        // descriptor says so: the per-channel control bitmaps are all zero. Answering for a
        // channel the descriptor left empty would be inventing a control.
        if (channel != 0) return UsbControlResult.Stall();

        return selector switch
        {
            UsbAudioRequest.Mute => Scalar(request, value, index, length, data, [0], null, null, null),
            UsbAudioRequest.Volume => Scalar(
                request, value, index, length, data,
                Sixteen(VolumeZero), Sixteen(VolumeMinimum), Sixteen(VolumeZero), Sixteen(VolumeStep)),
            _ => UsbControlResult.Stall(),
        };
    }

    /// <summary>
    /// A request aimed at an isochronous endpoint. The only one that matters is the sampling
    /// frequency, which usbaudio.sys sets before it opens the stream — and the answer is
    /// fixed, because the interface declares exactly one discrete rate.
    /// </summary>
    public UsbControlResult Endpoint(byte request, ushort value, ushort index, int length, ReadOnlySpan<byte> data)
    {
        var selector = (byte)(value >> 8);
        if (selector != UsbAudioRequest.SamplingFrequency) return UsbControlResult.Stall();

        var rate = TwentyFour(_sampleRate);
        // The rate is not settable: the alternate setting names one and only one. A SET_CUR
        // asking for it is accepted, and one asking for anything else is refused rather than
        // silently ignored, so a host that wanted 44.1 kHz learns it cannot have it here.
        if (request == UsbAudioRequest.SetCur)
        {
            return data.Length >= 3 && data[0] == rate[0] && data[1] == rate[1] && data[2] == rate[2]
                ? UsbControlResult.Ok()
                : UsbControlResult.Stall();
        }

        return request switch
        {
            UsbAudioRequest.GetCur or UsbAudioRequest.GetMin or UsbAudioRequest.GetMax =>
                Truncate(rate, length),
            UsbAudioRequest.GetRes => Truncate(TwentyFour(0), length),
            _ => UsbControlResult.Stall(),
        };
    }

    /// <summary>
    /// One control with a current value and, when it has a range, a minimum, a maximum and a
    /// step. A null range is how a control like mute says it has only a current value.
    /// </summary>
    private UsbControlResult Scalar(
        byte request, ushort value, ushort index, int length, ReadOnlySpan<byte> data,
        byte[] initial, byte[]? minimum, byte[]? maximum, byte[]? resolution)
    {
        switch (request)
        {
            case UsbAudioRequest.SetCur:
                if (data.Length < initial.Length) return UsbControlResult.Stall();
                _current[Key(value, index)] = data[..initial.Length].ToArray();
                return UsbControlResult.Ok();

            case UsbAudioRequest.GetCur:
                return Truncate(_current.GetValueOrDefault(Key(value, index), initial), length);

            case UsbAudioRequest.GetMin:
                return minimum is null ? UsbControlResult.Stall() : Truncate(minimum, length);
            case UsbAudioRequest.GetMax:
                return maximum is null ? UsbControlResult.Stall() : Truncate(maximum, length);
            case UsbAudioRequest.GetRes:
                return resolution is null ? UsbControlResult.Stall() : Truncate(resolution, length);

            default:
                return UsbControlResult.Stall();
        }
    }

    private static byte[] Sixteen(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] TwentyFour(int value) =>
        [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];

    private static UsbControlResult Truncate(byte[] source, int length) =>
        UsbControlResult.Ok(length >= source.Length ? source : source[..Math.Max(0, length)]);
}
