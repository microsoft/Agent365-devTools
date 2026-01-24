# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
GitHub Service - Wrapper for PyGithub with caching and rate limit handling
"""
import os
import time
import logging
from datetime import datetime, timedelta, timezone
from typing import Optional, List, Dict, Set, Tuple, Any
from github import Github, GithubException
from difflib import get_close_matches
from functools import lru_cache

logger = logging.getLogger(__name__)

# Constants for triage identification
TRIAGE_BOT_USERS = ['github-actions[bot]', 'dependabot[bot]']

# In-memory cache with TTL
_cache: Dict[str, Tuple[Any, datetime]] = {}
CACHE_TTL_SECONDS = 900  # 15 minutes - good for demos

# API request limits
MAX_ITEMS_PER_REQUEST = 100  # Maximum items to fetch per API request
MAX_MERGED_PRS_TO_FETCH = 50  # Maximum merged PRs to fetch
MIN_RATE_LIMIT_REMAINING = 10  # Minimum remaining rate limit before warning
DEFAULT_PER_PAGE = 100  # Default items per page for GitHub API

# Repository analysis limits
MAX_FILES_TO_SCAN = 500  # Maximum files to scan in repository structure
MAX_SCAN_DEPTH = 3  # Maximum directory depth to scan
MAX_CONFIG_FILES = 3  # Maximum config files to fetch content for
MAX_FILE_CONTENT_SIZE = 10000  # Maximum file size in bytes to fetch
MAX_FILE_PATHS_TO_EXTRACT = 5  # Maximum file paths to extract from issue text
MAX_CONTRIBUTORS_TO_SHOW = 3  # Maximum contributors to show per file


def _get_cached(key: str):
    """Get cached value if not expired."""
    if key in _cache:
        value, expiry = _cache[key]
        if datetime.now(timezone.utc) < expiry:
            logger.debug(f"Cache hit: {key}")
            return value
        del _cache[key]
    return None


def _set_cached(key: str, value: Any, ttl: int = CACHE_TTL_SECONDS):
    """Set cached value with TTL."""
    _cache[key] = (value, datetime.now(timezone.utc) + timedelta(seconds=ttl))


def clear_cache():
    """Clear the entire cache."""
    global _cache
    _cache = {}


class GitHubService:
    """Service for interacting with GitHub API with caching and rate limit handling."""

    def __init__(self):
        token = os.environ.get("GITHUB_TOKEN", "")
        if not token:
            logger.warning("GITHUB_TOKEN not set - using unauthenticated requests (60/hour limit)")
        self.client = Github(token, per_page=DEFAULT_PER_PAGE) if token else Github(per_page=DEFAULT_PER_PAGE)
        self._repo_cache: Dict[str, Any] = {}

    def _get_repo(self, owner: str, repo: str):
        """Get repository with caching."""
        cache_key = f"{owner}/{repo}"
        if cache_key not in self._repo_cache:
            self._repo_cache[cache_key] = self.client.get_repo(cache_key)
        return self._repo_cache[cache_key]

    def get_rate_limit_status(self) -> Dict:
        """Get current rate limit status."""
        rate_limit = self.client.get_rate_limit()
        core = rate_limit.core
        return {
            "remaining": core.remaining,
            "limit": core.limit,
            "reset_at": core.reset.isoformat() if core.reset else None,
            "used": core.limit - core.remaining,
        }

    def check_rate_limit(self, min_remaining: int = MIN_RATE_LIMIT_REMAINING) -> bool:
        """Check if we have enough rate limit remaining. Returns True if OK."""
        try:
            status = self.get_rate_limit_status()
            if status["remaining"] < min_remaining:
                logger.warning(f"Rate limit low: {status['remaining']} remaining, resets at {status['reset_at']}")
                return False
            return True
        except Exception as e:
            logger.error(f"Failed to check rate limit: {e}")
            return True  # Assume OK if we can't check

    def get_issue(self, owner: str, repo: str, issue_number: int):
        """Get a specific issue with caching."""
        cache_key = f"issue:{owner}/{repo}#{issue_number}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)
            _set_cached(cache_key, issue)
            return issue
        except GithubException:
            return None

    def get_recent_issues(self, owner: str, repo: str, since: datetime) -> list:
        """Get issues updated since a given date with caching."""
        cache_key = f"recent_issues:{owner}/{repo}:{since.isoformat()}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            issues = repository.get_issues(state="all", since=since, sort="updated")
            # Limit to avoid excessive API calls - iterate up to 100
            result = []
            for issue in issues:
                result.append(issue)
                if len(result) >= 100:
                    break
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get recent issues: {e}")
            return []

    def get_closed_issues(self, owner: str, repo: str, since: datetime) -> list:
        """Get issues closed since a given date with caching."""
        cache_key = f"closed_issues:{owner}/{repo}:{since.isoformat()}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            issues = repository.get_issues(state="closed", since=since, sort="updated")
            # Limit and filter - iterate up to 100
            result = []
            for issue in issues:
                if issue.closed_at and issue.closed_at >= since:
                    result.append(issue)
                if len(result) >= 100:
                    break
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get closed issues: {e}")
            return []

    def get_recent_pull_requests(self, owner: str, repo: str, since: datetime) -> list:
        """Get PRs updated since a given date with caching."""
        cache_key = f"recent_prs:{owner}/{repo}:{since.isoformat()}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            prs = repository.get_pulls(state="all", sort="updated", direction="desc")
            # Stop early once we hit PRs older than since
            result = []
            for pr in prs:
                if pr.updated_at < since:
                    break
                result.append(pr)
                if len(result) >= 100:  # Limit
                    break
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get recent PRs: {e}")
            return []

    def get_open_issues(self, owner: str, repo: str) -> list:
        """Get ALL open issues for a repository (not filtered by date)."""
        cache_key = f"open_issues:{owner}/{repo}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            issues = repository.get_issues(state="open", sort="updated", direction="desc")
            # Filter out PRs (GitHub API returns both issues and PRs from get_issues)
            result = [issue for issue in issues if not issue.pull_request][:MAX_ITEMS_PER_REQUEST]  # Limit open issues
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get open issues: {e}")
            return []

    def get_open_pull_requests(self, owner: str, repo: str) -> list:
        """Get ALL open PRs for a repository (not filtered by date)."""
        cache_key = f"open_prs:{owner}/{repo}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            prs = repository.get_pulls(state="open", sort="updated", direction="desc")
            result = list(prs)[:MAX_ITEMS_PER_REQUEST]  # Limit open PRs
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get open PRs: {e}")
            return []

    def get_merged_pull_requests(self, owner: str, repo: str, since: datetime) -> list:
        """Get PRs merged since a given date with caching."""
        cache_key = f"merged_prs:{owner}/{repo}:{since.isoformat()}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            prs = repository.get_pulls(state="closed", sort="updated", direction="desc")
            # Stop early once we hit PRs merged before since
            result = []
            for pr in prs:
                if not pr.merged:
                    continue
                if pr.merged_at and pr.merged_at < since:
                    break
                if pr.merged_at and pr.merged_at >= since:
                    result.append(pr)
                if len(result) >= MAX_MERGED_PRS_TO_FETCH:  # Limit merged PRs
                    break
            _set_cached(cache_key, result)
            return result
        except GithubException as e:
            logger.error(f"Failed to get merged PRs: {e}")
            return []

    def apply_labels(self, owner: str, repo: str, issue_number: int, labels: List[str]) -> bool:
        """Apply labels to an issue."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)
            issue.add_to_labels(*labels)
            return True
        except GithubException:
            return False

    def assign_issue(self, owner: str, repo: str, issue_number: int, assignee: str) -> bool:
        """Assign an issue to a user."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)
            issue.add_to_assignees(assignee)
            return True
        except GithubException:
            return False

    def add_comment(self, owner: str, repo: str, issue_number: int, comment: str) -> bool:
        """Add a comment to an issue."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)
            issue.create_comment(comment)
            return True
        except GithubException:
            return False

    def search_similar_issues(self, owner: str, repo: str, title: str) -> list:
        """Search for similar issues by title with caching."""
        cache_key = f"search:{owner}/{repo}:{title[:50]}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            query = f"{title} repo:{owner}/{repo} is:issue is:open"
            results = self.client.search_issues(query)
            result = list(results[:20])  # Limit search results
            _set_cached(cache_key, result)
            return result
        except GithubException:
            return []

    def remove_labels(self, owner: str, repo: str, issue_number: int, labels: List[str]) -> bool:
        """Remove specific labels from an issue."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)
            for label in labels:
                try:
                    issue.remove_from_labels(label)
                except GithubException:
                    # Continue removing other labels even if one fails
                    pass
            return True
        except GithubException:
            return False

    def replace_labels(self, owner: str, repo: str, issue_number: int, old_labels: List[str], new_labels: List[str]) -> bool:
        """Replace old labels with new labels atomically."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)

            # Remove old labels
            for label in old_labels:
                try:
                    issue.remove_from_labels(label)
                except GithubException as e:
                    # Continue removing other labels even if one fails
                    logger.debug(f"Could not remove label '{label}' from issue #{issue_number}: {e}")

            # Add new labels
            if new_labels:
                issue.add_to_labels(*new_labels)

            return True
        except GithubException:
            return False

    def set_priority_label(self, owner: str, repo: str, issue_number: int, priority: str, remove_existing: bool = True) -> bool:
        """Set priority label, optionally removing existing priority labels."""
        try:
            repository = self._get_repo(owner, repo)
            issue = repository.get_issue(issue_number)

            if remove_existing:
                # Remove existing priority labels
                existing_labels = [label.name for label in issue.labels]
                priority_labels_to_remove = []

                for label in existing_labels:
                    label_lower = label.lower()
                    if any(pattern in label_lower for pattern in ['p0', 'p1', 'p2', 'p3', 'p4', 'priority']):
                        priority_labels_to_remove.append(label)

                for label in priority_labels_to_remove:
                    issue.remove_from_labels(label)

            # Add new priority label
            issue.add_to_labels(priority)
            return True
        except GithubException:
            return False

    def apply_triage_result(self, owner: str, repo: str, issue_number: int,
                          labels: List[str] = None, assignee: str = None,
                          comment: str = None, remove_existing_priority: bool = True) -> Dict[str, bool]:
        """Apply complete triage result to an issue.

        Returns:
            Dict with success status for each operation:
            {'labels': bool, 'assignee': bool, 'comment': bool}
        """
        results = {'labels': True, 'assignee': True, 'comment': True}

        # Apply labels
        if labels:
            # Check if we need to handle priority labels specially
            priority_labels = []
            other_labels = []

            for label in labels:
                label_lower = label.lower()
                if any(pattern in label_lower for pattern in ['p0', 'p1', 'p2', 'p3', 'p4', 'priority']):
                    priority_labels.append(label)
                else:
                    other_labels.append(label)

            # Apply priority labels (with replacement)
            if priority_labels and remove_existing_priority:
                for priority_label in priority_labels:
                    results['labels'] = results['labels'] and self.set_priority_label(
                        owner, repo, issue_number, priority_label, remove_existing=True
                    )
            elif priority_labels:
                results['labels'] = results['labels'] and self.apply_labels(
                    owner, repo, issue_number, priority_labels
                )

            # Apply other labels normally
            if other_labels:
                results['labels'] = results['labels'] and self.apply_labels(
                    owner, repo, issue_number, other_labels
                )

        # Apply assignee
        if assignee:
            results['assignee'] = self.assign_issue(owner, repo, issue_number, assignee)

        # Add comment
        if comment:
            results['comment'] = self.add_comment(owner, repo, issue_number, comment)

        return results

    @lru_cache(maxsize=100)
    def get_repository_labels(self, owner: str, repo: str) -> Dict[str, dict]:
        """Get all labels from a repository with caching.

        Returns:
            Dict mapping label names to label info (name, color, description)
        """
        try:
            repository = self._get_repo(owner, repo)
            labels = repository.get_labels()
            return {
                label.name: {
                    'name': label.name,
                    'color': label.color,
                    'description': label.description or ''
                }
                for label in labels
            }
        except GithubException:
            return {}

    @lru_cache(maxsize=50)
    def get_repository_context(self, owner: str, repo: str) -> Dict[str, Any]:
        """Get repository context for better AI suggestions.

        Returns:
            Dict with repository metadata: description, languages, topics, readme_excerpt
        """
        cache_key = f"repo_context:{owner}/{repo}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)

            # Get primary language and all languages
            languages = {}
            try:
                languages = repository.get_languages()  # Returns dict like {"Python": 12345, "JavaScript": 5678}
            except Exception as e:
                logger.debug(f"Could not fetch languages for {owner}/{repo}: {e}")

            # Get topics (tags)
            topics = []
            try:
                topics = repository.get_topics()
            except Exception as e:
                logger.debug(f"Could not fetch topics for {owner}/{repo}: {e}")

            # Get README excerpt (first 1000 chars)
            readme_excerpt = ""
            try:
                readme = repository.get_readme()
                readme_content = readme.decoded_content.decode('utf-8')
                # Take first 1000 chars, or up to first major section
                readme_excerpt = readme_content[:1000]
                if '\n##' in readme_excerpt:
                    readme_excerpt = readme_excerpt[:readme_excerpt.index('\n##')]
            except Exception as e:
                logger.debug(f"Could not fetch README for {owner}/{repo}: {e}")

            context = {
                "name": repository.name,
                "full_name": repository.full_name,
                "description": repository.description or "",
                "primary_language": repository.language or "Unknown",
                "languages": list(languages.keys())[:5],  # Top 5 languages
                "topics": topics[:10],  # Top 10 topics
                "readme_excerpt": readme_excerpt.strip(),
                "is_fork": repository.fork,
                "default_branch": repository.default_branch
            }

            # Cache for 1 hour
            _set_cached(cache_key, context, ttl=3600)
            return context

        except GithubException as e:
            logger.warning(f"Failed to get repository context: {e}")
            return {
                "name": repo,
                "full_name": f"{owner}/{repo}",
                "description": "",
                "primary_language": "Unknown",
                "languages": [],
                "topics": [],
                "readme_excerpt": "",
                "is_fork": False,
                "default_branch": "main"
            }

    @lru_cache(maxsize=50)
    def get_repository_structure(self, owner: str, repo: str, max_depth: int = MAX_SCAN_DEPTH) -> Dict[str, Any]:
        """Get repository directory structure for understanding project layout.

        Args:
            owner: Repository owner
            repo: Repository name
            max_depth: Maximum directory depth to traverse (default: 3)

        Returns:
            Dict with directory structure and key files
        """
        cache_key = f"repo_structure:{owner}/{repo}:{max_depth}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            default_branch = repository.default_branch

            # Get tree (directory structure) from default branch
            tree = repository.get_git_tree(default_branch, recursive=True)

            # Organize into directory structure
            directories = set()
            files_by_type = {
                "config": [],
                "source": [],
                "tests": [],
                "docs": []
            }

            for item in tree.tree[:MAX_FILES_TO_SCAN]:  # Limit files to scan
                path = item.path
                depth = path.count('/')

                if depth > max_depth:
                    continue

                if item.type == "tree":
                    directories.add(path)
                elif item.type == "blob":
                    filename = path.split('/')[-1].lower()

                    # Config files
                    if filename in ['package.json', 'requirements.txt', 'pyproject.toml', 'tsconfig.json',
                                   'webpack.config.js', 'vite.config.js', 'jest.config.js',
                                   'cargo.toml', 'go.mod', 'pom.xml', 'build.gradle']:
                        files_by_type["config"].append(path)
                    # Test files
                    elif 'test' in path.lower() or filename.startswith('test_'):
                        files_by_type["tests"].append(path)
                    # Documentation
                    elif filename.endswith('.md'):
                        files_by_type["docs"].append(path)
                    # Source files
                    elif any(path.endswith(ext) for ext in ['.py', '.ts', '.js', '.jsx', '.tsx', '.cs']):
                        files_by_type["source"].append(path)

            top_dirs = sorted([d for d in directories if '/' not in d])

            result = {
                "top_level_directories": top_dirs[:20],
                "config_files": files_by_type["config"][:10],
                "test_directories": [d for d in directories if 'test' in d.lower()][:5],
                "has_tests": len(files_by_type["tests"]) > 0,
                "has_docs": len(files_by_type["docs"]) > 0
            }

            _set_cached(cache_key, result, ttl=3600)
            return result

        except Exception as e:
            logger.warning(f"Failed to get repository structure: {e}")
            return {
                "top_level_directories": [],
                "config_files": [],
                "test_directories": [],
                "has_tests": False,
                "has_docs": False
            }

    @lru_cache(maxsize=200)
    def get_file_content(self, owner: str, repo: str, file_path: str, max_size: int = MAX_FILE_CONTENT_SIZE) -> Optional[str]:
        """Get content of a specific file from the repository.

        Args:
            owner: Repository owner
            repo: Repository name
            file_path: Path to file in repository
            max_size: Maximum file size in bytes (default: 10KB)

        Returns:
            File content as string, or None if not found or too large
        """
        cache_key = f"file_content:{owner}/{repo}:{file_path}"
        cached = _get_cached(cache_key)
        if cached:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            file_content = repository.get_contents(file_path)

            if isinstance(file_content, list):
                return None  # It's a directory

            if file_content.size > max_size:
                return None  # Too large

            content = file_content.decoded_content.decode('utf-8')
            _set_cached(cache_key, content, ttl=3600)
            return content

        except Exception as e:
            logger.debug(f"Failed to get file {file_path}: {e}")
            return None

    def validate_labels(self, owner: str, repo: str, proposed_labels: List[str]) -> Dict[str, dict]:
        """Validate proposed labels against repository labels.

        Returns:
            Dict with validation results:
            {
                'valid': List[str] - labels that exist in repo
                'invalid': List[str] - labels that don't exist
                'suggestions': Dict[str, List[str]] - suggested alternatives for invalid labels
            }
        """
        repo_labels = self.get_repository_labels(owner, repo)
        repo_label_names = set(repo_labels.keys())

        valid_labels = []
        invalid_labels = []
        suggestions = {}

        for label in proposed_labels:
            if label in repo_label_names:
                valid_labels.append(label)
            else:
                invalid_labels.append(label)
                # Find similar labels
                close_matches = get_close_matches(
                    label,
                    repo_label_names,
                    n=3,
                    cutoff=0.6
                )
                if close_matches:
                    suggestions[label] = close_matches

        return {
            'valid': valid_labels,
            'invalid': invalid_labels,
            'suggestions': suggestions
        }

    def get_triage_status(self, issue, repo_labels: Dict[str, dict] = None) -> dict:
        """Get detailed triage status for an issue.

        Args:
            issue: GitHub issue object
            repo_labels: Repository labels dict (from get_repository_labels)

        Returns:
            dict with keys:
            - needs_labeling: bool - True if issue needs priority/triage labels
            - needs_assignment: bool - True if issue needs engineer assignment
            - has_bot_triage: bool - True if bot has already triaged
        """
        status = {
            'needs_labeling': True,
            'needs_assignment': True,
            'has_bot_triage': False
        }

        # Check for triage labels
        issue_labels = [label.name.lower() for label in issue.labels]

        # If repo_labels provided, use actual repository labels for validation
        if repo_labels:
            repo_triage_labels = []

            # Find actual triage labels in the repository
            for label_name in repo_labels.keys():
                label_lower = label_name.lower()
                if any(pattern in label_lower for pattern in ['p0', 'p1', 'p2', 'p3', 'p4', 'priority', 'triage', 'triaged']):
                    repo_triage_labels.append(label_lower)

            # Check if issue has any of the actual triage labels
            for label in issue_labels:
                if label in repo_triage_labels:
                    status['needs_labeling'] = False
                    break
        else:
            # Fallback to pattern matching if no repo labels provided
            for label in issue_labels:
                if any(pattern in label for pattern in ['p0', 'p1', 'p2', 'p3', 'p4', 'priority', 'triage', 'triaged']):
                    status['needs_labeling'] = False
                    break

        # Check if issue has been assigned to an engineer
        if issue.assignees:
            status['needs_assignment'] = False

        # Check for bot comments indicating triage
        try:
            comments = list(issue.get_comments())
            for comment in comments:
                if any(bot in comment.user.login for bot in TRIAGE_BOT_USERS):
                    if 'triage' in comment.body.lower() or 'priority' in comment.body.lower():
                        status['has_bot_triage'] = True
                        break
        except GithubException as e:
            logger.debug(f"Could not fetch comments for issue #{issue.number}: {e}")

        return status

    def needs_triage(self, issue, repo_labels: Dict[str, dict] = None) -> bool:
        """Check if an issue needs any triage action."""
        status = self.get_triage_status(issue, repo_labels)
        return status['needs_labeling'] or status['needs_assignment']

    def is_fully_triaged(self, issue, repo_labels: Dict[str, dict] = None) -> bool:
        """Check if an issue is completely triaged (has both labels and assignment)."""
        status = self.get_triage_status(issue, repo_labels)
        return not status['needs_labeling'] and not status['needs_assignment']

    def filter_untriaged_issues(self, issues: List, repo_labels: Dict[str, dict] = None) -> List:
        """Filter issues that still need triage actions.

        Returns issues that need either labeling or assignment.
        Only filters out issues that are completely triaged.
        """
        issues_needing_triage = []
        for issue in issues:
            # Skip pull requests (they have different triage needs)
            if hasattr(issue, 'pull_request') and issue.pull_request:
                continue

            # Get detailed triage status
            triage_status = self.get_triage_status(issue, repo_labels)

            # Keep issues that need any triage action
            if triage_status['needs_labeling'] or triage_status['needs_assignment']:
                # Add triage status to issue for downstream processing
                issue._triage_status = triage_status
                issues_needing_triage.append(issue)

        return issues_needing_triage

    def get_new_untriaged_issues(self, owner: str, repo: str, since: datetime) -> List:
        """Get only new, untriaged issues since a given date."""
        try:
            repository = self._get_repo(owner, repo)
            # Get open issues created since the specified date
            issues = repository.get_issues(
                state="open",
                since=since,
                sort="created",
                direction="desc"
            )
            all_issues = list(issues)

            # Get repository labels once for efficient triage detection
            repo_labels = self.get_repository_labels(owner, repo)

            # Filter to only include untriaged issues
            return self.filter_untriaged_issues(all_issues, repo_labels)

        except GithubException:
            return []

    @lru_cache(maxsize=200)
    def get_file_contributors(
        self,
        owner: str,
        repo: str,
        file_path: str,
        months: int = 6
    ) -> Dict[str, int]:
        """
        Get contributors to a specific file over the last N months.

        Args:
            owner: Repository owner
            repo: Repository name
            file_path: Path to the file (e.g., 'src/main.py')
            months: Number of months to look back (default: 6)

        Returns:
            Dict mapping GitHub login to commit count
            Example: {'mengyimicro': 15, 'joratz': 3}
        """
        cache_key = f"file_contributors:{owner}:{repo}:{file_path}:{months}"
        cached = _get_cached(cache_key)
        if cached is not None:
            return cached

        try:
            repository = self._get_repo(owner, repo)
            since_date = datetime.now(timezone.utc) - timedelta(days=months * 30)

            # Get commits for this specific file
            commits = repository.get_commits(
                path=file_path,
                since=since_date
            )

            # Count commits per author
            contributors = {}
            for commit in commits:
                if commit.author and commit.author.login:
                    login = commit.author.login
                    contributors[login] = contributors.get(login, 0) + 1

            logger.info(f"Found {len(contributors)} contributors to {file_path} in last {months} months")
            _set_cached(cache_key, contributors, ttl=3600)  # Cache for 1 hour
            return contributors

        except GithubException as e:
            logger.warning(f"Could not fetch contributors for {file_path}: {e}")
            return {}

    def extract_file_paths_from_text(self, text: str) -> List[str]:
        """
        Extract potential file paths from issue text.

        Looks for patterns like:
        - src/services/github_service.py
        - autoTriage/models/issue_classification.py
        - backend/config/settings.json

        Args:
            text: Issue title or body text

        Returns:
            List of potential file paths
        """
        import re

        if not text:
            return []

        # Pattern to match file paths with extensions
        # Matches: word/word/file.ext or word/file.ext
        pattern = r'\b[a-zA-Z0-9_\-./]+/[a-zA-Z0-9_\-./]+\.[a-zA-Z0-9]{1,10}\b'

        matches = re.findall(pattern, text)

        # Filter out URLs (http://, https://)
        file_paths = [
            match for match in matches
            if not match.startswith(('http://', 'https://'))
        ]

        # Deduplicate while preserving order
        seen = set()
        unique_paths = []
        for path in file_paths:
            if path not in seen:
                seen.add(path)
                unique_paths.append(path)

        logger.debug(f"Extracted {len(unique_paths)} file paths from text")
        return unique_paths[:MAX_FILE_PATHS_TO_EXTRACT]  # Limit file paths extracted

    def get_contributors_for_issue(
        self,
        owner: str,
        repo: str,
        issue_title: str,
        issue_body: str
    ) -> Dict[str, Dict[str, int]]:
        """
        Analyze an issue and get contributor information for mentioned files.

        Args:
            owner: Repository owner
            repo: Repository name
            issue_title: Issue title
            issue_body: Issue body

        Returns:
            Dict mapping file paths to contributor info
            Example:
            {
                'src/github_service.py': {'mengyimicro': 15, 'joratz': 3},
                'config/prompts.yaml': {'sellakumaran': 8}
            }
        """
        # Extract file paths from issue text
        combined_text = f"{issue_title}\n{issue_body or ''}"
        file_paths = self.extract_file_paths_from_text(combined_text)

        if not file_paths:
            logger.debug("No file paths found in issue text")
            return {}

        # Get contributors for each file
        file_contributors = {}
        for file_path in file_paths:
            contributors = self.get_file_contributors(owner, repo, file_path)
            if contributors:
                file_contributors[file_path] = contributors

        return file_contributors
