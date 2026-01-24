#!/usr/bin/env python3
"""
CLI script to triage a GitHub issue using the intake service.
Used by GitHub Actions for auto-triage on new issue creation.
"""
import argparse
import json
import os
import sys
from pathlib import Path

# Add current directory to path for imports
sys.path.insert(0, str(Path(__file__).parent))

from services.intake_service import triage_issues


def main():
    """Main entry point for issue triage CLI."""
    parser = argparse.ArgumentParser(
        description='Triage a GitHub issue using AI-powered analysis'
    )
    parser.add_argument('--owner', required=True, help='Repository owner')
    parser.add_argument('--repo', required=True, help='Repository name')
    parser.add_argument('--issue-number', required=True, type=int, help='Issue number')
    parser.add_argument('--output', default='/tmp/triage_result.json',
                       help='Output file path (default: /tmp/triage_result.json)')
    parser.add_argument('--apply', action='store_true',
                       help='Apply triage changes (labels, assignee) to the issue')

    args = parser.parse_args()

    print(f"[TRIAGE] Triaging issue #{args.issue_number} in {args.owner}/{args.repo}")

    # Check for GITHUB_TOKEN
    github_token = os.getenv('GITHUB_TOKEN')
    if not github_token:
        print("[ERROR] GITHUB_TOKEN environment variable not set")
        sys.exit(1)

    try:
        # Run triage using the intake service function
        print("[AI] Running AI triage analysis...")
        result = triage_issues(
            owner=args.owner,
            repo=args.repo,
            issue_numbers=[args.issue_number],
            apply_changes=args.apply,
            output_logs=False
        )

        # Extract result for the specific issue
        if not result or 'results' not in result or len(result['results']) == 0:
            print(f"[ERROR] ERROR: No triage result returned for issue #{args.issue_number}")
            sys.exit(1)

        issue_result = result['results'][0]
        issue_data = issue_result.get('issue', {})

        # Prepare output for GitHub Action
        output = {
            'issue_number': args.issue_number,
            'issue_type': issue_data.get('issue_type', 'unknown'),
            'priority': issue_data.get('priority', 'P3'),
            'confidence': issue_data.get('confidence', 0.0),
            'recommended_assignee': issue_data.get('suggested_assignee'),  # Fixed: was looking for wrong field name
            'rationale': issue_data.get('rationale', ''),
            'is_copilot_fixable': issue_data.get('is_copilot_fixable', False),
            'suggested_labels': issue_data.get('suggested_labels', []),
            'fix_suggestions': issue_data.get('fix_suggestions', [])
        }

        # Write result to file for GitHub Action to read
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)

        with open(output_path, 'w') as f:
            json.dump(output, f, indent=2)

        # Print summary
        print("\n" + "=" * 80)
        print("[OK] TRIAGE COMPLETE")
        print("=" * 80)
        print(f"Classification: {output['issue_type']}")
        print(f"Priority: {output['priority']}")
        print(f"Confidence: {output['confidence'] * 100:.1f}%")
        if output['recommended_assignee']:
            print(f"Recommended Assignee: @{output['recommended_assignee']}")
        print(f"Copilot-fixable: {'Yes' if output['is_copilot_fixable'] else 'No'}")
        print(f"\nRationale: {output['rationale']}")

        if output['fix_suggestions']:
            print(f"\nFix Suggestions ({len(output['fix_suggestions'])}):")
            for i, suggestion in enumerate(output['fix_suggestions'], 1):
                print(f"  {i}. {suggestion}")

        print(f"\nResult written to: {output_path}")

    except Exception as e:
        print(f"\n[ERROR] ERROR: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
