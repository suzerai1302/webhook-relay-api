using System.Security.Cryptography;
using System.Text;

namespace WebhookRelay.Core.Services;

// HMAC-SHA256 over the raw request body, keyed by the endpoint's signing secret.
// Subscribers recompute this to verify authenticity. Format matches Stripe's:
// "sha256=" + lowercase hex.
public static class SignatureService
{
    public static string Sign(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
