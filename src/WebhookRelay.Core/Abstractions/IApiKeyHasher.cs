namespace WebhookRelay.Core.Abstractions;

/// <summary>Hashes API-key secrets for at-rest storage (SHA-256, keys are high-entropy).</summary>
public interface IApiKeyHasher
{
    string Hash(string secret);
}
