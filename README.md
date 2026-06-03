# Hosted Triage Agent

A demo of a GitHub issue triage agent built as a **Microsoft Foundry Hosted
Agent**, with **GitHub Issues as the chat UX**. The point of the demo is to
showcase Foundry Hosted Agents as a stronger hosting option than rolling your
own on Azure App Service or Container Apps.

> **Status**: M5 complete — end-to-end working. Open an issue, the agent
> posts a structured triage comment as its own `wdhm-triage-agent[bot]`
> identity and applies category/severity/routing labels. Multi-turn follow-ups
> work via `@wdhm-triage-agent <question>` comments.

## Architecture

```
┌────────────────┐    issue.opened /     ┌──────────────────────┐
│ GitHub Issue   │  issue_comment.created│ GitHub Actions       │
│ + comments     │ ────────────────────► │  triage.yml          │
└────────────────┘                        │  • OIDC to Azure     │
        ▲                                 │  • Mint App token    │
        │ comment + labels                │  • curl invocation   │
        │ as wdhm-triage-agent[bot]       └─────────┬────────────┘
        │                                           │ POST /invocations
        │                                           ▼
        │                              ┌──────────────────────────┐
        │                              │ .NET 10 Hosted Agent      │
        │                              │ on Microsoft Foundry      │
        │ direct REST                  │                           │
        │ (api.github.com)             │  TriageInvocationHandler  │
        └──────────────────────────────│  ↓                        │
                                       │  ChatClientAgent          │
                                       │  ↓ tools                  │
                                       │  GitHubRestTools          │
                                       │  (add_issue_comment,      │
                                       │   set_issue_labels)       │
                                       └──────────────────────────┘
```

- **Agent**: `src/triage-agent/` — .NET 10, `Microsoft.Agents.AI.Foundry.Hosting`,
  Invocations protocol.
- **Infra**: `infra/` — Bicep deployed via `azd` (Foundry account + project +
  ACR + App Insights). No App Service plan, no container app, no autoscale
  config.
- **Trigger**: `.github/workflows/triage.yml` — fires on `issues.opened` and
  `@wdhm-triage-agent`-mentioning `issue_comment.created`, OIDCs into Azure,
  mints a GitHub App installation token, posts to the agent.

## Why Foundry Hosted Agents (vs. App Service / Container Apps)

| Concern | Foundry Hosted Agent | DIY on App Service / Container Apps |
|---|---|---|
| **Provisioning** | `azd up` — Foundry account + project + ACR + App Insights + role assignments wired automatically | You manage plan, slot, autoscale, MI assignment, ACR, App Insights, Key Vault, role assignments yourself |
| **Identity** | Each agent version gets its own platform-assigned managed identity automatically | Manual MI provisioning + RBAC |
| **Versioning** | `azd deploy` = new agent version; old version drains gracefully; `azd ai agent show` lists versions | Slot swaps, blue/green wiring, or roll-your-own |
| **Sessions / memory** | Foundry's thread model is the durable conversation store; `agent.RunAsync(message, session)` Just Works across container restarts | You wire Redis/Cosmos/SQL yourself |
| **Scale-to-zero** | Default — no idle compute bill | Have to configure autoscale rules; cold-start handling is on you |
| **Local↔cloud parity** | `azd ai agent run` runs the exact same container locally on `http://localhost:8088/invocations` | Docker-compose / Aspire / etc. — separate config |
| **Tool-call observability** | App Insights wired by the platform; per-invocation trace IDs returned in response headers | App Insights manual setup |

## Highlighted technical details

A few non-obvious things this demo had to solve along the way (each worth a
line in a customer conversation):

1. **GitHub App identity through Foundry's invocation gateway.** The gateway
   strips all non-allowlisted request headers, so the per-request App
   installation token cannot ride on `X-GitHub-Token` — it has to go in the
   JSON body and be pushed onto an `AsyncLocal<string?>` inside the handler,
   so the outbound REST calls pick it up correctly.

2. **Why not the GitHub MCP server for writes.** We tried. The hosted MCP at
   `api.githubcopilot.com/mcp/` rejects the agent's session-id intermittently
   (`400 invalid session` — the HTTP session handshake doesn't survive
   Foundry's process recycling), AND the default endpoint's `context` toolset
   forces the LLM to call `get_me` which hits `GET /user` which returns 403
   for GitHub App installation tokens. We dropped MCP for writes and call
   `api.github.com` directly via lightweight `AIFunction`-wrapped methods —
   bot identity is preserved (the `ghs_` installation token attributes
   comments to `<app>[bot]` automatically).

3. **Idempotency without poisoning.** Using the session file as both
   conversation memory AND idempotency marker had a subtle failure mode: a
   failed run that still wrote the file would short-circuit every subsequent
   retry with a fake "already triaged". Fixed by gating `SaveAsync` on actual
   success (verified via post-run AsyncLocal counters tracking tool-call
   outcomes, not the LLM's self-report).

4. **Per-issue concurrency.** Workflow concurrency group is
   `triage-${repo_id}-${issue_number}` with `cancel-in-progress: false`, so
   rapid follow-up comments queue instead of racing on the same Foundry
   thread.

## Quickstart (from scratch)

Prereqs: `azd` ≥ 1.25, `dotnet` ≥ 10, `az`, `gh`, an Azure subscription, a
GitHub repo.

```bash
azd auth login
az login

# Provision Foundry account, project, ACR, App Insights (≈ 4 min)
azd provision

# Build container, push to ACR, register agent version (≈ 3 min)
azd deploy
```

### GitHub side

1. Create a GitHub App owned by the repo's account with:
   - Repository permissions: **Issues: Read & write**, **Metadata: Read**
   - Install it on the repo
2. Add these repo secrets:
   - `TRIAGE_AGENT_APP_ID` — the App ID
   - `TRIAGE_AGENT_APP_PRIVATE_KEY` — the App's private key (PEM)
   - `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` — the
     OIDC service principal that can invoke the Foundry agent
3. Add this repo variable:
   - `FOUNDRY_INVOCATIONS_URL` — the agent endpoint output by `azd deploy`
4. Open an issue. Within ~25 s the bot posts the triage comment.

## Triggers

| Event | Workflow filter | What the agent does |
|---|---|---|
| Issue opened | always | Full triage: categorize, severity, label, post comment |
| Issue comment containing `@wdhm-triage-agent` | (and not from a `[bot]`, not containing the hidden reply marker) | Follow-up reply with conversation memory |

> Note: GitHub does NOT autocomplete `[bot]` identities in the @-mention picker
> — type `@wdhm-triage-agent` literally. The workflow's `contains()` filter
> matches the text regardless of whether GitHub renders it as a hyperlink.

## Layout

```
.
├── .github/workflows/triage.yml      # Trigger + invocation
├── azure.yaml                         # azd config
├── infra/                             # Bicep (Foundry, ACR, App Insights)
└── src/triage-agent/
    ├── Program.cs                     # Startup, DI, agent + tool wiring
    ├── TriageInvocationHandler.cs     # Per-invocation routing + system prompts
    ├── GitHubRestTools.cs             # add_issue_comment, set_issue_labels (REST)
    ├── GitHubTokenProvider.cs         # Per-request AsyncLocal token
    ├── FoundrySessionStore.cs         # Session-file persistence + quarantine
    ├── agent.yaml                     # Hosted agent manifest
    └── triage-agent.csproj
```
