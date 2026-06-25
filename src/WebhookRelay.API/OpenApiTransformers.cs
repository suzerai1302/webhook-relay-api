using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebhookRelay.API;

// Documents both auth schemes in the OpenAPI spec so Scalar renders "Authorize" boxes.
// The API has two planes: JWT bearer (control plane) and an X-API-Key header (data plane).
public sealed class SecuritySchemesTransformer : IOpenApiDocumentTransformer
{
    public const string BearerSchemeId = "Bearer";
    public const string ApiKeySchemeId = "ApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[BearerSchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "JWT from POST /auth/login. Paste only the token — the 'Bearer ' prefix is added automatically.",
        };
        document.Components.SecuritySchemes[ApiKeySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = "X-API-Key",
            In = ParameterLocation.Header,
            Description = "An API key (whk_live_...) from POST /v1/keys. Used by the data-plane ingest routes.",
        };

        return Task.CompletedTask;
    }
}

// Marks each authed operation as secured in the spec, picking the scheme the endpoint
// actually uses so the Authorize token is sent only where it belongs.
public sealed class AuthorizationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var authData = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>().ToList();
        if (authData.Count == 0) return Task.CompletedTask;

        // ApiKey scheme if any AuthorizeData names it; otherwise the JWT bearer default.
        var usesApiKey = authData.Any(a =>
            a.AuthenticationSchemes?.Contains("ApiKey") == true);
        var schemeId = usesApiKey
            ? SecuritySchemesTransformer.ApiKeySchemeId
            : SecuritySchemesTransformer.BearerSchemeId;

        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(schemeId, null)] = new List<string>(),
        });

        return Task.CompletedTask;
    }
}
