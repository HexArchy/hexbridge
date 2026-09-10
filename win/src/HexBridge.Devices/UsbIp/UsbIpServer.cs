using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace HexBridge.Devices;

/// <summary>
/// The USB/IP server side, protocol version 1.1.1, speaking to the usbip-win2 client on
/// loopback.
///
/// One connection carries both phases: the client opens a socket, asks for the device list
/// or imports a device, and from the moment an import succeeds the same socket becomes the
/// URB channel that the vhci driver drives. Which is why the operational phase and the URB
/// phase live in one loop here rather than in two servers.
///
/// One server exports up to four devices at once, each on its own busid and each on its own
/// client connection: <c>usbip.exe attach</c> is run once per busid and the vhci driver
/// opens a socket per attachment. So "is the device imported" is a per-busid question, not
/// a property of the server.
/// </summary>
public sealed class UsbIpServer : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<IUsbIpDevice>> _devices;
    private readonly Action<LogLevel, string> _log;

    /// <summary>Busids currently in their URB phase.</summary>
    private readonly ConcurrentDictionary<string, byte> _importedBusIds = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cancel;
    private Task? _accept;

    private int _clients;

    public UsbIpServer(Func<IReadOnlyList<IUsbIpDevice>> devices, Action<LogLevel, string> log)
    {
        _devices = devices;
        _log = log;
    }

    /// <summary>Where we ended up listening. Port 0 in the request resolves to a real port here.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>A usbip client has a socket open — listing counts, so this is not "attached".</summary>
    public bool HasClient => Volatile.Read(ref _clients) > 0;

    /// <summary>At least one device is imported and URBs are flowing.</summary>
    public bool AnyImported => !_importedBusIds.IsEmpty;

    /// <summary>Whether this particular busid is imported.</summary>
    public bool IsImported(string busId) => _importedBusIds.ContainsKey(busId);

    public void Start(IPEndPoint listen)
    {
        if (_listener is not null) return;

        var listener = new TcpListener(listen);
        // No SO_REUSEADDR: on Windows that would let a second HexBridge silently steal the
        // port from a running one, and the failure to bind is the honest answer.
        listener.Start();

        _listener = listener;
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _cancel = new CancellationTokenSource();
        _accept = AcceptLoop(listener, _cancel.Token);
    }

    public async Task StopAsync()
    {
        var cancel = _cancel;
        var accept = _accept;
        var listener = _listener;

        _cancel = null;
        _accept = null;
        _listener = null;

        if (cancel is null) return;
        await cancel.CancelAsync().ConfigureAwait(false);
        listener?.Stop();

        if (accept is not null)
        {
            try
            {
                await accept.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown races on the listener socket are expected.
            }
        }

        cancel.Dispose();
        _importedBusIds.Clear();
        LocalEndPoint = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // MARK: - Connections

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        var sessions = new List<Task>();

        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            sessions.RemoveAll(t => t.IsCompleted);
            sessions.Add(Session(client, token));
        }

        try
        {
            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each session already logged whatever went wrong.
        }
    }

    private async Task Session(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _clients);
        try
        {
            // Interrupt IN URBs sit idle for as long as the player holds still, so Nagle
            // would be batching a report that is already as late as it will ever be.
            client.NoDelay = true;
            using var _ = client;
            await using var stream = client.GetStream();
            await Operational(stream, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (EndOfStreamException)
        {
            // The client hung up, which is how usbip.exe ends a device listing.
        }
        catch (IOException)
        {
            // Same, one layer down.
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"usbip: сессия оборвалась: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
        }
    }

    /// <summary>Phase 1: device list and import, until the client leaves or an import lands.</summary>
    private async Task Operational(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[UsbIpProtocol.OpHeaderSize + UsbIpProtocol.BusIdSize];

        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, UsbIpProtocol.OpHeaderSize), token).ConfigureAwait(false);
            var header = OpHeader.Read(buffer);

            if (header.Version != UsbIpProtocol.Version)
            {
                _log(LogLevel.Warning,
                    $"usbip: клиент говорит на версии 0x{header.Version:x4}, а мы на 0x{UsbIpProtocol.Version:x4}");
                return;
            }

            switch (header.Code)
            {
                case UsbIpProtocol.OpReqDevList:
                    await Send(stream, DeviceList(), token).ConfigureAwait(false);
                    break;

                case UsbIpProtocol.OpReqImport:
                {
                    await stream.ReadExactlyAsync(
                        buffer.AsMemory(0, UsbIpProtocol.BusIdSize), token).ConfigureAwait(false);
                    var busId = UsbIpProtocol.ReadString(buffer.AsSpan(0, UsbIpProtocol.BusIdSize));

                    var device = _devices().FirstOrDefault(d => d.Info.BusId == busId);
                    if (device is null || !_importedBusIds.TryAdd(busId, 0))
                    {
                        _log(LogLevel.Warning, device is null
                            ? $"usbip: запрошен busid {busId}, отдать нечего"
                            : $"usbip: busid {busId} уже импортирован");
                        await Send(stream,
                            OpHeader.Reply(UsbIpProtocol.OpRepImport, UsbIpProtocol.StatusNoDevice).ToArray(),
                            token).ConfigureAwait(false);
                        break;
                    }

                    var reply = new byte[UsbIpProtocol.OpHeaderSize + UsbIpProtocol.UsbDeviceSize];
                    OpHeader.Reply(UsbIpProtocol.OpRepImport).Write(reply);
                    device.Info.Write(reply.AsSpan(UsbIpProtocol.OpHeaderSize));
                    await Send(stream, reply, token).ConfigureAwait(false);

                    _log(LogLevel.Info, $"usbip: {busId} импортирован, пошли URB");
                    try
                    {
                        await UrbLoop(stream, device, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _importedBusIds.TryRemove(busId, out _);
                        _log(LogLevel.Info, $"usbip: {busId} отключён");
                    }
                    return;
                }

                default:
                    _log(LogLevel.Warning, $"usbip: неизвестная команда 0x{header.Code:x4}");
                    return;
            }
        }
    }

    /// <summary>
    /// OP_REP_DEVLIST: a count and then one variable-length entry per exported device, in
    /// busid order so <c>usbip list</c> reads the same way twice running.
    /// </summary>
    private byte[] DeviceList()
    {
        var entries = _devices()
            .OrderBy(d => d.Info.BusId, StringComparer.Ordinal)
            .Select(d => d.Info.ToDevListEntry())
            .ToArray();

        var reply = new byte[UsbIpProtocol.OpHeaderSize + 4 + entries.Sum(e => e.Length)];
        OpHeader.Reply(UsbIpProtocol.OpRepDevList).Write(reply);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            reply.AsSpan(UsbIpProtocol.OpHeaderSize), (uint)entries.Length);

        var at = UsbIpProtocol.OpHeaderSize + 4;
        foreach (var entry in entries)
        {
            entry.CopyTo(reply, at);
            at += entry.Length;
        }
        return reply;
    }

    // MARK: - URBs

    /// <summary>
    /// Phase 2. Control transfers are answered inline because they are pure computation;
    /// an interrupt IN is parked on its own task because it must wait for the player to do
    /// something, and the loop has to stay free to take the next command meanwhile.
    /// </summary>
    private async Task UrbLoop(NetworkStream stream, IUsbIpDevice device, CancellationToken token)
    {
        using var sessionCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var writeLock = new SemaphoreSlim(1, 1);
        var pending = new Dictionary<uint, CancellationTokenSource>();
        var pendingLock = new object();
        var inFlight = new List<Task>();

        var header = new byte[UsbIpProtocol.UrbHeaderSize];

        try
        {
            while (!sessionCancel.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, sessionCancel.Token).ConfigureAwait(false);
                var basic = UrbHeader.Read(header);

                if (basic.Command == UsbIpProtocol.CmdUnlink)
                {
                    var unlink = UsbIpUnlink.Read(header);
                    CancellationTokenSource? victim;
                    lock (pendingLock)
                    {
                        pending.Remove(unlink.UnlinkSeqnum, out victim);
                    }
                    victim?.Cancel();

                    // -ECONNRESET says the URB was found and killed; 0 says it had already
                    // completed and its RET_SUBMIT is on its way.
                    var reply = new UsbIpUnlinkReply
                    {
                        Seqnum = unlink.Header.Seqnum,
                        Status = victim is null ? UsbIpProtocol.StatusSuccess : UsbIpProtocol.StatusUnlinked,
                    };
                    await SendLocked(stream, writeLock, reply.ToArray(), sessionCancel.Token).ConfigureAwait(false);
                    continue;
                }

                if (basic.Command != UsbIpProtocol.CmdSubmit)
                {
                    _log(LogLevel.Warning, $"usbip: URB с командой {basic.Command}, закрываю сессию");
                    return;
                }

                var submit = UsbIpSubmit.ReadHeader(header);

                // An OUT transfer carries its data straight after the header.
                if (!submit.IsIn && submit.TransferBufferLength > 0)
                {
                    var payload = new byte[submit.TransferBufferLength];
                    await stream.ReadExactlyAsync(payload, sessionCancel.Token).ConfigureAwait(false);
                    submit = submit with { TransferBuffer = payload };
                }

                // Isochronous descriptors follow both directions. We export no isochronous
                // endpoint, so they are read off the socket and the URB is stalled — the
                // alternative is a stream that never resynchronises.
                if (submit.IsIsochronous)
                {
                    var descriptors = new byte[submit.NumberOfPackets * 16];
                    await stream.ReadExactlyAsync(descriptors, sessionCancel.Token).ConfigureAwait(false);
                    await SendLocked(stream, writeLock,
                        UsbIpSubmitReply.ForIn(submit.Seqnum, UsbIpProtocol.StatusStall,
                            ReadOnlySpan<byte>.Empty, submit.TransferBufferLength).ToArray(),
                        sessionCancel.Token).ConfigureAwait(false);
                    continue;
                }

                if (submit.IsControl)
                {
                    var result = device.Control(submit.Setup, submit.TransferBuffer, submit.TransferBufferLength);
                    var reply = submit.IsIn
                        ? UsbIpSubmitReply.ForIn(submit.Seqnum, result.Status, result.Data, submit.TransferBufferLength)
                        : UsbIpSubmitReply.ForOut(submit.Seqnum, result.Status,
                            submit.TransferBufferLength, submit.TransferBufferLength);
                    await SendLocked(stream, writeLock, reply.ToArray(), sessionCancel.Token).ConfigureAwait(false);
                    continue;
                }

                if (submit.IsIn && submit.Header.Endpoint == (device.InterruptInEndpoint & 0x0F))
                {
                    var urbCancel = CancellationTokenSource.CreateLinkedTokenSource(sessionCancel.Token);
                    lock (pendingLock) pending[submit.Seqnum] = urbCancel;

                    inFlight.RemoveAll(t => t.IsCompleted);
                    inFlight.Add(InterruptIn(stream, writeLock, device, submit, urbCancel, pending, pendingLock));
                    continue;
                }

                if (!submit.IsIn && submit.Header.Endpoint == (device.InterruptOutEndpoint & 0x0F))
                {
                    device.WriteInterrupt(submit.TransferBuffer);
                    await SendLocked(stream, writeLock,
                        UsbIpSubmitReply.ForOut(submit.Seqnum, UsbIpProtocol.StatusSuccess,
                            submit.TransferBufferLength, submit.TransferBufferLength).ToArray(),
                        sessionCancel.Token).ConfigureAwait(false);
                    continue;
                }

                // Any other endpoint is one we never advertised.
                await SendLocked(stream, writeLock,
                    UsbIpSubmitReply.ForIn(submit.Seqnum, UsbIpProtocol.StatusStall,
                        ReadOnlySpan<byte>.Empty, submit.TransferBufferLength).ToArray(),
                    sessionCancel.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await sessionCancel.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Parked URBs end in cancellation on the way out; that is the point.
            }
            writeLock.Dispose();
        }
    }

    private async Task InterruptIn(
        NetworkStream stream,
        SemaphoreSlim writeLock,
        IUsbIpDevice device,
        UsbIpSubmit submit,
        CancellationTokenSource urbCancel,
        Dictionary<uint, CancellationTokenSource> pending,
        object pendingLock)
    {
        try
        {
            var report = await device.ReadInterruptAsync(urbCancel.Token).ConfigureAwait(false);

            lock (pendingLock)
            {
                // An unlink that arrived while we were waiting already answered for this URB.
                if (!pending.Remove(submit.Seqnum)) return;
            }

            var reply = UsbIpSubmitReply.ForIn(
                submit.Seqnum, UsbIpProtocol.StatusSuccess, report, submit.TransferBufferLength);
            await SendLocked(stream, writeLock, reply.ToArray(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Unlinked or shutting down: RET_UNLINK is the only answer this URB gets.
        }
        catch (ObjectDisposedException)
        {
            // The device went away under us.
        }
        catch (IOException)
        {
            // The client hung up while this URB was parked.
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"usbip: прерывание IN: {ex.Message}");
        }
        finally
        {
            lock (pendingLock) pending.Remove(submit.Seqnum);
            urbCancel.Dispose();
        }
    }

    private static async Task Send(NetworkStream stream, byte[] bytes, CancellationToken token) =>
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);

    private static async Task SendLocked(
        NetworkStream stream, SemaphoreSlim writeLock, byte[] bytes, CancellationToken token)
    {
        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
