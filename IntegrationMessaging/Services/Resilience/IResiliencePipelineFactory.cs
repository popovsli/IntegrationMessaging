// Services/Resilience/IResiliencePipelineFactory.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;
using Polly;

namespace IntegrationMessaging.Services.Resilience;

public interface IResiliencePipelineFactory
{
    /// <summary>
    /// Builds a pipeline for REST clients.
    /// Retries on HttpRequestException and 5xx responses.
    /// </summary>
    ResiliencePipeline<IntegrationResponse> CreateRestPipeline(IntegrationSystem system);

    /// <summary>
    /// Builds a pipeline for SOAP clients (raw + typed).
    /// Retries on CommunicationException and TimeoutException.
    /// FaultException is never retried — it is a business rejection.
    /// </summary>
    ResiliencePipeline<IntegrationResponse> CreateSoapPipeline(IntegrationSystem system);
}