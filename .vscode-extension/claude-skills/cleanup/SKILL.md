---
name: cleanup
description: Clean up all Azure and Entra resources for a non-DW Agent 365 agent by name. Runs a365 cleanup --agent-name from the project directory. Useful for testing teardown.
allowed-tools: Bash(a365:*), Bash(cd:*)
---

# Cleanup Skill

Guided cleanup of all resources provisioned for a non-DW Agent 365 agent. Identifies resources from the project directory, shows a preview, then deletes on confirmation.

## Usage

```bash
/cleanup                                          # Interactive — prompts for agent-name and directory
/cleanup sellakautonomousdemodeveloperapril65     # Use specific agent-name
/cleanup developer --project-dir C:\Samples\MyAgent
```

## What this skill does

1. **Parses arguments** — reads optional agent-name and `--project-dir` from the command line
2. **Prompts for missing inputs** — asks for agent-name and project directory if not provided
3. **Runs cleanup** — executes `a365 cleanup --agent-name <name>` from the project directory
4. **The CLI handles the rest** — shows a preview of resources to delete, asks for confirmation, then deletes

## Implementation

When this skill is invoked, follow these steps exactly:

### Step 1 — Parse arguments

Extract from ARGUMENTS (the text after `/cleanup`):
- First non-flag word → `agent_name`
- `--project-dir <path>` → `project_dir`

If `agent_name` is empty, ask the user: **"What is the agent name to clean up?"**
Do not proceed without an agent name.

If `project_dir` is not already known from context (e.g., from a previous `/provision` in the same conversation), ask the user:
**"Project directory (where a365.generated.config.json lives)? Reply with a path, or 'default' to use the current directory."**
If the user replies `default` or leaves it blank, use the current working directory.
If `project_dir` is already known from context, skip this question and use it directly.

### Step 2 — Run cleanup

Run from `project_dir`:
```bash
cd "<project_dir>" && a365 cleanup --agent-name <agent_name> --yes
```

The CLI will:
- Detect the tenant from `az account show`
- Resolve the blueprint ID from Entra by agent name
- Load the agent registration ID from `a365.generated.config.json` (if blueprint IDs match)
- Show a preview of all resources to be deleted
- Ask for `y/N` confirmation and then `DELETE` confirmation
- Delete all resources and back up + delete the generated config file

### Step 3 — Report outcome

After the command completes:
- If successful: confirm which resources were deleted and that the generated config was backed up
- If failed: show the error and suggest next steps (re-run, or delete manually via Entra portal)

## Notes

- The `--agent-name` value should match what was used during `/provision` — it's used to look up the blueprint app by display name in Entra
- The project directory must contain `a365.generated.config.json` for the agent registration ID to be found
- All resources are shown in a preview before any deletion occurs — the user must type `DELETE` to confirm
- The generated config is backed up as `a365.generated.config.backup-<timestamp>.json` before deletion

## Requirements

- `a365` CLI installed and on PATH
- Azure CLI authenticated (`az login`)
- Active subscription selected (`az account show`)
- `a365.generated.config.json` in the project directory (written by `/provision`)
