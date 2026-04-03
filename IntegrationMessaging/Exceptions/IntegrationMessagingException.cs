namespace IntegrationMessaging.Exceptions;

public sealed class IntegrationMessagingException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class CircuitOpenException(string systemCode)
    : Exception($"Circuit breaker is OPEN for system '{systemCode}'. Calls are suspended.");

public sealed class PrerequisiteCreateNotFoundException(int entityId, string systemCode)
    : Exception(
        $"Cannot process Update/Delete for EntityId={entityId} on system '{systemCode}': " +
        "no successful Create found in history and no pending Create found in queue.");
