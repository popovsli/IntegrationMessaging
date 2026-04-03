namespace IntegrationMessaging.Models;

/// <summary>
/// Result of endpoint resolution. SoapAction is only populated for SOAP/WCF clients.
/// </summary>
public sealed record EndpointResolution(
    string  ResolvedPath,
    string  HttpMethod,
    string? SoapAction = null);
