using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

namespace HexBridge.App.ViewModels;

/// <summary>Everything the Status page shows, refreshed from a snapshot ten times a second.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    /// <summary>Bottom of the meter scale. Speech peaks live between -30 and -6 dBFS.</summary>
    public const double MeterFloorDb = -60;

    [ObservableProperty] private string _headline = "Приём остановлен";
    [ObservableProperty] private string _subline = "Нажмите «Запустить», чтобы принимать звук с Mac";

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

    // Packets, latency, uptime and buffer depth live on the «Качество» page:
    // this one answers whether the sound is arriving, that one how well.
    // The session id, the listen address and the relay left the window
    // altogether — the first is a protocol number nobody can act on, and the
    // other two are settings, shown where they are set.
    [ObservableProperty] private string _senderText = "не подключён";
    [ObservableProperty] private string _peerText = "—";
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

        SenderText = string.IsNullOrEmpty(s.SenderName) ? (s.PeerAddress is null ? "не подключён" : "имя неизвестно") : s.SenderName;
        PeerText = s.PeerAddress ?? "—";
        OutputText = string.IsNullOrEmpty(m?.OutputDescription) ? "—" : m.OutputDescription;

        GameHint = m?.PairedCaptureName;
        HasGameHint = m?.PairedCaptureName is not null;
    }

    private static (string, string) Describe(ReceiverSnapshot s, MicrophoneState? m) => s.Status switch
    {
        ReceiverStatus.Live => ("Звук идёт", $"{Who(s)} → {m?.DeviceName ?? m?.OutputDescription ?? "вывод"}"),
        ReceiverStatus.Muted => ("Микрофон заглушен", $"{Who(s)} поставил микрофон на мут"),
        ReceiverStatus.SenderLost => ("Mac замолчал",
            s.LastPacketAt is { } at
                ? $"нет пакетов уже {Duration(DateTime.UtcNow - at)}"
                : "нет пакетов"),
        ReceiverStatus.WaitingForSender => ("Ждём Mac", $"порт {s.Listen} открыт"),
        ReceiverStatus.Failed => ("Ошибка", s.Detail ?? "не удалось запустить приём"),
        _ => ("Приём остановлен", "Нажмите «Запустить», чтобы принимать звук с Mac"),
    };

    /// The machine on the other end is a Mac, and «отправитель» is what the
    /// protocol calls it. The Mac side says «игровой ПК» about this machine for
    /// the same reason: each end names the other by what it is.
    private static string Who(ReceiverSnapshot s) =>
        string.IsNullOrEmpty(s.SenderName) ? s.PeerAddress ?? "Mac" : s.SenderName;

    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} ч {t.Minutes:00} м"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes} м {t.Seconds:00} с"
            : $"{t.Seconds} с";
}
