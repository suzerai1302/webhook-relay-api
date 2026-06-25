using System.Collections.Concurrent;
using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.Tests.Fakes;

// Records every outbound call and returns a scripted result, so dispatcher tests can
// assert on the signed request and drive success/failure deterministically.
public class FakeHttpDelivery : IHttpDelivery
{
    public record Call(string Url, string Body, IDictionary<string, string> Headers);

    public ConcurrentQueue<Call> Calls { get; } = new();

    // Default: every send succeeds with 200. Tests flip this to exercise retries.
    public Func<Call, DeliveryResult> Respond { get; set; } =
        _ => new DeliveryResult(true, 200, "ok");

    public Task<DeliveryResult> SendAsync(
        string url, string body, IDictionary<string, string> headers, CancellationToken ct)
    {
        var call = new Call(url, body, new Dictionary<string, string>(headers));
        Calls.Enqueue(call);
        return Task.FromResult(Respond(call));
    }
}
