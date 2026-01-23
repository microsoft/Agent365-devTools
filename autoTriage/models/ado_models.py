"""
Azure DevOps data models for work items and configuration.
"""
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional, List


@dataclass
class AdoWorkItem:
    """Represents an Azure DevOps work item."""
    id: int
    title: str
    work_item_type: str  # Bug, Task, User Story, Feature, Epic
    state: str  # New, Active, Resolved, Closed, Removed
    assigned_to: Optional[str]
    created_date: datetime
    changed_date: datetime
    closed_date: Optional[datetime]
    url: str
    priority: Optional[int]
    tags: List[str]
    area_path: str
    iteration_path: str
    parent_id: Optional[int]
    story_points: Optional[float]
    source: str = "ado"  # Identifier for UI to distinguish from GitHub items

    def __post_init__(self):
        """Ensure source is always 'ado'."""
        self.source = "ado"


@dataclass
class AdoConfig:
    """Configuration for Azure DevOps integration."""
    organization: str
    project: str
    enabled: bool = True
    tracked_work_item_types: List[str] = field(
        default_factory=lambda: ["User Story", "Task", "Bug", "Feature"]
    )
    ado_token_env: str = "ADO_PAT_TOKEN"  # Environment variable name for PAT token

    def __post_init__(self):
        """Validate configuration."""
        if not self.organization:
            raise ValueError("ADO organization is required")
        if not self.project:
            raise ValueError("ADO project is required")
