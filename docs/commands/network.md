# `a365 network vnet`

Links an Azure virtual network to Agent 365 via a Power Platform **NetworkInjection enterprise
policy**, without needing the id of the Power Platform environment.

## Why this command exists

The documented subnet-injection flow
([Set up virtual network support](https://learn.microsoft.com/power-platform/admin/vnet-support-setup-configure))
ends with `Enable-SubnetInjection` from the `Microsoft.PowerPlatform.EnterprisePolicies` module,
which takes an `-environmentId`. Agent 365 provisions a managed Power Platform environment for the
tenant and does not publish its id, so that final step cannot be run.

`a365 network vnet` replaces only that last step. The CLI reads the policy's `systemId` from Azure
using your existing `az login`, then asks the Agent 365 platform to perform the link against the
environment it resolves for your tenant.

Everything before the final step is unchanged — keep using the PowerShell module to create the
subnets, delegate them to `Microsoft.PowerPlatform/enterprisePolicies`, and create the policy with
`New-SubnetInjectionEnterprisePolicy`.

## Prerequisites

- **Global Administrator** or **Power Platform Administrator** in the tenant. The platform rejects
  anyone else.
- An active `az login` session in the same tenant. Used only to read the enterprise policy.
- A NetworkInjection enterprise policy already created by `New-SubnetInjectionEnterprisePolicy`,
  with subnets delegated to `Microsoft.PowerPlatform/enterprisePolicies`.
- Public cloud only. Sovereign clouds are not supported.

## Subcommands

| Command | Description |
| --- | --- |
| `a365 network vnet link` | Link a NetworkInjection enterprise policy to the tenant's Agent 365 environment. |
| `a365 network vnet unlink` | Remove the virtual network link. |
| `a365 network vnet status` | Show the current link, or check a running operation. |

### `link`

```bash
a365 network vnet link --policy-arm-id <arm-id> [--swap] [--tenant-id <guid>] [--wait]
```

| Option | Description |
| --- | --- |
| `--policy-arm-id`, `-p` | **Required.** ARM resource id of the policy, as returned by `New-SubnetInjectionEnterprisePolicy`. |
| `--swap` | Replace an existing link to a *different* policy. Without it, a different existing link is reported as a conflict instead of being silently replaced. |
| `--tenant-id` | Tenant to authenticate against for the Azure policy read. Defaults to the tenant of your current `az login`. |
| `--wait` | Poll until the operation settles instead of returning an operation id. |

Linking the policy that is already linked is a no-op and succeeds without `--swap`.

### `unlink`

```bash
a365 network vnet unlink [--wait]
```

Unlink needs no policy id — the platform remembers which policy it linked.

### `status`

```bash
a365 network vnet status [--operation-id <id>]
```

Without `--operation-id`, reports the environment's current link. With one, reports that specific
operation.

## Statuses and exit codes

| Status | Meaning |
| --- | --- |
| `Linked` | A policy is linked; `Policy` names it. |
| `NotLinked` | No policy is linked. |
| `Running` / `NotStarted` | The operation is still in flight; `Operation` is the handle to poll. |
| `Failed` | The operation failed; `Reason` explains why. |

Exit code is `1` on `Failed` or on any request error, and `0` otherwise — including a still-running
operation, which is a legitimate outcome when `--wait` is not passed.

## Typical flow

```bash
# 1. Create the policy with the PowerShell module (unchanged).
./SubnetInjection/NewSubnetInjectionEnterprisePolicy.ps1 `
    -subscription <sub> -resourceGroup <rg> -enterprisePolicyName <name> `
    -enterprisePolicyLocation <region> -virtualNetworkId <vnetId> -subnetName <subnet>

# 2. Link it — this replaces Enable-SubnetInjection.
a365 network vnet link --policy-arm-id /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.PowerPlatform/enterprisePolicies/<name> --wait

# 3. Confirm.
a365 network vnet status
```

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `Could not determine your Azure tenant` | No `az login` session. Run `az login`, or pass `--tenant-id`. |
| `403` from the platform | Caller is not a Global or Power Platform Administrator, or the CLI app lacks consent for the `AgentTools.VNet.*` scopes. |
| Conflict reported on `link` | A *different* policy is already linked. Re-run with `--swap`, or `unlink` first. |
| Policy read fails | The policy ARM id is wrong, or your `az login` identity cannot read it. |
