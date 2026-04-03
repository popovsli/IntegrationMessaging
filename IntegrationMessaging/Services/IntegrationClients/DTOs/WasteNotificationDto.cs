// Systems/WasteSoap/WasteNotificationDto.cs
namespace IntegrationMessaging.Services.IntegrationClients.DTOs;

public sealed class WasteNotificationDto
{
    public int VesselCallId { get; init; }
    public string WasteTypeCode { get; init; } = string.Empty;
    public double QuantityM3 { get; init; }
    public string PortCode { get; init; } = string.Empty;
}
