using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Expense.Tracking.Identity.ContractTests;

/// <summary>
/// IDX-6 (ADR-0003 do expense.tracking): o que os consumidores do Identity usam e nao pode quebrar
/// sem versao major. Discovery, JWKS, Authorization Code + PKCE pela tela de login, refresh com
/// rotacao, client credentials e as claims <c>iss</c>, <c>aud</c>, <c>sub</c> e <c>scope</c>.
/// </summary>
public sealed class OidcContractTests
{
    private static readonly string BaseUrl = ContractEnvironment.BaseUrl ?? "http://localhost";

    private static HttpClient NewClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
    };

    [ContractFact]
    public async Task Discovery_PublishesIssuerEndpointsAndCapabilities()
    {
        using var client = NewClient();

        var doc = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");

        Assert.Equal(BaseUrl, doc.GetProperty("issuer").GetString());
        foreach (var endpoint in new[] { "authorization_endpoint", "token_endpoint", "jwks_uri", "userinfo_endpoint" })
        {
            Assert.StartsWith("http", doc.GetProperty(endpoint).GetString());
        }

        var grants = Strings(doc.GetProperty("grant_types_supported"));
        Assert.Contains("authorization_code", grants);
        Assert.Contains("refresh_token", grants);
        Assert.Contains("client_credentials", grants);
        Assert.Contains("S256", Strings(doc.GetProperty("code_challenge_methods_supported")));
    }

    [ContractFact]
    public async Task Jwks_HasAnRsaSigningKey()
    {
        using var client = NewClient();

        var jwks = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json"));

        var key = Assert.Single(jwks.Keys, k => k.Kty == "RSA");
        Assert.False(string.IsNullOrEmpty(key.Kid));
        Assert.False(string.IsNullOrEmpty(key.N));
        Assert.False(string.IsNullOrEmpty(key.E));
    }

    [ContractFact]
    public async Task AuthorizationCodeWithPkce_ThroughTheLoginPage_IssuesValidTokens_AndRefreshRotates()
    {
        using var client = NewClient();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = "/oauth/authorize?response_type=code" +
            $"&client_id={ContractEnvironment.SpaClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(ContractEnvironment.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid profile offline_access contract.read")}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=contract";

        // Sem sessao: vai para a tela de login hospedada pelo Identity.
        var toLogin = await client.GetAsync(authorize);
        Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
        Assert.Contains("/account/login", toLogin.Headers.Location!.OriginalString);

        var login = await client.PostAsync("/account/login/submit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = ContractEnvironment.UserEmail,
            ["password"] = ContractEnvironment.UserPassword,
            ["returnUrl"] = authorize,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var toCallback = await client.GetAsync(login.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, toCallback.StatusCode);
        var callback = toCallback.Headers.Location!;
        Assert.StartsWith(ContractEnvironment.RedirectUri, callback.OriginalString);
        var query = Query(callback);
        Assert.Equal("contract", query["state"]);

        var tokens = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ContractEnvironment.SpaClientId,
            ["code"] = query["code"],
            ["redirect_uri"] = ContractEnvironment.RedirectUri,
            ["code_verifier"] = verifier,
        });

        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString(), ignoreCase: true);
        Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);
        var jwt = await ValidateAsync(client, tokens.GetProperty("access_token").GetString()!);
        Assert.True(Guid.TryParse(jwt.Subject, out _), "sub do usuario deve ser o id (GUID)");
        Assert.Contains("contract.read", jwt.Claims.Single(c => c.Type == "scope").Value.Split(' '));

        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        var refreshed = await TokenAsync(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ContractEnvironment.SpaClientId,
            ["refresh_token"] = refreshToken,
        });
        Assert.NotEqual(refreshToken, refreshed.GetProperty("refresh_token").GetString());
        Assert.Equal(jwt.Subject, (await ValidateAsync(client, refreshed.GetProperty("access_token").GetString()!)).Subject);

        // Rotacao: o refresh token ja usado nao vale mais.
        var reused = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ContractEnvironment.SpaClientId,
            ["refresh_token"] = refreshToken,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal("invalid_grant", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [ContractFact]
    public async Task ClientCredentials_IssuesAValidTokenWithTheRequestedScope_AndNoRefresh()
    {
        using var client = NewClient();

        var tokens = await TokenAsync(client, new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ContractEnvironment.ServiceClientId,
            ["client_secret"] = ContractEnvironment.ServiceClientSecret,
            ["scope"] = "contract.internal",
        });

        var jwt = await ValidateAsync(client, tokens.GetProperty("access_token").GetString()!);
        Assert.False(string.IsNullOrEmpty(jwt.Subject));
        Assert.Equal("contract.internal", jwt.Claims.Single(c => c.Type == "scope").Value);
        Assert.False(tokens.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String);
    }

    [ContractFact]
    public async Task ClientCredentials_WithWrongSecret_IsInvalidClient()
    {
        using var client = NewClient();

        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ContractEnvironment.ServiceClientId,
            ["client_secret"] = "errado",
            ["scope"] = "contract.internal",
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>Valida assinatura (JWKS), issuer, audiencia e validade, como os servicos consumidores.</summary>
    private static async Task<JwtSecurityToken> ValidateAsync(HttpClient client, string accessToken)
    {
        var jwks = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json"));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        handler.ValidateToken(accessToken, new TokenValidationParameters
        {
            ValidIssuer = BaseUrl,
            ValidAudience = ContractEnvironment.Audience,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        }, out var validated);

        return (JwtSecurityToken)validated;
    }

    private static async Task<JsonElement> TokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"/oauth/token {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
