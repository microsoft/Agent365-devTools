# a365 setup permissions copilotstudio Command

## Overview

The `a365 setup permissions copilotstudio` command configures OAuth2 permission grants and inheritable permissions for the agent blueprint to invoke Copilot Studio copilots via the Power Platform API.

## Usage

```bash
a365 setup permissions copilotstudio [options]
```

## Options

| Option | Alias | Description | Default |
|--------|-------|-------------|---------|
| `--config` | `-c` | Configuration file path | `a365.config.json` |
| `--verbose` | `-v` | Show detailed output | `false` |
| `--dry-run` | | Show what would be done without making changes | `false` |

## Prerequisites

1. **Blueprint Created**: Run `a365 setup blueprint` first
2. **Global Administrator**: Required for admin consent operations
3. **Azure CLI Authentication**: `az login` with appropriate permissions

## What This Command Does

1. **Ensures Power Platform API Service Principal** exists in your tenant
2. **Creates OAuth2 Permission Grant** from blueprint to Power Platform API
3. **Sets Inheritable Permissions** so agent instances can invoke Copilot Studio

## Permission Configured

| Resource | Scope | Type |
|----------|-------|------|
| Power Platform API (`8578e004-a5c6-46e7-913e-12f58912df43`) | `CopilotStudio.Copilots.Invoke` | Delegated |

## Examples

### Configure CopilotStudio permissions
```bash
a365 setup permissions copilotstudio
```

### Preview changes without executing
```bash
a365 setup permissions copilotstudio --dry-run
```

### Use custom configuration file
```bash
a365 setup permissions copilotstudio --config my-agent.config.json
```

### Show detailed output
```bash
a365 setup permissions copilotstudio --verbose
```

## When to Use This Command

Use this command when your agent needs to:
- Invoke Copilot Studio copilots at runtime
- Call Power Platform APIs that require CopilotStudio permissions

## Related Commands

- `a365 setup blueprint` - Create the agent blueprint (prerequisite)
- `a365 setup permissions mcp` - Configure MCP server permissions
- `a365 setup permissions bot` - Configure Messaging Bot API permissions

## Common Issues

### "Blueprint ID not found" Error
```
ERROR: Blueprint ID not found. Run 'a365 setup blueprint' first.
```
**Solution**: Run `a365 setup blueprint` to create the agent blueprint first.

### Permission Grant Failures
If the command fails during permission grant creation, verify:
- You have Global Administrator permissions
- You're authenticated with `az login`
- The Power Platform API service principal exists in your tenant

## Output Examples

### Successful Execution
```
Configuring CopilotStudio permissions...

✓ Power Platform API service principal found
✓ OAuth2 permission grant created: CopilotStudio.Copilots.Invoke
✓ Inheritable permissions configured

CopilotStudio permissions configured successfully

Your agent blueprint can now invoke Copilot Studio copilots.
```

### Dry Run Output
```
DRY RUN: Configure CopilotStudio Permissions
Would configure Power Platform API permissions:
  - Blueprint: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
  - Resource: Power Platform API (8578e004-a5c6-46e7-913e-12f58912df43)
  - Scopes: CopilotStudio.Copilots.Invoke
```