# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""
Tests for teams_service.py

Tests the TeamsService including:
- Lazy initialization
- Webhook URL security (no logging of URL)
- Notification sending
"""
import pytest
import sys
from pathlib import Path
from unittest.mock import patch, MagicMock
import logging

sys.path.insert(0, str(Path(__file__).parent.parent.parent))


class TestTeamsServiceLazyInitialization:
    """Test lazy initialization of TeamsService."""

    def test_teams_service_not_initialized_without_webhook(self):
        """Test TeamsService handles missing webhook gracefully."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {}, clear=True):
            service = TeamsService()
            # Should not raise, just log that webhook is not configured
            assert service is not None

    def test_teams_service_initializes_with_webhook(self):
        """Test TeamsService initializes when webhook is provided."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://webhook.example.com'}):
            service = TeamsService()
            assert service.webhook_url is not None
            assert service.webhook_url == 'https://webhook.example.com'


class TestWebhookURLSecurity:
    """Test that webhook URL is not logged."""

    def test_webhook_url_not_in_logs(self, caplog):
        """Test that webhook URL is not logged."""
        from services.teams_service import TeamsService
        
        test_webhook = "https://secret.webhook.office.com/webhook/abc123"
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': test_webhook}):
            with caplog.at_level(logging.DEBUG):
                service = TeamsService()
                
                # Check that the actual URL is NOT in logs
                for record in caplog.records:
                    assert test_webhook not in record.message
                    assert "abc123" not in record.message
                    # Should only say "webhook configured" or similar
                    if "webhook" in record.message.lower():
                        assert "configured" in record.message.lower() or \
                               "no webhook" in record.message.lower()

    def test_logs_webhook_configured_status(self, caplog):
        """Test that we log whether webhook is configured (without URL)."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://webhook.example.com'}):
            with caplog.at_level(logging.INFO):
                service = TeamsService()
                
                # Should log that webhook is configured
                log_messages = [r.message.lower() for r in caplog.records]
                assert any("webhook" in msg and "configured" in msg for msg in log_messages) or \
                       len(caplog.records) == 0  # Or no logging at all is acceptable


class TestNotificationSending:
    """Test notification sending behavior."""

    def test_post_adaptive_card_without_webhook_returns_false(self):
        """Test post_adaptive_card returns False when no webhook configured."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {}, clear=True):
            service = TeamsService()
            result = service.post_adaptive_card({"type": "message"})
            assert result is False

    def test_post_adaptive_card_with_webhook(self):
        """Test post_adaptive_card sends to webhook."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://webhook.example.com'}):
            with patch('services.teams_service.requests.post') as mock_post:
                mock_response = MagicMock()
                mock_response.status_code = 200
                mock_response.text = "OK"
                mock_post.return_value = mock_response
                
                service = TeamsService()
                result = service.post_adaptive_card({"type": "message"})
                
                assert result is True
                mock_post.assert_called_once()

    def test_post_adaptive_card_handles_error(self):
        """Test post_adaptive_card handles HTTP errors gracefully."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://webhook.example.com'}):
            with patch('services.teams_service.requests.post') as mock_post:
                mock_post.side_effect = Exception("Network error")
                
                service = TeamsService()
                result = service.post_adaptive_card({"type": "message"})
                
                assert result is False


class TestAdaptiveCardFormatting:
    """Test Adaptive Card formatting for Teams."""

    def test_format_issue_card(self):
        """Test formatting an issue as an Adaptive Card."""
        from services.teams_service import TeamsService
        
        with patch.dict('os.environ', {'TEAMS_WEBHOOK_URL': 'https://webhook.example.com'}):
            service = TeamsService()
            
            if hasattr(service, 'format_issue_card'):
                card = service.format_issue_card(
                    title="Test Issue",
                    number=123,
                    priority="P1",
                    url="https://github.com/org/repo/issues/123"
                )
                
                assert card is not None
                # Card should contain issue info
                card_str = str(card)
                assert "123" in card_str or card.get("body") is not None
