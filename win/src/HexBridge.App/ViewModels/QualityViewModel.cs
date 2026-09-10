using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

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
    [ObservableProperty] private string _sentText = "0";
    [ObservableProperty] private string _packetBytesText = "—";
    [ObservableProperty] private string _remoteReceivedText = "0";
    [ObservableProperty] private string _remoteLostText = "0";
    [ObservableProperty] private string _bitrateText = "—";

    [ObservableProperty] private double _depth;
    [ObservableProperty] private double _targetDepth = 1;
    [ObservableProperty] private double _maxDepth = 1;
    [ObservableProperty] private string _depthText = "—";
    [ObservableProperty] private string _targetText = "—";

    /// <summary>Buffer depth as a fraction of the trim threshold, for the progress bar.</summary>
    [ObservableProperty] private double _depthFraction;

    // Moved off the Status page: how well it works, not whether it works.
    [ObservableProperty] private string _packetsText = "—";
    [ObservableProperty] private string _rttText = "—";
    [ObservableProperty] private string _uptimeText = "—";

    [ObservableProperty] private string _concealedText = "0";
    [ObservableProperty] private string _lateText = "0";
    [ObservableProperty] private string _underrunsText = "0";
    [ObservableProperty] private string _rejectedText = "0";
    [ObservableProperty] private string _decodedText = "0";
    [ObservableProperty] private string _lossText = "0 %";

    [ObservableProperty] private double[] _packetHistory = new double[HistorySeconds];
    [ObservableProperty] private double[] _peakHistory = new double[HistorySeconds];
    [ObservableProperty] private double[] _depthHistory = new double[HistorySeconds];
    [ObservableProperty] private double _packetHistoryMax = 60;

    public void Apply(ReceiverSnapshot s, MicrophoneState? m)
    {
        IsGiving = s.Role == BridgeRole.Sender;

        SentText = (m?.Sent ?? 0).ToString("N0");
        PacketBytesText = m is { LastPacketBytes: > 0 } ? $"{m.LastPacketBytes} Б" : "—";
        RemoteReceivedText = s.RemoteReceived.ToString("N0");
        RemoteLostText = s.RemoteLost.ToString("N0");
        BitrateText = m is { Bitrate: > 0 } ? $"{m.Bitrate / 1000} кбит/с" : "—";

        var depth = m?.Depth ?? 0;
        var target = m?.TargetDepth ?? 0;
        var max = m?.MaxDepth ?? 0;

        Depth = depth;
        TargetDepth = Math.Max(1, target);
        MaxDepth = Math.Max(1, max);
        DepthFraction = Math.Clamp(depth / (double)Math.Max(1, max), 0, 1);
        DepthText = s.IsRunning ? $"{depth} кадр. · {depth * 20} мс" : "—";
        TargetText = $"цель {target * 20} мс · подрезка от {max * 20} мс";

        PacketsText = s.IsRunning ? $"{m?.PacketsPerSecond ?? 0:F0}" : "—";
        RttText = s.RttMs is { } rtt ? $"{rtt:F0} мс" : "—";
        UptimeText = s.IsRunning ? Duration(s.Uptime) : "—";

        ConcealedText = (m?.Concealed ?? 0).ToString("N0");
        LateText = (m?.DroppedLate ?? 0).ToString("N0");
        UnderrunsText = (m?.Underruns ?? 0).ToString("N0");
        RejectedText = s.Rejected.ToString("N0");
        DecodedText = (m?.Decoded ?? 0).ToString("N0");

        // Two different measurements of the same thing, each taken where it can be taken.
        // On the giving side the only truthful number is the one the far end reports; on the
        // taking side it is what the buffer had to invent.
        if (IsGiving)
        {
            var delivered = s.RemoteReceived + s.RemoteLost;
            LossText = delivered > 0 ? $"{100.0 * s.RemoteLost / delivered:F2} %" : 0d.ToString("F2") + " %";
            return;
        }

        var damaged = (m?.Concealed ?? 0) + (m?.DroppedLate ?? 0);
        var total = (m?.Decoded ?? 0) + damaged;
        LossText = total > 0 ? $"{100.0 * damaged / total:F2} %" : 0d.ToString("F2") + " %";
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

    /// <summary>«2 ч 05 м», «3 м 12 с», «41 с» — never a bare count of seconds past a minute.</summary>
    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} ч {t.Minutes:00} м"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes} м {t.Seconds:00} с"
            : $"{t.Seconds} с";

    private static void Push(double[] window, double value)
    {
        Array.Copy(window, 1, window, 0, window.Length - 1);
        window[^1] = value;
    }
}
