# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Shared sanitisation utilities for autoTriage.

Functions in this module are used by multiple services to sanitise user-supplied
content and exception messages before they reach LLM prompts or log sinks.
Keeping them in a single place prevents subtle divergence between callers.
"""

import re
import logging

logger = logging.getLogger(__name__)

# Maximum input lengths for LLM calls (prevents excessive token usage).
# These constants are defined here so all callers use the same limits.
MAX_ISSUE_TITLE_LENGTH: int = 200
MAX_ISSUE_BODY_LENGTH: int = 2000


def sanitise_exception(e: Exception) -> str:
    """
    Return a safe string representation of an exception with credentials redacted.

    API client libraries (e.g. OpenAI, PyGithub) sometimes embed request
    headers or query parameters — including Authorization values and API keys —
    inside exception messages. Logging those messages verbatim would expose
    secrets in log streams.

    This function:
    - Strips key=value / key: value pairs whose key matches a known credential
      field name.
    - Redacts Bearer tokens (e.g. JWTs in Authorization headers).
    - Redacts OpenAI-style API key prefixes (sk-...).

    Args:
        e: The exception to sanitise.

    Returns:
        A redacted string safe for logging.
    """
    text = str(e)

    # Pass 1: Redact Bearer tokens FIRST so the full token is captured before
    # the key=value pass below strips the header name and leaves the token orphaned.
    # e.g. "Authorization: Bearer eyJ..." → "Authorization: Bearer [REDACTED]"
    text = re.sub(r'(?i)Bearer\s+[A-Za-z0-9\-._~+/]+=*', 'Bearer [REDACTED]', text)

    # Pass 2: Redact key=value / key: value credential pairs.
    text = re.sub(
        r'(?i)(authorization|api.?key|token|secret|password|client.?secret)'
        r'["\']?\s*[:=]\s*["\']?[^\s"\']*',
        r'\1=[REDACTED]',
        text,
    )

    # Pass 3: Redact OpenAI-style API key values (sk-... or sk-proj-...).
    text = re.sub(r'sk-[A-Za-z0-9\-]{20,}', '[REDACTED]', text)

    return text


def sanitise_user_content(text: str, max_length: int = MAX_ISSUE_BODY_LENGTH) -> str:
    """
    Sanitise user-supplied content before LLM submission.

    Defends against prompt injection by:
    - Truncating to a maximum length to bound the attack surface.
    - Stripping ASCII control characters that have no legitimate place in
      issue titles/bodies but can confuse tokenisers or inject hidden
      instructions (keeps \\n and \\t which are meaningful in Markdown).
    - Escaping XML special characters so user content cannot break out of
      the XML tags used in prompts (e.g. <issue_title>...</issue_title>).

    Note: & is escaped before < and > to prevent double-encoding of existing
    XML entities (e.g. &amp; must not become &amp;amp;).

    Args:
        text: Raw user-supplied string (issue title, body, etc.)
        max_length: Maximum number of characters to retain.

    Returns:
        Sanitised string safe for interpolation into an LLM prompt.
    """
    if not text:
        return ""

    # Log and truncate if the content exceeds the limit.
    if len(text) > max_length:
        logger.info("Truncated input from %d to %d characters", len(text), max_length)
        text = text[:max_length]

    # Strip C0 control characters except tab (0x09) and newline (0x0a).
    # Also strips DEL (0x7f) which has no printable meaning.
    text = re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]', '', text)

    # Escape XML special characters in the correct order:
    # & must come first to avoid double-encoding subsequent replacements.
    text = text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')

    return text
