# imoney-api

Backend do iMoney: recebe as transações do agregador Open Finance, normaliza,
deduplica, persiste e expõe a API que o app consome.

Este repositório é independente. Ele é desenvolvido a partir do workspace
`imoney-workspace`, que guarda as specs e clona este repo em `repos/api`.

## Estado

Ainda **sem código**. O esqueleto existe para que o workspace tenha o que clonar
e para que as guardas de commit estejam ativas desde o primeiro commit. O código
começa na Phase 1 do roadmap.

## Stack decidida

Decidida nos ADRs do workspace, não aqui.

| Item | Escolha |
| --- | --- |
| Runtime | .NET 10 LTS |
| Estrutura | Minimal APIs + vertical slices, projeto único |
| Persistência | PostgreSQL (Neon Free) via EF Core 10 |
| Contrato | OpenAPI 3.1 gerado no build, commitado em `contracts/openapi.json` |
| Testes | xUnit v3, Testcontainers PostgreSQL, WireMock.Net para o agregador |
| Hospedagem | Azure Container Apps, com o sync como ACA Job |

## Convenções

- Commits em **Conventional Commits**, validados pelo hook `commit-msg`.
- Todo commit feito dentro de uma feature leva o trailer `Spec: <NNN-slug>`,
  apontando para `.specs/features/<NNN-slug>/` no workspace.
- Segredos nunca entram no repo. O hook `pre-commit` roda `gitleaks` e **recusa o
  commit se o gitleaks não estiver instalado**, em vez de deixar passar.
- Nada de dado financeiro real em fixture, teste, seed ou log. Dados de teste são
  sintéticos.

## Hooks

Instalados por `core.hooksPath` apontando para `.githooks/`, o que o
`ws.mjs sync` configura ao clonar. Se você clonar este repo fora do workspace, o
hook `commit-msg` não encontra o validador e recusa o commit por segurança.
