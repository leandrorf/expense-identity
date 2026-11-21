namespace Expense.Tracking.Identity.ContractTests;

/// <summary>
/// Onde esta o Identity sob teste e quais clientes/usuario ele foi configurado para ter
/// (tests/contract/docker-compose.yml). Valores por variavel de ambiente, com os padroes do compose.
/// </summary>
public static class ContractEnvironment
{
    public static string? BaseUrl => Environment.GetEnvironmentVariable("IDENTITY_CONTRACT_BASE_URL")?.TrimEnd('/');

    public static string Audience => Get("IDENTITY_CONTRACT_AUDIENCE", "expense-tracking-api");

    public const string SpaClientId = "contract-spa";
    public const string RedirectUri = "http://localhost:9999/callback";
    public const string ServiceClientId = "contract-service";
    public const string ServiceClientSecret = "contract-secret";
    public const string UserEmail = "contract@example.test";
    public const string UserPassword = "Contract123!";

    private static string Get(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>Teste de contrato: ignorado quando nao ha um Identity no ar para testar.</summary>
public sealed class ContractFactAttribute : FactAttribute
{
    public ContractFactAttribute()
    {
        if (ContractEnvironment.BaseUrl is null)
        {
            Skip = "Defina IDENTITY_CONTRACT_BASE_URL (ver tests/contract/) para rodar o contrato OIDC.";
        }
    }
}
