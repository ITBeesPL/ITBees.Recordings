namespace ITBees.Recordings.Models;

/// <summary>Installing or controlling the recorder failed (download, checksum, sudo, systemd).</summary>
public class RecordingsException : Exception
{
    public RecordingsException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The camera answered an ONVIF call with a SOAP fault or an HTTP error.</summary>
public sealed class OnvifException : RecordingsException
{
    public OnvifException(string operation, int httpStatus, string? faultCode, string? reason)
        : base($"ONVIF {operation} failed: HTTP {httpStatus}{(faultCode != null ? $", {faultCode}" : "")}{(reason != null ? $" - {reason}" : "")}")
    {
        Operation = operation;
        HttpStatus = httpStatus;
        FaultCode = faultCode;
        Reason = reason;
    }

    public string Operation { get; }
    public int HttpStatus { get; }

    /// <summary>Most specific fault code, e.g. <c>ter:NotAuthorized</c>.</summary>
    public string? FaultCode { get; }

    public string? Reason { get; }

    public bool IsNotAuthorized =>
        HttpStatus == 401 || (FaultCode?.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase) ?? false);
}
