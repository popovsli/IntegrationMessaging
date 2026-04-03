namespace IntegrationMessaging.Entities;

public class IntegrationEndpoint
{
    public int Id { get; set; }
    public string IntegrationSystemCode { get; set; } = string.Empty;

    /// <summary>
    /// Matches IntegrationMessageQueue.MessageTypeName exactly.
    /// One endpoint per message type per system.
    /// </summary>
    public string MessageTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Path relative to IntegrationSystem.BaseAddress.
    /// Supports {EntityId} token e.g. "api/vessels/{EntityId}/waste"
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = "POST";

    /// <summary>
    /// SOAP Action URI. Required for SOAP/WCF clients only.
    /// e.g. "http://tempuri.org/IWasteService/SubmitWasteNotification"
    /// </summary>
    public string? SoapAction { get; set; }

    public string? Description { get; set; }

    public IntegrationSystem? IntegrationSystem { get; set; }
}
