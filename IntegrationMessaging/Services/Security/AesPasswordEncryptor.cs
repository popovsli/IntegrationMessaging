// Services/Security/AesPasswordEncryptor.cs
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace IntegrationMessaging.Services.Security;

/// <summary>
/// AES-256-GCM authenticated encryption.
/// Key must be 32 bytes (256 bits), stored in secrets manager / env var.
/// Format on wire: Base64( nonce[12] + tag[16] + ciphertext )
/// </summary>
public sealed class AesPasswordEncryptor(
    IOptions<EncryptionOptions> options) : IPasswordEncryptor
{
    private readonly byte[] _key = Convert.FromBase64String(options.Value.Key);

    public string Encrypt(string plainText)
    {
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];   // 12 bytes
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];     // 16 bytes
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Pack: nonce | tag | cipher
        var packed = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        cipherBytes.CopyTo(packed, nonce.Length + tag.Length);

        return Convert.ToBase64String(packed);
    }

    public string Decrypt(string cipherText)
    {
        var packed = Convert.FromBase64String(cipherText);

        var nonce = packed[..12];
        var tag = packed[12..28];
        var cipher = packed[28..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}