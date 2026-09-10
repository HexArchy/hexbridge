using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Devices;
using HexBridge.Microphone;
using Net.Codecrete.QrCodeGenerator;

namespace HexBridge.App.ViewModels;

/// <summary>
/// One line of the step-1 checklist: a fact about this PC and, when the fact is not what
/// the user wants, the sentence that says what to do about it.
/// </summary>
public sealed partial class ReadinessItem(string title, string hint) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>What to do about it. Shown only while the line is not green.</summary>
    public string Hint { get; } = hint;

    [ObservableProperty] private string _value = "проверяется";
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isBad;

    public void Set(bool ok, string value)
    {
        IsOk = ok;
        IsBad = !ok;
        Value = value;
    }
}

/// <summary>
/// One machine visible on the network, on the screen where a code is entered.
///
/// Strangers are listed rather than hidden. Somebody who cannot see the neighbour's
/// HexBridge has no way to understand why the one machine on screen is not the one being
/// connected to — and the rule that decides is the tag, which no name can fake.
/// </summary>
public sealed partial class FoundHostViewModel(DiscoveredHost host, string note, bool isOurs) : ObservableObject
{
    public DiscoveredHost Host { get; } = host;

    public string Title { get; } = string.IsNullOrWhiteSpace(host.Name) ? "без имени" : host.Name;
    public string Address { get; } = host.Target.Length == 0 ? "адрес выясняется" : host.Target;
    public string Note { get; } = note;
    public bool IsOurs { get; } = isOurs;
    public bool CanDial { get; } = host.Target.Length > 0;
}

/// <summary>One line of the §9.4 connection check.</summary>
public sealed partial class CheckItem(int number, string title) : ObservableObject
{
    public int Number { get; } = number;

    /// <summary>
    /// Settable because the same six lines ask the same six questions in both roles, and
    /// four of them are worded for whichever machine is asking. Six more CheckItems for the
    /// other role would be six more places for the wording and the logic to drift apart.
    /// </summary>
    [ObservableProperty] private string _title = title;

    [ObservableProperty] private CheckState _state = CheckState.Pending;
    [ObservableProperty] private string _detail = "";

    public bool IsPending => State is CheckState.Pending;
    public bool IsRunning => State is CheckState.Running;
    public bool IsPassed => State is CheckState.Passed;
    public bool IsFailed => State is CheckState.Failed;
    public bool IsSkipped => State is CheckState.Skipped;

    /// <summary>A line that has not been reached yet is not drawn at all (§9.4).</summary>
    public bool IsVisible => State is not CheckState.Pending;

    public void Apply(CheckOutcome outcome)
    {
        State = outcome.State;
        Detail = outcome.Detail;
    }

    partial void OnStateChanged(CheckState value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsVisible));
    }
}

/// <summary>
/// The pairing wizard (DESIGN.md §9) — four steps that replace «run keygen, copy base64
/// into two config files by hand, then type the IP twice».
///
/// <para>
/// The key is generated <b>here</b>, on Windows, because Windows is the side that listens:
/// it is the only machine that knows its own address, and the address has to travel with
/// the key or the user is back to editing files. The Mac only ever receives a payload;
/// its half is already written (<c>mac/…/Core/Pairing.swift</c>) and this side is built to
/// match it byte for byte.
/// </para>
///
/// <para>
/// All three transports from §9.1 are offered at once, because which one works depends on
/// facts the app cannot know: a QR code (needs a camera), autodiscovery over Bonjour
/// (needs one subnet and a switch that passes multicast), and a twelve-character code the
/// user types (needs nothing, and is therefore never taken away).
/// </para>
/// </summary>
public sealed partial class PairingViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Step 1 «Готовность» through step 4 «Проверка связи».</summary>
    public const int StepCount = 4;

    private readonly Func<ReceiverConfig> _readConfig;
    private readonly Func<PairingPayload, Task> _commit;
    private readonly Action<LogLevel, string> _log;

    /// <summary>
    /// The shell's advertisement, borrowed rather than owned.
    ///
    /// The wizard used to run its own: it was the only place autodiscovery was needed,
    /// because pairing was the only thing it was for. It is not any more — a paired Mac
    /// finds the host again by its tag after the router changes its address, and that has
    /// to work with no wizard open. So the advertisement outlives this window, and the
    /// wizard only reports on it and asks for a refresh when the port may have moved.
    /// </summary>
    private readonly DiscoveryPublisher _discovery;

    private DispatcherTimer? _countdown;
    private PairingExchangeServer? _exchange;
    private CancellationTokenSource? _checks;

    /// <summary>
    /// The browse, alive only while this window is. Unlike the advertisement it has no
    /// reason to outlive the wizard: once an address is in the config the giving machine
    /// dials it directly, and a multicast query every few seconds forever would be traffic
    /// bought for nothing.
    /// </summary>
    private ServiceBrowser? _browser;

    private DateTime _codeExpiresAt;
    private DateTime _soundStartedAt = DateTime.MaxValue;
    private float _soundPeak;
    private ReceiverSnapshot _snapshot = new();
    private bool _sawPackets;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _step;
    [ObservableProperty] private string _title = "Связать этот компьютер со второй машиной";

    /// <summary>
    /// Which side of the pairing this machine is. The one that listens makes the key and
    /// shows a code; the one that dials reads it. Set by the shell from the config.
    /// </summary>
    [ObservableProperty] private BridgeRole _role = BridgeRole.Receiver;

    // Step 1.
    public ObservableCollection<ReadinessItem> Readiness { get; } = [];

    // Step 2.
    [ObservableProperty] private Geometry? _qr;
    [ObservableProperty] private Thickness _qrQuietZone = new(16);
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _uri = "";
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _discoveryText = "";
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private string _machineName = Environment.MachineName;

    // Step 2, giving role: what is on the network, and what the user typed.
    public ObservableCollection<FoundHostViewModel> Found { get; } = [];

    [ObservableProperty] private string _enteredCode = "";
    [ObservableProperty] private string _enteredHost = "";
    [ObservableProperty] private string? _connectError;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _browseText = "";

    // Step 4.
    public ObservableCollection<CheckItem> Checks { get; } = [];

    [ObservableProperty] private string _resultHeadline = "";
    [ObservableProperty] private string _resultDetail = "";
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private bool _allPassed;
    [ObservableProperty] private double _soundLevel;
    [ObservableProperty] private string _soundCountdown = "";

    /// <summary>
    /// True only while check 5 is asking for a voice. It drives the one panel in the
    /// wizard that asks the user to do something, so it is a property rather than a look
    /// into <see cref="Checks"/> by index — an indexed binding would compile and then be
    /// wrong the first time the list order changed.
    /// </summary>
    [ObservableProperty] private bool _isSoundPrompt;

    /// <summary>Set by the shell so the wizard never reaches for a clipboard itself.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public PairingViewModel(
        Func<ReceiverConfig> readConfig,
        Func<PairingPayload, Task> commit,
        Action<LogLevel, string> log,
        DiscoveryPublisher discovery)
    {
        _readConfig = readConfig;
        _commit = commit;
        _log = log;
        _discovery = discovery;

        Checks.Add(new CheckItem(1, "Адрес разрешается"));
        Checks.Add(new CheckItem(2, "Пакеты доходят"));
        Checks.Add(new CheckItem(3, "Ключи совпадают"));
        Checks.Add(new CheckItem(4, "Аудиоустройство найдено"));
        Checks.Add(new CheckItem(5, "Звук проходит насквозь"));
        Checks.Add(new CheckItem(6, "Проброшенные устройства"));
    }

    public bool IsGiving => Role == BridgeRole.Sender;

    /// <summary>
    /// Check 5 asks for a voice, and where the voice has to be depends on which machine has
    /// the microphone. Getting this backwards would have somebody talking at the wrong
    /// computer and concluding the product is broken.
    /// </summary>
    public string SoundPrompt => IsGiving
        ? "Скажите что-нибудь вслух рядом с этим компьютером"
        : "Скажите что-нибудь вслух рядом со второй машиной";

    public string SoundNote => IsGiving
        ? "Уровень измеряется здесь, до отправки."
        : "Уровень измеряется на этом компьютере, а не на второй машине.";

    partial void OnRoleChanged(BridgeRole value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsGiving));
        OnPropertyChanged(nameof(IsStep3Visible));
        OnPropertyChanged(nameof(SoundPrompt));
        OnPropertyChanged(nameof(SoundNote));
        BuildReadiness();
        Retitle();
    }

    /// <summary>
    /// The four lines of step 1, which are not the same four in both roles: half of what a
    /// listening machine has to have ready — a virtual cable, an open port, a gamepad driver
    /// — is not part of giving a microphone away at all.
    /// </summary>
    private void BuildReadiness()
    {
        Readiness.Clear();
        if (IsGiving)
        {
            Readiness.Add(new ReadinessItem("Микрофон",
                "Подключите микрофон или гарнитуру — это то, что будет уходить на вторую машину."));
            Readiness.Add(new ReadinessItem("Доступ к микрофону",
                "Разрешите приложениям доступ к микрофону: Параметры → Конфиденциальность → Микрофон."));
            Readiness.Add(new ReadinessItem("Адрес в сети",
                "Подключите этот компьютер к той же сети, что и вторую — по кабелю или по Wi-Fi."));
            Readiness.Add(new ReadinessItem("Вторая машина",
                "Откройте HexBridge на второй машине и дойдите там до экрана с кодом."));
            return;
        }

        Readiness.Add(new ReadinessItem("Драйвер usbip-win2", UsbIpAttacher.InstallHint));
        Readiness.Add(new ReadinessItem("Виртуальный аудиокабель",
            "Без него принятый звук некуда отдать. Установите Steam или VB-Audio Virtual Cable."));
        Readiness.Add(new ReadinessItem("Порт UDP",
            "Разрешите HexBridge в брандмауэре Windows для частной сети."));
        Readiness.Add(new ReadinessItem("Адрес в сети",
            "Подключите этот компьютер к той же сети, что и вторую — по кабелю или по Wi-Fi."));
    }

    // MARK: - Step bookkeeping

    public bool IsStep1 => Step == 0;
    public bool IsStep2 => Step == 1;
    public bool IsStep3 => Step == 2;
    public bool IsStep4 => Step == 3;

    /// <summary>
    /// «Ждём» is a step only for the machine that hands the key over: it has nothing to do
    /// but wait for somebody to take it. The machine that types the code finds out whether
    /// it worked the moment it presses the button, so it goes straight to the checks.
    /// </summary>
    public bool IsStep3Visible => !IsGiving;

    partial void OnStepChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
        Retitle();
    }

    private void Retitle() => Title = Step switch
    {
        0 => "Связать этот компьютер со второй машиной",
        1 => IsGiving ? "Код со второй машины" : "Код для второй машины",
        2 => "Ждём вторую машину",
        _ => "Проверка связи",
    };

    // MARK: - Opening and closing

    [RelayCommand]
    public void Open()
    {
        Step = 0;
        IsFinished = false;
        ResultHeadline = "";
        ResultDetail = "";
        foreach (var check in Checks) check.Apply(new CheckOutcome(CheckState.Pending, ""));

        if (Readiness.Count == 0) BuildReadiness();
        Survey();
        IsOpen = true;
    }

    /// <summary>Straight to §9.4, which is also what «Проверить связь» in the settings does.</summary>
    [RelayCommand]
    public void OpenChecks()
    {
        Step = 3;
        IsOpen = true;
        _ = RunChecksAsync();
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        IsOpen = false;
        await StopServicesAsync();
    }

    // MARK: - Step 2, giving role: taking a code off the other machine

    /// <summary>
    /// Fills the list of machines on the network and starts the browse behind it. Nothing
    /// here is required: the code and the address can always be typed, which is the whole
    /// reason the short code exists.
    /// </summary>
    private void StartBrowsing()
    {
        if (_browser is not null) return;

        var browser = new ServiceBrowser();
        browser.Changed += hosts => Dispatcher.UIThread.Post(() => ShowFound(hosts));
        browser.Start();
        _browser = browser;

        BrowseText = browser.Error is { } error
            ? $"Автопоиск недоступен: {error}. Введите адрес второй машины вручную."
            : "Ищем HexBridge в сети…";
        ShowFound(browser.Hosts);
    }

    private void ShowFound(IReadOnlyList<DiscoveredHost> hosts)
    {
        var ownTag = DiscoveryTag.ForPsk(_readConfig().Psk);

        Found.Clear();
        foreach (var host in hosts)
        {
            var ours = DiscoveryTag.Same(ownTag, host.Tag);
            var note = ours ? "это ваша вторая машина"
                : host.Tag is null ? "ещё ни с кем не связана"
                : "связана с другой машиной";
            Found.Add(new FoundHostViewModel(host, note, ours));
        }

        if (_browser?.Error is not null) return;
        BrowseText = Found.Count == 0
            ? "Пока никого не видно. Откройте HexBridge на второй машине — или введите её адрес вручную."
            : "Выберите машину и введите код с её экрана.";

        // A machine that already published our own tag is the one we are paired with, so its
        // address is filled in without asking. The key is not touched: an address is not a
        // secret, and re-pairing is still an explicit act.
        if (EnteredHost.Length == 0 && Found.FirstOrDefault(h => h.IsOurs && h.CanDial) is { } ours2)
        {
            EnteredHost = ours2.Host.Target;
        }
    }

    [RelayCommand]
    private void Pick(FoundHostViewModel? host)
    {
        if (host is null || !host.CanDial) return;
        EnteredHost = host.Host.Target;
        ConnectError = null;
    }

    /// <summary>
    /// Turns what the user typed into a pairing. Two shapes are accepted because both turn
    /// up in practice: a twelve-character code beside an address, and the whole
    /// <c>hexbridge://pair?…</c> link pasted out of a message.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) return;
        ConnectError = null;

        var typed = EnteredCode.Trim();
        if (PairingPayload.TryParse(typed, out var pasted, out _))
        {
            Adopt(pasted);
            await RunChecksAsync();
            return;
        }

        if (!ShortCode.IsComplete(typed))
        {
            ConnectError = "Код состоит из двенадцати символов — например ABCD-EFGH-JKLM. "
                + "Можно вставить и ссылку hexbridge://pair целиком.";
            return;
        }

        var (host, port) = SplitTarget(EnteredHost);
        if (host.Length == 0)
        {
            ConnectError = "Не указан адрес второй машины. Выберите её в списке или впишите вида 192.168.1.10.";
            return;
        }

        IsConnecting = true;
        try
        {
            var result = await PairingExchangeClient.FetchAsync(host, port, typed);
            if (!result.IsOk)
            {
                ConnectError = result.Error;
                return;
            }

            _log(LogLevel.Info, $"hexbridge: код принят, отпечаток {result.Payload!.Fingerprint}");
            Adopt(result.Payload);
        }
        finally
        {
            IsConnecting = false;
        }

        await RunChecksAsync();
    }

    /// <summary>Holds the payload for <see cref="RunChecksAsync"/> to commit.</summary>
    private void Adopt(PairingPayload payload)
    {
        Pending = payload;
        Fingerprint = payload.Fingerprint;
        EnteredHost = $"{payload.Host}:{payload.Port}";
    }

    /// <summary>«host», «host:port» or a bare address; the port defaults to the standard one.</summary>
    internal static (string Host, int Port) SplitTarget(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return ("", PairingPayload.DefaultPort);

        // An IPv6 literal is written [::1]:47702, and its own colons are not separators.
        if (text.StartsWith('[') && text.IndexOf(']') > 0)
        {
            var close = text.IndexOf(']');
            var inner = text[1..close];
            var rest = text[(close + 1)..];
            return rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketed)
                ? (inner, bracketed)
                : (inner, PairingPayload.DefaultPort);
        }

        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and <= 65535)
        {
            return (text[..colon], port);
        }
        return (text, PairingPayload.DefaultPort);
    }

    // MARK: - Step 1: readiness

    /// <summary>
    /// Reads the four facts the checklist shows. Nothing here blocks «Далее»: §9.2 is
    /// explicit that the user may pair now and install the driver later, and a wizard that
    /// refuses to continue over a missing gamepad driver would be lying about what it
    /// needs.
    /// </summary>
    private void Survey()
    {
        var config = _readConfig();
        if (IsGiving)
        {
            SurveyGiving(config);
            return;
        }

        var driver = UsbIpAttacher.Locate(config.UsbIpPath);
        Readiness[0].Set(driver is not null, driver ?? "не установлен");

        var device = FindOutputDevice(config);
        Readiness[1].Set(device is not null, device ?? "не найден");

        // Whether the port is actually open cannot be known from inside this process —
        // that needs a packet from outside — so the line says what is true and no more.
        var port = ParsePort(config.Listen);
        Readiness[2].Set(true, $"{port} — проверяется на шаге 4");

        var address = MulticastDns.LocalAddresses().FirstOrDefault();
        Readiness[3].Set(address is not null, address?.ToString() ?? "сеть недоступна");
    }

    /// <summary>
    /// The same four-line shape for the machine that gives its microphone away. The fourth
    /// line is about the other machine and cannot be measured from here, so it says what it
    /// is — a reminder — rather than pretending to a fact.
    /// </summary>
    private void SurveyGiving(ReceiverConfig config)
    {
        var microphone = FindInputDevice(config);
        Readiness[0].Set(microphone is not null, microphone ?? "не найден");

        // Whether Windows will actually hand over the samples cannot be known without asking
        // for them, and asking here would put a permission prompt in front of somebody who
        // is reading a checklist. It becomes a real answer at step 4, where sound is measured.
        Readiness[1].Set(true, "проверяется на шаге 4");

        var address = MulticastDns.LocalAddresses().FirstOrDefault();
        Readiness[2].Set(address is not null, address?.ToString() ?? "сеть недоступна");

        Readiness[3].Set(true, "нужен её код");
    }

    private static string? FindInputDevice(ReceiverConfig config)
    {
        if (!OperatingSystem.IsWindows()) return config.InputDevice;

        try
        {
            return FindWindowsInputDevice(config.InputDevice);
        }
        catch (Exception)
        {
            // A machine with no audio stack at all, or one where the enumerator throws.
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindWindowsInputDevice(string? chosen) =>
        DeviceCatalog.PickCapture(chosen)?.FriendlyName;

    private static string? FindOutputDevice(ReceiverConfig config)
    {
        // Off Windows there is no endpoint enumeration at all, so the honest answer is
        // whatever the config already names — which is also what the receiver would use.
        if (!OperatingSystem.IsWindows()) return config.Device;

        try
        {
            return FindWindowsOutputDevice(config.Device);
        }
        catch (Exception)
        {
            // A machine with no audio stack at all, or one where the enumerator throws.
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindWindowsOutputDevice(string? chosen)
    {
        var devices = DeviceCatalog.RenderDevices().Select(device => device.FriendlyName).ToList();
        if (chosen is not null && devices.Contains(chosen)) return chosen;

        return devices.FirstOrDefault(name =>
            DeviceCatalog.PreferredPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static int ParsePort(string listen)
    {
        try
        {
            return ReceiverConfig.ParseEndpoint(listen, PairingPayload.DefaultPort).Port;
        }
        catch (Exception)
        {
            return PairingPayload.DefaultPort;
        }
    }

    // MARK: - Step 2: the code

    [RelayCommand]
    private void Next()
    {
        Step = 1;
        if (IsGiving)
        {
            StartBrowsing();
            return;
        }
        Regenerate();
    }

    /// <summary>
    /// A fresh key, a fresh short code, and the services that hand them over. Called on
    /// entering step 2 and again from «Обновить код» when the three minutes run out.
    /// </summary>
    [RelayCommand]
    private void Regenerate()
    {
        var config = _readConfig();
        var port = ParsePort(config.Listen);
        var host = MulticastDns.LocalAddresses().FirstOrDefault()?.ToString() ?? "127.0.0.1";

        var payload = PairingPayload.Create(host, port, MachineName);
        Uri = payload.ToUri();
        Fingerprint = payload.Fingerprint;
        Code = ShortCode.Generate();
        Pending = payload;

        BuildQr(Uri);

        _codeExpiresAt = DateTime.UtcNow + ShortCode.Lifetime;
        StartCountdown();
        StartServices(port);
    }

    /// <summary>The payload on screen, kept until the user finishes or gives up.</summary>
    public PairingPayload? Pending { get; private set; }

    /// <summary>
    /// The QR as one path, per §9.2. Error correction M: the payload is short, and a higher
    /// level would only make the modules smaller for no gain.
    ///
    /// <c>ToGraphicsPath</c> hands back an SVG path string that <see cref="Geometry.Parse"/>
    /// takes as is, so no raster ever exists — which is also why it stays sharp at any DPI.
    /// </summary>
    private void BuildQr(string text)
    {
        try
        {
            var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
            // Border 0: the quiet zone is drawn as padding on the white panel instead,
            // because a border baked into the path is not part of its bounding box and
            // would be dropped the moment the geometry is stretched to fit.
            Qr = Geometry.Parse(qr.ToGraphicsPath(0));

            // §9.2 and the QR standard both want at least four modules of white around it.
            const double Side = 240;
            var module = Side / qr.Size;
            QrQuietZone = new Thickness(Math.Ceiling(module * 4));
        }
        catch (Exception ex)
        {
            // A payload that will not encode is a bug, not a user error — but the short
            // code still works, so the wizard keeps going and says so in the log.
            Qr = null;
            _log(LogLevel.Warning, $"hexbridge: QR не построился: {ex.Message}");
        }
    }

    private void StartCountdown()
    {
        _countdown ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        Tick();
        _countdown.Start();
    }

    private void Tick()
    {
        var left = _codeExpiresAt - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            CountdownText = "Код истёк";
            // The listener stops answering the moment the code is dead, rather than
            // waiting for the user to notice the label.
            if (_exchange is not null) _exchange.Code = "";
            return;
        }

        CountdownText = $"Код действует ещё {left.Minutes}:{left.Seconds:00}";
    }

    /// <summary>
    /// The two things a Mac can reach: the Bonjour advertisement it browses for, and the
    /// listener that trades the short code for the payload.
    ///
    /// Neither is required. Bonjour fails on networks that block multicast and on machines
    /// where Apple's own service owns UDP 5353; the exchange fails if something already
    /// holds the port. Both report it and the QR keeps working, because the QR needs
    /// nothing but a camera.
    /// </summary>
    private void StartServices(int dataPort)
    {
        // «Обновить код» comes back through here, and the listener is kept rather than
        // rebuilt: rebinding a port the previous listener has not finished releasing fails,
        // and the wizard would then show a code nothing answers. Rotating the code on the
        // live listener also invalidates the old one at that same instant, which is what
        // the three-minute window is supposed to mean.
        if (_exchange is not null)
        {
            _exchange.Code = Code;
            _exchange.Uri = Uri;
        }
        else
        {
            try
            {
                _exchange = new PairingExchangeServer(PairingPayload.ExchangePort(dataPort));
                _exchange.Code = Code;
                _exchange.Uri = Uri;
                _exchange.Paired += OnPaired;
                _exchange.Start();
            }
            catch (Exception ex)
            {
                _exchange = null;
                _log(LogLevel.Warning, $"hexbridge: обмен по короткому коду недоступен: {ex.Message}");
            }
        }

        // The advertisement carries the tag of the key this PC is *currently* running on,
        // not of the one on screen: the key on screen is an offer nobody has accepted yet,
        // and a Mac already paired with this PC must keep finding it until the moment the
        // new key is committed. `CommitPairingAsync` republishes then.
        _discovery.Publish(_readConfig(), MachineName);

        IsDiscovering = _discovery.IsPublishing;
        DiscoveryText = _discovery.Error is { } error
            ? $"Автопоиск недоступен: {error}. Код и QR работают как обычно."
            : "Mac найдёт этот ПК сам — выберите его в списке и введите код с этого экрана.";
    }

    /// <summary>Raised on the exchange server's own thread; the UI is touched on the UI one.</summary>
    private void OnPaired() => Dispatcher.UIThread.Post(() =>
    {
        _log(LogLevel.Info, "hexbridge: Mac забрал ключ связывания");
        if (Step <= 2) Step = 3;
        _ = RunChecksAsync();
    });

    [RelayCommand]
    private void Wait() => Step = 2;

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (CopyToClipboard is not null) await CopyToClipboard(Code);
    }

    [RelayCommand]
    private async Task CopyUriAsync()
    {
        if (CopyToClipboard is not null) await CopyToClipboard(Uri);
    }

    // MARK: - Step 4: the connection check

    /// <summary>
    /// Runs the six checks one at a time with a pause between them. The pause is not
    /// decoration: six lines appearing at once cannot be read, and §9.4 asks for at least
    /// 250 ms so the user can watch the thing progress.
    /// </summary>
    private async Task RunChecksAsync()
    {
        if (_checks is not null) return;

        _checks = new CancellationTokenSource();
        var token = _checks.Token;
        IsFinished = false;
        Step = 3;

        try
        {
            // The key is committed before the checks run, or checks 2 to 5 would be
            // measuring the previous pairing.
            if (Pending is { } payload) await _commit(payload);

            var config = _readConfig();
            var gap = ReducedMotion.Pick(TimeSpan.FromMilliseconds(280));

            // The first line asks the same question of a different address: the machine that
            // listens checks what it is listening on, the one that dials checks what it dials.
            Checks[0].Title = IsGiving ? "Адрес второй машины разбирается" : "Адрес разрешается";
            Checks[1].Title = IsGiving ? "Ответ приходит" : "Пакеты доходят";
            Checks[3].Title = IsGiving ? "Микрофон найден" : "Аудиоустройство найдено";

            await Advance(Checks[0],
                () => PairingChecks.Address(IsGiving ? config.Relay ?? config.Target : config.Listen), gap, token);
            await Advance(Checks[1],
                () => PairingChecks.Packets(_snapshot.LastPacketAt, DateTime.UtcNow, _snapshot.RttMs), gap, token);
            await Advance(Checks[2], () => PairingChecks.Keys(config.Psk, _sawPackets), gap, token);

            var microphone = _snapshot.Feature<MicrophoneState>("microphone");
            await Advance(Checks[3],
                () => IsGiving
                    ? PairingChecks.Input(microphone?.DeviceName ?? microphone?.OutputDescription)
                    : PairingChecks.Device(microphone?.DeviceName), gap, token);

            await RunSoundCheckAsync(token);

            var devices = _snapshot.Feature<DevicesState>("devices");
            await Advance(Checks[5],
                () => IsGiving
                    ? CheckOutcome.Skip("отсюда устройства не пробрасываются")
                    : PairingChecks.Controller(config.Gamepad, devices?.DriverInstalled ?? false,
                        devices?.Attached ?? false, Named(devices)), gap, token);

            Conclude();
        }
        catch (OperationCanceledException)
        {
            // The wizard was closed while the checks were running. Nothing to report.
        }
        finally
        {
            Quieten();
            _checks?.Dispose();
            _checks = null;
        }
    }

    /// <summary>Every forwarded device on one line, or null when there are none.</summary>
    private static string? Named(DevicesState? devices)
    {
        if (devices is null || devices.Devices.Count == 0) return null;
        return string.Join(", ", devices.Devices.Select(d => d.Product));
    }

    private static async Task Advance(CheckItem item, Func<CheckOutcome> evaluate, TimeSpan gap, CancellationToken token)
    {
        item.State = CheckState.Running;
        await Task.Delay(gap, token);
        item.Apply(evaluate());
    }

    /// <summary>
    /// Check 5, and the only one that proves anything end to end: the wizard asks for a
    /// voice and shows the level measured on <b>this</b> machine. Five seconds, counted
    /// down, because an open-ended «say something» has no obvious end.
    /// </summary>
    private async Task RunSoundCheckAsync(CancellationToken token)
    {
        var item = Checks[4];
        item.State = CheckState.Running;
        item.Detail = "скажите что-нибудь вслух";
        Checks[4].Title = IsGiving ? "Микрофон слышит голос" : "Звук проходит насквозь";

        _soundPeak = 0;
        _soundStartedAt = DateTime.UtcNow;
        IsSoundPrompt = true;

        while (!token.IsCancellationRequested)
        {
            var elapsed = DateTime.UtcNow - _soundStartedAt;
            var left = PairingChecks.SoundWindow - elapsed;
            if (left <= TimeSpan.Zero) break;

            SoundCountdown = $"{left.TotalSeconds:0} с";
            var outcome = PairingChecks.Sound(_soundPeak, windowElapsed: false, capturing: IsGiving);
            if (outcome.State == CheckState.Passed)
            {
                item.Apply(outcome);
                Quieten();
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        }

        Quieten();
        item.Apply(PairingChecks.Sound(_soundPeak, windowElapsed: true, capturing: IsGiving));
    }

    /// <summary>Takes the microphone prompt off the screen, however check 5 ended.</summary>
    private void Quieten()
    {
        _soundStartedAt = DateTime.MaxValue;
        SoundCountdown = "";
        IsSoundPrompt = false;
    }

    /// <summary>
    /// §9.4: the heading is never «Ошибка». Either everything works and it says so, or it
    /// names the one thing that did not.
    /// </summary>
    private void Conclude()
    {
        IsFinished = true;
        var failed = Checks.Where(c => c.IsFailed).ToList();
        AllPassed = failed.Count == 0;

        if (AllPassed)
        {
            var device = _snapshot.Feature<MicrophoneState>("microphone");
            ResultHeadline = "Всё работает";
            var peer = _snapshot.SenderName is { Length: > 0 } named ? named : "вторую машину";
            ResultDetail = IsGiving
                ? $"Микрофон этого компьютера уходит на {peer}."
                : device?.PairedCaptureName is { } capture
                    ? $"Звук идёт на {MachineName}. В играх выбирайте микрофон {capture}."
                    : $"Звук идёт на {MachineName}.";
            return;
        }

        ResultHeadline = failed[0].Number switch
        {
            1 => IsGiving ? "Адрес второй машины не разбирается" : "Адрес этого компьютера не определяется",
            2 => IsGiving ? "Вторая машина не отвечает" : "Пакеты со второй машины не доходят",
            3 => "Ключи не совпадают",
            4 => IsGiving ? "Микрофон не найден" : "Принятый звук некуда отдать",
            5 => IsGiving ? "Микрофон молчит" : "Звук не доходит",
            _ => "Контроллер не проброшен",
        };
        ResultDetail = failed[0].Detail;
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        foreach (var check in Checks) check.Apply(new CheckOutcome(CheckState.Pending, ""));
        await RunChecksAsync();
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        // §9.5: the config is written, the features come up, the wizard closes and never
        // shows itself again — it stays available from the settings as «Связать заново».
        IsOpen = false;
        Pending = null;
        await StopServicesAsync();
    }

    // MARK: - Live state

    /// <summary>Fed from the shell's ten-a-second tick so the checks see the real receiver.</summary>
    public void Apply(ReceiverSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (snapshot.Received > 0) _sawPackets = true;

        Role = snapshot.Role;
        if (snapshot.Feature<MicrophoneState>("microphone") is not { } microphone) return;

        SoundLevel = Math.Clamp((MicrophoneLevel.ToDbfs(microphone.PeakHold) + 60) / 60, 0, 1);
        if (DateTime.UtcNow >= _soundStartedAt) _soundPeak = Math.Max(_soundPeak, microphone.PeakHold);
    }

    // MARK: - Shutdown

    private async Task StopServicesAsync()
    {
        _countdown?.Stop();
        if (_checks is not null) await _checks.CancelAsync();

        // The advertisement is deliberately left running: it belongs to the shell, and
        // taking it down when the wizard closes is exactly what would break finding this
        // PC again tomorrow on a new address.
        if (_exchange is not null)
        {
            _exchange.Paired -= OnPaired;
            await _exchange.DisposeAsync();
            _exchange = null;
        }

        // The browse, unlike the advertisement, belongs to this window: once an address is
        // in the config there is nothing left to look for.
        _browser?.Dispose();
        _browser = null;
    }

    public async ValueTask DisposeAsync() => await StopServicesAsync();
}
