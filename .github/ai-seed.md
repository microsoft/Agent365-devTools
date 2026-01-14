# AI Seed - Repository Setup Tool

> **Quick Commands:**
> - "run AI Seed" - bootstrap (auto-checks for seed updates first)
> - "re-run AI Seed" - force re-download all files
> - "re-download AI Seed" - update this seed file only

This prompt helps you bootstrap your repository with the latest AI-First configuration files.

## Instructions

**To update this seed file itself:**
Tell me "re-download AI Seed" or "update the seed file"

**To bootstrap your repository (first time or update):**
Tell me "run AI Seed" or "bootstrap the AI-First files"

**To force re-download all files (already ran before):**
Tell me "re-run AI Seed" or "force AI Seed"

---

## 🔄 Self-Update Check (ALWAYS FIRST)

**CRITICAL: Every time AI Seed runs, it MUST check for updates to itself first.**

### Self-Update Workflow

1. **Fetch upstream ai-seed.md** from `gim-home/ai-first` main branch
2. **Compare with local version** - check for differences
3. **If upstream is newer:**
   - Download the new `ai-seed.md` to `.github/ai-seed.md`
   - Notify user: "AI Seed has been updated. Re-reading instructions..."
   - **Re-read the updated file** and continue with the new instructions
4. **If no changes**, proceed with normal seed operation

This ensures you always run the latest seed logic before downloading configuration files.

---

## 📝 Modified File Handling

**For configuration files that users may have customized (e.g., `copilot-instructions.md`):**

### Check for Local Modifications

1. **Compare local file with upstream version**
2. **If local modifications detected**, ask the user:
   - "Your `[filename]` has local modifications. Would you like me to:"
     - **Overwrite** - Replace with the upstream version (your changes will be lost)
     - **Integrate** - Merge upstream changes with your local modifications

### If User Chooses Integrate

1. Show a diff or summary of what will change
2. Propose the merged content
3. Ask: "Does this integration look correct? (yes/no)"
4. Only apply if user confirms

### If No Local Modifications

Update the file silently.

### Files Subject to This Workflow

- `copilot-instructions.md` - Main instruction file
- `.github/copilot/project-config.md` - Feature toggles
- Any file in `.github/copilot/` that may have user customizations

**Note:** `ado-project-info.md` is always preserved if it exists (never overwritten).

---

## What This Does

When you run AI Seed, I will:

1. Download the following files and directories from the ai-first repository
2. Create the proper directory structure in your workspace
3. Preserve your existing `.github/copilot/ado-project-info.md` if it exists (project-specific)
4. Report what was downloaded and any issues encountered

---

## Base Configuration

**Base URL:** `https://github.com/gim-home/ai-first/tree/main/.github`

**Files and Directories to Download:**

### Individual Files

- `README.md` (index of everything in .github/)
- `copilot-instructions.md`

### Directories (all contents)

- `prompts/` (all files)
- `instructions/` (all files)
- `agents/` (all files)
- `copilot/` (all files, includes `ado-project-info.md` template - won't overwrite existing)

---

## How to Use

### First Time Setup

1. Copy this file to your repository's `.github/` directory
2. Open this file in VS Code with GitHub Copilot
3. In chat, say: "run AI Seed"
4. I'll download all the configuration files
5. **Create `.developer` file** in repo root with your NAME= and EMAIL=
   - Add `.developer` to your `.gitignore`
6. **Configure `.github/copilot/project-config.md`** - Set feature toggles (ADO, Memory Bank, GitHub Projects)
7. If ADO = ✅ ON: Update `.github/copilot/ado-project-info.md` with your project details

### Updating Configuration

1. Open this file in VS Code
2. In chat, say: "run AI Seed"
3. I'll download the latest versions of all files

### Updating This Seed File

1. Open this file in VS Code
2. In chat, say: "re-download AI Seed"
3. I'll fetch the latest version of this file from the repository

---

## Technical Details

### Download Process
When you run AI Seed, I will:

1. **🔄 SELF-UPDATE CHECK (ALWAYS FIRST)** - Fetch upstream ai-seed.md, compare, update if newer, re-read and restart
2. **Check for existing files** - Identify what's already present
3. **Create directory structure** - Ensure `.github/prompts/`, `.github/instructions/`, and `.github/copilot/` exist
4. **Use git to fetch files** - Clone or pull from the repository using your existing git credentials
5. **Copy files to workspace** - Copy the specified files and directories to your current workspace
6. **Handle user-modified files** - For `copilot-instructions.md` and other config files, check for local modifications and ask user to Overwrite or Integrate
7. **Special handling for ado-project-info.md** - Only copy if it doesn't exist (preserves your project info)
8. **Report results** - Show what was downloaded, skipped, or failed

### Git-Based Approach
This tool uses git commands to access the repository, which works with authenticated repositories. The process:

1. Creates a temporary directory
2. Performs a sparse checkout of only the `.github` directory from the main branch
3. Copies specified files to your workspace
4. Cleans up the temporary directory

This approach:

- Works with private/enterprise repositories using your existing git credentials
- Only downloads what's needed (sparse checkout)
- Faster than cloning the entire repository

### File Mapping
```
Source → Destination
------   -----------
.github/copilot-instructions.md → .github/copilot-instructions.md
.github/prompts/* → .github/prompts/*
.github/instructions/* → .github/instructions/*
.github/agents/* → .github/agents/*
.github/copilot/* → .github/copilot/* (ado-project-info.md only if new)
```

---

## Self-Update

This file can be updated from the repository using the same git-based approach. Just ask me to "re-download AI Seed" and I'll fetch the latest version from:
```
https://github.com/gim-home/ai-first/blob/main/.github/ai-seed.md
```

---

## Notes

- This seed file uses git commands to access the repository, working with your existing credentials
- Works with private, enterprise, and public repositories
- The `ado-project-info.md` file in `.github/copilot/` contains project-specific information and won't be overwritten if it exists
- All other files will be updated to the latest version from the main branch
- Directory structures are created automatically as needed
- Requires git to be installed and configured with access to the repository

---

## Version

**Last Updated:** December 3, 2025 by gregrata
**Repository:** gim-home/ai-first
**Branch:** main

---

## Ready to Run?

Just say one of these:

- "run AI Seed"
- "bootstrap AI-First files"
- "download the configuration files"
- "re-download AI Seed" (to update this file only)
