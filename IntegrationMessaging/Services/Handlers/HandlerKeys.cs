namespace IntegrationMessaging.Services.Handlers;

/// <summary>
/// Single source of truth for all MessageTypeName values.
/// These must match values stored in IntegrationMessageQueue.MessageTypeName
/// and the keyed DI registration keys in ServiceCollectionExtensions.
/// </summary>
public static class HandlerKeys
{
    public const string WasteNotification   = "WasteNotification";
    public const string WasteRequest        = "WasteRequest";
    public const string SSNNotification     = "SSNNotification";
    public const string SHIPSANNotification = "SHIPSANNotification";
    public const string PCSNotification     = "PCSNotification";
    public const string BdzSiteSchedule     = "BdzSiteSchedule";
}
