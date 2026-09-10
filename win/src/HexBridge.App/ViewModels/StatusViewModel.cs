using CommunityToolkit.Mvvm.ComponentModel;
using HexBridge.Microphone;

namespace HexBridge.App.ViewModels;

/// <summary>Everything the Status page shows, refreshed from a snapshot ten times a second.</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    /// <summary>Bottom of the meter scale. Speech peaks live between -30 and -6 dBFS.</summary>
    public const double MeterFloorDb = -60;

    [ObservableProperty] private string _headline = "Приём остановлен";
    [ObservableProperty] private string _subline = "Нажмите «Запустить», чтобы принимать звук со второй машины";

    /// <summary>
    /// This machine is the one holding the microphone. The page is the same page either
    /// way — one meter, one connection card — but three of its labels are about a direction
    /// and would be backwards without this.
    /// </summary>
    [ObservableProperty] private bool _isGiving;

    [ObservableProperty] private string _endpointLabel = "Звук идёт в";
    [ObservableProperty] private string _peerLabel = "Вторая машина";

    // Three mutually exclusive flags rather than a brush: the view picks colours through
    // style classes, so the card follows the light/dark theme without any work here.
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isBad;

    [ObservableProperty] private double _level;      // 0..1, mapped from dBFS
    [ObservableProperty] private double _peakMark;   // 0..1, short-lived hold marker
    [ObservableProperty] private string _peakText = "—";

    /// <summary>
    /// The microphone is muted — ours, or the far end's. The meter keeps moving, because
    /// the audio is still being read, but it drops the colour scale: «горячо» and «тихо»
    /// are not questions worth answering while nobody is hearing any of it.
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
        IsGiving = s.Role == BridgeRole.Sender;
        EndpointLabel = IsGiving ? "Звук берётся с" : "Звук идёт в";
        PeerLabel = IsGiving ? "Принимает" : "Отдаёт микрофон";

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

        // A machine names itself in its first HELLO, so «имя неизвестно» is only ever true of
        // one that has been heard from. One that has not is a different fact and a different
        // thing to do about it.
        SenderText = s.SenderName is { Length: > 0 } name ? name
            : s.PeerAddress is null ? "не подключён"
            : s.LastPacketAt is null ? "ещё не отвечала"
            : "имя неизвестно";
        PeerText = s.PeerAddress ?? "—";
        OutputText = string.IsNullOrEmpty(m?.OutputDescription) ? "—" : m.OutputDescription;

        // The hint is about picking a microphone in a game, which is a thing to do on the
        // machine the games are on. Here that is the machine that receives.
        GameHint = m?.PairedCaptureName;
        HasGameHint = !IsGiving && m?.PairedCaptureName is not null;
    }

    /// <summary>
    /// The same six states in the words of whichever end this is. Every line names the
    /// other machine by what it does rather than by what it sends — «отправитель» is a fact
    /// about datagrams, and nobody is standing here thinking about datagrams.
    /// </summary>
    private static (string, string) Describe(ReceiverSnapshot s, MicrophoneState? m)
    {
        var giving = s.Role == BridgeRole.Sender;
        var endpoint = m?.DeviceName ?? m?.OutputDescription ?? (giving ? "микрофон" : "вывод");

        return s.Status switch
        {
            ReceiverStatus.Live => ("Звук идёт", giving
                ? $"{endpoint} → {Who(s)}"
                : $"{Who(s)} → {endpoint}"),
            ReceiverStatus.Muted => ("Микрофон заглушен", giving
                ? "звук не отправляется, связь держится"
                : $"{Who(s)} поставил микрофон на мут"),
            ReceiverStatus.SenderLost => ("Связь пропала",
                s.LastPacketAt is { } at
                    ? $"нет пакетов уже {Duration(DateTime.UtcNow - at)}"
                    : "нет пакетов"),
            ReceiverStatus.WaitingForSender => ("Ждём вторую машину", giving
                ? $"звук уходит на {s.PeerAddress ?? "второй компьютер"}, ответа пока нет"
                : $"порт {s.Listen} открыт"),
            ReceiverStatus.Failed => ("Ошибка", s.Detail ?? "не удалось запустить"),
            _ => giving
                ? ("Передача остановлена", "Нажмите «Запустить», чтобы отдавать микрофон")
                : ("Приём остановлен", "Нажмите «Запустить», чтобы принимать звук со второй машины"),
        };
    }

    /// <summary>
    /// The machine at the other end, by the name it gave in its HELLO. Each end names the
    /// other by what it is rather than by what the protocol calls it.
    /// </summary>
    private static string Who(ReceiverSnapshot s) =>
        string.IsNullOrEmpty(s.SenderName) ? s.PeerAddress ?? "вторая машина" : s.SenderName;

    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours} ч {t.Minutes:00} м"
        : t.TotalMinutes >= 1
            ? $"{t.Minutes} м {t.Seconds:00} с"
            : $"{t.Seconds} с";
}
