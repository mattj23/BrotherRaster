namespace BrotherRaster.Printing;

public enum LabelPrinterFailure
{
    /// <summary>No connection was made, so the printer has nothing. Safe to try again.</summary>
    NotConnected,

    /// <summary>
    /// The connection broke part-way through. The printer may hold a partial job (a QL-810W then blinks
    /// and stays BUSY until switched off and on) or, rarely, the whole job. Not safe to resend automatically.
    /// </summary>
    DeliveryInterrupted,

    /// <summary>Refused before any network traffic, by a policy of the caller's (for example a guard on a production printer).</summary>
    Refused,
}

public sealed class LabelPrinterException : Exception
{
    public LabelPrinterException(string message, LabelPrinterFailure failure)
        : base(message)
    {
        Failure = failure;
    }

    public LabelPrinterException(string message, LabelPrinterFailure failure, Exception inner)
        : base(message, inner)
    {
        Failure = failure;
    }

    public LabelPrinterFailure Failure { get; }
}
