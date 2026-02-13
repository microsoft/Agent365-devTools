#!/usr/bin/env python3
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
"""
PR Review Poster

Reads YAML review file and posts comments to GitHub.
NOTE: Review generation is now handled by Claude Code directly.

Usage:
    python review-pr.py <pr-number>

Example:
    python review-pr.py 180              # Post review to GitHub
    python review-pr.py 180 --dry-run    # Preview without posting

Requirements:
    pip install PyYAML
    gh CLI authenticated
"""
import argparse
import subprocess
import sys
import tempfile
import time
from pathlib import Path

try:
    import yaml
except ImportError:
    print("Error: PyYAML not installed. Run: pip install PyYAML")
    sys.exit(1)


class PRReviewPoster:
    """Post PR review comments from YAML file to GitHub."""

    def __init__(self, pr_number: int, dry_run: bool = False):
        self.pr_number = pr_number
        self.dry_run = dry_run

    def run_command(self, args: list, check: bool = True) -> str:
        """Execute command with argument list and return output.

        Uses shell=False to prevent shell injection vulnerabilities.
        """
        result = subprocess.run(
            args,
            capture_output=True,
            text=True,
            encoding='utf-8',
            errors='replace'
        )

        if check and result.returncode != 0:
            raise Exception(f"Command failed with exit code {result.returncode}: {result.stderr}")

        return result.stdout.strip()

    def preview_comments(self, comments_file: Path):
        """Display preview of comments."""
        with open(comments_file, 'r', encoding='utf-8') as f:
            data = yaml.safe_load(f)

        print(f"\n{'='*60}")
        print(f"PR #{data['pr_number']}: {data['pr_title']}")
        print(f"{'='*60}")
        print(f"Decision: {data.get('overall_decision', 'COMMENT')}")
        print(f"Overall: {data.get('overall_body', '')[:100]}...")
        print(f"\nComments ({len(data.get('comments', []))}):")

        for i, comment in enumerate(data.get('comments', []), 1):
            if comment.get('enabled', True):
                file_ref = comment.get('file', 'General')
                severity = comment.get('severity', 'info').upper()
                body_preview = comment.get('body', '')[:80]
                print(f"\n{i}. [{severity}] {file_ref}")
                print(f"   {body_preview}...")

    def generate_manual_format(self, comments_file: Path):
        """Generate markdown file for manual copy/paste posting."""
        with open(comments_file, 'r', encoding='utf-8') as f:
            data = yaml.safe_load(f)

        enabled_comments = [c for c in data.get('comments', []) if c.get('enabled', True)]

        # Create markdown file
        md_file = comments_file.parent / f"pr-{self.pr_number}-review-manual.md"

        with open(md_file, 'w', encoding='utf-8') as f:
            f.write(f"# PR #{self.pr_number} Review Comments\n\n")
            f.write(f"**PR Title:** {data.get('pr_title', '')}\n\n")
            f.write(f"**PR URL:** {data.get('pr_url', '')}\n\n")
            f.write(f"---\n\n")
            f.write(f"## Instructions\n\n")
            f.write(f"Copy and paste each comment below to the GitHub PR.\n\n")
            f.write(f"For file-specific comments, click on the file in the PR and add the comment to the appropriate location.\n\n")
            f.write(f"---\n\n")

            # Overall review
            f.write(f"## Overall Review\n\n")
            f.write(f"{data.get('overall_body', '')}\n\n")
            f.write(f"---\n\n")

            # Individual comments
            f.write(f"## Comments ({len(enabled_comments)})\n\n")

            for i, comment in enumerate(enabled_comments, 1):
                f.write(f"### Comment {i} - [{comment.get('severity', 'info').upper()}]\n\n")

                if 'file' in comment:
                    f.write(f"**File:** `{comment['file']}`\n\n")
                else:
                    f.write(f"**Location:** General comment\n\n")

                f.write(f"{comment.get('body', '')}\n\n")
                f.write(f"---\n\n")

        print(f"\n[OK] Manual format generated: {md_file}")
        print(f"\nOpen the file and copy/paste comments to GitHub:")
        print(f"  {md_file}")
        return md_file

    def post_review(self, comments_file: Path):
        """Post review comments to GitHub."""
        with open(comments_file, 'r', encoding='utf-8') as f:
            data = yaml.safe_load(f)

        enabled_comments = [c for c in data.get('comments', []) if c.get('enabled', True)]

        if self.dry_run:
            print("\n[DRY RUN - No changes will be made]")
            self.preview_comments(comments_file)
            return

        print(f"\nPosting {len(enabled_comments)} comments to PR #{self.pr_number}...")

        try:
            # Post overall review
            decision = data.get('overall_decision', 'COMMENT').lower()
            overall_body = data.get('overall_body', '')

            self.run_command(
                ['gh', 'pr', 'review', str(self.pr_number),
                 f'--{decision}', '--body', overall_body]
            )

            print("[OK] Overall review posted")

            # Post individual comments
            for i, comment in enumerate(enabled_comments, 1):
                body = comment.get('body', '')

                print(f"  [{i}/{len(enabled_comments)}] Posting comment...")

                self.run_command(
                    ['gh', 'pr', 'comment', str(self.pr_number),
                     '--body', body],
                    check=False
                )

                time.sleep(0.5)  # Rate limiting

            print(f"\n[OK] Successfully posted review to PR #{self.pr_number}")
            print(f"  View at: {data.get('pr_url', '')}")

        except Exception as e:
            error_msg = str(e)
            # Check if it's a GitHub API permission error
            if 'Unauthorized' in error_msg or 'Enterprise Managed User' in error_msg or 'permission' in error_msg.lower():
                print(f"\n[WARNING] GitHub API posting failed due to permissions.")
                print(f"Error: {error_msg}")
                print(f"\n[INFO] Generating manual copy/paste format instead...")
                self.generate_manual_format(comments_file)
            else:
                # Re-raise other errors
                raise


def main():
    """Main entry point."""
    parser = argparse.ArgumentParser(
        description='Post structured PR review comments to GitHub'
    )
    parser.add_argument(
        'pr_number',
        type=int,
        help='Pull request number'
    )
    parser.add_argument(
        '--output',
        type=Path,
        default=None,
        help='Path to review YAML file'
    )
    parser.add_argument(
        '--dry-run',
        action='store_true',
        help='Preview without posting'
    )

    args = parser.parse_args()

    # Create output path
    if args.output is None:
        output_dir = Path(tempfile.gettempdir()) / 'pr-reviews'
        output_dir.mkdir(exist_ok=True)
        args.output = output_dir / f'pr-{args.pr_number}-review.yaml'

    # Create poster
    poster = PRReviewPoster(args.pr_number, dry_run=args.dry_run)

    # Execute posting workflow
    try:
        if not args.output.exists():
            print(f"Error: Review file not found: {args.output}", file=sys.stderr)
            print(f"\nGenerate the review first by running:", file=sys.stderr)
            print(f"  /review-pr {args.pr_number}", file=sys.stderr)
            sys.exit(1)

        print(f"Reading review from: {args.output}")
        poster.preview_comments(args.output)

        print(f"\n" + "="*60)
        print(f"Ready to post review to PR #{args.pr_number}")
        print("="*60)

        poster.post_review(args.output)

    except KeyboardInterrupt:
        print("\n\nCancelled by user.")
        sys.exit(1)
    except Exception as e:
        print(f"\nError: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == '__main__':
    main()
