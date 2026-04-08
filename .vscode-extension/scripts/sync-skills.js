// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// sync-skills.js
// Syncs SKILL.md files from ../.claude/skills/ to three targets:
//   1. .vscode-extension/prompts/<name>.prompt.md     — bundled into VSIX, copied to workspace .github/prompts/ on activate
//   2. .github/prompts/<name>.prompt.md               — Copilot Chat prompt files for repo-based workflows
//   3. .vscode-extension/claude-skills/<name>/SKILL.md — bundled into VSIX, copied to workspace .claude/skills/ on activate
//
// Source of truth is always ../.claude/skills/<name>/SKILL.md
// Targets 1 & 2 (Copilot): excludes Claude-only skills (review-pr, review-staged)
// Target 3 (Claude Code):   includes all skills

const fs = require('fs');
const path = require('path');

const REPO_ROOT = path.join(__dirname, '..', '..');

const SOURCE_DIR = path.join(REPO_ROOT, '.claude', 'skills');

// Target 1: VS Code extension bundled prompts — copied to workspace .github/prompts/ on activate
const EXTENSION_PROMPTS_DIR = path.join(__dirname, '..', 'prompts');

// Target 2: Copilot Chat prompt files (.github/prompts/) — for repo-based workflows
const PROMPTS_DIR = path.join(REPO_ROOT, '.github', 'prompts');

// Target 3: VS Code extension bundled Claude skills — copied to workspace .claude/skills/ on activate
const EXTENSION_CLAUDE_SKILLS_DIR = path.join(__dirname, '..', 'claude-skills');

// Skills excluded from Copilot targets (targets 1 & 2) — devTools repo only
const COPILOT_EXCLUDED_SKILLS = ['review-pr', 'review-staged'];

// Copilot prompt frontmatter per skill — controls agent mode, tools, and description in the prompt picker
const PROMPT_FRONTMATTER = {
    'provision': {
        description: 'Provision Azure infrastructure for an Agent 365 agent',
        tools: ['runCommands', 'terminalLastCommand'],
    },
    'cleanup': {
        description: 'Clean up all Azure and Entra resources for an agent',
        tools: ['runCommands', 'terminalLastCommand'],
    },
    'add-observability': {
        description: 'Add Application Insights observability to an agent project',
        tools: ['runCommands', 'terminalLastCommand', 'editFiles', 'codebase'],
    },
};

function syncSkills() {
    if (!fs.existsSync(SOURCE_DIR)) {
        console.error(`Source directory not found: ${SOURCE_DIR}`);
        process.exit(1);
    }

    for (const dir of [EXTENSION_PROMPTS_DIR, PROMPTS_DIR, EXTENSION_CLAUDE_SKILLS_DIR]) {
        if (!fs.existsSync(dir)) {
            fs.mkdirSync(dir, { recursive: true });
        }
    }

    const skillDirs = fs.readdirSync(SOURCE_DIR, { withFileTypes: true })
        .filter(d => d.isDirectory())
        .map(d => d.name);

    let copilotSynced = 0;
    let claudeSynced = 0;
    let skipped = 0;

    for (const skillName of skillDirs) {
        const sourceFile = path.join(SOURCE_DIR, skillName, 'SKILL.md');
        if (!fs.existsSync(sourceFile)) {
            console.log(`  skip  ${skillName} (no SKILL.md)`);
            skipped++;
            continue;
        }

        const content = fs.readFileSync(sourceFile, 'utf8');

        // Target 3: Claude Code skill — all skills, plain copy preserving full SKILL.md content
        const claudeSkillDir = path.join(EXTENSION_CLAUDE_SKILLS_DIR, skillName);
        if (!fs.existsSync(claudeSkillDir)) {
            fs.mkdirSync(claudeSkillDir, { recursive: true });
        }
        fs.writeFileSync(path.join(claudeSkillDir, 'SKILL.md'), content, 'utf8');
        console.log(`  sync  ${skillName} -> .vscode-extension/claude-skills/${skillName}/SKILL.md`);
        claudeSynced++;

        // Targets 1 & 2: Copilot prompt files — exclude devTools-only skills
        if (COPILOT_EXCLUDED_SKILLS.includes(skillName)) {
            console.log(`  skip  ${skillName} -> Copilot targets (devTools-only)`);
            continue;
        }

        const promptContent = buildPromptFrontmatter(skillName) + stripFrontmatter(content);
        const fileName = `${skillName}.prompt.md`;

        fs.writeFileSync(path.join(EXTENSION_PROMPTS_DIR, fileName), promptContent, 'utf8');
        console.log(`  sync  ${skillName} -> .vscode-extension/prompts/${fileName}`);

        fs.writeFileSync(path.join(PROMPTS_DIR, fileName), promptContent, 'utf8');
        console.log(`  sync  ${skillName} -> .github/prompts/${fileName}`);

        copilotSynced++;
    }

    console.log(`\nDone. ${claudeSynced} Claude skill(s) synced, ${copilotSynced} Copilot prompt(s) synced, ${skipped} skipped.`);
    console.log('\nClaude Code: /provision');
    console.log('Copilot Chat: #provision.prompt.md  help me provision my agent');
}

// Build Copilot prompt frontmatter for a skill (agent mode, tools, description)
function buildPromptFrontmatter(skillName) {
    const meta = PROMPT_FRONTMATTER[skillName];
    if (!meta) return '';
    const toolsList = meta.tools.map(t => `  - ${t}`).join('\n');
    return `---\nagent: agent\ndescription: ${meta.description}\ntools:\n${toolsList}\n---\n\n`;
}

// Strip YAML frontmatter (--- ... ---) from skill content
function stripFrontmatter(content) {
    if (!content.startsWith('---')) {
        return content;
    }
    const end = content.indexOf('\r\n---', 3) !== -1
        ? content.indexOf('\r\n---', 3)
        : content.indexOf('\n---', 3);
    if (end === -1) {
        return content;
    }
    return content.slice(end).replace(/^\r?\n---\r?\n?/, '').trimStart();
}

syncSkills();
