# Hosted Triage Agent

A demo showing how to build a GitHub issue triage agent as a **Microsoft Foundry
Hosted Agent**, with GitHub Issues as the chat UX. The point of the demo is to
showcase Foundry Hosted Agents as a superior hosting option vs. self-hosting an
agent on Azure App Service or Container Apps.

> **Status**: M1 — echo skeleton. The agent echoes the issue body back. LLM,
> MCP, and multi-turn come in later milestones.

## Architecture

```
GitHub Issue ──► GitHub Action (OIDC + curl) ──► .NET Hosted Agent in Foundry
     ▲                                                       │
     └────── reply posted as issue comment ◄─────────────────┘
```

- **Agent**: `src/triage-agent/` — .NET 10, `Microsoft.Agents.AI.Foundry.Hosting`,
  implements the Invocations protocol.
- **Infra**: `infra/` — Bicep deployed via `azd` (Foundry account + project +
  ACR + App Insights).
- **Trigger**: `.github/workflows/triage.yml` — runs on `issues.opened`,
  authenticates to Azure via OIDC, calls the agent, posts the reply.

## Quickstart

Prereqs: `azd` ≥ 1.25, `dotnet` ≥ 10, `az`, `gh`, Azure subscription, GitHub repo.

```bash
azd auth login
az login

# Scaffold + provision (already done for this repo)
azd provision    # ~4 min: Foundry account, project, ACR, App Insights
azd deploy       # ~3 min: build + push container, register agent

# Required header on every invocation while Hosted Agents are preview:
#   Foundry-Features: HostedAgents=V1Preview
```

Open an issue in this repo to trigger the agent.

## Why Hosted Agents (not App Service)

This README will grow side-by-side comparisons through the milestones. M1 alone
already demonstrates:

- **Zero infra config**: `azd up` provisions the whole runtime; we never touched
  an App Service plan, autoscale rule, or Dockerfile build pipeline.
- **Auto-assigned managed identity**: the agent has its own Entra principal
  (`instance_identity.principal_id`) created by the platform, no manual MI
  provisioning.
- **Scale-to-zero by default**: no continuous compute bill while idle.

## Milestones

See `plan.md` (in the session workspace) for the full plan. Current: **M1**.
