namespace IntegrationMessaging.Configuration;

public sealed class IntegrationMessagingOptions
{
    public const string Section = "IntegrationMessaging";

    public int BatchSize { get; set; } = 50;
    public int PollIntervalSeconds { get; set; } = 5;
    public int LockDurationMinutes { get; set; } = 5;
    public int EndpointCacheMinutes { get; set; } = 5;
}
