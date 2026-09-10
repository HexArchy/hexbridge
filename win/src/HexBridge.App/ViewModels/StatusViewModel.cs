using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

namespace HexBridge.App.ViewModels;

/// <summary>Everything the Status page shows, refreshed from a snapshot ten times a second.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    /// <summary>Bottom of the meter scale. Speech peaks live between -30 and -6 dBFS.</summary>
    public const double MeterFloorDb = -60;

    [ObservableProperty] private string _headline = "Приём остановлен";
    [ObservableProperty] private string _subline = "Нажмите «Старт», чтобы принимать звук с Mac";

    // Three mutually exclusive flags rather than a brush: the view picks colours through
    // style classes, so the card follows the light/dark theme without any work here.
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    [ObservableProperty] private double _level;      // 0..1, mapped from dBFS
    [ObservableProperty] private double _peakMark;   // 0..1, short-lived hold marker
    [ObservableProperty] private string _peakText = "—";

    /// <summary>
    /// The Mac has the microphone on mute. The meter keeps moving — the audio is still
    /// being read and sent, it is simply not going anywhere useful — but it drops the
    /// colour scale, because «горячо» and «тихо» are not questions worth answering while
    /// nothing is being heard on the other end.
    /// </summary>
    [ObservableProperty] private bool _isMuted;

    [ObservableProperty] private string _packetsText = "—";
    [ObservableProperty] private string _rttText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _bufferText = "—";

    [ObservableProperty] private string _senderText = "нет";
    [ObservableProperty] private string _peerText = "—";
    [ObservableProperty] private string _sessionText = "—";
    [ObservableProperty] private string _listenText = "—";
    [ObservableProperty] private string _relayText = "прямое соединение";
    [ObservableProperty] private string _outputText = "—";

    [ObservableProperty] private string? _gameHint;
    [ObservableProperty] private bool _hasGameHint;

    // Peak hold decays instead of snapping back, so a transient stays readable.
    private double _holdValue;
    private DateTime _holdSetAt;

    public void Apply(ReceiverSnapshot s, MicrophoneState? m)
    {
        (Headline, Subline) = Describe(s, m);
        IsGood = s.Status is ReceiverStatus.Live;
        IsWaiting = s.Status is ReceiverStatus.WaitingForSender or ReceiverStatus.Muted or ReceiverStatus.SenderLost;
        IsBad = s.Status is ReceiverStatus.Failed;

        IsMuted = s.Status is ReceiverStatus.Muted;

        var peakHold = m?.PeakHold ?? 0;
        var db = MicrophoneState.ToDbfs(peakHold);
        var normalised = peakHold > 0 ? Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1) : 0;
        Level = normalised;
        PeakText = s.Status is ReceiverStatus.Live or ReceiverStatus.Muted && peakHold > 0
            ? $"{db:F1} dBFS"
            : "—";

        var now = DateTime.UtcNow;
        if (normalised >= _holdValue)
        {
            _holdValue = normalised;
            _holdSetAt = now;
        }
        else if (now - _holdSetAt > TimeSpan.FromSeconds(1))
        {
            _holdValue = Math.Max(normalised, _holdValue - 0.02);
        }
        PeakMark = _holdValue;

        PacketsText = s.IsRunning ? $"{m?.PacketsPerSecond ?? 0:F0}" : "—";
        RttText = s.RttMs is { } rtt ? $"{rtt:F0} мс" : "—";
        UptimeText = s.IsRunning ? Duration(s.Uptime) : "—";
        BufferText = s.IsRunning ? $"{(m?.Depth ?? 0) * 20} мс" : "—";

        SenderText = string.IsNullOrEmpty(s.SenderName) ? (s.PeerAddress is null ? "нет" : "неизвестен") : s.SenderName;
        PeerText = s.PeerAddress ?? "—";
        SessionText = s.Session == 0 ? "—" : $"{s.Session:x8}";
        ListenText = string.IsNullOrEmpty(s.Listen) ? "—" : s.Listen;
        RelayText = s.Relay ?? "прямое соединение";
        OutputText = string.IsNullOrEmpty(m?.OutputDescription) ? "—" : m.OutputDescription;

        GameHint = m?.PairedCaptureName;
        HasGameHint = m?.PairedCaptureName is not null;
    }

    private static (string, string) Describe(ReceiverSnapshot s, MicrophoneState? m) => s.Status switch
    {
        ReceiverStatus.Live => ("Звук идёт", $"{Who(s)} → {m?.DeviceName ?? m?.OutputDescription ?? "вывод"}"),
        ReceiverStatus.Muted => ("Микрофон выключен", $"{Who(s)} поставил микрофон на мут"),
        ReceiverStatus.SenderLost => ("Отправитель молчит",
            s.LastPacketAt is { } at
                ? $"нет пакетов уже {Duration(DateTime.UtcNow - at)}"
                : "нет пакетов"),
        ReceiverStatus.WaitingForSender => ("Нет отправителя", $"порт {s.Listen} открыт, ждём Mac"),
        ReceiverStatus.Failed => ("Ошибка", s.Detail ?? "не удалось запустить приём"),
        _ => ("Приём остановлен", "Нажмите «Старт», чтобы принимать звук с Mac"),
    };

    private static string Who(ReceiverSnapshot s) =>
        string.IsNullOrEmpty(s.SenderName) ? s.PeerAddress ?? "отправитель" : s.SenderName;

    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} ч {t.Minutes:00} м"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes} м {t.Seconds:00} с"
            : $"{t.Seconds} с";
}
