using IntegrationMessaging.Entities;

namespace IntegrationMessaging.Services.CircuitBreaker;

public interface ICircuitBreakerService
{
    bool IsOpen(string systemCode);
    void RecordSuccess(string systemCode);
    void RecordFailure(string systemCode, IntegrationSystem system);
    void Reset(string systemCode);
}
