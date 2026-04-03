// Models/IntegrationResponse.cs
using IntegrationMessaging.Services.Clients.Soap;
using System.ServiceModel;

namespace IntegrationMessaging.Models;

public sealed class IntegrationResponse
{
    // ── Core ─────────────────────────────────────────────────────────────

    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Raw response body. Null for typed SOAP one-way/void operations.
    /// JSON string for REST. XML string for raw SOAP (IRequestChannel).
    /// Typed SOAP operations that return a value set this via ResponsePayload.
    /// </summary>
    public string? ResponsePayload { get; init; }

    // ── REST-specific ─────────────────────────────────────────────────────

    /// <summary>
    /// HTTP status code. Populated by REST clients only.
    /// Null for all SOAP clients (SOAP always returns 200 at transport level).
    /// </summary>
    public int? HttpStatusCode { get; init; }

    // ── SOAP-specific ─────────────────────────────────────────────────────

    /// <summary>
    /// Structured SOAP fault. Populated when IsSuccess=false from a SOAP client.
    /// Null for REST clients and successful SOAP calls.
    /// </summary>
    public SoapFault? SoapFault { get; init; }

    // ── Shared error ──────────────────────────────────────────────────────

    /// <summary>
    /// Human-readable error summary. Always populated when IsSuccess=false.
    /// For SOAP faults, mirrors SoapFault.Reason for quick access.
    /// For REST errors, contains "HTTP {code}: {body}".
    /// </summary>
    public string? Error { get; init; }

    // ── Factory helpers ───────────────────────────────────────────────────

    public static IntegrationResponse Ok(string? payload = null) => new()
    {
        IsSuccess = true,
        ResponsePayload = payload
    };

    public static IntegrationResponse RestError(int statusCode, string body) => new()
    {
        IsSuccess = false,
        HttpStatusCode = statusCode,
        ResponsePayload = body,
        Error = $"HTTP {statusCode}: {body[..Math.Min(500, body.Length)]}"
    };

    public static IntegrationResponse Fault(SoapFault fault, string? rawXml = null) => new()
    {
        IsSuccess = false,
        ResponsePayload = rawXml,
        SoapFault = fault,
        Error = $"SOAP Fault [{fault.Code}]: {fault.Reason}"
                        + (fault.Detail is not null ? $" | {fault.Detail}" : string.Empty)
    };

    public static IntegrationResponse FaultFromException(FaultException ex) => new()
    {
        IsSuccess = false,
        SoapFault = new SoapFault(ex.Code?.Name ?? "Sender", ex.Message),
        Error = $"SOAP Fault: {ex.Message}"
    };
}