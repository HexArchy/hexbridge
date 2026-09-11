using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexBridge.Devices;
using HexBridge.Microphone;

using HexBridge.Localization;

namespace HexBridge.App.ViewModels;

/// <summary>
/// One line of the step-1 checklist: a fact about this PC and, when the fact is not what
/// the user wants, the sentence that says what to do about it.
/// </summary>
public sealed partial class ReadinessItem(string titleKey, string hintKey) : ObservableObject
{
    // Keys rather than strings: the checklist is built once, when the wizard opens, and a
    // language switched while it is on screen has to reach the lines already drawn.
    public string TitleKey { get; } = titleKey;

    public string HintKey { get; } = hintKey;

    public string Title => Loc.Of(TitleKey);

    /// <summary>What to do about it. Shown only while the line is not green.</summary>
    public string Hint => Loc.Of(HintKey);

    [ObservableProperty] private string _value = Strings.Pairing_Ready_Checking;
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private bool _isBad;

    public void Set(bool ok, string value)
    {
        IsOk = ok;
        IsBad = !ok;
        Value = value;
    }

    public void Retranslate()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
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

    public string Title { get; } = string.IsNullOrWhiteSpace(host.Name) ? Strings.Pairing_Host_Unnamed : host.Name;
    public string Address { get; } = host.Target.Length == 0 ? Strings.Pairing_Host_NoAddress : host.Target;
    public string Note { get; } = note;
    public bool IsOurs { get; } = isOurs;
    public bool CanDial { get; } = host.Target.Length > 0;
}

/// <summary>One line of the §9.4 connection check.</summary>
public sealed partial class CheckItem(int number, string titleKey) : ObservableObject
{
    public int Number { get; } = number;

    /// <summary>
    /// Settable because the same six lines ask the same six questions in both roles, and
    /// four of them are worded for whichever machine is asking. Six more CheckItems for the
    /// other role would be six more places for the wording and the logic to drift apart.
    ///
    /// <para>It holds the key, not the sentence, so a language change reaches a line already
    /// drawn on the screen.</para>
    /// </summary>
    [ObservableProperty] private string _titleKey = titleKey;

    public string Title => Loc.Of(TitleKey);

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

    partial void OnTitleKeyChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>
    /// The language changed. The detail beside the line is not touched: it was measured at a
    /// moment — a fingerprint, a round trip, a device name — and the next run rewrites it.
    /// </summary>
    public void Retranslate() => OnPropertyChanged(nameof(Title));

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
/// facts the app cannot know: autodiscovery over Bonjour
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
    [ObservableProperty] private string _title = Strings.Pairing_Title_Step1;

    /// <summary>
    /// Which side of the pairing this machine is. The one that listens makes the key and
    /// shows a code; the one that dials reads it. Set by the shell from the config.
    /// </summary>
    [ObservableProperty] private BridgeRole _role = BridgeRole.Receiver;

    // Step 1.
    public ObservableCollection<ReadinessItem> Readiness { get; } = [];

    // Step 2.
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _uri = "";
    [ObservableProperty] private string _fingerprint = "";
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _discoveryText = "";

    /// <summary>
    /// What to type into the Mac's address field, shown beside the code.
    ///
    /// The QR used to carry the address, so nothing on this screen ever had to say it out
    /// loud; with the QR gone, the Mac asks for an address the PC was not telling anybody.
    /// Every local address is listed, not just the first: on a machine with a VPN up there
    /// are several, only one of them is the one the Mac can reach, and this side has no way
    /// to know which.
    /// </summary>
    [ObservableProperty] private string _thisAddress = "";

    /// <summary>
    /// What to do when the Mac is somewhere else entirely — which is not an edge case:
    /// streaming to a machine on another network is one of the things this is for. Names
    /// both ports, because the sound and the pairing use different ones and only the
    /// sound's is opened by the installer on its own.
    /// </summary>
    public string RemoteAddressNote =>
        // Raw, not Loc.Integer: a port is an identifier, and «47 702» is not a port.
        Loc.F(Strings.Pairing_Caption_RemoteAddress, _dataPort, PairingPayload.ExchangePort(_dataPort));

    /// <summary>The data port the note talks about, kept from the last time a code was made.</summary>
    private int _dataPort = 47702;

    /// <summary>
    /// Whether this code was left with the relay as well as served from here. Changes what
    /// the screen tells the person to type on the Mac: the relay's address, not this one's.
    /// </summary>
    [ObservableProperty] private bool _viaRelay;

    /// <summary>Where the deposit went, shown so the Mac can be pointed at the same place.</summary>
    [ObservableProperty] private string _relayAddress = "";

    /// <summary>
    /// Splits the relay setting into host and port without resolving it.
    ///
    /// Deliberately not <c>ReceiverConfig.ParseEndpoint</c>, which resolves: the host goes
    /// into the payload the Mac keeps, and baking today's address of a name into it would
    /// outlive the address. The Mac resolves it when it connects, every time.
    /// </summary>
    private static (string Host, int Port)? ParseRelay(string? relay, int fallbackPort)
    {
        if (string.IsNullOrWhiteSpace(relay)) return null;

        var value = relay.Trim();

        // [::1]:47702 — the only form where a colon is not the separator.
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0) return null;

            var literal = value[1..close];
            var rest = value[(close + 1)..];
            return (literal, rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketed)
                ? bracketed
                : fallbackPort);
        }

        var mark = value.LastIndexOf(':');
        if (mark <= 0) return value.Length > 0 ? (value, fallbackPort) : null;

        var host = value[..mark];
        return (host, int.TryParse(value[(mark + 1)..], out var port) ? port : fallbackPort);
    }
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

        Checks.Add(new CheckItem(1, "Check_Address_Receiving"));
        Checks.Add(new CheckItem(2, "Check_Traffic_Receiving"));
        Checks.Add(new CheckItem(3, "Check_Keys"));
        Checks.Add(new CheckItem(4, "Check_Device_Receiving"));
        Checks.Add(new CheckItem(5, "Check_Sound_Receiving"));
        Checks.Add(new CheckItem(6, "Check_Devices"));
    }

    public bool IsGiving => Role == BridgeRole.Sender;

    /// <summary>
    /// Check 5 asks for a voice, and where the voice has to be depends on which machine has
    /// the microphone. Getting this backwards would have somebody talking at the wrong
    /// computer and concluding the product is broken.
    /// </summary>
    public string SoundPrompt => IsGiving
        ? Strings.Pairing_Sound_Prompt_Sharing
        : Strings.Pairing_Sound_Prompt_Receiving;

    public string SoundNote => IsGiving
        ? Strings.Pairing_Sound_Note_Sharing
        : Strings.Pairing_Sound_Note_Receiving;

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
            Readiness.Add(new ReadinessItem("Pairing_Ready_Microphone", "Pairing_Ready_Microphone_Hint"));
            Readiness.Add(new ReadinessItem("Pairing_Ready_MicAccess", "Pairing_Ready_MicAccess_Hint"));
            Readiness.Add(new ReadinessItem("Pairing_Ready_Network", "Pairing_Ready_Network_Hint"));
            Readiness.Add(new ReadinessItem("Pairing_Ready_Peer", "Pairing_Ready_Peer_Hint"));
            return;
        }

        Readiness.Add(new ReadinessItem("Pairing_Ready_Driver", "Devices_Driver_InstallHint"));
        Readiness.Add(new ReadinessItem("Pairing_Ready_Cable", "Pairing_Ready_Cable_Hint"));
        Readiness.Add(new ReadinessItem("Pairing_Ready_Port", "Pairing_Ready_Port_Hint"));
        Readiness.Add(new ReadinessItem("Pairing_Ready_Network", "Pairing_Ready_Network_Hint"));
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
        0 => Strings.Pairing_Title_Step1,
        1 => IsGiving ? Strings.Pairing_Title_Step2_Sharing : Strings.Pairing_Title_Step2_Receiving,
        2 => Strings.Pairing_Title_Step3,
        _ => Strings.Pairing_Title_Step4,
    };

    /// <summary>
    /// The language changed. The wizard is the one screen in the app that is not rebuilt from
    /// a snapshot ten times a second, so every line of it is listed here by hand.
    /// </summary>
    public void Retranslate()
    {
        Retitle();
        OnPropertyChanged(nameof(SoundPrompt));
        OnPropertyChanged(nameof(SoundNote));
        foreach (var item in Readiness) item.Retranslate();
        foreach (var check in Checks) check.Retranslate();

        // Two lines that say what the network is doing right now, and one that reports a
        // measurement the user is looking at. Re-reading the facts is the only honest way to
        // put them into another language.
        if (IsOpen && Step == 0) Survey();
        if (_browser is not null) ShowFound(_browser.Hosts);
        if (IsFinished) Conclude();
    }

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
            ? Loc.F(Strings.Pairing_Browse_Unavailable, error)
            : Strings.Pairing_Browse_Searching;
        ShowFound(browser.Hosts);
    }

    private void ShowFound(IReadOnlyList<DiscoveredHost> hosts)
    {
        var ownTag = DiscoveryTag.ForPsk(_readConfig().Psk);

        Found.Clear();
        foreach (var host in hosts)
        {
            var ours = DiscoveryTag.Same(ownTag, host.Tag);
            var note = ours ? Strings.Pairing_Host_Ours
                : host.Tag is null ? Strings.Pairing_Host_Unpaired
                : Strings.Pairing_Host_Other;
            Found.Add(new FoundHostViewModel(host, note, ours));
        }

        if (_browser?.Error is not null) return;
        BrowseText = Found.Count == 0
            ? Strings.Pairing_Browse_Empty
            : Strings.Pairing_Browse_Found;

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
            ConnectError = Strings.Pairing_Error_ShortCode;
            return;
        }

        var (host, port) = SplitTarget(EnteredHost);
        if (host.Length == 0)
        {
            ConnectError = Strings.Pairing_Error_NoHost;
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

            _log(LogLevel.Info, Loc.F(Strings.Log_CodeAccepted, result.Payload!.Fingerprint));
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
        Readiness[0].Set(driver is not null, driver ?? Strings.Pairing_Ready_NotInstalled);

        var device = FindOutputDevice(config);
        Readiness[1].Set(device is not null, device ?? Strings.Pairing_Ready_NotFound);

        // Whether the port is actually open cannot be known from inside this process —
        // that needs a packet from outside — so the line says what is true and no more.
        var port = ParsePort(config.Listen);
        Readiness[2].Set(true, Loc.F(Strings.Pairing_Ready_PortAtStep4, port));

        var address = MulticastDns.LocalAddresses().FirstOrDefault();
        Readiness[3].Set(address is not null, address?.ToString() ?? Strings.Pairing_Ready_NoNetwork);
    }

    /// <summary>
    /// The same four-line shape for the machine that gives its microphone away. The fourth
    /// line is about the other machine and cannot be measured from here, so it says what it
    /// is — a reminder — rather than pretending to a fact.
    /// </summary>
    private void SurveyGiving(ReceiverConfig config)
    {
        var microphone = FindInputDevice(config);
        Readiness[0].Set(microphone is not null, microphone ?? Strings.Pairing_Ready_NotFound);

        // Whether Windows will actually hand over the samples cannot be known without asking
        // for them, and asking here would put a permission prompt in front of somebody who
        // is reading a checklist. It becomes a real answer at step 4, where sound is measured.
        Readiness[1].Set(true, Strings.Pairing_Ready_AtStep4);

        var address = MulticastDns.LocalAddresses().FirstOrDefault();
        Readiness[2].Set(address is not null, address?.ToString() ?? Strings.Pairing_Ready_NoNetwork);

        Readiness[3].Set(true, Strings.Pairing_Ready_NeedsCode);
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

    /// <summary>
    /// Leaves the sealed answer with the relay, so a Mac that cannot reach this machine
    /// can still collect it.
    ///
    /// <para>
    /// Sealed before it goes, under the code on screen — the relay is handed something it
    /// cannot read, filed under a name derived from the code by a one-way hash. It never
    /// learns the code, and therefore never the key.
    /// </para>
    ///
    /// <para>
    /// Fire and forget. The direct listener on this machine is still running and still
    /// serves the same code, so a relay that is down or misconfigured costs nothing on a
    /// network where the two machines can see each other. It is written to the log rather
    /// than to the screen for the same reason.
    /// </para>
    /// </summary>
    private void LeaveWithRelay((string Host, int Port) relay, string uri, string code)
    {
        var blob = PairingSeal.Seal(uri, code);
        var id = PairingSeal.RendezvousId(code);
        var address = $"http://{Host(relay.Host)}:{PairingPayload.ExchangePort(relay.Port)}/rendezvous?id={id}";

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var content = new StringContent(blob);
                var answer = await client.PutAsync(address, content).ConfigureAwait(false);

                _log(answer.IsSuccessStatusCode ? LogLevel.Info : LogLevel.Warning,
                     Loc.F(Strings.Log_RelayDeposit, relay.Host, (int)answer.StatusCode));
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warning, Loc.F(Strings.Log_RelayDepositFailed, relay.Host, ex.Message));
            }
        });
    }

    /// <summary>Wraps an IPv6 literal in brackets so it can go into a URL.</summary>
    private static string Host(string host) => host.Contains(':') ? $"[{host}]" : host;

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
        var addresses = MulticastDns.LocalAddresses().Select(a => a.ToString()).ToArray();
        var host = addresses.FirstOrDefault() ?? "127.0.0.1";
        ThisAddress = addresses.Length > 0 ? string.Join("   ", addresses) : host;
        _dataPort = port;
        OnPropertyChanged(nameof(RemoteAddressNote));

        // With a relay configured, the address the Mac should aim at is the relay, not
        // this machine: that is the whole point of having one, and it is the only address
        // that works when neither side can accept a connection. The payload carries it,
        // so the Mac needs no separate setting.
        var relay = ParseRelay(config.Relay, port);
        var payload = relay is { } via
            ? PairingPayload.Create(via.Host, via.Port, MachineName)
            : PairingPayload.Create(host, port, MachineName);

        Uri = payload.ToUri();
        Fingerprint = payload.Fingerprint;
        Code = ShortCode.Generate();
        Pending = payload;
        ViaRelay = relay is not null;

        if (relay is { } destination)
        {
            RelayAddress = $"{destination.Host}:{destination.Port}";
            LeaveWithRelay(destination, payload.ToUri(), Code);
        }

        _codeExpiresAt = DateTime.UtcNow + ShortCode.Lifetime;
        StartCountdown();
        StartServices(port);
    }

    /// <summary>The payload on screen, kept until the user finishes or gives up.</summary>
    public PairingPayload? Pending { get; private set; }

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
            CountdownText = Strings.Pairing_Code_Expired;
            // The listener stops answering the moment the code is dead, rather than
            // waiting for the user to notice the label.
            if (_exchange is not null) _exchange.Code = "";
            return;
        }

        CountdownText = Loc.F(Strings.Pairing_Code_Valid,
            $"{left.Minutes}:{left.Seconds.ToString("00", System.Globalization.CultureInfo.InvariantCulture)}");
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
                _log(LogLevel.Warning, Loc.F(Strings.Log_ExchangeUnavailable, ex.Message));
            }
        }

        // The advertisement carries the tag of the key this PC is *currently* running on,
        // not of the one on screen: the key on screen is an offer nobody has accepted yet,
        // and a Mac already paired with this PC must keep finding it until the moment the
        // new key is committed. `CommitPairingAsync` republishes then.
        _discovery.Publish(_readConfig(), MachineName);

        IsDiscovering = _discovery.IsPublishing;
        DiscoveryText = _discovery.Error is { } error
            ? Loc.F(Strings.Pairing_Discovery_Unavailable, error)
            : Strings.Pairing_Discovery_Ok;
    }

    /// <summary>Raised on the exchange server's own thread; the UI is touched on the UI one.</summary>
    private void OnPaired() => Dispatcher.UIThread.Post(() =>
    {
        _log(LogLevel.Info, Strings.Log_KeyTaken);
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
            Checks[0].TitleKey = IsGiving ? "Check_Address_Sharing" : "Check_Address_Receiving";
            Checks[1].TitleKey = IsGiving ? "Check_Traffic_Sharing" : "Check_Traffic_Receiving";
            Checks[3].TitleKey = IsGiving ? "Check_Device_Sharing" : "Check_Device_Receiving";

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
                    ? CheckOutcome.Skip(Strings.Check_Detail_NoForwardingHere)
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
        item.Detail = Strings.Check_Detail_SaySomething;
        Checks[4].TitleKey = IsGiving ? "Check_Sound_Sharing" : "Check_Sound_Receiving";

        _soundPeak = 0;
        _soundStartedAt = DateTime.UtcNow;
        IsSoundPrompt = true;

        while (!token.IsCancellationRequested)
        {
            var elapsed = DateTime.UtcNow - _soundStartedAt;
            var left = PairingChecks.SoundWindow - elapsed;
            if (left <= TimeSpan.Zero) break;

            SoundCountdown = Loc.Seconds(left.TotalSeconds);
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
            ResultHeadline = Strings.Pairing_Result_Ok;
            var peer = _snapshot.SenderName is { Length: > 0 } named ? named : Strings.Pairing_Peer_Fallback;
            ResultDetail = IsGiving
                ? Loc.F(Strings.Pairing_Result_Ok_Sharing, peer)
                : device?.PairedCaptureName is { } capture
                    ? Loc.F(Strings.Pairing_Result_Ok_Receiving_WithCapture, MachineName, capture)
                    : Loc.F(Strings.Pairing_Result_Ok_Receiving, MachineName);
            return;
        }

        ResultHeadline = failed[0].Number switch
        {
            1 => IsGiving ? Strings.Pairing_Fail_Address_Sharing : Strings.Pairing_Fail_Address_Receiving,
            2 => IsGiving ? Strings.Pairing_Fail_Traffic_Sharing : Strings.Pairing_Fail_Traffic_Receiving,
            3 => Strings.Pairing_Fail_Keys,
            4 => IsGiving ? Strings.Pairing_Fail_Device_Sharing : Strings.Pairing_Fail_Device_Receiving,
            5 => IsGiving ? Strings.Pairing_Fail_Sound_Sharing : Strings.Pairing_Fail_Sound_Receiving,
            _ => Strings.Pairing_Fail_Controller,
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
