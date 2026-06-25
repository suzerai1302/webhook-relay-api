using WebhookRelay.Core.Entities;

namespace WebhookRelay.Core.Abstractions;

public interface ITokenIssuer
{
    string CreateToken(User user);
}
