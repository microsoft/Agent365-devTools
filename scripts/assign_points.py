# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

import os
import json
import yaml
import sys

# Exit codes used by this script:
# 0 = Success - points were awarded and leaderboard updated
# 1 = Error - something went wrong (missing config, permissions, etc.)
# 2 = No-op - no points awarded, but not an error (duplicate event, no criteria matched)

# Path to config file inside scripts folder
CONFIG_FILE = os.path.join(os.path.dirname(__file__), 'config_points.yml')
PROCESSED_FILE = os.path.join(os.path.dirname(__file__), 'processed_ids.json')
# Path to leaderboard in repository root (one level up from scripts/)
LEADERBOARD_FILE = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'leaderboard.json')

# Exit codes
EXIT_SUCCESS = 0
EXIT_ERROR = 1
EXIT_NO_OP = 2  # No changes needed

def load_config():
    """
    Load the points configuration from config_points.yml.
    
    Returns:
        dict: Configuration dictionary with 'points' section
    
    Exits:
        1 if config file is missing or contains invalid YAML
    """
    if not os.path.exists(CONFIG_FILE):
        print(f"ERROR: Config file not found: {CONFIG_FILE}", file=sys.stderr)
        print("Expected location: scripts/config_points.yml", file=sys.stderr)
        sys.exit(1)
    
    try:
        with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
            config = yaml.safe_load(f)
    except yaml.YAMLError as e:
        print(f"ERROR: Invalid YAML in config file: {e}", file=sys.stderr)
        print(f"File location: {CONFIG_FILE}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Failed to read config file: {e}", file=sys.stderr)
        sys.exit(1)
    
    # Validate that config has the expected structure
    if not isinstance(config, dict) or 'points' not in config:
        print(f"ERROR: Invalid config structure in {CONFIG_FILE}", file=sys.stderr)
        print("Expected format: { points: { basic_review: 5, ... } }", file=sys.stderr)
        sys.exit(1)
    
    # Validate required keys
    required_keys = ['basic_review', 'detailed_review', 'performance_improvement', 'approve_pr']
    missing_keys = [key for key in required_keys if key not in config['points']]
    if missing_keys:
        print(f"ERROR: Missing required keys in config: {', '.join(missing_keys)}", file=sys.stderr)
        print(f"Required keys: {', '.join(required_keys)}", file=sys.stderr)
        sys.exit(1)
    
    # Validate that all required point values are positive integers
    invalid_keys = [
        key for key in required_keys
        if not isinstance(config['points'][key], int) or config['points'][key] <= 0
    ]
    if invalid_keys:
        print(f"ERROR: The following point values are not positive integers: {', '.join(invalid_keys)}", file=sys.stderr)
        for key in invalid_keys:
            print(f"  {key}: {config['points'][key]!r}", file=sys.stderr)
        sys.exit(1)
    
    return config

def load_event():
    event_path = os.getenv('GITHUB_EVENT_PATH')
    if not event_path:
        print("ERROR: GITHUB_EVENT_PATH is not set.")
        sys.exit(1)
    if not os.path.exists(event_path):
        print(f"ERROR: Event file not found: {event_path}")
        sys.exit(1)
    
    try:
        with open(event_path, 'r', encoding='utf-8') as f:
            event = json.load(f)
    except json.JSONDecodeError as e:
        print(f"ERROR: Invalid JSON in event file: {e}", file=sys.stderr)
        print(f"File location: {event_path}", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Failed to read event file: {e}", file=sys.stderr)
        sys.exit(1)

    # Check if this is a comment on a regular issue (not a PR)
    if 'issue' in event and 'pull_request' not in event.get('issue', {}):
        print("INFO: Skipping - this is a comment on a regular issue, not a pull request.")
        sys.exit(EXIT_NO_OP)

    return event

def load_processed_ids():
    if os.path.exists(PROCESSED_FILE):
        with open(PROCESSED_FILE, 'r', encoding='utf-8') as f:
            try:
                # Load as list, convert to set for efficient lookup
                return set(json.load(f))
            except json.JSONDecodeError:
                return set()
    return set()

def save_processed_ids(processed_ids):
    try:
        with open(PROCESSED_FILE, 'w', encoding='utf-8') as f:
            # Convert set to list for JSON serialization
            json.dump(list(processed_ids), f, indent=2)
    except PermissionError as e:
        print(f"ERROR: Permission denied writing to {PROCESSED_FILE}: {e}", file=sys.stderr)
        print("Check file permissions and ensure the workflow has write access.", file=sys.stderr)
        sys.exit(1)
    except IOError as e:
        print(f"ERROR: Failed to write processed IDs to {PROCESSED_FILE}: {e}", file=sys.stderr)
        print("This may be due to disk space issues or file system problems.", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Unexpected error saving processed IDs: {e}", file=sys.stderr)
        sys.exit(1)

def extract_user(event):
    """
    Extract user login from GitHub event with multiple fallback strategies.
    
    Priority:
    1. review.user.login (for pull_request_review events)
    2. comment.user.login (for issue_comment events)
    3. sender.login (top-level event sender)
    
    Returns:
        tuple: (user_login: str, source: str) or (None, None) if extraction fails
    """
    # Try review user first
    review = event.get('review')
    if review and isinstance(review, dict):
        review_user = review.get('user')
        if review_user and isinstance(review_user, dict):
            login = review_user.get('login')
            if login:
                return login, 'review.user'
    
    # Try comment user
    comment = event.get('comment')
    if comment and isinstance(comment, dict):
        comment_user = comment.get('user')
        if comment_user and isinstance(comment_user, dict):
            login = comment_user.get('login')
            if login:
                return login, 'comment.user'
    
    # Fallback to sender
    sender = event.get('sender')
    if sender and isinstance(sender, dict):
        login = sender.get('login')
        if login:
            return login, 'sender'
    
    return None, None

def detect_points(event, cfg):
    """
    Detect points based on the GitHub event and configuration.
    
    All keyword matching is CASE-INSENSITIVE. Contributors can use any capitalization.
    
    Scoring Rules:
    1. Review types (mutually exclusive - only the highest applies):
       - Include "detailed" anywhere in your review = detailed_review points (10)
       - Include "basic review" anywhere in your review = basic_review points (5)
       - If both keywords present, only "detailed" counts (higher value)
    
    2. Bonus points (additive - can stack with review types):
       - Include "performance" anywhere = performance_improvement bonus (+4)
       - Approve the PR (state=approved) = approve_pr bonus (+3)
    
    Keyword Examples (all case-insensitive):
    - "detailed", "Detailed", "DETAILED" all work
    - "basic review", "Basic Review", "BASIC REVIEW" all work
    - "performance", "Performance", "PERFORMANCE" all work
    
    Args:
        event: GitHub webhook event JSON
        cfg: Configuration dictionary with points values
    
    Returns:
        tuple: (points: int, user: str)
    
    Exits:
        1 if unable to extract user from event
        2 if no points criteria matched (not an error)
    """
    action = event.get('action')
    review = event.get('review', {})
    comment = event.get('comment', {})

    # Note: The review and comment bodies are user-controlled content from the GitHub API.
    # No sanitization is performed here because the current usage only checks for keywords.
    # If this code is extended to process or display these bodies, input validation should be added.
    review_body = (review.get('body') or '').lower()
    review_state = (review.get('state') or '').lower()
    comment_body = (comment.get('body') or '').lower()

    user, source = extract_user(event)
    if not user:
        print("ERROR: Unable to extract user from event. Checked review.user, comment.user, and sender fields.")
        print("Event structure:", json.dumps({
            'has_review': 'review' in event,
            'has_comment': 'comment' in event,
            'has_sender': 'sender' in event,
            'action': action
        }, indent=2))
        sys.exit(1)
    
    print(f"User identified: {user} (source: {source})")

    points = 0
    scoring_breakdown = []
    
    # Review type scoring (mutually exclusive - highest wins)
    if "detailed" in review_body:
        points += cfg['points']['detailed_review']
        scoring_breakdown.append(f"detailed_review: +{cfg['points']['detailed_review']}")
    elif "basic review" in review_body:
        points += cfg['points']['basic_review']
        scoring_breakdown.append(f"basic_review: +{cfg['points']['basic_review']}")

    # Performance improvement bonus (additive)
    if "performance" in comment_body or "performance" in review_body:
        points += cfg['points']['performance_improvement']
        scoring_breakdown.append(f"performance_improvement: +{cfg['points']['performance_improvement']}")

    # PR approval bonus (additive)
    if action == "submitted" and review_state == "approved":
        points += cfg['points']['approve_pr']
        scoring_breakdown.append(f"approve_pr: +{cfg['points']['approve_pr']}")

    # Log scoring breakdown for transparency
    if scoring_breakdown:
        print(f"Scoring breakdown: {', '.join(scoring_breakdown)} = {points} total")
    else:
        print("No scoring criteria matched.")

    return points, user

def update_leaderboard(user, points):
    """
    Update the leaderboard with awarded points for a user.
    
    Args:
        user: GitHub username
        points: Points to award
    """
    leaderboard = {}

    if os.path.exists(LEADERBOARD_FILE):
        with open(LEADERBOARD_FILE, 'r', encoding='utf-8') as f:
            try:
                data = json.load(f)
                # Filter out 'top' key to prevent unbounded growth
                leaderboard = {k: v for k, v in data.items() if k != 'top' and isinstance(v, (int, float))}
            except json.JSONDecodeError:
                leaderboard = {}

    leaderboard[user] = leaderboard.get(user, 0) + points
    print(f"Updated {user}: {leaderboard[user]} total points (awarded +{points})")
    
    try:
        with open(LEADERBOARD_FILE, 'w', encoding='utf-8') as f:
            json.dump(leaderboard, f, indent=2)
    except PermissionError as e:
        print(f"ERROR: Permission denied writing to {LEADERBOARD_FILE}: {e}", file=sys.stderr)
        print("Check file permissions and ensure the workflow has write access.", file=sys.stderr)
        sys.exit(1)
    except IOError as e:
        print(f"ERROR: Failed to write leaderboard to {LEADERBOARD_FILE}: {e}", file=sys.stderr)
        print("This may be due to disk space issues or file system problems.", file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Unexpected error saving leaderboard: {e}", file=sys.stderr)
        sys.exit(1)

def main():
    cfg = load_config()
    event = load_event()
    points, user = detect_points(event, cfg)

    # Extract unique ID for duplicate prevention
    event_id = event.get('review', {}).get('id')
    if event_id is None:
        event_id = event.get('comment', {}).get('id')
    if event_id is None:
        print("No unique ID found in event. Skipping duplicate check.")
        sys.exit(EXIT_NO_OP)

    processed_ids = load_processed_ids()
    if event_id in processed_ids:
        print(f"Event {event_id} already processed. Skipping scoring.")
        sys.exit(EXIT_NO_OP)

    if points <= 0:
        print("No points awarded. Skipping leaderboard update.")
        sys.exit(EXIT_NO_OP)

    # Update leaderboard and mark event as processed
    update_leaderboard(user, points)
    processed_ids.add(event_id)
    save_processed_ids(processed_ids)
    
    print(f"SUCCESS: Awarded {points} points to {user}")

if __name__ == '__main__':
    main()
