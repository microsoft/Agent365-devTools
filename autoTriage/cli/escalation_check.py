#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
CLI script to check for SLA breaches and escalate issues.
Used by GitHub Actions for scheduled SLA enforcement.
"""
import argparse
import json
import os
import sys
from pathlib import Path

# Add parent directory (autoTriage) to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from services.escalation_service import EscalationService


def main():
    """Main entry point for SLA escalation check CLI."""
    parser = argparse.ArgumentParser(
        description='Check for SLA breaches and escalate overdue issues'
    )
    parser.add_argument('--owner', required=True, help='Repository owner')
    parser.add_argument('--repo', required=True, help='Repository name')
    parser.add_argument('--output', default='/tmp/escalation_result.json',
                       help='Output file path (default: /tmp/escalation_result.json)')
    parser.add_argument('--apply', action='store_true',
                       help='Apply escalation actions (assign to lead, post comment)')

    args = parser.parse_args()

    print(f"[ESCALATION] Checking SLA breaches in {args.owner}/{args.repo}")

    # Check for GITHUB_TOKEN
    github_token = os.getenv('GITHUB_TOKEN')
    if not github_token:
        print("[ERROR] GITHUB_TOKEN environment variable not set")
        sys.exit(1)

    try:
        service = EscalationService()
        result = service.run_escalation_check(
            owner=args.owner,
            repo=args.repo,
            apply=args.apply
        )

        # Write result to file
        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)

        with open(output_path, 'w') as f:
            json.dump(result, f, indent=2)

        # Print summary
        print("\n" + "=" * 80)
        print("[ESCALATION] SLA CHECK COMPLETE")
        print("=" * 80)
        print(f"Repository: {result['repository']}")
        print(f"Checked at: {result['checked_at']}")
        print(f"Apply mode: {result['apply_mode']}")
        print(f"Total breached: {result['total_breached']}")
        
        if result['issues']:
            print("\nBreached Issues:")
            print("-" * 60)
            for issue in result['issues']:
                status = "[ESCALATED]" if issue['applied'] else "[PENDING]"
                print(f"  #{issue['number']}: {issue['title'][:50]}...")
                print(f"    Priority: {issue['priority']} | Open: {issue['hours_open']}h / SLA: {issue['sla_hours']}h")
                print(f"    Action: {issue['escalation_action']} -> {issue['escalated_to']}")
                if args.apply:
                    print(f"    Status: {status}")
                print()
        else:
            print("\n[OK] No SLA breaches detected!")

        print(f"\nResult written to: {output_path}")
        
        # Exit with code 1 if there are breaches (useful for alerting)
        if result['total_breached'] > 0 and not args.apply:
            sys.exit(1)

    except Exception as e:
        print(f"\n[ERROR] Failed to run escalation check: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
