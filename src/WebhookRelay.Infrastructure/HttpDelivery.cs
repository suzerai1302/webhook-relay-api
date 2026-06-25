using System.Net.Http.Headers;
using System.Text;
using WebhookRelay.Core.Abstractions;

namespace WebhookRelay.Infrastructure;

// Real outbound delivery: POST the JSON payload plus signature/identity headers to the
// subscriber. Network/HTTP errors surface as an unsuccessful result, not an exception,
// so the processor records them as a normal retry.
public class HttpDelivery : IHttpDelivery
{
    private readonly HttpClient _client;

    public HttpDelivery(HttpClient client)
    {
        _client = client;
        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<DeliveryResult> SendAsync(
        string url, string body, IDictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            foreach (var (k, v) in headers)
                req.Headers.TryAddWithoutValidation(k, v);

            using var res = await _client.SendAsync(req, ct);
            var snippet = await ReadSnippetAsync(res, ct);
            return new DeliveryResult(res.IsSuccessStatusCode, (int)res.StatusCode, snippet);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeliveryResult(false, null, ex.Message);
        }
    }

    private static async Task<string?> ReadSnippetAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            return text.Length > 500 ? text[..500] : text;
        }
        catch
        {
            return null;
        }
    }
}
