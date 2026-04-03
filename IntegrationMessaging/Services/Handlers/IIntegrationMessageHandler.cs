using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services.Handlers;

public interface IIntegrationMessageHandler
{
    /// <summary>
    /// Must match IntegrationMessageQueue.MessageTypeName exactly.
    /// Declare as a constant in HandlerKeys.
    /// </summary>
    public static string MessageTypeName => "UNDEFINED";  // Implicit impl


    /// <summary>
    /// Validates and shapes the raw queue payload into a typed request.
    /// MessageOperation governs payload shape only — not endpoint selection.
    /// </summary>
    Task<IntegrationRequest> BuildRequestAsync(SendContext context, CancellationToken ct = default);
}
