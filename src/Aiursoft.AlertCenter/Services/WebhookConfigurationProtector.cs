using System.Security.Cryptography;
using System.Text;

namespace Aiursoft.AlertCenter.Services;

public class WebhookConfigurationProtector(IConfiguration configuration)
{
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[]? _key = ParseKey(configuration["Webhook:HeaderProtectionKey"]);

    public bool IsAvailable => _key != null;

    public bool TryProtect(string? value, out string? protectedValue)
    {
        protectedValue = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (_key == null) return false;
        var plaintext = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[NonceLength + TagLength + plaintext.Length];
        var nonce = encrypted.AsSpan(0, NonceLength);
        var tag = encrypted.AsSpan(NonceLength, TagLength);
        var ciphertext = encrypted.AsSpan(NonceLength + TagLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        protectedValue = "v1:" + Convert.ToBase64String(encrypted);
        return true;
    }

    public bool TryUnprotect(string? protectedValue, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(protectedValue)) return true;
        if (_key == null || !protectedValue.StartsWith("v1:", StringComparison.Ordinal)) return false;
        try
        {
            var encrypted = Convert.FromBase64String(protectedValue[3..]);
            if (encrypted.Length < NonceLength + TagLength) return false;
            var plaintext = new byte[encrypted.Length - NonceLength - TagLength];
            using var aes = new AesGcm(_key, TagLength);
            aes.Decrypt(encrypted.AsSpan(0, NonceLength), encrypted.AsSpan(NonceLength + TagLength),
                encrypted.AsSpan(NonceLength, TagLength), plaintext);
            value = Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[]? ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == KeyLength ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
