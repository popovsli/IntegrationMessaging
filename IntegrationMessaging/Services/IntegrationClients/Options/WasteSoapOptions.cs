// Systems/WasteSoap/WasteSoapOptions.cs
namespace IntegrationMessaging.Services.IntegrationClients.Options;

public sealed class WasteSoapOptions
{
    public const string Section = "IntegrationSystems:WasteSoap";

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Override the binding type if the target environment uses a different
    /// security profile. Defaults to BasicHttps (Transport security).
    /// Accepted values: SOAP_BASIC | SOAP_BASIC_HTTPS | SOAP_WS
    /// </summary>
    public string BindingType { get; set; } = "SOAP_BASIC_HTTPS";
}
