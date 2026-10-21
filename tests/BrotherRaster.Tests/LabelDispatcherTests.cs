using BrotherRaster.Printing;
using BrotherRaster.Status;

namespace BrotherRaster.Tests;

public sealed class LabelDispatcherTests
{
    private static readonly byte[] Job = [0x1B, 0x40, 0x1A];

    [Fact]
    public async Task A_ready_printer_prints_in_one_send()
    {
        var transport = new ScriptedTransport(Ok);
        var outcome = await Dispatcher(transport, PrinterCondition.Ready).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.Printed, outcome.Result);
        Assert.False(outcome.NeedsAttention);
        Assert.Equal(1, transport.Sends);
    }

    [Theory]
    [InlineData(PrinterCondition.CoverOpen)]
    [InlineData(PrinterCondition.NoMedia)]
    [InlineData(PrinterCondition.WrongMedia)]
    [InlineData(PrinterCondition.Error)]
    public async Task A_faulted_printer_is_still_sent_the_job_once(PrinterCondition condition)
    {
        // A QL-810W holds the job and prints it when the fault is cleared.
        var transport = new ScriptedTransport(Ok);
        var outcome = await Dispatcher(transport, condition).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.Held, outcome.Result);
        Assert.True(outcome.NeedsAttention);
        Assert.Equal(1, transport.Sends);
        Assert.Contains("waiting", outcome.Message);
    }

    [Fact]
    public async Task A_printer_switched_on_during_the_retries_prints()
    {
        // Off at first (auto power-off), then someone presses the button.
        var transport = new ScriptedTransport(NotConnected, NotConnected, NotConnected, Ok);
        var delays = new List<TimeSpan>();
        var outcome = await Dispatcher(transport, PrinterCondition.Unreachable, delays).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.Printed, outcome.Result);
        Assert.Equal(4, outcome.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
    }

    [Fact]
    public async Task A_printer_that_stays_off_gives_up_within_the_budget()
    {
        var transport = new ScriptedTransport(Enumerable.Repeat<Action>(NotConnected, 100).ToArray());
        var delays = new List<TimeSpan>();
        var outcome = await Dispatcher(transport, PrinterCondition.Unreachable, delays).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.NotDelivered, outcome.Result);
        Assert.True(delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) <= TimeSpan.FromMinutes(3));
        Assert.True(delays.Aggregate(TimeSpan.Zero, (a, b) => a + b) >= TimeSpan.FromMinutes(2.5), "should keep trying for most of a cold boot's worth of time");
        Assert.Contains("Reprint", outcome.Message);
    }

    [Fact]
    public async Task A_job_broken_off_mid_send_is_never_resent()
    {
        var transport = new ScriptedTransport(Interrupted, Ok);
        var outcome = await Dispatcher(transport, PrinterCondition.Ready).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.Uncertain, outcome.Result);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task A_refused_printer_is_not_retried()
    {
        var transport = new ScriptedTransport(Refused, Ok);
        var outcome = await Dispatcher(transport, PrinterCondition.Ready).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.NotDelivered, outcome.Result);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task Printing_works_when_snmp_is_blocked_but_the_raster_port_is_not()
    {
        var outcome = await Dispatcher(new ScriptedTransport(Ok), PrinterCondition.Unreachable).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.Printed, outcome.Result);
    }

    [Fact]
    public async Task The_file_transport_writes_the_job()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brother-raster-{Guid.NewGuid():N}");
        try
        {
            await new FileLabelTransport(dir).SendAsync(Job);
            Assert.Equal(Job, File.ReadAllBytes(Directory.GetFiles(dir).Single()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_printer_not_known_to_hold_jobs_is_not_sent_one_while_faulted()
    {
        var transport = new ScriptedTransport(Ok);
        var outcome = await Dispatcher(transport, PrinterCondition.CoverOpen, options: new LabelDispatchOptions { PrinterHoldsJobsOnFault = false }).DispatchAsync(Job);

        Assert.Equal(LabelDispatchResult.NotDelivered, outcome.Result);
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, outcome.Attempts);
    }

    [Fact]
    public async Task The_serialized_transport_lets_one_job_through_at_a_time()
    {
        var inner = new SlowTransport();
        using var serialized = new SerializedTransport(inner);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => serialized.SendAsync(Job)));

        Assert.Equal(8, inner.Sends);
        Assert.Equal(1, inner.MaxConcurrent);
    }

    private static void Ok()
    {
    }

    private static void NotConnected() => throw new LabelPrinterException("connect timeout", LabelPrinterFailure.NotConnected);

    private static void Interrupted() => throw new LabelPrinterException("reset", LabelPrinterFailure.DeliveryInterrupted);

    private static void Refused() => throw new LabelPrinterException("production printer", LabelPrinterFailure.Refused);

    private static LabelDispatcher Dispatcher(ILabelTransport transport, PrinterCondition condition, List<TimeSpan>? delays = null, LabelDispatchOptions? options = null) =>
        new(transport, new FixedStatus(condition), options)
        {
            Delay = (d, _) =>
            {
                delays?.Add(d);
                return Task.CompletedTask;
            },
        };

    private sealed class FixedStatus(PrinterCondition condition) : IPrinterStatusSource
    {
        public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrinterStatus(condition, $"{condition}.", null));
    }

    private sealed class ScriptedTransport(params Action[] script) : ILabelTransport
    {
        public int Sends { get; private set; }

        public string Description => "scripted";

        public Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
        {
            script[Sends++]();
            return Task.CompletedTask;
        }
    }

    private sealed class SlowTransport : ILabelTransport
    {
        private int _inFlight;

        public int Sends { get; private set; }

        public int MaxConcurrent { get; private set; }

        public string Description => "slow";

        public async Task SendAsync(ReadOnlyMemory<byte> job, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(10, cancellationToken);
            Sends++;
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
