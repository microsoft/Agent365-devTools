# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

import os
import json
import sys

# File paths
LEADERBOARD_JSON = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'leaderboard.json')
LEADERBOARD_MD = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'LEADERBOARD.md')

def load_leaderboard():
    """
    Load the leaderboard from JSON file.
    
    Returns:
        dict: Dictionary mapping usernames to points
    
    Exits:
        1 if leaderboard file is missing or contains invalid JSON
    """
    if not os.path.exists(LEADERBOARD_JSON):
        print(f"ERROR: Leaderboard file not found: {LEADERBOARD_JSON}", file=sys.stderr)
        sys.exit(1)
    
    try:
        with open(LEADERBOARD_JSON, 'r', encoding='utf-8') as f:
            leaderboard = json.load(f)
    except json.JSONDecodeError as e:
        print(f"ERROR: Invalid JSON in leaderboard file: {e}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Failed to read leaderboard file: {e}", file=sys.stderr)
        sys.exit(1)
    
    return leaderboard

def update_leaderboard_json(leaderboard):
    """
    Update leaderboard.json with top contributor information.
    
    Args:
        leaderboard: Dictionary mapping usernames to points
    """
    if not leaderboard:
        top_contributor = "No contributors yet"
    else:
        # Find top contributor
        top_user = max(leaderboard, key=leaderboard.get)
        top_contributor = f"{top_user} ({leaderboard[top_user]} pts)"
    
    # Update the JSON with top contributor info for the badge
    output = dict(leaderboard)
    output['top'] = top_contributor
    
    try:
        with open(LEADERBOARD_JSON, 'w', encoding='utf-8') as f:
            json.dump(output, f, indent=2)
        print(f"Updated leaderboard.json with top contributor: {top_contributor}")
    except Exception as e:
        print(f"ERROR: Failed to write leaderboard.json: {e}", file=sys.stderr)
        sys.exit(1)

def generate_markdown(leaderboard):
    """
    Generate markdown leaderboard from points data.
    
    Args:
        leaderboard: Dictionary mapping usernames to points
    
    Returns:
        str: Formatted markdown string
    """
    if not leaderboard:
        return "# 🏆 Leaderboard\n\nNo contributors yet. Be the first to contribute!\n"
    
    # Sort by points (descending), then by username (ascending)
    sorted_leaders = sorted(leaderboard.items(), key=lambda x: (-x[1], x[0]))
    
    markdown = "# 🏆 Leaderboard\n\n"
    markdown += "Thank you to all our contributors! Points are awarded for code reviews, "
    markdown += "performance improvements, and quality contributions.\n\n"
    markdown += "| Rank | Contributor | Points |\n"
    markdown += "|------|-------------|--------|\n"
    
    for rank, (user, points) in enumerate(sorted_leaders, 1):
        # Add trophy emoji for top 3
        trophy = ""
        if rank == 1:
            trophy = "🥇 "
        elif rank == 2:
            trophy = "🥈 "
        elif rank == 3:
            trophy = "🥉 "
        
        markdown += f"| {rank} | {trophy}[@{user}](https://github.com/{user}) | {points} |\n"
    
    markdown += "\n---\n\n"
    markdown += "### How to Earn Points\n\n"
    markdown += "- **Basic Review** (5 pts): Include \"basic review\" in your PR review\n"
    markdown += "- **Detailed Review** (10 pts): Include \"detailed\" in your in-depth review\n"
    markdown += "- **Performance Improvement** (+4 pts): Mention \"performance\" optimizations\n"
    markdown += "- **Approve PR** (+3 pts): Approve a pull request\n\n"
    markdown += "_Points are case-insensitive and bonuses stack!_\n"
    
    return markdown

def main():
    """
    Main function to update leaderboard files.
    """
    leaderboard = load_leaderboard()
    
    # Update JSON with top contributor
    update_leaderboard_json(leaderboard)
    
    # Remove 'top' key for markdown generation
    leaderboard_for_md = {k: v for k, v in leaderboard.items() if k != 'top'}
    
    # Generate and write markdown
    markdown_content = generate_markdown(leaderboard_for_md)
    
    try:
        with open(LEADERBOARD_MD, 'w', encoding='utf-8') as f:
            f.write(markdown_content)
        print(f"Generated {LEADERBOARD_MD}")
    except Exception as e:
        print(f"ERROR: Failed to write {LEADERBOARD_MD}: {e}", file=sys.stderr)
        sys.exit(1)
    
    print("SUCCESS: Leaderboard updated")

if __name__ == '__main__':
    main()
