// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';

export function activate(context: vscode.ExtensionContext): void {
    syncToWorkspace(context);
}

export function deactivate(): void {
    // nothing to clean up
}

// Copies bundled Agent 365 prompt and skill files into each open workspace:
//   .github/prompts/<name>.prompt.md  — for GitHub Copilot Chat (agent mode)
//   .claude/skills/<name>/SKILL.md    — for Claude Code (/provision, /cleanup, etc.)
// Only writes a file if it is missing or the content has changed.
function syncToWorkspace(context: vscode.ExtensionContext): void {
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (!workspaceFolders || workspaceFolders.length === 0) {
        return;
    }

    const bundledPromptsDir = path.join(context.extensionPath, 'prompts');
    const bundledClaudeSkillsDir = path.join(context.extensionPath, 'claude-skills');

    let synced = 0;

    for (const folder of workspaceFolders) {
        synced += copyPromptFiles(bundledPromptsDir, folder.uri.fsPath);
        synced += copyClaudeSkills(bundledClaudeSkillsDir, folder.uri.fsPath);
    }

    if (synced > 0) {
        vscode.window.showInformationMessage(
            `Agent 365: ${synced} file(s) added — use #provision.prompt.md in Copilot Chat or /provision in Claude Code`
        );
    }
}

function copyPromptFiles(bundledPromptsDir: string, workspacePath: string): number {
    if (!fs.existsSync(bundledPromptsDir)) {
        return 0;
    }

    const promptFiles = fs.readdirSync(bundledPromptsDir).filter(f => f.endsWith('.prompt.md'));
    if (promptFiles.length === 0) {
        return 0;
    }

    const targetDir = path.join(workspacePath, '.github', 'prompts');
    ensureDir(targetDir);

    let synced = 0;
    for (const file of promptFiles) {
        if (copyIfChanged(path.join(bundledPromptsDir, file), path.join(targetDir, file))) {
            synced++;
        }
    }
    return synced;
}

function copyClaudeSkills(bundledClaudeSkillsDir: string, workspacePath: string): number {
    if (!fs.existsSync(bundledClaudeSkillsDir)) {
        return 0;
    }

    const skillDirs = fs.readdirSync(bundledClaudeSkillsDir, { withFileTypes: true })
        .filter(d => d.isDirectory())
        .map(d => d.name);

    if (skillDirs.length === 0) {
        return 0;
    }

    let synced = 0;
    for (const skillName of skillDirs) {
        const src = path.join(bundledClaudeSkillsDir, skillName, 'SKILL.md');
        if (!fs.existsSync(src)) {
            continue;
        }

        const targetDir = path.join(workspacePath, '.claude', 'skills', skillName);
        ensureDir(targetDir);

        if (copyIfChanged(src, path.join(targetDir, 'SKILL.md'))) {
            synced++;
        }
    }
    return synced;
}

function copyIfChanged(src: string, dest: string): boolean {
    const srcContent = fs.readFileSync(src, 'utf8');
    if (!fs.existsSync(dest) || fs.readFileSync(dest, 'utf8') !== srcContent) {
        fs.writeFileSync(dest, srcContent, 'utf8');
        return true;
    }
    return false;
}

function ensureDir(dir: string): void {
    if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
    }
}
