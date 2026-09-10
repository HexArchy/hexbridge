namespace HexBridge;

/// <summary>
/// Which end of the link this machine is. One executable does both: the transport, the
/// features and the window are the same code, and this is the only thing that differs.
///
/// <para>
/// The protocol has always been asymmetric in exactly one way — <c>direction</c> in the
/// nonce and the <c>role</c> byte in HELLO — and symmetric in every other. So a role is
/// not a second program: it is which direction this process seals with, whether it dials
/// a peer or waits for one, and which of the two microphone features is the enabled one.
/// </para>
///
/// <para>
/// The stored name is part of config.json and therefore frozen. <see cref="Receiver"/> is
/// first so that a config written before roles existed — every deployed one — reads back
/// as the behaviour it was written for.
/// </para>
/// </summary>
public enum BridgeRole
{
    /// <summary>
    /// This machine takes somebody else's microphone and hands it to games here. The side
    /// that listens on a port, generates the shared key and publishes itself for discovery.
    /// </summary>
    Receiver,

    /// <summary>
    /// This machine gives its own microphone away. The side that dials, because only the
    /// listening end knows an address worth putting in a pairing code.
    /// </summary>
    Sender,
}

/// <summary>
/// How the two roles are named to a human. «Отправитель» and «приёмник» are protocol
/// words: they describe packets, and the user is not thinking about packets — they are
/// thinking about which machine has the microphone plugged into it.
/// </summary>
public static class RoleWording
{
    public static string Title(BridgeRole role) => role switch
    {
        BridgeRole.Sender => "Отдаёт свой микрофон",
        _ => "Принимает чужой микрофон",
    };

    public static string Summary(BridgeRole role) => role switch
    {
        BridgeRole.Sender =>
            "Микрофон подключён к этому компьютеру, а звук нужен на другом — там он подставится играм.",
        _ =>
            "Микрофон подключён к другому компьютеру, а игры запускаются здесь — звук придёт сюда.",
    };

    /// <summary>What the machine on the far end is, for a status line.</summary>
    public static string Peer(BridgeRole role) => role switch
    {
        BridgeRole.Sender => "компьютер, который принимает звук",
        _ => "компьютер с микрофоном",
    };
}
