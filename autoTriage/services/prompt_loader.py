"""
Prompt Loader Service - Load AI prompts from configuration files
"""
import os
import yaml
import logging
from typing import Dict, Optional
from pathlib import Path


class PromptLoader:
    """Service for loading AI prompts from YAML configuration files."""

    _instance: Optional['PromptLoader'] = None
    _prompts: Dict[str, str] = {}

    def __new__(cls):
        """Singleton pattern to avoid reloading prompts multiple times."""
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance._load_prompts()
        return cls._instance

    def _load_prompts(self):
        """Load prompts from the YAML configuration file."""
        # Find the prompts.yaml file relative to this service
        config_paths = [
            Path(__file__).parent.parent / "config" / "prompts.yaml",
            Path(__file__).parent.parent.parent / "config" / "prompts.yaml",
            Path("config/prompts.yaml"),
        ]

        prompts_file = None
        for path in config_paths:
            if path.exists():
                prompts_file = path
                break

        if not prompts_file:
            logging.warning("prompts.yaml not found, using default prompts")
            self._prompts = {}
            return

        try:
            with open(prompts_file, 'r', encoding='utf-8') as f:
                self._prompts = yaml.safe_load(f) or {}
            logging.info(f"Loaded {len(self._prompts)} prompts from {prompts_file}")
        except Exception as e:
            logging.error(f"Failed to load prompts.yaml: {e}")
            self._prompts = {}

    def get(self, prompt_name: str, default: str = "") -> str:
        """
        Get a prompt by name.

        Args:
            prompt_name: The name of the prompt (e.g., 'daily_digest_system')
            default: Default value if prompt not found

        Returns:
            The prompt string
        """
        return self._prompts.get(prompt_name, default)

    def format(self, prompt_name: str, default: str = "", **kwargs) -> str:
        """
        Get a prompt and format it with the provided variables.

        Args:
            prompt_name: The name of the prompt
            default: Default value if prompt not found
            **kwargs: Variables to substitute in the prompt

        Returns:
            The formatted prompt string
        """
        prompt = self.get(prompt_name, default)
        if not prompt:
            return default

        try:
            return prompt.format(**kwargs)
        except KeyError as e:
            logging.warning(f"Missing variable {e} in prompt '{prompt_name}'")
            return prompt

    def reload(self):
        """Force reload prompts from file (useful for development)."""
        self._load_prompts()


# Convenience function for getting the singleton instance
def get_prompt_loader() -> PromptLoader:
    """Get the singleton PromptLoader instance."""
    return PromptLoader()
