// Configuration/EncryptionOptions.cs
namespace IntegrationMessaging.Services.Security;

public sealed class EncryptionOptions
{
    public const string Section = "Encryption";

    /// <summary>Base64-encoded 32-byte AES-256 key. Store in secrets manager.</summary>
    public string Key { get; set; } = string.Empty;
}