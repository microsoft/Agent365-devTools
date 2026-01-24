#!/usr/bin/env python3
"""
Script to automatically update team member contributions based on current workload.

This script:
1. Fetches current open issues assigned to each team member
2. Calculates contributions score based on workload
3. Updates team-members.json with new scores

Run this script periodically (e.g., weekly via cron or GitHub Actions)
"""
import json
import os
import sys
from pathlib import Path
from typing import Dict

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from services.github_service import GitHubService


def calculate_contribution_score(open_issues: int, open_prs: int = 0) -> int:
    """
    Calculate contribution score based on workload.

    Higher score = More busy = Less available for new assignments
    Lower score = Less busy = More available for new assignments

    Args:
        open_issues: Number of open issues assigned
        open_prs: Number of open PRs (optional)

    Returns:
        Contribution score (5-50)
    """
    # Base score of 5 (minimum)
    base_score = 5

    # Add 2 points per open issue
    issue_score = open_issues * 2

    # Add 1 point per open PR (PRs are usually shorter-lived than issues)
    pr_score = open_prs * 1

    total = base_score + issue_score + pr_score

    # Cap at 50 (very busy)
    return min(total, 50)


def get_team_member_workload(
    github_service: GitHubService,
    owner: str,
    repo: str,
    username: str
) -> Dict[str, int]:
    """
    Get current workload for a team member.

    Returns:
        Dict with 'open_issues' and 'open_prs' counts
    """
    try:
        # Get open issues assigned to this user
        issues = github_service.client.search_issues(
            f"repo:{owner}/{repo} is:open is:issue assignee:{username}"
        )
        open_issues = issues.totalCount

        # Get open PRs created by this user (optional metric)
        prs = github_service.client.search_issues(
            f"repo:{owner}/{repo} is:open is:pr author:{username}"
        )
        open_prs = prs.totalCount

        return {
            "open_issues": open_issues,
            "open_prs": open_prs
        }
    except Exception as e:
        print(f"Warning: Could not fetch workload for {username}: {e}")
        return {
            "open_issues": 0,
            "open_prs": 0
        }


def update_team_contributions(owner: str, repo: str, dry_run: bool = False):
    """
    Update contributions for all team members based on current workload.

    Args:
        owner: Repository owner
        repo: Repository name
        dry_run: If True, print changes without updating file
    """
    # Initialize GitHub service
    github_service = GitHubService()

    # Load current team members
    config_path = Path(__file__).parent.parent / "config" / "team-members.json"

    if not config_path.exists():
        print(f"Error: team-members.json not found at {config_path}")
        sys.exit(1)

    with open(config_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    team_members = data.get("team_members", [])

    if not team_members:
        print("Error: No team members found in team-members.json")
        sys.exit(1)

    print("=" * 80)
    print("UPDATING TEAM MEMBER CONTRIBUTIONS")
    print("=" * 80)
    print(f"Repository: {owner}/{repo}")
    print(f"Team size: {len(team_members)}")
    print()

    # Update each team member
    updated_members = []
    changes = []

    for member in team_members:
        name = member.get("name", "Unknown")
        login = member.get("login", "")
        old_contributions = member.get("contributions", 10)

        if not login:
            print(f"[SKIP] {name}: No GitHub login specified")
            updated_members.append(member)
            continue

        # Get current workload
        workload = get_team_member_workload(github_service, owner, repo, login)
        open_issues = workload["open_issues"]
        open_prs = workload["open_prs"]

        # Calculate new contribution score
        new_contributions = calculate_contribution_score(open_issues, open_prs)

        # Update member data
        member["contributions"] = new_contributions
        updated_members.append(member)

        # Track changes
        change = new_contributions - old_contributions
        change_str = f"+{change}" if change > 0 else str(change)

        print(f"[UPDATE] {name} (@{login})")
        print(f"         Open issues: {open_issues}, Open PRs: {open_prs}")
        print(f"         Contributions: {old_contributions} → {new_contributions} ({change_str})")

        if change != 0:
            changes.append({
                "name": name,
                "login": login,
                "old": old_contributions,
                "new": new_contributions,
                "change": change
            })

        print()

    # Summary
    print("=" * 80)
    print(f"SUMMARY: {len(changes)} team members updated")
    print("=" * 80)

    if changes:
        for change in changes:
            direction = "🔴 Busier" if change["change"] > 0 else "🟢 More available"
            print(f"{direction}: {change['name']} ({change['old']} → {change['new']})")
    else:
        print("No changes - all team members have same workload")

    print()

    # Write updated data
    if not dry_run:
        data["team_members"] = updated_members

        with open(config_path, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=2, ensure_ascii=False)

        print(f"✅ Updated team-members.json at {config_path}")
    else:
        print("🔍 DRY RUN - No changes written to file")
        print("Run without --dry-run to apply changes")


def main():
    import argparse

    parser = argparse.ArgumentParser(
        description='Update team member contributions based on current workload'
    )
    parser.add_argument('--owner', default='microsoft',
                       help='Repository owner (default: microsoft)')
    parser.add_argument('--repo', default='Agent365-devTools',
                       help='Repository name (default: Agent365-devTools)')
    parser.add_argument('--dry-run', action='store_true',
                       help='Preview changes without updating file')

    args = parser.parse_args()

    # Check for GITHUB_TOKEN
    if not os.getenv('GITHUB_TOKEN'):
        print("Error: GITHUB_TOKEN environment variable not set")
        sys.exit(1)

    update_team_contributions(args.owner, args.repo, args.dry_run)


if __name__ == '__main__':
    main()
