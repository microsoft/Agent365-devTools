# Agent 365 — VS Code Extension

GitHub Copilot Chat participant for Microsoft Agent 365 developers.

## Usage

```
@agent365 /provision            Provision Azure infrastructure for an agent
@agent365 /cleanup              Clean up all Azure and Entra resources
@agent365 /add-observability    Add Application Insights to an agent project
```

## Skill Sync

Skill content lives in `../.claude/skills/` — the same source used by Claude Code.
The VS Code extension reads from `skills/*.md` which are copied at build time.

To update skills after editing `.claude/skills/<name>/SKILL.md`:
```bash
npm run sync-skills
```

Skills excluded from the VS Code extension (Claude Code only):
- `review-pr`
- `review-staged`

## Development

```bash
cd .vscode-extension

# Install dependencies
npm install

# Sync skills from .claude/skills/
npm run sync-skills

# Compile TypeScript
npm run compile

# Press F5 in VS Code to launch extension host for testing
```

## Packaging & Publishing

```bash
# Build VSIX
npm run package

# Publish to VS Marketplace (requires MARKETPLACE_PAT env var)
npx vsce publish --pat $env:MARKETPLACE_PAT
```

The GitHub Actions workflow `.github/workflows/publish-vscode-extension.yml` automates
publishing on tags matching `vscode-v*`.
