# GitHub Auto-Triage Action

This directory contains an AI-powered GitHub Action that automatically triages new issues using Azure OpenAI or GitHub Models.

## Features

- **Automatic Classification**: Classifies issues as bug, feature, enhancement, documentation, or question
- **Priority Assessment**: Assigns P0-P4 priority labels based on issue content
- **Smart Assignment**: Recommends team members based on expertise matching
- **Confidence Scoring**: Provides confidence level for all classifications
- **Copilot Detection**: Identifies issues that could be fixed by GitHub Copilot

## Setup

### 1. Configure GitHub Secrets

Add these secrets to your repository (Settings > Secrets and variables > Actions):

**For Azure OpenAI (Recommended):**
- `AZURE_OPENAI_API_KEY` - Your Azure OpenAI API key
- `AZURE_OPENAI_ENDPOINT` - Your Azure OpenAI endpoint (e.g., `https://your-resource.openai.azure.com`)
- `AZURE_OPENAI_DEPLOYMENT` - Your deployment name (e.g., `gpt-4o`)
- `AZURE_OPENAI_API_VERSION` - API version (e.g., `2024-02-01`)

**For GitHub Models (Alternative):**
- `MODELS_API_KEY` - Your GitHub Models API key

The `GITHUB_TOKEN` is automatically provided by GitHub Actions.

### 2. Configure Team Members

Edit `autoTriage/config/team-members.json` to add your team:

```json
{
  "team_members": [
    {
      "name": "Your Name",
      "role": "Tech Lead",
      "login": "github_username",
      "contributions": 50,
      "expertise": ["architecture", "backend", "API design"]
    }
  ]
}
```

**Important**: The `login` field must match GitHub usernames that are repository collaborators.

### 3. Create Required Labels

Create these labels in your repository (Issues > Labels):

**Priority Labels:**
- `P0` (Critical) - Red
- `P1` (High) - Orange
- `P2` (Medium) - Yellow
- `P3` (Low) - Green
- `P4` (Nice-to-have) - Blue

**Type Labels:**
- `bug` - Red
- `feature` - Purple
- `enhancement` - Blue
- `documentation` - Light blue
- `question` - Pink

### 4. Deploy the Workflow

The workflow at `.github/workflows/auto-triage-issues.yml` will automatically trigger when new issues are created.

### 5. Automatic Workload Updates (Optional)

The `contributions` field in team-members.json represents current workload and should be updated periodically. We provide an automated solution:

**Automatic Updates via GitHub Actions:**

A workflow runs every Monday at 9am UTC to update team workload based on open issues:
- Counts open issues assigned to each team member
- Calculates contribution scores (higher = busier)
- Commits updated team-members.json automatically

The workflow is at `.github/workflows/update-team-workload.yml` and can also be triggered manually from the Actions tab.

**Manual Updates:**

You can also run the update script locally:

```bash
cd autoTriage
export GITHUB_TOKEN="your_token"

# Dry run to preview changes
python scripts/update_contributions.py --dry-run

# Apply changes
python scripts/update_contributions.py
```

**How Contribution Scores Work:**

- **Lower score** = Less busy = More likely to be assigned new issues
- **Higher score** = Busier = Less likely to be assigned new issues
- **Formula**: Base score (5) + (2 × open issues) + (1 × open PRs)
- **Range**: 5 (completely free) to 50 (very busy)

This ensures workload is balanced automatically as team members complete or take on work.

## File Structure

```
autoTriage/
├── triage_issue.py           # Main CLI script
├── requirements.txt          # Python dependencies
├── README.md                 # This file
├── scripts/                  # Utility scripts
│   ├── update_contributions.py  # Auto-update team workload
│   └── __init__.py
├── services/                 # Core services
│   ├── intake_service.py     # Triage logic
│   ├── github_service.py     # GitHub API wrapper
│   ├── llm_service.py        # AI integration
│   ├── config_parser.py      # Config loading
│   ├── prompt_loader.py      # Prompt management
│   └── teams_service.py      # Teams notifications (optional)
├── models/                   # Data models
│   ├── issue_classification.py
│   ├── team_config.py
│   └── ado_models.py
└── config/                   # Configuration files
    ├── team-members.json     # Team roster
    └── prompts.yaml          # AI prompts

.github/
└── workflows/
    ├── auto-triage-issues.yml    # Auto-triage on new issues
    └── update-team-workload.yml  # Weekly workload updates
```

## How It Works

1. **Trigger**: When a new issue is created, the GitHub Action is triggered
2. **Analysis**: The triage script fetches the issue and sends it to Azure OpenAI/GitHub Models
3. **Classification**: AI analyzes the issue content and recommends:
   - Issue type (bug, feature, etc.)
   - Priority level (P0-P4)
   - Best team member to assign
   - Whether it's Copilot-fixable
4. **Application**: The action posts results as a comment and applies labels and assignee

## Local Testing

To test triage locally:

```bash
# Set up environment
cd autoTriage
pip install -r requirements.txt

# Set environment variables
export GITHUB_TOKEN="your_github_token"
export AZURE_OPENAI_API_KEY="your_key"
export AZURE_OPENAI_ENDPOINT="your_endpoint"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o"
export AZURE_OPENAI_API_VERSION="2024-02-01"

# Run triage
python triage_issue.py \
  --owner microsoft \
  --repo Agent365-devTools \
  --issue-number 123
```

## Troubleshooting

**Labels not applied:**
- Verify labels exist in repository with exact names (case-sensitive)
- Check workflow logs for error messages

**Assignee not set:**
- Ensure team member's GitHub username matches their `login` in team-members.json
- Verify they are repository collaborators (Settings > Collaborators)

**AI not responding:**
- Check that Azure OpenAI secrets are correctly configured
- Verify API key has permissions and quota available
- Review workflow logs for API error messages

**Workflow not triggering:**
- Ensure workflow file is on the default branch (main/master)
- Check repository Actions settings allow workflows to run

## Customization

### Adjust AI Prompts

Edit `config/prompts.yaml` to customize how the AI analyzes issues.

### Modify Priority Rules

Edit `services/config_parser.py` to adjust default priority keywords and rules.

### Change Label Mapping

Edit the workflow's "Post triage results" step to customize which labels are applied.

## Dependencies

- Python 3.11+
- PyGithub
- Azure OpenAI / OpenAI SDK
- PyYAML

See `requirements.txt` for complete list.

## License

Part of the Agent365-devTools project.
