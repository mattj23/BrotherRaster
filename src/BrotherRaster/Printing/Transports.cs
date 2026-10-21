using System.Net.Sockets;

namespace BrotherRaster.Printing;

/// <summary>Something that takes a finished raster job: a network printer, a file sink, or a wrapper around one.</summary>
public interface ILabelTransport
{
    string Description { get; }

    /// <summary>Delivers the whole job or throws <see cref="LabelPrinterException"/>.</summary>
    Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default);
}

public sealed record RawTcpTransportOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 9100;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the printer to take the whole job before giving up.</summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Sends a job over raw TCP (port 9100), the way both the QL-810W and the PT-P750W take them.
///
/// Neither printer sends anything back on this port, so there is no status to wait for. What matters is
/// that the whole job arrives: a write returning only means the bytes reached the local kernel. If the
/// connection is torn down while bytes are still queued (a process exiting right after its write, say),
/// a QL-810W holds a partial job, blinks, and stays BUSY until it is switched off and on. So this sends,
/// half-closes, and waits for the printer to close its side, which it does once it has read everything.
/// </summary>
public sealed class RawTcpTransport(RawTcpTransportOptions options) : ILabelTransport
{
    public string Description => $"{options.Host}:{options.Port}";

    public async Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(options.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(options.Host, options.Port, connect.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LabelPrinterException($"Label printer {Description} did not accept a connection within {options.ConnectTimeout.TotalSeconds:0} s.", LabelPrinterFailure.NotConnected);
            }
            catch (SocketException e)
            {
                throw new LabelPrinterException($"Label printer {Description} is unreachable: {e.SocketErrorCode}.", LabelPrinterFailure.NotConnected, e);
            }
        }

        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        delivery.CancelAfter(options.DeliveryTimeout);
        try
        {
            var sent = 0;
            while (sent < job.Length)
            {
                sent += await socket.SendAsync(job[sent..], SocketFlags.None, delivery.Token);
            }

            socket.Shutdown(SocketShutdown.Send);

            // Drain until the printer closes: that is the only acknowledgement it gives.
            var sink = new byte[256];
            while (await socket.ReceiveAsync(sink, SocketFlags.None, delivery.Token) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LabelPrinterException($"Label printer {Description} did not take the whole job within {options.DeliveryTimeout.TotalSeconds:0} s.", LabelPrinterFailure.DeliveryInterrupted);
        }
        catch (SocketException e)
        {
            throw new LabelPrinterException($"Label printer {Description} dropped the connection: {e.SocketErrorCode}.", LabelPrinterFailure.DeliveryInterrupted, e);
        }
    }
}

/// <summary>Writes each job to a directory as a timestamped <c>.bin</c>. For development without a printer.</summary>
public sealed class FileLabelTransport(string directory) : ILabelTransport
{
    public string Description => $"file:{directory}";

    public async Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, job.ToArray(), cancellationToken);
    }
}

/// <summary>
/// Lets one job at a time through to the transport it wraps, in the order they arrive. Share one
/// instance per printer within a process; it does nothing about other processes sending to the same
/// printer.
/// </summary>
public sealed class SerializedTransport(ILabelTransport inner) : ILabelTransport, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Description => inner.Description;

    public async Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await inner.SendAsync(job, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
