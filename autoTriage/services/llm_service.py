# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
LLM Service - GitHub Models / Azure OpenAI integration
"""
import os
import json
import logging
from typing import Dict, Any, List, Optional
from openai import OpenAI, AzureOpenAI
from models.team_config import PriorityRules, CopilotFixableConfig
from services.prompt_loader import get_prompt_loader

# Display limits
MAX_CONTRIBUTORS_TO_SHOW = 3  # Maximum contributors to show per file in commit history


class LlmService:
    """Service for LLM-based classification and summarization."""

    def __init__(self):
        self.prompts = get_prompt_loader()

        # Check if using Azure OpenAI
        azure_endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT")
        azure_key = os.environ.get("AZURE_OPENAI_API_KEY")
        azure_deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT")

        # Initialize OpenAI client
        self._client: Optional[OpenAI] = None

        if azure_endpoint and azure_key and azure_deployment:
            # Use Azure OpenAI
            logging.info("Initializing Azure OpenAI client")
            self.model = azure_deployment
            self._client = AzureOpenAI(
                api_key=azure_key,
                api_version=os.environ.get("AZURE_OPENAI_API_VERSION", "2024-02-01"),
                azure_endpoint=azure_endpoint
            )
        else:
            # Use GitHub Models or standard OpenAI
            logging.info("Initializing GitHub Models/OpenAI client")
            self.endpoint = os.environ.get("GITHUB_MODELS_ENDPOINT", "https://models.inference.ai.azure.com")
            self.api_key = os.environ.get("GITHUB_TOKEN", os.environ.get("GITHUB_MODELS_KEY", ""))
            self.model = os.environ.get("GITHUB_MODELS_MODEL", "gpt-4o-mini")

            if self.api_key:
                self._client = OpenAI(
                    base_url=self.endpoint,
                    api_key=self.api_key
                )

    def _call_llm(self, system_prompt: str, user_prompt: str, json_response: bool = False) -> Optional[str]:
        """Make a call to the LLM and return the response."""
        if not self._client:
            logging.warning("LLM client not initialized - API key missing")
            return None

        try:
            # Build kwargs dynamically to avoid passing None for response_format
            kwargs = {
                "model": self.model,
                "messages": [
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt}
                ],
                "temperature": 0.3,
                "max_tokens": 1000
            }

            # Only add response_format when json_response is True
            if json_response:
                kwargs["response_format"] = {"type": "json_object"}

            response = self._client.chat.completions.create(**kwargs)
            return response.choices[0].message.content
        except Exception as e:
            logging.error(f"LLM call failed: {e}")
            return None

    def classify_issue(self, title: str, body: str, rules: PriorityRules) -> Dict[str, Any]:
        """
        Classify an issue by type and priority using AI.
        Falls back to keyword matching if LLM unavailable.

        Returns:
            Dict with keys: type, priority, type_rationale, priority_rationale, confidence
        """
        combined = f"{title} {body}".lower()

        # Get prompts from config (with fallback defaults)
        default_system = """You are an issue classifier for a software development team.
Classify the issue and respond in JSON format with these fields:
- type: one of "bug", "feature", "documentation", "question"
- priority: one of "P1" (critical), "P2" (high), "P3" (medium), "P4" (low)
- type_rationale: brief explanation of why you chose this type (1 sentence)
- priority_rationale: brief explanation of why you chose this priority (1 sentence)
- confidence: your confidence in this classification (0.0 to 1.0)"""

        system_prompt = self.prompts.get("classify_issue_system", default_system)

        user_prompt = self.prompts.format(
            "classify_issue_user",
            default=f"Classify this issue:\nTitle: {title}\nBody: {body}",
            title=title,
            body=body,
            p1_keywords=', '.join(rules.p1_keywords),
            p2_keywords=', '.join(rules.p2_keywords),
            p3_keywords=', '.join(rules.p3_keywords),
            p4_keywords=', '.join(rules.p4_keywords)
        )

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                parsed = json.loads(result)
                # Ensure all expected fields are present
                return {
                    "type": parsed.get("type", "bug"),
                    "priority": parsed.get("priority", "P3"),
                    "type_rationale": parsed.get("type_rationale", ""),
                    "priority_rationale": parsed.get("priority_rationale", ""),
                    "confidence": parsed.get("confidence", 0.8)
                }
            except json.JSONDecodeError:
                pass

        # Fallback to keyword matching
        issue_type = self._determine_type(combined)
        priority = self._determine_priority(combined, rules)

        return {
            "type": issue_type,
            "priority": priority,
            "type_rationale": f"Classified as '{issue_type}' based on keyword matching",
            "priority_rationale": f"Assigned {priority} based on keyword matching",
            "confidence": 0.5
        }

    def generate_summary(self, content: str) -> str:
        """
        Generate a summary of the given content using AI.
        """
        default_system = "You are a concise technical writer. Summarize the content in 1-2 sentences."
        system_prompt = self.prompts.get("summary_system", default_system)
        result = self._call_llm(system_prompt, content)
        return result if result else f"Summary of {len(content)} characters"

    def analyze_daily_digest(
        self,
        new_issues: List[Dict],
        open_prs: List[Dict],
        merged_prs: List[Dict],
        stale_prs: List[Dict],
        ci_failures: int,
        decision_items: List[str]
    ) -> Dict[str, Any]:
        """
        Use AI to analyze daily activity and provide intelligent recommendations.

        Returns:
            Dict with keys: standup_needed, standup_reason, summary, highlights, recommendations
        """

        # Get prompts from config (with fallback defaults)
        default_system = """You are a smart engineering team assistant analyzing daily repository activity.
Your job is to determine if a synchronous standup meeting is needed, or if async updates are sufficient.

Respond in JSON format with:
- standup_needed: boolean - true only if there are items requiring real-time discussion
- standup_reason: string - A clear, actionable explanation (2-3 sentences). Explain WHY a meeting is needed and what will be discussed, or WHY async is fine.
- summary: string - 2-3 sentence natural language summary of overnight activity
- tone: string - one of "quiet", "normal", "busy", "urgent"
- top_priorities: array of strings - max 3 items team should focus on today

Guidelines for standup_needed:
- TRUE if: unassigned issues need owners, unassigned P1/P2 bugs, security issues, blocked PRs needing decisions, CI broken on main
- FALSE if: only documentation changes, routine PR reviews, all issues assigned, minor bugs

IMPORTANT: If there are unassigned issues, always mention them in standup_reason with specific count."""

        system_prompt = self.prompts.get("daily_digest_system", default_system)

        # Build user prompt from config or use default
        decision_items_text = '\n'.join(decision_items) if decision_items else "None"

        user_prompt = self.prompts.format(
            "daily_digest_user",
            default=f"Analyze daily activity: {len(new_issues)} issues, {len(open_prs)} PRs, {ci_failures} CI failures",
            new_issues_count=len(new_issues),
            new_issues_json=json.dumps(new_issues[:5], indent=2) if new_issues else "None",
            open_prs_count=len(open_prs),
            open_prs_json=json.dumps(open_prs[:5], indent=2) if open_prs else "None",
            merged_prs_count=len(merged_prs),
            merged_prs_json=json.dumps(merged_prs[:3], indent=2) if merged_prs else "None",
            stale_prs_count=len(stale_prs),
            stale_prs_json=json.dumps(stale_prs, indent=2) if stale_prs else "None",
            ci_failures=ci_failures,
            decision_items=decision_items_text
        )

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                return json.loads(result)
            except json.JSONDecodeError:
                pass

        # Fallback to rule-based logic
        standup_needed = len(decision_items) > 0
        return {
            "standup_needed": standup_needed,
            "standup_reason": f"{len(decision_items)} item(s) require discussion" if standup_needed else "No action items requiring discussion",
            "summary": f"Overnight: {len(new_issues)} new issues, {len(merged_prs)} PRs merged, {len(open_prs)} PRs open.",
            "tone": "urgent" if ci_failures > 0 else "normal" if decision_items else "quiet",
            "top_priorities": decision_items[:3] if decision_items else ["Continue current work"]
        }

    def analyze_weekly_planning(
        self,
        closed_issues: List[Dict],
        merged_prs: List[Dict],
        open_issues: List[Dict],
        open_prs: List[Dict],
        decision_items: List[Dict],
        slipped_issues: List[Dict],
    ) -> Dict[str, Any]:
        """
        Use AI to analyze weekly activity and provide planning recommendations.

        Returns:
            Dict with keys: meeting_needed, meeting_reason, suggested_duration,
                           summary, highlights, risks, recommendations
        """
        system_prompt = """You are a smart engineering team assistant summarizing weekly repository activity.
Your job is to provide a concise, actionable summary of the week's progress.

Respond in JSON format with:
- summary: string - 2-3 sentence natural language summary of the week's activity
- velocity_assessment: string - one of "ahead", "on_track", "behind", "blocked"
- highlights: array of strings - max 3 positive accomplishments written professionally
- attention_items: array of strings - max 3 items that may need attention (can be empty if none)
- recommendations: array of strings - max 3 actionable next steps

Guidelines for highlights:
- Write each highlight as a complete, professional sentence
- Combine metrics with context to tell a story
- Use the labels/areas to mention WHAT general areas saw progress (e.g., "authentication", "CLI", "infrastructure")
- Include quantitative achievements (issues closed, PRs merged, lines of code)
- Sound like a professional status update, not a list of bug titles
- You can mention "no slipped work" ONLY if the Slipped Work section shows "None"
- Examples:
  - "Productive week with 8 issues resolved and 8 PRs merged, demonstrating strong team velocity"
  - "Significant progress in the authentication and CLI areas based on completed work"
  - "Codebase grew by over 1,000 lines with focused improvements across multiple components"

Do NOT:
- List individual bug titles or detailed technical descriptions
- Mention "blockers" (we don't track that)
- Make up information not in the data provided"""

        # Calculate additional metrics for richer context
        total_additions = sum(pr.get("additions", 0) or 0 for pr in merged_prs)
        total_deletions = sum(pr.get("deletions", 0) or 0 for pr in merged_prs)
        
        # Extract labels/themes
        all_labels = []
        for issue in closed_issues:
            all_labels.extend(issue.get("labels", []))
        for pr in merged_prs:
            all_labels.extend(pr.get("labels", []))
        label_counts = {}
        for label in all_labels:
            label_counts[label] = label_counts.get(label, 0) + 1
        top_labels = sorted(label_counts.items(), key=lambda x: x[1], reverse=True)[:5]

        # Prepare context for LLM
        user_prompt = f"""Analyze this week's repository activity:

## Completed Work
- Issues closed: {len(closed_issues)}
- PRs merged: {len(merged_prs)}
- Lines of code: +{total_additions:,} / -{total_deletions:,} (net: {total_additions - total_deletions:+,})

## Top Labels/Areas
{', '.join([f"{label} ({count})" for label, count in top_labels]) if top_labels else "None tagged"}

## In Progress
- Open issues: {len(open_issues)}
- Open PRs: {len(open_prs)}

## Items Needing Attention ({len(decision_items)} total)
{json.dumps(decision_items[:5], indent=2) if decision_items else "None"}

## Slipped Work
{json.dumps(slipped_issues[:3], indent=2) if slipped_issues else "None"}

## Closed Issues (titles reveal what was fixed)
{json.dumps([{"title": i.get("title"), "labels": i.get("labels", [])} for i in closed_issues[:6]], indent=2) if closed_issues else "None"}

## Merged PRs (titles reveal what was built)
{json.dumps([{"title": p.get("title"), "additions": p.get("additions"), "deletions": p.get("deletions")} for p in merged_prs[:6]], indent=2) if merged_prs else "None"}

Provide a specific summary based on what was actually worked on. Extract themes from the titles."""

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                parsed = json.loads(result)
                # Ensure consistent field names
                if "risks" in parsed and "attention_items" not in parsed:
                    parsed["attention_items"] = parsed.pop("risks")
                return parsed
            except json.JSONDecodeError:
                pass

        # Fallback to rule-based summary
        if len(closed_issues) == 0 and len(merged_prs) == 0:
            summary = "Quiet week with no completed issues or merged PRs."
            velocity = "on_track"
        else:
            summary = f"This week: {len(closed_issues)} issues closed, {len(merged_prs)} PRs merged."
            velocity = "on_track"

        if len(decision_items) > 0:
            summary += f" {len(decision_items)} items may need attention."
            velocity = "behind" if len(decision_items) > 3 else "on_track"

        highlights = []
        if len(closed_issues) > 0:
            highlights.append(f"Closed {len(closed_issues)} issues")
        if len(merged_prs) > 0:
            highlights.append(f"Merged {len(merged_prs)} PRs")

        return {
            "summary": summary,
            "velocity_assessment": velocity,
            "highlights": highlights if highlights else ["Steady progress"],
            "attention_items": [item.get("reason", "Unknown") for item in decision_items[:3]] if decision_items else [],
            "recommendations": ["Review attention items"] if decision_items else ["Continue current momentum"]
        }

    def is_copilot_fixable(
        self,
        title: str,
        body: str,
        config: CopilotFixableConfig,
        issue_type: str = "bug",
        priority: str = "P3"
    ) -> Dict[str, Any]:
        """
        Determine if an issue can be fixed by Copilot using LLM analysis.

        Args:
            title: Issue title
            body: Issue body/description
            config: CopilotFixableConfig with enabled flag and criteria
            issue_type: Type of issue (bug, feature, documentation, question)
            priority: Priority level (P1, P2, P3, P4)

        Returns:
            Dict with keys: is_copilot_fixable, confidence, reasoning, suggested_approach
        """
        default_result = {
            "is_copilot_fixable": False,
            "confidence": 0.0,
            "reasoning": "Copilot coding agent is disabled in configuration",
            "suggested_approach": ""
        }

        if not config.enabled:
            return default_result

        # Get prompts from config
        default_system = """You are an intelligent issue assessment assistant.
Determine if an issue can be fixed by GitHub Copilot (an AI coding agent).
Respond in JSON format with: is_copilot_fixable, confidence, reasoning, suggested_approach."""

        system_prompt = self.prompts.get("copilot_fixable_system", default_system)

        criteria_text = ", ".join(config.criteria) if config.criteria else "typo, simple fix, documentation"

        user_prompt = self.prompts.format(
            "copilot_fixable_user",
            default=f"Assess if this issue can be fixed by Copilot: {title}",
            title=title,
            body=body[:2000] if body else "No description provided",
            issue_type=issue_type,
            priority=priority,
            criteria=criteria_text
        )

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                parsed = json.loads(result)
                return {
                    "is_copilot_fixable": parsed.get("is_copilot_fixable", False),
                    "confidence": parsed.get("confidence", 0.5),
                    "reasoning": parsed.get("reasoning", "LLM assessment completed"),
                    "suggested_approach": parsed.get("suggested_approach", "")
                }
            except json.JSONDecodeError:
                logging.warning("Failed to parse LLM copilot-fixable response")

        # Fallback to keyword matching
        combined = f"{title} {body}".lower()
        for criterion in config.criteria:
            if criterion.lower() in combined:
                return {
                    "is_copilot_fixable": True,
                    "confidence": 0.5,
                    "reasoning": f"Matched keyword criterion: '{criterion}'",
                    "suggested_approach": ""
                }

        return {
            "is_copilot_fixable": False,
            "confidence": 0.5,
            "reasoning": "No matching criteria found via keyword matching (LLM unavailable)",
            "suggested_approach": ""
        }

    def generate_fix_suggestions(
        self,
        title: str,
        body: str,
        issue_type: str = "bug",
        priority: str = "P3",
        repo_context: Optional[Dict[str, Any]] = None
    ) -> List[str]:
        """
        Generate actionable fix suggestions for an issue using LLM analysis.

        Args:
            title: Issue title
            body: Issue body/description
            issue_type: Type of issue (bug, feature, documentation, question)
            priority: Priority level (P1, P2, P3, P4)
            repo_context: Optional repository context (languages, topics, README, etc.)

        Returns:
            List of 3-5 actionable suggestions
        """
        # Get prompts from config
        default_system = """You are an expert software engineer helping to provide actionable fix suggestions.
Provide 3-5 specific, practical suggestions in JSON format with: suggestions (array of strings)."""

        system_prompt = self.prompts.get("fix_suggestions_system", default_system)

        # Prepare repository context for prompt
        if repo_context:
            repo_name = repo_context.get("full_name", "Unknown")
            repo_description = repo_context.get("description", "No description available")
            primary_language = repo_context.get("primary_language", "Unknown")
            languages = ", ".join(repo_context.get("languages", [])) or "Unknown"
            topics = ", ".join(repo_context.get("topics", [])) or "None"
            readme_excerpt = repo_context.get("readme_excerpt", "No README available")[:1000]

            # Structure information
            structure = repo_context.get("structure", {})
            top_directories = ", ".join(structure.get("top_level_directories", [])) or "Unknown"
            config_files = ", ".join(structure.get("config_files", [])) or "None"
            has_tests = "Yes" if structure.get("has_tests", False) else "No"
            test_dirs = ", ".join(structure.get("test_directories", [])) or "None"

            # Config file contents
            config_contents_dict = repo_context.get("config_files_content", {})
            config_contents_str = ""
            for filename, content in config_contents_dict.items():
                config_contents_str += f"\n{filename}:\n```\n{content}\n```\n"
            if not config_contents_str:
                config_contents_str = "No config files available"
        else:
            repo_name = "Unknown"
            repo_description = "No description available"
            primary_language = "Unknown"
            languages = "Unknown"
            topics = "None"
            readme_excerpt = "No README available"
            top_directories = "Unknown"
            config_files = "None"
            has_tests = "Unknown"
            test_dirs = "None"
            config_contents_str = "No config files available"

        user_prompt = self.prompts.format(
            "fix_suggestions_user",
            default=f"Generate fix suggestions for: {title}",
            title=title,
            body=body[:2000] if body else "No description provided",
            issue_type=issue_type,
            priority=priority,
            repo_name=repo_name,
            repo_description=repo_description,
            primary_language=primary_language,
            languages=languages,
            topics=topics,
            readme_excerpt=readme_excerpt,
            top_directories=top_directories,
            config_files=config_files,
            has_tests=has_tests,
            test_dirs=test_dirs,
            config_contents=config_contents_str
        )

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                parsed = json.loads(result)
                suggestions = parsed.get("suggestions", [])
                if suggestions and isinstance(suggestions, list):
                    return suggestions[:5]  # Limit to 5 suggestions
            except json.JSONDecodeError:
                logging.warning("Failed to parse LLM fix suggestions response")

        # Fallback generic suggestions based on issue type
        if issue_type == "bug":
            return [
                "Review the error logs and stack traces to identify the root cause",
                "Check recent code changes that might have introduced the issue",
                "Add unit tests to reproduce the bug and verify the fix"
            ]
        elif issue_type == "feature":
            return [
                "Break down the feature into smaller, manageable tasks",
                "Create a design document outlining the implementation approach",
                "Consider edge cases and add appropriate error handling"
            ]
        elif issue_type == "documentation":
            return [
                "Identify which sections of documentation need updates",
                "Review similar documentation for consistency in style and format",
                "Include code examples to illustrate key concepts"
            ]
        else:  # question
            return [
                "Check existing documentation and codebase for related information",
                "Search for similar issues or discussions in the repository",
                "Consult with team members who have expertise in the relevant area"
            ]

    def _determine_type(self, content: str) -> str:
        """Determine issue type based on keywords."""
        if any(kw in content for kw in ["bug", "error", "broken", "crash", "fail"]):
            return "bug"
        if any(kw in content for kw in ["feature", "enhancement", "request", "add"]):
            return "feature"
        if any(kw in content for kw in ["doc", "typo", "readme", "documentation"]):
            return "documentation"
        if any(kw in content for kw in ["question", "how to", "help", "?"]):
            return "question"
        return "bug"  # Default

    def _determine_priority(self, content: str, rules: PriorityRules) -> str:
        """Determine priority based on configured rules."""
        for keyword in rules.p1_keywords:
            if keyword.lower() in content:
                return "P1"

        for keyword in rules.p2_keywords:
            if keyword.lower() in content:
                return "P2"

        for keyword in rules.p3_keywords:
            if keyword.lower() in content:
                return "P3"

        for keyword in rules.p4_keywords:
            if keyword.lower() in content:
                return "P4"

        return "P3"  # Default

    def generate_engineer_summary(
        self,
        engineer: str,
        closed_issues: List[Dict],
        merged_prs: List[Dict]
    ) -> str:
        """
        Generate a factual, professional summary of an engineer's contributions.

        Args:
            engineer: GitHub username
            closed_issues: List of issues closed by this engineer
            merged_prs: List of PRs merged by this engineer

        Returns:
            A brief, factual summary string
        """
        if not closed_issues and not merged_prs:
            return "Working on upcoming deliverables."

        system_prompt = """You are a technical writer summarizing engineering work.
Write a single sentence (max 40 words) that:
- States what work was completed factually
- Focuses on the technical areas or features addressed
- Uses neutral, professional language
- Does NOT praise, compliment, or comment on the person
- Does NOT use words like "significant", "valuable", "great", "excellent", "impressive"
- Does NOT mention the engineer's name or use @mentions

Just describe the work done. Example: "Addressed authentication issues and improved secret handling with setup command idempotency."
Respond with just the summary sentence, no JSON."""

        # Build context
        issues_summary = ""
        if closed_issues:
            issue_titles = [i.get("title", "issue") for i in closed_issues[:5]]
            issues_summary = f"Closed issues: {', '.join(issue_titles)}"

        prs_summary = ""
        if merged_prs:
            pr_titles = [p.get("title", "PR") for p in merged_prs[:5]]
            prs_summary = f"Merged PRs: {', '.join(pr_titles)}"

        user_prompt = f"""Summarize this work factually:

{issues_summary}
{prs_summary}

Remember: No praise, no comments on the person. Just describe the technical work completed."""

        result = self._call_llm(system_prompt, user_prompt, json_response=False)
        if result:
            # Clean up the response
            return result.strip().strip('"')

        # Fallback to simple factual summary
        activities = []
        if closed_issues:
            activities.append("issue resolution")
        if merged_prs:
            activities.append("code changes")

        return f"Work included {' and '.join(activities)}."

    def select_assignee(
        self,
        title: str,
        body: str,
        issue_type: str,
        priority: str,
        team_members: List[Dict[str, Any]],
        file_contributors: Optional[Dict[str, Dict[str, int]]] = None
    ) -> Dict[str, Any]:
        """
        Select the best engineer to work on an issue using AI.

        Args:
            title: Issue title
            body: Issue body/description
            issue_type: Type of issue (bug, feature, documentation, question)
            priority: Priority level (P1, P2, P3, P4)
            team_members: List of team members with name, login, role, and expertise
            file_contributors: Optional dict mapping file paths to contributors and commit counts

        Returns:
            Dict with keys: assignee (login), rationale, confidence
        """
        if not team_members:
            return {
                "assignee": None,
                "rationale": "No team members available for assignment",
                "confidence": 0.0
            }

        # Get prompts from config
        default_system = """You are an intelligent issue assignment assistant.
Select the best engineer based on issue content and engineer expertise.
Respond in JSON format with: assignee, rationale, confidence."""

        system_prompt = self.prompts.get("select_assignee_system", default_system)

        # Format engineers for the prompt
        engineers_info = []
        for member in team_members:
            engineer = {
                "login": member.get("login", member.get("name", "unknown")),
                "name": member.get("name", ""),
                "role": member.get("role", "Developer"),
                "expertise": member.get("expertise", []),
                "contributions": member.get("contributions", 0)
            }
            engineers_info.append(engineer)

        # Format file contributor information
        contributor_context = "No specific files mentioned in the issue."
        if file_contributors:
            contributor_lines = []
            for file_path, contributors in file_contributors.items():
                # Sort contributors by commit count
                sorted_contributors = sorted(contributors.items(), key=lambda x: x[1], reverse=True)
                contributor_str = ", ".join([f"{login} ({count} commits)" for login, count in sorted_contributors[:MAX_CONTRIBUTORS_TO_SHOW]])
                contributor_lines.append(f"  - {file_path}: {contributor_str}")

            if contributor_lines:
                contributor_context = "Recent contributors to files mentioned in this issue:\n" + "\n".join(contributor_lines)

        user_prompt = self.prompts.format(
            "select_assignee_user",
            default=f"Select an assignee for: {title}",
            title=title,
            body=body[:2000] if body else "No description provided",  # Limit body length
            issue_type=issue_type,
            priority=priority,
            engineers_json=json.dumps(engineers_info, indent=2),
            file_contributor_context=contributor_context
        )

        result = self._call_llm(system_prompt, user_prompt, json_response=True)
        if result:
            try:
                parsed = json.loads(result)
                # Validate the assignee is in our team (match by login or name)
                assignee = parsed.get("assignee", "")

                # Try to match by login first
                for member in team_members:
                    if member.get("login", "").lower() == assignee.lower():
                        return {
                            "assignee": member.get("login"),
                            "rationale": parsed.get("rationale", "Selected by AI based on expertise match"),
                            "confidence": parsed.get("confidence", 0.8)
                        }

                # If not found, try to match by name
                for member in team_members:
                    if member.get("name", "").lower() == assignee.lower():
                        logging.info(f"LLM returned name '{assignee}', mapped to login '{member.get('login')}'")
                        return {
                            "assignee": member.get("login"),
                            "rationale": parsed.get("rationale", "Selected by AI based on expertise match"),
                            "confidence": parsed.get("confidence", 0.8)
                        }

                logging.warning(f"LLM suggested invalid assignee: {assignee}")
            except json.JSONDecodeError:
                logging.warning("Failed to parse LLM assignee selection response")

        # Fallback: select based on role matching
        return self._fallback_assignee_selection(issue_type, priority, team_members)

    def _fallback_assignee_selection(
        self,
        issue_type: str,
        priority: str,
        team_members: List[Dict[str, Any]]
    ) -> Dict[str, Any]:
        """Fallback assignee selection using rule-based matching."""
        # For P1/P2, prefer tech lead
        if priority in ["P1", "P2"]:
            for member in team_members:
                if "lead" in member.get("role", "").lower():
                    return {
                        "assignee": member.get("login"),
                        "rationale": f"High priority ({priority}) issue assigned to tech lead",
                        "confidence": 0.6
                    }

        # Match by role keywords
        role_keywords = {
            "bug": ["backend", "full stack", "developer"],
            "feature": ["full stack", "developer", "backend", "frontend"],
            "documentation": ["developer", "writer"],
            "question": ["lead", "senior"]
        }

        keywords = role_keywords.get(issue_type.lower(), ["developer"])
        for keyword in keywords:
            for member in team_members:
                if keyword in member.get("role", "").lower():
                    return {
                        "assignee": member.get("login"),
                        "rationale": f"Assigned to {member.get('role')} based on issue type ({issue_type})",
                        "confidence": 0.5
                    }

        # Default to first available member
        first_member = team_members[0]
        return {
            "assignee": first_member.get("login"),
            "rationale": "Default assignment - no specific expertise match found",
            "confidence": 0.3
        }
