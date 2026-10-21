using BrotherRaster.Status;

namespace BrotherRaster.Printing;

public enum LabelDispatchResult
{
    /// <summary>Delivered to a ready printer (or one whose status could not be read).</summary>
    Printed,

    /// <summary>Delivered, but the printer reported a fault: it holds the job and prints it once the fault is cleared.</summary>
    Held,

    /// <summary>Never delivered: the printer is off, unreachable, refused by policy or faulted. Nothing will print.</summary>
    NotDelivered,

    /// <summary>The connection broke mid-job. Something may or may not print; check before sending again.</summary>
    Uncertain,
}

public sealed record LabelDispatchOutcome(LabelDispatchResult Result, string Message, PrinterStatus StatusBefore, int Attempts)
{
    /// <summary>Whether someone needs to be told something.</summary>
    public bool NeedsAttention => Result != LabelDispatchResult.Printed;
}

public sealed record LabelDispatchOptions
{
    /// <summary>
    /// Stop retrying a printer that will not connect once this much time has been spent waiting. A
    /// QL-810W takes about 77 s from power-on to ready, so the default covers someone switching it on
    /// after the first failure.
    /// </summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Waits between connection attempts; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// Send to a printer that reports cover open, no media, wrong media or another fault, because it will
    /// hold the job and print it once the fault is cleared. Verified on the QL-810W (cover open, no media)
    /// and the PT-P750W (cover open). Set it false for a
    /// printer that has not been shown to do that: the job is then not sent, and the outcome is
    /// <see cref="LabelDispatchResult.NotDelivered"/>.
    /// </summary>
    public bool PrinterHoldsJobsOnFault { get; init; } = true;
}

/// <summary>
/// Sends one job by these rules:
/// <list type="number">
/// <item>Read the status first, to be able to say why a label is not coming out.</item>
/// <item>Send even when the printer reports a fault, if it holds jobs (<see cref="LabelDispatchOptions.PrinterHoldsJobsOnFault"/>).</item>
/// <item>Retry only a job that never connected. A job the printer took is never sent again: it may be
///   held and print later, and a second send would print two copies.</item>
/// </list>
/// The dispatcher never throws for printer trouble; it returns an outcome to record and show.
/// </summary>
public sealed class LabelDispatcher(ILabelTransport transport, IPrinterStatusSource status, LabelDispatchOptions? options = null)
{
    private readonly LabelDispatchOptions _options = options ?? new LabelDispatchOptions();

    /// <summary>Replaceable so tests do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public async Task<LabelDispatchOutcome> DispatchAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
    {
        var before = await status.GetStatusAsync(cancellationToken);
        var faulted = before.Condition is not (PrinterCondition.Ready or PrinterCondition.Unreachable);
        if (faulted && !_options.PrinterHoldsJobsOnFault)
        {
            return new(LabelDispatchResult.NotDelivered, $"The label was not sent. {before.Message}", before, 0);
        }

        var waited = TimeSpan.Zero;
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                await transport.SendAsync(job, cancellationToken);
                return faulted
                    ? new(LabelDispatchResult.Held, $"Label waiting at the printer. {before.Message}", before, attempt)
                    : new(LabelDispatchResult.Printed, "Label sent.", before, attempt);
            }
            catch (LabelPrinterException e) when (e.Failure == LabelPrinterFailure.DeliveryInterrupted)
            {
                return new(
                    LabelDispatchResult.Uncertain,
                    $"The printer connection broke while sending the label ({e.Message}). Check whether a label came out before reprinting; the printer may need to be switched off and on.",
                    before,
                    attempt);
            }
            catch (LabelPrinterException e) when (e.Failure == LabelPrinterFailure.Refused)
            {
                return new(LabelDispatchResult.NotDelivered, e.Message, before, attempt);
            }
            catch (LabelPrinterException e) when (e.Failure == LabelPrinterFailure.NotConnected)
            {
                var next = _options.Backoff[Math.Min(attempt - 1, _options.Backoff.Count - 1)];
                if (waited + next > _options.GiveUpAfter)
                {
                    var hint = before.Condition == PrinterCondition.Unreachable ? $" {before.Message}" : "";
                    return new(LabelDispatchResult.NotDelivered, $"The label was not printed: {e.Message}{hint} Reprint it once the printer is on.", before, attempt);
                }

                await Delay(next, cancellationToken);
                waited += next;
            }
        }
    }
}
