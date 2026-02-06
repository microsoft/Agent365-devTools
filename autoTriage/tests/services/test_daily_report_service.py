# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for daily_report_service.py

Tests the DailyReportService including:
- SLA status calculation
- SLA hours delegation
- Report dataclass creation
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestSLAStatusCalculation:
    """Test SLA status calculation."""

    # Test data: (hours_open, sla_hours, expected_status)
    SLA_STATUS_CASES = [
        (10, 24, "within", "10h open, 24h SLA = within"),
        (20, 24, "warning", "20h open (>80% of 24h) = warning"),
        (25, 24, "breached", "25h open > 24h SLA = breached"),
        (72, 72, "warning", "Exactly at SLA (72h) = warning (not breached)"),
        (73, 72, "breached", "73h > 72h SLA = breached"),
        (58, 72, "warning", "58h open (>80% of 72h) = warning"),
        (40, 72, "within", "40h open (55% of 72h) = within"),
        (50, 72, "within", "50h open (69% of 72h) = within"),
        (60, 72, "warning", "60h open (83% of 72h) = warning"),
        (80, 72, "breached", "80h open > 72h SLA = breached"),
    ]

    @pytest.mark.parametrize(
        "hours_open,sla_hours,expected_status,description",
        SLA_STATUS_CASES,
        ids=[c[3] for c in SLA_STATUS_CASES]
    )
    def test_sla_status_calculation(self, hours_open, sla_hours, expected_status, description):
        """Test SLA status calculation (within, warning, breached)."""
        if hours_open > sla_hours:
            status = "breached"
        elif hours_open > (sla_hours * 0.8):
            status = "warning"
        else:
            status = "within"
        
        assert status == expected_status, f"SLA status mismatch: {description}"

    @pytest.mark.parametrize(
        "hours_open,sla_hours,expected_status",
        [(h, s, e) for h, s, e, _ in SLA_STATUS_CASES[:6]],
        ids=[f"{h}h/{s}h={e}" for h, s, e, _ in SLA_STATUS_CASES[:6]]
    )
    def test_calculate_sla_status_service(self, hours_open, sla_hours, expected_status):
        """Test SLA status calculation in daily report service."""
        from services.daily_report_service import DailyReportService
        
        with patch('services.daily_report_service.GitHubService'), \
             patch('services.daily_report_service.EscalationService'), \
             patch('services.daily_report_service.ConfigParser'):
            service = DailyReportService()
            status = service.calculate_sla_status(hours_open, sla_hours)
            assert status == expected_status


class TestSLAHoursDelegation:
    """Test SLA hours delegation to escalation service."""

    def test_get_sla_hours_delegates_to_escalation_service(self):
        """Test that daily report delegates SLA hours to escalation service."""
        from services.daily_report_service import DailyReportService
        
        mock_escalation = MagicMock()
        mock_escalation.get_sla_hours.return_value = 48
        
        with patch('services.daily_report_service.GitHubService'), \
             patch('services.daily_report_service.EscalationService', return_value=mock_escalation), \
             patch('services.daily_report_service.ConfigParser'):
            service = DailyReportService()
            result = service.get_sla_hours("P1")
            
            assert result == 48
            mock_escalation.get_sla_hours.assert_called_once_with("P1")


class TestReportDataclasses:
    """Test report dataclass creation."""

    def test_issue_report_item_creation(self):
        """Test IssueReportItem dataclass creation."""
        from services.daily_report_service import IssueReportItem
        
        item = IssueReportItem(
            number=123,
            title="Test issue",
            priority="P2",
            labels=["priority-2", "area-security"],
            assignee="testuser",
            hours_open=24.5,
            sla_hours=72,
            sla_status="within",
            url="https://github.com/owner/repo/issues/123"
        )
        
        assert item.number == 123
        assert item.priority == "P2"
        assert item.sla_status == "within"
        assert item.hours_open == 24.5
        assert "priority-2" in item.labels

    def test_daily_report_dataclass(self):
        """Test DailyReport dataclass creation."""
        from services.daily_report_service import DailyReport
        
        report = DailyReport(
            repository="owner/repo",
            generated_at="2026-02-06 10:00 UTC",
            total_open=5,
            sla_compliance_pct=80.0,
            breached_count=1,
            warning_count=1,
            by_priority={"P1": 2, "P2": 3},
            issues=[]
        )
        
        assert report.total_open == 5
        assert report.sla_compliance_pct == 80.0
        assert report.breached_count == 1
        assert report.repository == "owner/repo"

    def test_daily_report_with_issues(self):
        """Test DailyReport with issue items."""
        from services.daily_report_service import DailyReport, IssueReportItem
        
        issues = [
            IssueReportItem(
                number=1, title="Issue 1", priority="P1",
                labels=["priority-1"], assignee="user1", hours_open=10, sla_hours=48,
                sla_status="within", url="https://github.com/o/r/issues/1"
            ),
            IssueReportItem(
                number=2, title="Issue 2", priority="P2",
                labels=["priority-2"], assignee="user2", hours_open=80, sla_hours=72,
                sla_status="breached", url="https://github.com/o/r/issues/2"
            ),
        ]
        
        report = DailyReport(
            repository="owner/repo",
            generated_at="2026-02-06",
            total_open=2,
            sla_compliance_pct=50.0,
            breached_count=1,
            warning_count=0,
            by_priority={"P1": 1, "P2": 1},
            issues=issues
        )
        
        assert len(report.issues) == 2
        assert report.issues[0].number == 1
        assert report.issues[1].sla_status == "breached"
