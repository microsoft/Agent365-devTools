# `a365 network gsa`

Turns **Global Secure Access** on or off for the tenant's Agent 365 Power Platform environment,
without needing the id of that environment.

## Why this command exists

Global Secure Access is a per-environment Power Platform setting. Agent 365 provisions a managed
environment for the tenant and does not publish its id, so the setting cannot be reached through
the Power Platform admin surfaces that take an environment id. These subcommands ask the Agent 365
platform to apply the change against the environment it resolves for your tenant.

## Prerequisites

- **Global Administrator** or **Power Platform Administrator** in the tenant. The platform rejects
  anyone else.
- An `az login` to the tenant you intend to configure.
- Public cloud only. Sovereign clouds are not supported.

Nothing is read from Azure — unlike `a365 network vnet` — but the `az login` still matters: it is
what selects the tenant. The commands authenticate against the tenant and account of your current
`az account show`, so `az login --tenant <id>` is how you choose which tenant to configure when you
have more than one. Without an explicit tenant the Windows broker silently returns whichever
account Windows prefers, which would apply a tenant-wide setting to the wrong tenant. If the
account you are signed into cannot be matched, the command fails rather than falling back.

## Subcommands

| Command | Description |
| --- | --- |
| `a365 network gsa enable` | Turn Global Secure Access on. |
| `a365 network gsa disable` | Turn Global Secure Access off. |
| `a365 network gsa status` | Show whether Global Secure Access is on. |

### `enable` and `disable`

```bash
a365 network gsa enable [--wait] [--yes]
a365 network gsa disable [--wait] [--yes]
```

| Option | Description |
| --- | --- |
| `--wait` | Keep polling until the change appears on the environment, instead of returning while it is still being applied. |
| `--yes`, `-y` | Skip the confirmation prompt. |

Both verbs prompt before changing the tenant-wide setting; pass `--yes` in automation.
Requesting the value the environment already holds is a no-op and succeeds.

### `status`

```bash
a365 network gsa status
```

There is no operation handle to pass. Power Platform applies the change asynchronously but issues
no operation id for it, so the CLI reports progress by re-reading the setting rather than by
polling a handle.

## Statuses and exit codes

| Status | Meaning |
| --- | --- |
| `Enabled` | Global Secure Access is on. |
| `Disabled` | Global Secure Access is off. |
| `NotConfigured` | The tenant has never set the value. This is **not** the same as `Disabled`. |

A change that has been accepted but has not yet surfaced is reported as still being applied, with
the status still showing the value it has not yet displaced.

Exit code is `1` on any request error, and `0` otherwise — including a change that is still being
applied, which is a legitimate outcome when `--wait` is not passed.

## Typical flow

```bash
a365 network gsa enable --wait
a365 network gsa status
```

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `403` from the platform | Caller is not a Global or Power Platform Administrator, or the CLI app lacks consent for the `AgentTools.Gsa.*` scopes. |
| `409`, reporting a governing policy | A Power Platform policy owns this setting. Change it through that policy; the environment-level value is ignored while the policy applies. |
| `404`, reporting no environment | The tenant has no Agent 365 environment yet. |
| Status stays `NotConfigured` after `disable` | Read it again — the change is applied asynchronously and `--wait` is the way to block on it. |
| `Could not determine your Azure tenant` | No usable `az login`. Run `az login --tenant <id>` for the tenant you want to configure. |
| Sign-in prompt names the wrong account | The tenant comes from `az account show`. Run `az account set` / `az login --tenant <id>` to point at the intended tenant, then retry. |
