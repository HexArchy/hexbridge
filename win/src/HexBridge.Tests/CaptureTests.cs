using HexBridge.Microphone;

namespace HexBridge.Tests;

/// <summary>
/// The capture pipeline on its own: whatever the device hands over becomes 20 ms of mono
/// 48 kHz, and what comes out the far end of Opus is the sound that went in.
/// </summary>
public class CaptureTests
{
    private const int Rate = FrameAssembler.SampleRate;
    private const int Frame = FrameAssembler.FrameSamples;

    /// <summary>Interleaved sine, at whatever rate and channel count the caller pretends to have.</summary>
    private static float[] Tone(int samples, int channels, int rate, double frequency = 440, float amplitude = 0.5f)
    {
        var buffer = new float[samples * channels];
        for (var i = 0; i < samples; i++)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
            for (var c = 0; c < channels; c++) buffer[i * channels + c] = value;
        }
        return buffer;
    }

    private static List<float[]> Collect(FrameAssembler assembler, ReadOnlySpan<float> input)
    {
        var frames = new List<float[]>();
        assembler.Push(input, (frame, _) => frames.Add(frame.ToArray()));
        return frames;
    }

    [Fact]
    public void AtTheRightRateAndChannelCountTheSamplesComeThroughUnchanged()
    {
        var assembler = new FrameAssembler(Rate, 1, gain: 1f);
        var input = Tone(Frame * 3, 1, Rate);

        var frames = Collect(assembler, input);

        Assert.Equal(3, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            for (var s = 0; s < Frame; s++)
            {
                Assert.Equal(input[i * Frame + s], frames[i][s], 5);
            }
        }
    }

    [Fact]
    public void PartialBuffersAreCarriedOverRatherThanPaddedOut()
    {
        var assembler = new FrameAssembler(Rate, 1, gain: 1f);

        // 700 samples is less than a frame. Emitting one anyway — padded with silence — is
        // how a capture callback that does not land on a 20 ms boundary turns into a click
        // fifty times a second.
        Assert.Empty(Collect(assembler, Tone(700, 1, Rate)));
        Assert.Single(Collect(assembler, Tone(300, 1, Rate)));
        Assert.Empty(Collect(assembler, Tone(100, 1, Rate)));
    }

    [Fact]
    public void StereoIsAveragedIntoOneChannel()
    {
        var assembler = new FrameAssembler(Rate, 2, gain: 1f);

        // Left at +0.5, right at -0.5: an average of zero, and a sum that would clip.
        var input = new float[Frame * 2];
        for (var i = 0; i < Frame; i++)
        {
            input[i * 2] = 0.5f;
            input[i * 2 + 1] = -0.5f;
        }

        var frames = Collect(assembler, input);
        var only = Assert.Single(frames);
        Assert.All(only, sample => Assert.Equal(0f, sample, 5));
    }

    [Fact]
    public void ADeviceAtTheWrongRateIsResampledToTheOneTheProtocolWants()
    {
        var assembler = new FrameAssembler(44100, 2, gain: 1f);

        // One second of 44.1 kHz stereo. Out the other side it has to be about a second of
        // 48 kHz mono, in whole 20 ms frames — 50 of them, give or take the resampler's own
        // latency at the start.
        var frames = Collect(assembler, Tone(44100, 2, 44100));

        Assert.InRange(frames.Count, 45, 50);

        // And it is still a 440 Hz tone rather than a rate-converted mess. Measured rather
        // than eyeballed: a resampler set up with the wrong ratio produces a perfectly clean
        // sine at the wrong pitch, which no assertion about smoothness would catch.
        var middle = frames.Skip(10).Take(20).SelectMany(f => f).ToArray();
        var atPitch = Strength(middle, 440);
        Assert.True(atPitch > 0.2, $"на 440 Гц почти ничего нет: {atPitch:F3}");
        Assert.True(Strength(middle, 400) < atPitch / 10, "энергия размазана — соотношение частот не то");
        Assert.True(Strength(middle, 500) < atPitch / 10, "энергия размазана — соотношение частот не то");
    }

    /// <summary>
    /// How much of one frequency is in a signal, by Goertzel. Half a dozen lines instead of
    /// an FFT dependency, because the question is only ever asked about frequencies that are
    /// known in advance.
    /// </summary>
    private static double Strength(IReadOnlyList<float> signal, double frequency)
    {
        var w = 2 * Math.PI * frequency / Rate;
        var coefficient = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;

        foreach (var sample in signal)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return Math.Sqrt(Math.Max(0, power)) * 2 / signal.Count;
    }

    [Fact]
    public void GainIsAppliedOnTheWayIn()
    {
        var quiet = new FrameAssembler(Rate, 1, gain: 1f);
        var loud = new FrameAssembler(Rate, 1, gain: 2f);
        var input = Tone(Frame, 1, Rate, amplitude: 0.25f);

        var quietPeak = 0f;
        var loudPeak = 0f;
        quiet.Push(input, (_, peak) => quietPeak = peak);
        loud.Push(input, (_, peak) => loudPeak = peak);

        Assert.Equal(0.25f, quietPeak, 2);
        Assert.Equal(0.5f, loudPeak, 2);
    }

    [Fact]
    public void GainThatWouldClipIsClampedRatherThanWrapped()
    {
        var assembler = new FrameAssembler(Rate, 1, gain: 8f);
        var peak = 0f;
        assembler.Push(Tone(Frame, 1, Rate, amplitude: 0.9f), (_, p) => peak = p);

        // A float sample past ±1 is silent distortion on some sinks and a full-scale square
        // wave on others. Neither is a thing to discover in somebody's headset.
        Assert.Equal(1.0f, peak, 3);
    }

    [Fact]
    public void WhatTheEncoderProducesIsWhatTheReceiverSJitterBufferPlays()
    {
        // The two halves of the voice path, back to back and nothing else: the exact class
        // the giving machine encodes with, feeding the exact class the taking machine
        // decodes with.
        var encoder = new VoiceEncoder(bitrate: 32000);
        var provider = new MicWaveProvider(jitterMs: 60, maxJitterMs: 240, gain: 1.0f);
        var assembler = new FrameAssembler(Rate, 1, gain: 1f);

        var packets = new List<byte[]>();
        assembler.Push(Tone(Frame * 40, 1, Rate), (frame, _) => packets.Add(encoder.Encode(frame).ToArray()));

        Assert.Equal(40, packets.Count);
        Assert.InRange(encoder.LastPacketBytes, 1, 400);

        // Fed at the pace it would arrive at, not all at once: the jitter buffer trims
        // anything past its ceiling, so dumping forty frames in would be testing the trim.
        var buffer = new byte[Frame * 2 * sizeof(float)];
        var loudest = 0f;
        for (var i = 0; i < packets.Count; i++)
        {
            provider.Push((uint)i, packets[i]);
            Assert.Equal(buffer.Length, provider.Read(buffer));
            loudest = Math.Max(loudest, provider.LastPeak);
        }

        Assert.Equal(40, provider.Received);
        Assert.True(provider.Decoded > 30, $"декодировано только {provider.Decoded} кадров");
        Assert.InRange(loudest, 0.2f, 1.0f);
        Assert.Equal(0, provider.DroppedLate);
    }

    [Fact]
    public void TheEncoderRefusesAFrameOfTheWrongLength()
    {
        var encoder = new VoiceEncoder(bitrate: 32000);

        // Opus would happily encode 480 samples as a 10 ms frame, and the receiver — which
        // counts frames rather than samples — would play the session at half speed with
        // nothing anywhere reporting a fault.
        Assert.Throws<ArgumentException>(() => encoder.Encode(new float[480]));
    }

    [Fact]
    public void ThePacedSourceProducesTwentyMillisecondFramesInOrder()
    {
        using var source = new ToneSource();
        var frames = new List<float[]>();
        var peaks = new List<float>();

        source.Start((frame, peak) =>
        {
            lock (frames)
            {
                frames.Add(frame.ToArray());
                peaks.Add(peak);
            }
        });

        // Half a second is 25 frames; the clock is allowed to be a little slow to start.
        Thread.Sleep(500);
        lock (frames)
        {
            Assert.InRange(frames.Count, 15, 30);
            Assert.All(frames, frame => Assert.Equal(Frame, frame.Length));
            Assert.All(peaks, peak => Assert.InRange(peak, 0.4f, 0.51f));

            // Consecutive frames continue the wave rather than restarting it: 960 samples is
            // 20 ms, and 440 Hz has no whole number of periods in that, so a source that
            // reset its phase every frame would show up as a discontinuity here.
            var joined = frames[5].Concat(frames[6]).ToArray();
            var period = Rate / ToneSource.Frequency;
            Assert.Equal(joined[900], joined[900 + (int)Math.Round(period * 4)], 2);
        }
    }
}
