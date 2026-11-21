# expense-identity

Servidor OAuth2/OIDC próprio do [expense.tracking](https://github.com/leandrorf/expense.tracking),
em repositório separado ([ADR-0003](https://github.com/leandrorf/expense.tracking/blob/main/docs/adr/0003-identity-em-repositorio-proprio.md)).
.NET 10, arquitetura hexagonal, EF Core + MySQL (provider Oracle `MySql.EntityFrameworkCore`).

- **Grants:** Authorization Code + PKCE, Refresh Token (com rotação), Client Credentials.
- **Tokens:** JWT RS256; chaves públicas em JWKS.
- **Endpoints:** `/.well-known/openid-configuration`, `/.well-known/jwks.json`, `/oauth/authorize`,
  `/oauth/token`, `/connect/userinfo`, tela de login em `/account/login`.
- **Saúde:** `/health/live` e `/health/ready` (banco); traces e métricas por OTLP quando
  `OTEL_EXPORTER_OTLP_ENDPOINT` está definido.

## Configuração

O Identity não conhece os consumidores no código: clientes OAuth, usuários iniciais, origens de
CORS e a dica da tela de login vêm da configuração de quem o roda.

| Chave | O que é |
|---|---|
| `ConnectionStrings:IdentityDb` | Conexão MySQL |
| `OAuth:Issuer` | Issuer público (`iss` dos tokens) |
| `OAuth:SigningKey:PrivateKeyPem` / `KeyId` | Chave RSA de assinatura; vazia = gerada a cada subida |
| `Seed:Enabled` | Roda o seed na subida (sempre em `Development`) |
| `Seed:Clients:<client_id>` | `Type` (`Public`/`Confidential`), `Secret`, `RedirectUris`, `Scopes`, `GrantTypes` (`authorization_code`, `refresh_token`, `client_credentials`), `RequirePkce`, `DisplayName` |
| `Seed:Users:<nome>` | `Email`, `Password`, `DisplayName` |
| `Cors:AllowedOrigins` | Origens liberadas para `/oauth/token` e `/connect/userinfo` |
| `LoginPage:Hint` | Texto opcional abaixo do formulário de login |

O seed é idempotente: cria o que falta e dá a um cliente existente os escopos novos da
configuração. Configuração inválida (grant desconhecido, cliente confidencial sem segredo, usuário
sem e-mail) impede a subida, com a chave errada na mensagem e sem o valor de segredos.

Exemplo (`appsettings.Production.json` montado no container):

```json
{
  "Seed": {
    "Clients": {
      "minha-spa": {
        "Type": "Public",
        "RedirectUris": [ "http://localhost:5173/callback" ],
        "Scopes": [ "openid", "profile", "offline_access" ],
        "GrantTypes": [ "authorization_code", "refresh_token" ]
      }
    }
  },
  "Cors": { "AllowedOrigins": [ "http://localhost:5173" ] }
}
```

Segredos por variável de ambiente, pelo nome: `Seed__Clients__meu-servico__Secret`,
`Seed__Users__demo__Password`.

## Imagem e versões

Cada tag `vX.Y.Z` publica `ghcr.io/leandrorf/expense-identity:X.Y.Z` (workflow `Release`). Não há
tag `latest`: quem consome fixa a versão exata. Versionamento SemVer; mudança incompatível no
contrato OIDC (rotas, claims, formato de token, escopos existentes) é versão major.

```bash
git tag v1.2.0 && git push origin v1.2.0
docker pull ghcr.io/leandrorf/expense-identity:1.2.0
```

## Desenvolvimento

```bash
dotnet test Expense.Tracking.Identity.slnx   # unitários + integração (EF InMemory, sem MySQL)
docker build -t expense-identity .
```

### Contrato OIDC

`tests/Expense.Tracking.Identity.ContractTests` testa, de fora, o que os consumidores usam: discovery,
JWKS, Authorization Code + PKCE pela tela de login, refresh com rotação, client credentials e as
claims `iss`, `aud`, `sub` e `scope` (assinatura validada pelo JWKS). Roda no job `oidc contract`
do CI contra a imagem do commit + MySQL; sem `IDENTITY_CONTRACT_BASE_URL`, os testes ficam ignorados.
Quebrar um desses testes de propósito é mudança de versão major.

```bash
docker compose -f tests/contract/docker-compose.yml up -d --build    # espere :5081/health/ready
IDENTITY_CONTRACT_BASE_URL=http://localhost:5081 dotnet test tests/Expense.Tracking.Identity.ContractTests
docker compose -f tests/contract/docker-compose.yml down -v
```

No expense.tracking, o `docker-compose.yml` declara os clientes do monorepo em
`infra/identity/appsettings.Production.json`.
