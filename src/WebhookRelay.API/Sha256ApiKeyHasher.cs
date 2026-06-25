using System.Security.Cryptography;
using System.Text;
using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.API;

/// <summary>SHA-256 hex. API keys are 32 random bytes so a fast hash is sufficient (no bcrypt).</summary>
public class Sha256ApiKeyHasher : IApiKeyHasher
{
    public string Hash(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(bytes);
    }
}
