from qnyh_tool.app import session_paths
from qnyh_tool.config import AppConfig, ToolPaths


def test_session_paths_use_configured_data_directories():
    config = AppConfig(paths=ToolPaths(logs="var/log", checkpoints="var/state"))
    log_path, checkpoint_path = session_paths(config)
    assert str(log_path).replace("\\", "/") == "var/log/events.jsonl"
    assert str(checkpoint_path).replace("\\", "/") == "var/state/checkpoints.db"
