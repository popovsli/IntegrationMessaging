using System.Collections.Concurrent;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;
using IntegrationMessaging.Entities;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services.Clients.Soap;

/// <summary>
/// Singleton-safe, thread-safe cache of WCF ChannelFactory instances.
/// ChannelFactory creation is expensive — reuse across calls for the same system.
/// </summary>
public sealed class SoapChannelFactory(
    ILogger<SoapChannelFactory> logger) : ISoapChannelFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, ChannelFactory<IRequestChannel>>
        _factories = new();

    public ChannelFactory<IRequestChannel> GetOrCreate(IntegrationSystem system) =>
        _factories.GetOrAdd(system.IntegrationSystemCode, _ =>
        {
            var factory = CreateFactory(system);
            logger.LogInformation(
                "Created WCF ChannelFactory for {SystemCode} | Binding={ClientType}",
                system.IntegrationSystemCode, system.ClientType);
            return factory;
        });

    public void Invalidate(string integrationSystemCode)
    {
        if (_factories.TryRemove(integrationSystemCode, out var factory))
        {
            SafeClose(factory);
            logger.LogInformation("Invalidated WCF ChannelFactory for {SystemCode}.", integrationSystemCode);
        }
    }

    private static ChannelFactory<IRequestChannel> CreateFactory(IntegrationSystem system)
    {
        var binding = BuildBinding(system);
        var endpoint = new EndpointAddress(system.BaseAddress);
        var factory = new ChannelFactory<IRequestChannel>(binding, endpoint);

        if (!string.IsNullOrWhiteSpace(system.UserName) &&
            !string.IsNullOrWhiteSpace(system.PasswordSecret))
        {
            factory.Credentials.UserName.UserName = system.UserName;
            factory.Credentials.UserName.Password = system.PasswordSecret;
        }

        return factory;
    }

    private static Binding BuildBinding(IntegrationSystem system)
    {
        var send = TimeSpan.FromSeconds(system.ClientTimeoutSeconds);
        var receive = TimeSpan.FromSeconds(system.ClientTimeoutSeconds * 2);
        const long maxMsg = 10 * 1024 * 1024;

        return system.ClientType switch
        {
            "SOAP_BASIC" => new BasicHttpBinding
            {
                Security = new BasicHttpSecurity
                {
                    Mode = BasicHttpSecurityMode.None,
                    Message = new BasicHttpMessageSecurity
                    { ClientCredentialType = BasicHttpMessageCredentialType.UserName }
                },
                MaxReceivedMessageSize = maxMsg,
                SendTimeout = send,
                ReceiveTimeout = receive,
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30)
            },

            "SOAP_BASIC_HTTPS" => new BasicHttpsBinding
            {
                Security = new BasicHttpsSecurity
                {
                    Mode = BasicHttpsSecurityMode.Transport,
                    Message = new BasicHttpMessageSecurity
                    { ClientCredentialType = BasicHttpMessageCredentialType.UserName }
                },
                MaxReceivedMessageSize = maxMsg,
                SendTimeout = send,
                ReceiveTimeout = receive,
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30)
            },

            "SOAP_WS" => new WSHttpBinding
            {
                Security = new WSHttpSecurity
                {
                    Mode = SecurityMode.Message,
                    Message = new NonDualMessageSecurityOverHttp
                    {
                        ClientCredentialType = MessageCredentialType.UserName,
                        EstablishSecurityContext = false
                    }
                },
                MaxReceivedMessageSize = maxMsg,
                SendTimeout = send,
                ReceiveTimeout = receive,
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30)
            },

            _ => throw new InvalidOperationException(
                $"Unknown SOAP ClientType '{system.ClientType}'.")
        };
    }

    private static void SafeClose(ChannelFactory<IRequestChannel> factory)
    {
        try { factory.Close(); }
        catch { factory.Abort(); }
    }

    public void Dispose()
    {
        foreach (var f in _factories.Values) SafeClose(f);
        _factories.Clear();
    }
}
