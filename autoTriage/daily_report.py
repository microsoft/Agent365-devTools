#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
CLI script to generate daily issue report.
Used by GitHub Actions for scheduled daily reports.
"""
import argparse
import json
import sys
from pathlib import Path
from datetime import datetime, timezone

# Add current directory to path for imports
sys.path.insert(0, str(Path(__file__).parent))

from services.daily_report_service import DailyReportService


def main():
    """Main entry point for daily report CLI."""
    parser = argparse.ArgumentParser(
        description='Generate daily issue status report'
    )
    parser.add_argument('--owner', required=True, help='Repository owner')
    parser.add_argument('--repo', required=True, help='Repository name')
    parser.add_argument('--output', default='report.json',
                       help='Output JSON file path (default: report.json)')
    parser.add_argument('--summary', default=None,
                       help='Path to write GitHub Summary markdown')
    parser.add_argument('--send-teams', action='store_true',
                       help='Send report to MS Teams (requires TEAMS_WEBHOOK_URL env)')

    args = parser.parse_args()

    print(f"[REPORT] Generating daily report for {args.owner}/{args.repo}")

    try:
        service = DailyReportService()
        report = service.generate_report(args.owner, args.repo)

        # Save report data to JSON
        report_data = {
            'repository': f"{args.owner}/{args.repo}",
            'generated_at': datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
            'total_open': report.total_open,
            'sla_compliance_pct': report.sla_compliance_pct,
            'breached_count': report.breached_count,
            'warning_count': report.warning_count,
            'by_priority': report.by_priority,
            'issues': [
                {
                    'number': i.number,
                    'title': i.title,
                    'priority': i.priority,
                    'assignee': i.assignee,
                    'hours_open': i.hours_open,
                    'sla_hours': i.sla_hours,
                    'sla_status': i.sla_status,
                    'url': i.url
                }
                for i in report.issues
            ]
        }

        with open(args.output, 'w') as f:
            json.dump(report_data, f, indent=2)

        print(f"[OK] Report saved to {args.output}")

        # Generate GitHub Summary if requested
        if args.summary:
            generate_summary(report_data, args.summary)

        # Send to Teams if requested
        if args.send_teams:
            success = service.send_to_teams(report)
            if success:
                print("[OK] Report sent to Teams")
            else:
                print("[WARN] Failed to send to Teams")

        # Print summary to console
        print("\n" + "=" * 60)
        print("DAILY ISSUE REPORT")
        print("=" * 60)
        print(f"Total Open: {report.total_open}")
        print(f"SLA Compliance: {report.sla_compliance_pct}%")
        print(f"Breached: {report.breached_count}")
        print(f"Warning: {report.warning_count}")

    except Exception as e:
        print(f"[ERROR] {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)


def generate_summary(report: dict, summary_path: str):
    """Generate GitHub Actions Summary markdown."""
    
    # Determine status
    if report['sla_compliance_pct'] >= 90:
        status = '[OK]'
        status_text = 'Healthy'
    elif report['sla_compliance_pct'] >= 70:
        status = '[WARNING]'
        status_text = 'Warning'
    else:
        status = '[CRITICAL]'
        status_text = 'Critical'

    lines = [
        '# Daily Issue Report',
        '',
        f"**Repository:** `{report['repository']}`",
        f"**Generated:** {report['generated_at']}",
        '',
        '## Summary',
        '',
        '| Metric | Value |',
        '|--------|-------|',
        f"| Total Open Issues | **{report['total_open']}** |",
        f"| SLA Compliance | {status} **{report['sla_compliance_pct']}%** ({status_text}) |",
        f"| Breached (Over SLA) | [CRITICAL] **{report['breached_count']}** |",
        f"| Warning (>80% SLA) | [WARNING] **{report['warning_count']}** |",
        '',
        '## By Priority',
        '',
        '| Priority | Count |',
        '|----------|-------|',
    ]

    for p in ['P0', 'P1', 'P2', 'P3', 'P4', 'None']:
        count = report['by_priority'].get(p, 0)
        if count > 0:
            lines.append(f'| {p} | {count} |')

    lines.extend([
        '',
        '## Issues (sorted by SLA urgency)',
        '',
        '| Issue | Priority | Assignee | Hours Open | SLA | Status |',
        '|-------|----------|----------|------------|-----|--------|',
    ])

    for issue in report['issues'][:20]:
        status_icon = '[CRITICAL]' if issue['sla_status'] == 'breached' else ('[WARNING]' if issue['sla_status'] == 'warning' else '[OK]')
        raw_assignee = issue.get('assignee', '')
        if not raw_assignee or str(raw_assignee).strip().lower() == 'unassigned':
            assignee_display = 'Unassigned'
        else:
            assignee_display = f"@{raw_assignee}"
        lines.append(f"| [#{issue['number']}]({issue['url']}) | {issue['priority']} | {assignee_display} | {issue['hours_open']}h | {issue['sla_hours']}h | {status_icon} |")

    if len(report['issues']) > 20:
        lines.append(f"| ... | *{len(report['issues']) - 20} more issues* | | | | |")

    lines.extend([
        '',
        '---',
        '*Generated by AutoTriage Daily Report*',
    ])

    summary_md = '\n'.join(lines)

    with open(summary_path, 'a', encoding='utf-8') as f:
        f.write(summary_md)

    print(f"[OK] Summary written to {summary_path}")


if __name__ == '__main__':
    main()
