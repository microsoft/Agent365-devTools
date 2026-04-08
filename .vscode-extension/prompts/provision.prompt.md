---
agent: agent
description: Provision Azure infrastructure for an Agent 365 agent
tools:
  - runCommands
  - terminalLastCommand
---

# Provision Resources Skill

Guided interactive provisioning of Azure infrastructure for an Agent 365 agent. Runs a safe dry-run preview first, asks for confirmation, then applies.

## Usage

```bash
/provision                          # Interactive — prompts for agent-name and mode
/provision developer                # Use agent-name "developer" (demo default)
/provision developer --aiteammate   # AI Teammate (Digital Worker) mode
```

## What this skill does

1. **Parses arguments** — reads optional agent-name and `--aiteammate` flag from the command line
2. **Prompts for missing inputs** — asks for agent-name if not provided; asks whether this is an AI Teammate deployment if `--aiteammate` was not passed (default: no)
3. **Runs dry-run** — executes `a365 setup all --agent-name <name> --dry-run` and shows the numbered setup steps
4. **Asks for confirmation** — pauses before applying any changes
5. **Applies setup** — executes `a365 setup all --agent-name <name>` and streams output
6. **Shows next steps** — surfaces what to do after provisioning based on mode

## Implementation

When this skill is invoked, follow these steps exactly:

### Step 1 — Parse arguments

Extract from ARGUMENTS (the text after `/provision`):
- First non-flag word → `agent_name`
- `--aiteammate` flag (presence) → `aiteammate=true`
- `--project-dir <path>` → `project_dir`

If `agent_name` is empty, ask the user: **"What agent name should be used? (reply with a name, or 'default' for 'developer')"**
If the answer is `default` or blank, use `developer`.

Ask the user: **"Project directory? (the folder where your agent app code resides — reply with a path, or 'default' for the current directory)"**
If the user replies `default` or leaves it blank, use the current working directory.

If `--aiteammate` was not supplied, ask the user: **"Is this an AI Teammate (Digital Worker) deployment? (reply 'yes' or 'no')"**
Default to `no` if the answer is `n` or `no`. Default to `yes` if the answer is `y` or `yes`.

### Step 2 — Dry-run

Run from `project_dir` and show full output:
```bash
cd "<project_dir>" && a365 setup all --agent-name <agent_name> --dry-run
```

After showing the output, ask: **"Proceed with the setup above? (yes/no)"**
If the user answers no or anything other than yes/y, stop and say "Setup cancelled."

### Step 3 — Apply

Run from `project_dir` and stream output:
```bash
cd "<project_dir>" && a365 setup all --agent-name <agent_name>
```

If the command fails (non-zero exit), show the error and stop. Do not continue to next steps.

### Step 4 — Next steps

After successful setup, surface the "Action Required" and any post-setup guidance **directly from the CLI output** — do not use hardcoded templates. Quote or paraphrase what the CLI actually printed.

## Demo defaults

- `agent-name` = `developer`
- `aiteammate` = `false` (non-DW path)

## Notes

- The `--aiteammate` flag is handled at the skill level only. It controls which next-steps guidance is shown. There is no corresponding `--aiteammate` flag in the `a365` CLI at this time.
- The skill runs `a365 setup all` (not subcommands individually). This covers: requirements check → infrastructure → blueprint → permissions → endpoint registration.
- If you need to skip infrastructure (blueprint + permissions only), run manually: `a365 setup all --agent-name <name> --skip-infrastructure`
- Admin consent for OAuth2 grants that require a Global Administrator is deferred by default. The output of `a365 setup all` will indicate if admin consent is still pending.

## Requirements

- `a365` CLI installed and on PATH (`a365 --version` to verify)
- Azure CLI authenticated (`az login` if not already)
- Active Azure subscription selected (`az account show`)
