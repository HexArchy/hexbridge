using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>Buffer health and loss counters, plus a minute of history for the sparklines.</summary>
public sealed partial class QualityViewModel : ObservableObject
{
    /// <summary>One sample per second for a minute — enough to see a stall, cheap to draw.</summary>
    public const int HistorySeconds = 60;

    private readonly double[] _packets = new double[HistorySeconds];
    private readonly double[] _peaks = new double[HistorySeconds];
    private readonly double[] _depths = new double[HistorySeconds];

    /// <summary>
    /// This machine holds the microphone. The page keeps the same three headline numbers
    /// either way; what changes is the six tiles under them, because a jitter buffer is a
    /// thing the receiving end has and the giving end does not.
    /// </summary>
    [ObservableProperty] private bool _isGiving;

    // The giving side's counters. Loss is reported by the far end in every PONG, which is
    // the only place either machine learns what actually arrived.
    [ObservableProperty] private string _sentText = Loc.Count(0);
    [ObservableProperty] private string _packetBytesText = Strings.Common_Empty;
    [ObservableProperty] private string _remoteReceivedText = Loc.Count(0);
    [ObservableProperty] private string _remoteLostText = Loc.Count(0);
    [ObservableProperty] private string _bitrateText = Strings.Common_Empty;

    [ObservableProperty] private double _depth;
    [ObservableProperty] private double _targetDepth = 1;
    [ObservableProperty] private double _maxDepth = 1;
    [ObservableProperty] private string _depthText = Strings.Common_Empty;
    [ObservableProperty] private string _targetText = Strings.Common_Empty;

    /// <summary>Buffer depth as a fraction of the trim threshold, for the progress bar.</summary>
    [ObservableProperty] private double _depthFraction;

    // Moved off the Status page: how well it works, not whether it works.
    [ObservableProperty] private string _packetsText = Strings.Common_Empty;
    [ObservableProperty] private string _rttText = Strings.Common_Empty;
    [ObservableProperty] private string _uptimeText = Strings.Common_Empty;

    [ObservableProperty] private string _concealedText = Loc.Count(0);
    [ObservableProperty] private string _lateText = Loc.Count(0);
    [ObservableProperty] private string _underrunsText = Loc.Count(0);
    [ObservableProperty] private string _rejectedText = Loc.Count(0);
    [ObservableProperty] private string _decodedText = Loc.Count(0);
    [ObservableProperty] private string _lossText = Loc.Percent(0);

    [ObservableProperty] private double[] _packetHistory = new double[HistorySeconds];
    [ObservableProperty] private double[] _peakHistory = new double[HistorySeconds];
    [ObservableProperty] private double[] _depthHistory = new double[HistorySeconds];
    [ObservableProperty] private double _packetHistoryMax = 60;

    public void Apply(ReceiverSnapshot s, MicrophoneState? m)
    {
        IsGiving = s.Role == BridgeRole.Sender;

        SentText = Loc.Count(m?.Sent ?? 0);
        PacketBytesText = m is { LastPacketBytes: > 0 }
            ? Loc.F(Strings.Unit_Bytes, m.LastPacketBytes)
            : Strings.Common_Empty;
        RemoteReceivedText = Loc.Count(s.RemoteReceived);
        RemoteLostText = Loc.Count(s.RemoteLost);
        BitrateText = m is { Bitrate: > 0 } ? Loc.Kbits(m.Bitrate) : Strings.Common_Empty;

        var depth = m?.Depth ?? 0;
        var target = m?.TargetDepth ?? 0;
        var max = m?.MaxDepth ?? 0;

        Depth = depth;
        TargetDepth = Math.Max(1, target);
        MaxDepth = Math.Max(1, max);
        DepthFraction = Math.Clamp(depth / (double)Math.Max(1, max), 0, 1);
        DepthText = s.IsRunning
            ? Loc.F(Strings.Quality_Depth, Loc.Frames(depth), Loc.Ms(depth * 20))
            : Strings.Common_Empty;
        TargetText = Loc.F(Strings.Quality_Target, Loc.Ms(target * 20), Loc.Ms(max * 20));

        PacketsText = s.IsRunning
            ? (m?.PacketsPerSecond ?? 0).ToString("F0", System.Globalization.CultureInfo.CurrentCulture)
            : Strings.Common_Empty;
        RttText = s.RttMs is { } rtt ? Loc.Ms(rtt) : Strings.Common_Empty;
        UptimeText = s.IsRunning ? Loc.Duration(s.Uptime) : Strings.Common_Empty;

        ConcealedText = Loc.Count(m?.Concealed ?? 0);
        LateText = Loc.Count(m?.DroppedLate ?? 0);
        UnderrunsText = Loc.Count(m?.Underruns ?? 0);
        RejectedText = Loc.Count(s.Rejected);
        DecodedText = Loc.Count(m?.Decoded ?? 0);

        // Two different measurements of the same thing, each taken where it can be taken.
        // On the giving side the only truthful number is the one the far end reports; on the
        // taking side it is what the buffer had to invent.
        if (IsGiving)
        {
            var delivered = s.RemoteReceived + s.RemoteLost;
            LossText = Loc.Percent(delivered > 0 ? 100.0 * s.RemoteLost / delivered : 0);
            return;
        }

        var damaged = (m?.Concealed ?? 0) + (m?.DroppedLate ?? 0);
        var total = (m?.Decoded ?? 0) + damaged;
        LossText = Loc.Percent(total > 0 ? 100.0 * damaged / total : 0);
    }

    /// <summary>Called once a second; shifts the history windows along.</summary>
    public void Sample(ReceiverSnapshot s, MicrophoneState? m)
    {
        _ = s;
        var peakHold = m?.PeakHold ?? 0;
        Push(_packets, m?.PacketsPerSecond ?? 0);
        Push(_peaks, peakHold > 0
            ? Math.Clamp((MicrophoneState.ToDbfs(peakHold) - StatusViewModel.MeterFloorDb) / -StatusViewModel.MeterFloorDb, 0, 1)
            : 0);
        Push(_depths, m?.Depth ?? 0);

        // The array instance has to change for the binding to notice.
        PacketHistory = (double[])_packets.Clone();
        PeakHistory = (double[])_peaks.Clone();
        DepthHistory = (double[])_depths.Clone();
        PacketHistoryMax = Math.Max(60, Math.Ceiling(_packets.Max() / 10) * 10);
    }

    public void Reset()
    {
        Array.Clear(_packets);
        Array.Clear(_peaks);
        Array.Clear(_depths);
        PacketHistory = new double[HistorySeconds];
        PeakHistory = new double[HistorySeconds];
        DepthHistory = new double[HistorySeconds];
    }

    private static void Push(double[] window, double value)
    {
        Array.Copy(window, 1, window, 0, window.Length - 1);
        window[^1] = value;
    }
}
