# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Shared constants for the autoTriage package.

All module-level constants that are referenced from more than one file must
live here to prevent accidental divergence between copies.
"""

# Maximum number of per-file contributors to include when building the assignee
# selection context sent to the LLM.  Keeping this small avoids ballooning
# token usage while still surfacing the most relevant code owners.
MAX_CONTRIBUTORS_TO_SHOW: int = 3
