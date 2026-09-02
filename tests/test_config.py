import json

import pytest

from qnyh_tool.config import AppConfig, ConfigError, SafetyMode, load_config


def write_config(tmp_path, payload):
    path = tmp_path / "config.json"
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def test_load_valid_config(tmp_path):
    path = write_config(tmp_path, {"version": 1, "safety_mode": "dry_run", "timezone": "Asia/Ho_Chi_Minh", "selected_clients": ["client_1", "client_2"], "paths": {"profiles": "profiles-local"}})
    config = load_config(path)
    assert config.safety_mode is SafetyMode.DRY_RUN
    assert config.selected_clients == ("client_1", "client_2")
    assert config.paths.profiles == "profiles-local"


def test_default_config_is_observation_only():
    config = AppConfig()
    assert config.safety_mode is SafetyMode.OBSERVATION
    assert config.to_dict()["safety_mode"] == "observation"


def test_invalid_mode_is_rejected(tmp_path):
    path = write_config(tmp_path, {"safety_mode": "click_everything"})
    with pytest.raises(ConfigError, match="invalid safety_mode"):
        load_config(path)


def test_missing_file_is_rejected(tmp_path):
    with pytest.raises(ConfigError, match="config file not found"):
        load_config(tmp_path / "missing.json")


def test_secret_like_fields_are_rejected(tmp_path):
    path = write_config(tmp_path, {"safety_mode": "observation", "session": "not-real"})
    with pytest.raises(ConfigError, match="credential-like field"):
        load_config(path)


def test_duplicate_clients_are_rejected(tmp_path):
    path = write_config(tmp_path, {"selected_clients": ["client_1", "client_1"]})
    with pytest.raises(ConfigError, match="duplicates"):
        load_config(path)
