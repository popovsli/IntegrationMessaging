// Services/Security/IPasswordEncryptor.cs
namespace IntegrationMessaging.Services.Security;

public interface IPasswordEncryptor
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}