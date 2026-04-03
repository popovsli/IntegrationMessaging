namespace IntegrationMessaging.Models;

// Models/IntegrationRequest.cs
public sealed class IntegrationRequest
{
    public string? EndpointPath { get; init; }
    public string HttpMethod { get; init; } = "POST";


    // REST and raw SOAP
    public string? Payload { get; init; }
    public string? SoapAction { get; init; }

    // Typed SOAP — handler puts the plain DTO here, client casts it
    public object? TypedPayload { get; init; }

    /// <summary>
    /// Real HTTP headers added to the outgoing request.
    /// </summary>
    public Dictionary<string, string> AdditionalHeaders { get; init; } = [];

    /// <summary>
    /// Internal handler → client metadata. Never sent as HTTP headers.
    /// Use this for transport-specific context like filenames, part names, etc.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    public SendContext? Context { get; init; }
}
